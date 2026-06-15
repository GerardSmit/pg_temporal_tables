/*
 * temporal_hooks.c
 *
 * Transparent AS OF time travel, driven by the temporal.as_of GUC.
 *
 * planner_hook: when as_of is set, every range-table reference to a tracked
 * table is replaced in place by a subquery
 *
 *     (SELECT <cols> FROM base    WHERE valid_from <= $asof
 *      UNION ALL
 *      SELECT <cols> FROM history WHERE valid_from <= $asof AND valid_to > $asof)
 *
 * so unmodified application queries (including JOINs, views, CTEs and
 * sublinks) read the data as it was at that moment. The hook runs on the
 * rewritten query tree, after view expansion, so tracked tables inside views
 * time-travel too. The timestamp is baked in as a Const; the as_of GUC's
 * assign hook calls ResetPlanCache() so cached plans never outlive it.
 *
 * Permission note: the converted RTE keeps its original RTEPermissionInfo
 * (the SELECT check against the base table, exactly like view expansion
 * keeps the view's own check), and the inner relations carry none. Time
 * travel therefore requires only the privileges the query needed anyway.
 *
 * ProcessUtility_hook: blocks table COPY and LOCK TABLE while as_of is set;
 * those utility commands bypass the planner, so COPY TO would silently export
 * present-day data, COPY FROM would write, and LOCK TABLE would lock the
 * present-day table while the session is meant to be historical/read-only.
 */
#include "postgres.h"

#include "access/table.h"
#include "access/transam.h"
#include "catalog/namespace.h"
#include "catalog/pg_class.h"
#include "catalog/pg_collation.h"
#include "catalog/pg_type.h"
#include "commands/copy.h"
#include "miscadmin.h"
#include "nodes/makefuncs.h"
#include "nodes/nodeFuncs.h"
#include "access/sysattr.h"
#include "nodes/parsenodes.h"
#include "nodes/plannodes.h"
#include "nodes/params.h"
#include "optimizer/optimizer.h"
#include "optimizer/planner.h"
#include "parser/parse_func.h"
#include "parser/parse_relation.h"
#include "tcop/utility.h"
#include "utils/builtins.h"
#include "utils/lsyscache.h"
#include "utils/rel.h"
#include "utils/timestamp.h"
#include "utils/varlena.h"

#include "temporal_catalog.h"
#include "temporal_hooks.h"

static planner_hook_type prev_planner_hook = NULL;
static ProcessUtility_hook_type prev_process_utility_hook = NULL;

static bool in_temporal_planner = false;

static void rewrite_query_for_as_of(Query *query, TimestampTz as_of);

static bool
rte_is_tracked_relation(RangeTblEntry *rte)
{
	return rte != NULL &&
		rte->rtekind == RTE_RELATION &&
		rte->relid >= FirstNormalObjectId &&
		temporal_lookup(rte->relid) != NULL;
}

static RangeTblEntry *
rte_at_index(Query *query, Index rtindex)
{
	if (rtindex == 0 || rtindex > (Index) list_length(query->rtable))
		return NULL;
	return (RangeTblEntry *) list_nth(query->rtable, rtindex - 1);
}

static void
ereport_temporal_asof_write(Oid relid)
{
	ereport(ERROR,
			(errcode(ERRCODE_READ_ONLY_SQL_TRANSACTION),
			 errmsg("cannot modify temporal table \"%s\" while temporal.as_of is set",
					get_rel_name(relid)),
			 errhint("RESET temporal.as_of to return to the present.")));
}

static void
ereport_temporal_asof_lock(Oid relid)
{
	ereport(ERROR,
			(errcode(ERRCODE_READ_ONLY_SQL_TRANSACTION),
			 errmsg("cannot lock temporal table \"%s\" while temporal.as_of is set",
					get_rel_name(relid)),
			 errhint("Remove the row-locking clause or RESET temporal.as_of to return to the present.")));
}

/* ─────────────────────────────────────────────────────────────────────────
 * Query-level AS OF marker: WHERE temporal.as_of('2026-06-01').
 *
 * PostgreSQL has no per-statement OPTION clause, so we provide one: the
 * marker function is recognized here at plan time, removed from the query,
 * and its timestamp time-travels the entire statement — equivalent to
 * running it with temporal.as_of set, but scoped to this query only.
 * If the marker ever actually executes (extension not preloaded), the SQL
 * function body raises an error instead of silently returning present-day
 * data.
 * ───────────────────────────────────────────────────────────────────────── */

