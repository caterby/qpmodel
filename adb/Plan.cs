﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;

namespace adb
{
    public class ProfileOption
    {
        public bool enabled_ = false;
    }

    public class OptimizeOption
    {
        // rewrite controls
        public bool enable_subquery_to_markjoin_ = true;
        public bool enable_hashjoin_ = true;
        public bool enable_nljoin_ = true;

        // optimizer controls
        public bool use_memo_ = false;
    }

    public static class ExplainOption {
        public static bool costoff_ = true;
    }

    public abstract class PlanNode<T> where T : PlanNode<T>
    {
        public List<T> children_ = new List<T>();
        public bool IsLeaf() => children_.Count == 0;

        // shortcut for conventional names
        public T child_() { Debug.Assert(children_.Count == 1); return children_[0]; }
        public T l_() { Debug.Assert(children_.Count == 2); return children_[0]; }
        public T r_() { Debug.Assert(children_.Count == 2); return children_[1]; }

        // print utilities
        public virtual string PrintOutput(int depth) => null;
        public virtual string PrintInlineDetails(int depth) => null;
        public virtual string PrintMoreDetails(int depth) => null;
        protected string PrintFilter(Expr filter, int depth)
        {
            string r = null;
            if (filter != null)
            {
                r = "Filter: " + filter.PrintString(depth);
                // append the subquery plan align with filter
                r += ExprHelper.PrintExprWithSubqueryExpanded(filter, depth);
            }
            return r;
        }

        public string PrintString(int depth)
        {
            string r = null;
            if (!(this is PhysicProfiling))
            {
                r = Utils.Tabs(depth);
                if (depth != 0)
                    r += "-> ";

                // output line of <nodeName> : <Estimation> <Actual>
                r += $"{this.GetType().Name} {PrintInlineDetails(depth)}";
                var phynode = this as PhysicNode;
                if (phynode != null && phynode.profile_ != null)
                {
                    if (!ExplainOption.costoff_)
                        r += $" (cost = {phynode.Cost()}, rows={phynode.logic_.EstCardinality()})";
                    r += $" (actual rows = {phynode.profile_.nrows_})";
                }
                r += "\n";
                var details = PrintMoreDetails(depth);

                // output of current node
                var output = PrintOutput(depth);
                if (output != null)
                    r += Utils.Tabs(depth + 2) + output + "\n";
                if (details != null)
                {
                    // remove the last \n in case the details is a subquery
                    var trailing = "\n";
                    if (details[details.Length - 1] == '\n')
                        trailing = "";
                    r += Utils.Tabs(depth + 2) + details + trailing;
                }

                depth += 2;
            }

            children_.ForEach(x => r += x.PrintString(depth));
            return r;
        }

        // traversal pattern EXISTS
        //  if any visit returns a true, stop recursion. So if you want to
        //  visit all nodes regardless, use TraverseEachNode(). 
        // 
        public bool VisitEachNodeExists(Func<PlanNode<T>, bool> callback)
        {
            bool exists = callback(this);
            if (!exists)
            {
                foreach (var c in children_)
                    if (c.VisitEachNodeExists(callback))
                        return true;
            }

            return exists;
        }

        // traversal pattern FOR EACH
        public void TraversEachNode(Action<PlanNode<T>> callback)
        {
            callback(this);
            foreach (var c in children_)
                c.TraversEachNode(callback);
        }

        // lookup all T1 types in the tree and return the parent-target relationship
        public int FindNodeTyped<T1>(List<T> parents, List<int> childIndex, List<T1> targets) where T1 : PlanNode<T>
        {
            if (this is T1 yf)
            {
                parents.Add(null);
                childIndex.Add(-1);
                targets.Add(yf);
            }

            TraversEachNode(x =>
            {
                for (int i = 0; i < x.children_.Count; i++)
                {
                    var y = x.children_[i];
                    if (y is T1 yf)
                    {
                        parents.Add(x as T);
                        childIndex.Add(i);
                        targets.Add(yf);
                    }
                }
            });

            Debug.Assert(parents.Count == targets.Count);
            return parents.Count;
        }

        public int CountNodeTyped<T1>() where T1 : PlanNode<T>
        {
            var parents = new List<T>();
            var indexes = new List<int>();
            var targets = new List<T1>();
            return FindNodeTyped<T1>(parents, indexes, targets);
        }
        public override int GetHashCode()
        {
            return GetType().GetHashCode() ^ Utils.ListHashCode(children_);
        }
        public override bool Equals(object obj)
        {
            if (obj is PlanNode<T> lo)
            {
                if (lo.GetType() != GetType())
                    return false;
                for (int i = 0; i < children_.Count; i++)
                {
                    if (!lo.children_[i].Equals(children_[i]))
                        return false;
                }
                return true;
            }
            return false;
        }
    }