typedef struct MarkerMutatorContext
{
	Oid			funcoid;
	ParamListInfo boundParams;
	bool		found;
	TimestampTz as_of;
} MarkerMutatorContext;

/* OID of temporal.as_of(timestamptz), or InvalidOid when not installed. */
static Oid
as_of_marker_funcoid(void)
{
	Oid			argtypes[1] = {TIMESTAMPTZOID};

	return LookupFuncName(list_make2(makeString("temporal"), makeString("as_of")),
						  1, argtypes, true);
}

static TimestampTz
marker_arg_timestamp(FuncExpr *fexpr, ParamListInfo boundParams)
{
	Node	   *arg = (Node *) linitial(fexpr->args);

	/* look through implicit coercions (e.g. timestamp -> timestamptz) */
	arg = strip_implicit_coercions(arg);

	if (IsA(arg, Const))
	{
		Const	   *c = (Const *) arg;

		if (c->constisnull)
			ereport(ERROR,
					(errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED),
					 errmsg("temporal.as_of() timestamp cannot be NULL")));
		if (c->consttype == TIMESTAMPTZOID)
			return DatumGetTimestampTz(c->constvalue);
	}
	else if (IsA(arg, Param) &&
			 ((Param *) arg)->paramkind == PARAM_EXTERN &&
			 boundParams != NULL)
	{
		Param	   *p = (Param *) arg;

		if (p->paramid > 0 && p->paramid <= boundParams->numParams)
		{
			ParamExternData *ped;
			ParamExternData peds;

			if (boundParams->paramFetch != NULL)
				ped = boundParams->paramFetch(boundParams, p->paramid, false, &peds);
			else
				ped = &boundParams->params[p->paramid - 1];

			if (OidIsValid(ped->ptype) && !ped->isnull &&
				ped->ptype == TIMESTAMPTZOID)
				return DatumGetTimestampTz(ped->value);
		}
	}

	ereport(ERROR,
			(errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
			 errmsg("temporal.as_of() requires a plan-time timestamptz value"),
			 errhint("Use a literal (or a bound parameter outside a generic plan), "
					 "e.g. WHERE temporal.as_of('2026-06-01').")));
	return 0;					/* unreachable */
}

static Node *
marker_mutator(Node *node, MarkerMutatorContext *ctx)
{
	if (node == NULL)
		return NULL;

	if (IsA(node, FuncExpr) && ((FuncExpr *) node)->funcid == ctx->funcoid)
	{
		TimestampTz ts = marker_arg_timestamp((FuncExpr *) node, ctx->boundParams);

		if (ctx->found && ts != ctx->as_of)
			ereport(ERROR,
					(errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
					 errmsg("conflicting temporal.as_of() timestamps in one statement")));
		ctx->found = true;
		ctx->as_of = ts;

		return makeBoolConst(true, false);
	}

	if (IsA(node, Query))
		return (Node *) query_tree_mutator((Query *) node, marker_mutator, ctx, 0);

	return expression_tree_mutator(node, marker_mutator, ctx);
}

/*
 * The active as_of timestamp, parsed from the GUC. Returns false when time
 * travel is off.
 */
static bool
get_as_of(TimestampTz *ts)
{
	if (temporal_as_of_guc == NULL || temporal_as_of_guc[0] == '\0')
		return false;

	*ts = DatumGetTimestampTz(DirectFunctionCall3(timestamptz_in,
												  CStringGetDatum(temporal_as_of_guc),
												  ObjectIdGetDatum(InvalidOid),
												  Int32GetDatum(-1)));
	return true;
}

/* timestamptz comparison OpExpr: <var-or-const> <op> <const> */
static Node *
make_tstz_cmp(const char *opname, Node *lhs, Node *rhs)
{
	Oid			opno;
	OpExpr	   *op;

	opno = OpernameGetOprid(list_make2(makeString("pg_catalog"),
									   makeString(pstrdup(opname))),
							TIMESTAMPTZOID, TIMESTAMPTZOID);
	if (!OidIsValid(opno))
		elog(ERROR, "timestamptz operator %s not found", opname);

	op = (OpExpr *) make_opclause(opno, BOOLOID, false,
								  (Expr *) lhs, (Expr *) rhs,
								  InvalidOid, InvalidOid);
	op->opfuncid = get_opcode(opno);
	return (Node *) op;
}