    public partial class SelectStmt : SQLStatement
    {
        List<SelectStmt> createSubQueryExprPlan(Expr expr)
        {
            var subplans = new List<SelectStmt>();
            expr.VisitEachExpr(x =>
            {
                if (x is SubqueryExpr sx)
                {
                    Debug.Assert(expr.HasSubQuery());
                    sx.query_.CreatePlan();
                    subplans.Add(sx.query_);
                }
            });

            subqueries_.AddRange(subplans);
            return subplans.Count > 0 ? subplans : null;
        }

        // select i, min(i/2), 2+min(i)+max(i) from A group by i
        // => min(i/2), 2+min(i)+max(i)
        List<Expr> getAggregations()
        {
            var r = new List<Expr>();
            selection_.ForEach(x =>
            {
                x.VisitEachExpr(y =>
                {
                    if (y is AggFunc)
                        r.Add(x);
                });
            });

            return r.Distinct().ToList();
        }

        // from clause -
        //  pair each from item with cross join, their join conditions will be handled
        //  with where clauss processing.
        //
        LogicNode transformFromClause()
        {
            LogicNode transformOneFrom(TableRef tab)
            {
                LogicNode from;
                switch (tab)
                {
                    case BaseTableRef bref:
                        from = new LogicScanTable(bref);
                        break;
                    case ExternalTableRef eref:
                        from = new LogicScanFile(eref);
                        break;
                    case QueryRef sref:
                        var plan = sref.query_.CreatePlan();
                        from = new LogicFromQuery(sref, plan);
                        subqueries_.Add(sref.query_);
                        fromqueries_.Add(sref.query_, from as LogicFromQuery);
                        break;
                    case JoinQueryRef jref:
                        // We will form join group on all tables and put a filter on top
                        // of the joins as a normalized form for later processing.
                        //
                        //      from a join b on a1=b1 or a3=b3 join c on a2=c2;
                        //   => from a , b, c where  (a1=b1 or a3=b3) and a2=c2;
                        //
                        LogicJoin subjoin = new LogicJoin(null, null);
                        Expr filterexpr = null;
                        for (int i = 0; i < jref.tables_.Count; i++)
                        {
                            LogicNode t = transformOneFrom(jref.tables_[i]);
                            var children = subjoin.children_;
                            if (children[0] is null)
                                children[0] = t;
                            else
                            {
                                if (children[1] is null)
                                    children[1] = t;
                                else
                                    subjoin = new LogicJoin(t, subjoin);
                                filterexpr = FilterHelper.AddAndFilter(filterexpr, jref.constraints_[i - 1]);
                            }
                        }
                        Debug.Assert(filterexpr != null);
                        from = new LogicFilter(subjoin, filterexpr);
                        break;
                    default:
                        throw new Exception();
                }

                return from;
            }

            LogicNode root;
            if (from_.Count >= 2)
            {
                var join = new LogicJoin(null, null);
                var children = join.children_;
                from_.ForEach(x =>
                {
                    LogicNode from = transformOneFrom(x);
                    if (children[0] is null)
                        children[0] = from;
                    else
                        children[1] = (children[1] is null) ? from :
                                        new LogicJoin(from, children[1]);
                });
                root = join;
            }
            else if (from_.Count == 1)
                root = transformOneFrom(from_[0]);
            else
                root = new LogicResult(selection_);

            return root;
        }

        /*
            SQL is implemented as if a query was executed in the following order:

            FROM clause
            WHERE clause
            GROUP BY clause
            HAVING clause
            SELECT clause
            ORDER BY clause
        */
        public override LogicNode CreatePlan()
        {
            LogicNode root = transformFromClause();

            // transform where clause
            if (where_ != null)
            {
                createSubQueryExprPlan(where_);
                root = new LogicFilter(root, where_);
            }

            // group by
            if (hasAgg_ || groupby_ != null)
                root = new LogicAgg(root, groupby_, getAggregations(), having_);

            // having
            if (having_ != null)
                createSubQueryExprPlan(having_);

            // order by
            if (orders_ != null)
                root = new LogicOrder(root, orders_, descends_);

            // selection list
            selection_.ForEach(x => createSubQueryExprPlan(x));

            logicPlan_ = root;
            return root;
        }