static Const *
make_asof_const(TimestampTz as_of)
{
	return makeConst(TIMESTAMPTZOID, -1, InvalidOid, sizeof(TimestampTz),
					 TimestampTzGetDatum(as_of), false, FLOAT8PASSBYVAL);
}

/*
 * Build one arm of the UNION ALL: a simple SELECT over rel projecting one
 * output column per base-table attribute (positionally, dropped attributes
 * become NULL::text), filtered by the given qual. Column lookup in `rel` is
 * by name, so the history table's own column order does not matter.
 */
static Query *
build_arm(Relation rel, TupleDesc base_td, Node *qual, AttrNumber *attno_map)
{
	Query	   *arm = makeNode(Query);
	RangeTblEntry *rte = makeNode(RangeTblEntry);
	RangeTblRef *rtr = makeNode(RangeTblRef);
	RTEPermissionInfo *perminfo;
	TupleDesc	rel_td = RelationGetDescr(rel);
	List	   *colnames = NIL;
	List	   *tlist = NIL;
	int			i;

	for (i = 0; i < rel_td->natts; i++)
	{
		Form_pg_attribute att = TupleDescAttr(rel_td, i);

		colnames = lappend(colnames, makeString(att->attisdropped ? "" :
												pstrdup(NameStr(att->attname))));
	}

	rte->rtekind = RTE_RELATION;
	rte->relid = RelationGetRelid(rel);
	rte->relkind = rel->rd_rel->relkind;
	rte->rellockmode = AccessShareLock;
	/* a partitioned base/history table must be scanned with inheritance on so
	 * the planner expands its partitions */
	rte->inh = (rel->rd_rel->relkind == RELKIND_PARTITIONED_TABLE);
	rte->lateral = false;
	rte->inFromCl = true;
	rte->eref = makeAlias(pstrdup(RelationGetRelationName(rel)), colnames);

	arm->commandType = CMD_SELECT;
	arm->canSetTag = false;
	arm->rtable = list_make1(rte);
	perminfo = addRTEPermissionInfo(&arm->rteperminfos, rte);
	perminfo->requiredPerms = 0;

	rtr->rtindex = 1;
	arm->jointree = makeFromExpr(list_make1(rtr), qual);

	for (i = 0; i < base_td->natts; i++)
	{
		Form_pg_attribute att = TupleDescAttr(base_td, i);
		TargetEntry *tle;
		Expr	   *expr;
		char	   *resname;

		if (att->attisdropped)
		{
			expr = (Expr *) makeConst(TEXTOID, -1, InvalidOid, -1,
									  (Datum) 0, true, false);
			resname = "";
		}
		else
		{
			AttrNumber	relattno = attno_map ? attno_map[i] : att->attnum;

			expr = (Expr *) makeVar(1, relattno, att->atttypid,
									att->atttypmod, att->attcollation, 0);
			resname = pstrdup(NameStr(att->attname));
		}
		tle = makeTargetEntry(expr, i + 1, resname, false);
		tlist = lappend(tlist, tle);
	}
	arm->targetList = tlist;

	return arm;
}

/* Wrap an arm query in an RTE_SUBQUERY for the set-operation rtable. */
static RangeTblEntry *
wrap_arm(Query *arm, const char *name, TupleDesc base_td)
{
	RangeTblEntry *rte = makeNode(RangeTblEntry);
	List	   *colnames = NIL;
	int			i;

	for (i = 0; i < base_td->natts; i++)
	{
		Form_pg_attribute att = TupleDescAttr(base_td, i);

		colnames = lappend(colnames, makeString(att->attisdropped ? "" :
												pstrdup(NameStr(att->attname))));
	}

	rte->rtekind = RTE_SUBQUERY;
	rte->subquery = arm;
	rte->security_barrier = false;
	rte->lateral = false;
	rte->inh = false;
	rte->inFromCl = true;
	rte->eref = makeAlias(pstrdup(name), colnames);

	return rte;
}

/*
 * Convert one tracked-table RTE into the UNION ALL subquery, in place.
 */