        public override BindContext Bind(BindContext parent)
        {
            BindContext context = new BindContext(this, parent);
            parent_ = parent?.stmt_ as SelectStmt;
            bindContext_ = context;

            var ret = BindWithContext(context);
            bounded_ = true;
            return ret;
        }
        internal BindContext BindWithContext(BindContext context)
        {
            void bindSelectionList(BindContext context)
            {
                List<SelStar> selstars = new List<SelStar>();
                selection_.ForEach(x =>
                {
                    if (x is SelStar xs)
                        selstars.Add(xs);
                    else
                    {
                        x.Bind(context);
                        if (x.HasAggFunc())
                            hasAgg_ = true;
                    }
                });

                // expand * into actual columns
                selstars.ForEach(x =>
                {
                    selection_.Remove(x);
                    selection_.AddRange(x.Expand(context));
                });
            }

            // bind stage is earlier than plan creation
            Debug.Assert(logicPlan_ == null);

            // rules:
            //  - groupby/orderby may reference selection list's alias, so let's 
            //    expand them first
            //  - from binding shall be the first since it may create new alias
            //
            groupby_ = replaceOutputNameToExpr(groupby_);
            orders_ = replaceOutputNameToExpr(orders_);

            // from binding shall be the first since it may create new alias
            bindFrom(context);
            bindSelectionList(context);
            where_?.Bind(context);
            groupby_?.ForEach(x => x.Bind(context));
            having_?.Bind(context);
            orders_?.ForEach(x => x.Bind(context));

            return context;
        }

        void bindFrom(BindContext context)
        {
            CTEQueryRef wayUpToFindCte(BindContext context, string alias)
            {
                var parent = context;
                do
                {
                    var topctes = (parent.stmt_ as SelectStmt).ctefrom_;
                    CTEQueryRef cte;
                    if (topctes != null &&
                        null != (cte = topctes.Find(x => x.alias_.Equals(alias))))
                        return cte;
                } while ((parent = parent.parent_) != null);
                return null;
            }

            // We enumerate all CTEs first
            if (ctes_ != null)
            {
                ctefrom_ = new List<CTEQueryRef>();
                ctes_.ForEach(x => {
                    var cte = new CTEQueryRef(x.query_ as SelectStmt, x.alias_);
                    ctefrom_.Add(cte);
                });
            }

            // replace any BaseTableRef that can't find in system to CTE
            for (int i = 0; i < from_.Count; i++)
            {
                var x = from_[i];
                if (x is BaseTableRef bref &&
                    Catalog.systable_.TryTable(bref.relname_) is null)
                {
                    from_[i] = wayUpToFindCte(context, bref.alias_);
                    if (from_[i] is null)
                        throw new Exception($@"table {bref.relname_} not exists");
                }
            }

            from_.ForEach(x =>
            {
                switch (x)
                {
                    case BaseTableRef bref:
                        Debug.Assert(Catalog.systable_.TryTable(bref.relname_) != null);
                        context.AddTable(bref);
                        break;
                    case ExternalTableRef eref:
                        if (Catalog.systable_.TryTable(eref.baseref_.relname_) != null)
                            context.AddTable(eref);
                        else
                            throw new Exception($@"base table {eref.baseref_.relname_} not exists");
                        break;
                    case QueryRef qref:
                        if (qref.query_.bindContext_ is null)
                            qref.query_.Bind(context);

                        // the subquery itself in from clause can be seen as a new table, so register it here
                        context.AddTable(qref);
                        break;
                    case JoinQueryRef jref:
                        jref.tables_.ForEach(context.AddTable);
                        jref.constraints_.ForEach(x => x.Bind(context));
                        break;
                    default:
                        throw new NotImplementedException();
                }
            });
        }

        // for each expr in @list, if expr has references an alias in selection list, 
        // replace that with the true expression.
        // example:
        //      selection_: a1*5 as alias1, a2, b3
        //      orders_: alias1+b =>  a1*5+b
        //
        List<Expr> replaceOutputNameToExpr(List<Expr> list)
        {
            List<Expr> selection = selection_;

            if (list is null)
                return null;

            var newlist = new List<Expr>();
            foreach (var v in list)
            {
                Expr newv = v;
                foreach (var s in selection)
                {
                    if (s.alias_ != null)
                        newv = newv.SearchReplace(s.alias_, s);
                }
                newlist.Add(newv);
            }

            Debug.Assert(newlist.Count == list.Count);
            return newlist;
        }
    }
}