static void
convert_rte_to_as_of(RangeTblEntry *rte, TemporalTableEntry *entry,
					 TimestampTz as_of)
{
	Relation	base_rel;
	Relation	hist_rel;
	TupleDesc	base_td;
	TupleDesc	hist_td;
	AttrNumber *hist_map;
	Query	   *base_arm;
	Query	   *hist_arm;
	Query	   *setop;
	SetOperationStmt *sostmt;
	RangeTblRef *lref;
	RangeTblRef *rref;
	Var		   *vf_var;
	Var		   *vt_var;
	Node	   *base_qual;
	Node	   *hist_qual;
	AttrNumber	hist_vf_attno = InvalidAttrNumber;
	AttrNumber	hist_vt_attno = InvalidAttrNumber;
	List	   *coltypes = NIL;
	List	   *coltypmods = NIL;
	List	   *colcollations = NIL;
	List	   *outer_tlist = NIL;
	int			i;

	if (rte->tablesample)
		ereport(ERROR,
				(errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
				 errmsg("TABLESAMPLE on temporal table is not supported while temporal.as_of is set")));

	/* the parser already holds rte->rellockmode on the base relation */
	base_rel = table_open(entry->base_relid, NoLock);
	hist_rel = table_open(entry->history_relid, AccessShareLock);
	base_td = RelationGetDescr(base_rel);
	hist_td = RelationGetDescr(hist_rel);

	/* map base attribute positions to history attnums, by column name */
	hist_map = palloc0(base_td->natts * sizeof(AttrNumber));
	for (i = 0; i < base_td->natts; i++)
	{
		Form_pg_attribute att = TupleDescAttr(base_td, i);
		int			j;

		if (att->attisdropped)
			continue;
		for (j = 0; j < hist_td->natts; j++)
		{
			Form_pg_attribute hatt = TupleDescAttr(hist_td, j);

			if (!hatt->attisdropped &&
				strcmp(NameStr(att->attname), NameStr(hatt->attname)) == 0)
			{
				hist_map[i] = hatt->attnum;
				break;
			}
		}
		if (hist_map[i] == InvalidAttrNumber)
			ereport(ERROR,
					(errcode(ERRCODE_DATA_CORRUPTED),
					 errmsg("history table \"%s\" is missing column \"%s\"",
							RelationGetRelationName(hist_rel),
							NameStr(att->attname))));
	}
	for (i = 0; i < hist_td->natts; i++)
	{
		Form_pg_attribute hatt = TupleDescAttr(hist_td, i);

		if (hatt->attisdropped)
			continue;
		if (strcmp(NameStr(hatt->attname), "valid_from") == 0)
			hist_vf_attno = hatt->attnum;
		else if (strcmp(NameStr(hatt->attname), "valid_to") == 0)
			hist_vt_attno = hatt->attnum;
	}

	/* base arm: valid_from <= asof */
	vf_var = makeVar(1, entry->valid_from_attnum, TIMESTAMPTZOID, -1,
					 InvalidOid, 0);
	base_qual = make_tstz_cmp("<=", (Node *) vf_var,
							  (Node *) make_asof_const(as_of));
	base_arm = build_arm(base_rel, base_td, base_qual, NULL);

	/* history arm: valid_from <= asof AND valid_to > asof */
	vf_var = makeVar(1, hist_vf_attno, TIMESTAMPTZOID, -1, InvalidOid, 0);
	vt_var = makeVar(1, hist_vt_attno, TIMESTAMPTZOID, -1, InvalidOid, 0);
	hist_qual = (Node *) makeBoolExpr(AND_EXPR,
									  list_make2(make_tstz_cmp("<=", (Node *) vf_var,
															   (Node *) make_asof_const(as_of)),
												 make_tstz_cmp(">", (Node *) vt_var,
															   (Node *) make_asof_const(as_of))),
									  -1);
	hist_arm = build_arm(hist_rel, base_td, hist_qual, hist_map);

	/* outer set-operation query: base UNION ALL history */
	setop = makeNode(Query);
	setop->commandType = CMD_SELECT;
	setop->canSetTag = false;
	setop->rtable = list_make2(wrap_arm(base_arm, "*SELECT* 1", base_td),
							   wrap_arm(hist_arm, "*SELECT* 2", base_td));
	setop->rteperminfos = NIL;
	setop->jointree = makeFromExpr(NIL, NULL);

	lref = makeNode(RangeTblRef);
	lref->rtindex = 1;
	rref = makeNode(RangeTblRef);
	rref->rtindex = 2;

	for (i = 0; i < base_td->natts; i++)
	{
		Form_pg_attribute att = TupleDescAttr(base_td, i);
		Oid			coltype = att->attisdropped ? TEXTOID : att->atttypid;
		int32		coltypmod = att->attisdropped ? -1 : att->atttypmod;
		Oid			colcoll = att->attisdropped ? InvalidOid : att->attcollation;
		Var		   *v;
		TargetEntry *tle;

		coltypes = lappend_oid(coltypes, coltype);
		coltypmods = lappend_int(coltypmods, coltypmod);
		colcollations = lappend_oid(colcollations, colcoll);

		v = makeVar(1, i + 1, coltype, coltypmod, colcoll, 0);
		tle = makeTargetEntry((Expr *) v, i + 1,
							  att->attisdropped ? "" : pstrdup(NameStr(att->attname)),
							  false);
		outer_tlist = lappend(outer_tlist, tle);
	}
	setop->targetList = outer_tlist;

	sostmt = makeNode(SetOperationStmt);
	sostmt->op = SETOP_UNION;
	sostmt->all = true;
	sostmt->larg = (Node *) lref;
	sostmt->rarg = (Node *) rref;
	sostmt->colTypes = coltypes;
	sostmt->colTypmods = coltypmods;
	sostmt->colCollations = colcollations;
	sostmt->groupClauses = NIL;
	setop->setOperations = (Node *) sostmt;

	table_close(hist_rel, NoLock);	/* keep the lock until end of xact */
	table_close(base_rel, NoLock);

	/*
	 * Mutate the original RTE in place. Vars elsewhere in the query keep
	 * their varno/varattno: the subquery's targetlist is positionally
	 * identical to the relation's attributes. We keep relid/relkind (like
	 * view expansion does) so plan-cache revalidation re-locks the table,
	 * the original perminfoindex (SELECT check on the base table), and any
	 * securityQuals (RLS filters the union output, Vars still align).
	 */
	rte->rtekind = RTE_SUBQUERY;
	rte->subquery = setop;
	rte->inh = false;
	rte->tablesample = NULL;
}

/* Recurse into sublink subselects anywhere in the expression trees. */
typedef struct AsOfWalkerContext
{
	TimestampTz as_of;
} AsOfWalkerContext;

static bool
as_of_expr_walker(Node *node, AsOfWalkerContext *ctx)
{
	if (node == NULL)
		return false;
	if (IsA(node, SubLink))
	{
		SubLink    *sl = (SubLink *) node;

		if (sl->subselect && IsA(sl->subselect, Query))
			rewrite_query_for_as_of((Query *) sl->subselect, ctx->as_of);
		/* still walk testexpr */
	}
	return expression_tree_walker(node, as_of_expr_walker, ctx);
}

static void
rewrite_query_for_as_of(Query *query, TimestampTz as_of)
{
	ListCell   *lc;
	AsOfWalkerContext ctx;

	/* Writes to tracked tables make no sense against a historical snapshot. */
	if (query->commandType != CMD_SELECT && query->resultRelation != 0)
	{
		RangeTblEntry *target_rte = rte_at_index(query, query->resultRelation);

		if (rte_is_tracked_relation(target_rte))
			ereport_temporal_asof_write(target_rte->relid);
	}

	foreach(lc, query->rowMarks)
	{
		RowMarkClause *rowmark = (RowMarkClause *) lfirst(lc);
		RangeTblEntry *rte = rte_at_index(query, rowmark->rti);

		if (rte_is_tracked_relation(rte))
			ereport_temporal_asof_lock(rte->relid);
	}

	foreach(lc, query->rtable)
	{
		RangeTblEntry *rte = (RangeTblEntry *) lfirst(lc);

		if (rte->rtekind == RTE_SUBQUERY && rte->subquery)
		{
			/* includes expanded views */
			rewrite_query_for_as_of(rte->subquery, as_of);
		}
		else if (rte->rtekind == RTE_RELATION &&
				 (rte->relkind == RELKIND_RELATION ||
				  rte->relkind == RELKIND_PARTITIONED_TABLE) &&
				 rte->relid >= FirstNormalObjectId)
		{
			TemporalTableEntry *entry = temporal_lookup(rte->relid);

			if (entry != NULL)
				convert_rte_to_as_of(rte, entry, as_of);
		}
	}

	foreach(lc, query->cteList)
	{
		CommonTableExpr *cte = (CommonTableExpr *) lfirst(lc);

		if (cte->ctequery && IsA(cte->ctequery, Query))
			rewrite_query_for_as_of((Query *) cte->ctequery, as_of);
	}

	/* sublinks in quals / targetlists / etc. */
	ctx.as_of = as_of;
	query_tree_walker(query, as_of_expr_walker, &ctx,
					  QTW_IGNORE_RANGE_TABLE | QTW_IGNORE_CTE_SUBQUERIES);
}

static PlannedStmt *
temporal_planner(Query *parse, const char *query_string, int cursorOptions,
				 ParamListInfo boundParams)
{
	/* volatile: reassigned inside PG_TRY and read after it (avoid clobber) */
	Query *volatile rewritten = parse;

	if (!in_temporal_planner &&
		IsTransactionState() &&
		!temporal_in_internal_write)
	{
		in_temporal_planner = true;
		PG_TRY();
		{
			TimestampTz as_of;
			bool		active;
			MarkerMutatorContext ctx = {0};

			/* query-level marker first: it overrides the session GUC */
			ctx.funcoid = as_of_marker_funcoid();
			ctx.boundParams = boundParams;
			if (OidIsValid(ctx.funcoid))
				rewritten = (Query *) marker_mutator((Node *) rewritten, &ctx);

			if (ctx.found)
			{
				as_of = ctx.as_of;
				active = true;
			}
			else
				active = get_as_of(&as_of);

			if (active)
				rewrite_query_for_as_of(rewritten, as_of);
		}
		PG_FINALLY();
		{
			in_temporal_planner = false;
		}
		PG_END_TRY();
	}

	if (prev_planner_hook)
		return prev_planner_hook(rewritten, query_string, cursorOptions, boundParams);
	return standard_planner(rewritten, query_string, cursorOptions, boundParams);
}

/*
 * Table COPY bypasses the planner; block it for tracked tables while time
 * traveling. COPY (SELECT ...) TO goes through the planner normally.
 */
static void
temporal_process_utility(PlannedStmt *pstmt, const char *queryString,
						 bool readOnlyTree, ProcessUtilityContext context,
						 ParamListInfo params, QueryEnvironment *queryEnv,
						 DestReceiver *dest, QueryCompletion *qc)
{
	if (IsA(pstmt->utilityStmt, CopyStmt))
	{
		CopyStmt   *stmt = (CopyStmt *) pstmt->utilityStmt;
		TimestampTz as_of;

		if (stmt->relation != NULL && IsTransactionState() && get_as_of(&as_of))
		{
			Oid			relid = RangeVarGetRelid(stmt->relation, AccessShareLock, true);

			if (OidIsValid(relid) && temporal_lookup(relid) != NULL)
			{
				if (stmt->is_from)
					ereport(ERROR,
							(errcode(ERRCODE_READ_ONLY_SQL_TRANSACTION),
							 errmsg("cannot modify temporal table \"%s\" while temporal.as_of is set",
									stmt->relation->relname),
							 errhint("RESET temporal.as_of to return to the present.")));

				ereport(ERROR,
						(errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
						 errmsg("COPY of temporal table \"%s\" is not allowed while temporal.as_of is set",
								stmt->relation->relname),
						 errhint("Use COPY (SELECT ...) TO instead; it honors temporal.as_of.")));
			}
		}
	}
	else if (IsA(pstmt->utilityStmt, LockStmt))
	{
		LockStmt   *stmt = (LockStmt *) pstmt->utilityStmt;
		TimestampTz as_of;
		ListCell   *lc;

		if (IsTransactionState() && get_as_of(&as_of))
		{
			foreach(lc, stmt->relations)
			{
				RangeVar   *rv = (RangeVar *) lfirst(lc);
				Oid			relid = RangeVarGetRelid(rv, AccessShareLock, true);

				if (OidIsValid(relid) && temporal_lookup(relid) != NULL)
					ereport_temporal_asof_lock(relid);
			}
		}
	}

	if (prev_process_utility_hook)
		prev_process_utility_hook(pstmt, queryString, readOnlyTree, context,
								  params, queryEnv, dest, qc);
	else
		standard_ProcessUtility(pstmt, queryString, readOnlyTree, context,
								params, queryEnv, dest, qc);
}

void
temporal_hooks_init(void)
{
	prev_planner_hook = planner_hook;
	planner_hook = temporal_planner;

	prev_process_utility_hook = ProcessUtility_hook;
	ProcessUtility_hook = temporal_process_utility;
}
