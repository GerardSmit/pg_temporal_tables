/*
 * temporal_trigger.c
 *
 * Row triggers implementing the versioning write path, plus the TRUNCATE
 * blocker and the history-table protect trigger.
 *
 * Semantics (SQL Server parity, see README):
 *   - All period timestamps are transaction_timestamp().
 *   - INSERT stamps valid_from/changed_by; no history row.
 *   - UPDATE copies OLD into history with valid_to = tx_ts, unless the
 *     change combines with the previous one (same transaction, or same
 *     non-NULL user within the table's combine interval) — then the
 *     existing history row is extended instead, or, when the previous
 *     change was the INSERT itself, the update melts into it entirely.
 *   - DELETE copies OLD into history with valid_to = tx_ts and stamps
 *     deleted_by; rows born in the same transaction vanish without trace.
 *   - Updates touching only excluded columns are invisible to history.
 */
#include "postgres.h"

#include "access/genam.h"
#include "access/htup_details.h"
#include "access/skey.h"
#include "access/table.h"
#include "catalog/indexing.h"
#include "catalog/namespace.h"
#include "catalog/partition.h"
#include "catalog/pg_class.h"
#include "catalog/pg_inherits.h"
#include "catalog/pg_type.h"
#include "commands/trigger.h"
#include "executor/spi.h"
#include "fmgr.h"
#include "lib/stringinfo.h"
#include "utils/builtins.h"
#include "utils/datum.h"
#include "utils/elog.h"
#include "utils/fmgroids.h"
#include "utils/lsyscache.h"
#include "utils/rel.h"
#include "utils/timestamp.h"

#include "temporal_catalog.h"
#include "temporal_hooks.h"

PG_FUNCTION_INFO_V1(temporal_row_stamp);
PG_FUNCTION_INFO_V1(temporal_write_history);
PG_FUNCTION_INFO_V1(temporal_block_truncate);
PG_FUNCTION_INFO_V1(temporal_protect_history);

/*
 * Current temporal.user_id, or NULL when unset/empty.
 */
static const char *
current_user_id(void)
{
	if (temporal_user_guc == NULL || temporal_user_guc[0] == '\0')
		return NULL;
	return temporal_user_guc;
}

static void
check_require_user(Relation rel)
{
	if (temporal_require_user_guc && current_user_id() == NULL)
		ereport(ERROR,
				(errcode(ERRCODE_INSUFFICIENT_PRIVILEGE),
				 errmsg("temporal.user_id is not set"),
				 errdetail("temporal.require_user is on, so writes to tracked table \"%s\" must be attributed.",
						   RelationGetRelationName(rel)),
				 errhint("SET temporal.user_id = '...' before modifying tracked tables.")));
}

static bool
relation_has_inheritance(Oid relid)
{
	Relation	inhrel;
	SysScanDesc scan;
	ScanKeyData key;
	HeapTuple	tup;
	bool		found;

	inhrel = table_open(InheritsRelationId, AccessShareLock);

	ScanKeyInit(&key,
				Anum_pg_inherits_inhparent,
				BTEqualStrategyNumber, F_OIDEQ,
				ObjectIdGetDatum(relid));
	scan = systable_beginscan(inhrel, InheritsParentIndexId, true,
							  NULL, 1, &key);
	tup = systable_getnext(scan);
	found = HeapTupleIsValid(tup);
	systable_endscan(scan);

	if (!found)
	{
		ScanKeyInit(&key,
					Anum_pg_inherits_inhrelid,
					BTEqualStrategyNumber, F_OIDEQ,
					ObjectIdGetDatum(relid));
		scan = systable_beginscan(inhrel, InheritsRelidSeqnoIndexId, true,
								  NULL, 1, &key);
		tup = systable_getnext(scan);
		found = HeapTupleIsValid(tup);
		systable_endscan(scan);
	}

	table_close(inhrel, AccessShareLock);
	return found;
}

static bool
relation_is_timescale_hypertable(Relation rel)
{
	Oid			ts_nspoid;
	Oid			hypertable_relid;
	Relation	hypertable_rel;
	TupleDesc	hypertable_desc;
	AttrNumber	schema_attnum;
	AttrNumber	table_attnum;
	SysScanDesc scan;
	HeapTuple	tup;
	char	   *schema_name;
	const char *table_name;
	bool		found = false;

	ts_nspoid = get_namespace_oid("_timescaledb_catalog", true);
	if (!OidIsValid(ts_nspoid))
		return false;

	hypertable_relid = get_relname_relid("hypertable", ts_nspoid);
	if (!OidIsValid(hypertable_relid))
		return false;

	schema_attnum = get_attnum(hypertable_relid, "schema_name");
	table_attnum = get_attnum(hypertable_relid, "table_name");
	if (schema_attnum == InvalidAttrNumber ||
		table_attnum == InvalidAttrNumber)
		return false;

	schema_name = get_namespace_name(RelationGetNamespace(rel));
	if (schema_name == NULL)
		return false;
	table_name = RelationGetRelationName(rel);

	hypertable_rel = table_open(hypertable_relid, AccessShareLock);
	hypertable_desc = RelationGetDescr(hypertable_rel);
	scan = systable_beginscan(hypertable_rel, InvalidOid, false,
							  NULL, 0, NULL);

	while ((tup = systable_getnext(scan)) != NULL)
	{
		Datum		schema_datum;
		Datum		table_datum;
		bool		schema_isnull;
		bool		table_isnull;
		Name		hypertable_schema;
		Name		hypertable_table;

		schema_datum = heap_getattr(tup, schema_attnum, hypertable_desc,
									&schema_isnull);
		table_datum = heap_getattr(tup, table_attnum, hypertable_desc,
								   &table_isnull);
		if (schema_isnull || table_isnull)
			continue;

		hypertable_schema = DatumGetName(schema_datum);
		hypertable_table = DatumGetName(table_datum);
		if (strcmp(NameStr(*hypertable_schema), schema_name) == 0 &&
			strcmp(NameStr(*hypertable_table), table_name) == 0)
		{
			found = true;
			break;
		}
	}

	systable_endscan(scan);
	table_close(hypertable_rel, AccessShareLock);
	pfree(schema_name);

	return found;
}

static void
check_supported_table_shape(Relation rel)
{
	/*
	 * Declarative partitioning is supported: the trigger fires on a leaf
	 * partition (relispartition) and the metadata lives on the partitioned
	 * root (RELKIND_PARTITIONED_TABLE). Legacy table inheritance and
	 * TimescaleDB hypertables are not.
	 */
	if (rel->rd_rel->relkind == RELKIND_PARTITIONED_TABLE ||
		rel->rd_rel->relispartition)
		return;

	if (relation_has_inheritance(RelationGetRelid(rel)) ||
		relation_is_timescale_hypertable(rel))
		ereport(ERROR,
				(errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
				 errmsg("cannot write temporal table \"%s\" because it has a legacy inheritance or TimescaleDB hypertable relationship",
						RelationGetRelationName(rel)),
				 errhint("Legacy table inheritance and TimescaleDB hypertables are not supported by pg_temporal_tables (declarative partitioning is).")));
}

/*
 * Look up the tracked-table entry for a relation a row trigger fired on.
 * Triggers on a partitioned table fire on the leaf partition, but the metadata
 * is keyed by the partitioned root, so on a miss we walk up the partition
 * ancestry to find the tracked root.
 */
static TemporalTableEntry *
lookup_with_partition_root(Relation rel)
{
	TemporalTableEntry *entry = temporal_lookup(RelationGetRelid(rel));

	if (entry == NULL && rel->rd_rel->relispartition)
	{
		List	   *ancestors = get_partition_ancestors(RelationGetRelid(rel));
		ListCell   *lc;

		foreach(lc, ancestors)
		{
			entry = temporal_lookup(lfirst_oid(lc));
			if (entry != NULL)
				break;
		}
		list_free(ancestors);
	}
	return entry;
}

static TimestampTz
old_valid_from(TemporalTableEntry *entry, HeapTuple tup, TupleDesc tupdesc)
{
	Datum		d;
	bool		isnull;

	d = heap_getattr(tup, entry->valid_from_attnum, tupdesc, &isnull);
	if (isnull)
		ereport(ERROR,
				(errcode(ERRCODE_DATA_CORRUPTED),
				 errmsg("valid_from is NULL on a tracked row")));
	return DatumGetTimestampTz(d);
}

/* OLD.changed_by as a C string, or NULL. Caller's memory context. */
static char *
old_changed_by(TemporalTableEntry *entry, HeapTuple tup, TupleDesc tupdesc)
{
	Datum		d;
	bool		isnull;

	d = heap_getattr(tup, entry->changed_by_attnum, tupdesc, &isnull);
	if (isnull)
		return NULL;
	return TextDatumGetCString(d);
}

/*
 * True when no column outside the excluded/managed set differs between the
 * two tuples — i.e. the update is invisible to history.
 */
static bool
only_excluded_columns_changed(Relation rel, HeapTuple old_tup, HeapTuple new_tup,
							  TemporalTableEntry *entry)
{
	TupleDesc	tupdesc = RelationGetDescr(rel);
	int			i;

	for (i = 0; i < tupdesc->natts; i++)
	{
		Form_pg_attribute att = TupleDescAttr(tupdesc, i);
		AttrNumber	attnum = att->attnum;
		Datum		d_old,
					d_new;
		bool		null_old,
					null_new;

		if (att->attisdropped)
			continue;
		if (attnum == entry->valid_from_attnum ||
			attnum == entry->changed_by_attnum)
			continue;
		if (bms_is_member(attnum, entry->excluded_attnums))
			continue;

		d_old = heap_getattr(old_tup, attnum, tupdesc, &null_old);
		d_new = heap_getattr(new_tup, attnum, tupdesc, &null_new);

		if (null_old != null_new)
			return false;
		if (!null_old &&
			!datum_image_eq(d_old, d_new, att->attbyval, att->attlen))
			return false;
	}
	return true;
}

/*
 * Combine eligibility: same transaction always combines; otherwise the
 * previous change must carry the same non-NULL user id and lie within the
 * table's combine interval. NULL is never "the same user".
 */
static bool
combine_eligible(TemporalTableEntry *entry, TimestampTz tx_ts,
				 TimestampTz old_vf, const char *old_user)
{
	const char *cur_user = current_user_id();

	if (old_vf == tx_ts)
		return true;			/* row version born in this transaction */

	if (entry->combine_interval_us <= 0)
		return false;
	if (old_user == NULL || cur_user == NULL)
		return false;
	if (strcmp(old_user, cur_user) != 0)
		return false;

	return (tx_ts - old_vf) <= entry->combine_interval_us;
}

/* ─────────────────────────────────────────────────────────────────────────
 * SPI helpers against the history table. Plans are cached per tracked table
 * inside the metadata cache entry and freed on invalidation.
 * ───────────────────────────────────────────────────────────────────────── */

/* Append "pk1 = $1 AND pk2 = $2 ..." and fill argtypes from the base tupdesc. */
static void
append_pk_clause(StringInfo buf, TemporalTableEntry *entry, TupleDesc tupdesc,
				 Oid *argtypes)
{
	int			i;

	for (i = 0; i < entry->n_pk; i++)
	{
		Form_pg_attribute att = TupleDescAttr(tupdesc, entry->pk_attnums[i] - 1);

		if (i > 0)
			appendStringInfoString(buf, " AND ");
		appendStringInfo(buf, "%s = $%d", quote_identifier(NameStr(att->attname)), i + 1);
		argtypes[i] = att->atttypid;
	}
}

static void
fill_pk_args(TemporalTableEntry *entry, HeapTuple tup, TupleDesc tupdesc,
			 Datum *values, char *nulls)
{
	int			i;

	for (i = 0; i < entry->n_pk; i++)
	{
		bool		isnull;

		values[i] = heap_getattr(tup, entry->pk_attnums[i], tupdesc, &isnull);
		nulls[i] = isnull ? 'n' : ' ';
	}
}

static char *
history_qualname(TemporalTableEntry *entry)
{
	return (char *) quote_qualified_identifier(
		get_namespace_name(get_rel_namespace(entry->history_relid)),
		get_rel_name(entry->history_relid));
}

/*
 * Does a history row chain-linked to OLD (valid_to == OLD.valid_from) exist?
 */
static bool
history_chain_row_exists(TemporalTableEntry *entry, Relation rel,
						 HeapTuple old_tup, TimestampTz old_vf)
{
	TupleDesc	tupdesc = RelationGetDescr(rel);
	int			nargs = entry->n_pk + 1;
	Datum	   *values = palloc(nargs * sizeof(Datum));
	char	   *nulls = palloc(nargs + 1);
	bool		found;
	int			rc;

	if (entry->plan_check == NULL)
	{
		StringInfoData sql;
		Oid		   *argtypes = palloc(nargs * sizeof(Oid));
		SPIPlanPtr	plan;

		initStringInfo(&sql);
		appendStringInfo(&sql, "SELECT 1 FROM %s WHERE ", history_qualname(entry));
		append_pk_clause(&sql, entry, tupdesc, argtypes);
		appendStringInfo(&sql, " AND valid_to = $%d", nargs);
		argtypes[nargs - 1] = TIMESTAMPTZOID;

		plan = SPI_prepare(sql.data, nargs, argtypes);
		if (plan == NULL)
			elog(ERROR, "SPI_prepare failed: %s", SPI_result_code_string(SPI_result));
		SPI_keepplan(plan);
		entry->plan_check = plan;
	}

	fill_pk_args(entry, old_tup, tupdesc, values, nulls);
	values[nargs - 1] = TimestampTzGetDatum(old_vf);
	nulls[nargs - 1] = ' ';
	nulls[nargs] = '\0';

	rc = SPI_execute_plan(entry->plan_check, values, nulls, true, 1);
	if (rc != SPI_OK_SELECT)
		elog(ERROR, "history lookup failed: %s", SPI_result_code_string(rc));
	found = SPI_processed > 0;

	pfree(values);
	pfree(nulls);
	return found;
}

/*
 * Extend the chain-linked history row's validity to tx_ts (combine).
 * Zero rows affected is fine — that is the insert-collapse case.
 */
static void
history_extend(TemporalTableEntry *entry, Relation rel, HeapTuple old_tup,
			   TimestampTz old_vf, TimestampTz tx_ts)
{
	TupleDesc	tupdesc = RelationGetDescr(rel);
	int			nargs = entry->n_pk + 2;
	Datum	   *values = palloc(nargs * sizeof(Datum));
	char	   *nulls = palloc(nargs + 1);
	bool		saved;
	int			rc;

	if (entry->plan_extend == NULL)
	{
		StringInfoData sql;
		Oid		   *argtypes = palloc(nargs * sizeof(Oid));
		SPIPlanPtr	plan;

		initStringInfo(&sql);
		appendStringInfo(&sql, "UPDATE %s SET valid_to = $%d WHERE ",
						 history_qualname(entry), nargs);
		append_pk_clause(&sql, entry, tupdesc, argtypes);
		appendStringInfo(&sql, " AND valid_to = $%d", nargs - 1);
		argtypes[nargs - 2] = TIMESTAMPTZOID;	/* old valid_from */
		argtypes[nargs - 1] = TIMESTAMPTZOID;	/* new valid_to */

		plan = SPI_prepare(sql.data, nargs, argtypes);
		if (plan == NULL)
			elog(ERROR, "SPI_prepare failed: %s", SPI_result_code_string(SPI_result));
		SPI_keepplan(plan);
		entry->plan_extend = plan;
	}

	fill_pk_args(entry, old_tup, tupdesc, values, nulls);
	values[nargs - 2] = TimestampTzGetDatum(old_vf);
	nulls[nargs - 2] = ' ';
	values[nargs - 1] = TimestampTzGetDatum(tx_ts);
	nulls[nargs - 1] = ' ';
	nulls[nargs] = '\0';

	saved = temporal_in_internal_write;
	temporal_in_internal_write = true;
	PG_TRY();
	{
		rc = SPI_execute_plan(entry->plan_extend, values, nulls, false, 0);
	}
	PG_FINALLY();
	{
		temporal_in_internal_write = saved;
	}
	PG_END_TRY();

	if (rc != SPI_OK_UPDATE)
		elog(ERROR, "history extend failed: %s", SPI_result_code_string(rc));

	pfree(values);
	pfree(nulls);
}

/*
 * Insert OLD into the history table with the given valid_to / deleted_by.
 * Columns are mapped by name (never positionally): the history table's
 * column order can drift from the base table's after ADD COLUMN propagation.
 */
static void
history_insert(TemporalTableEntry *entry, Relation rel, HeapTuple old_tup,
			   TimestampTz tx_ts, const char *deleted_by)
{
	TupleDesc	tupdesc = RelationGetDescr(rel);
	int			ncols = 0;
	int			nargs;
	Datum	   *values;
	char	   *nulls;
	int			i;
	int			argno;
	bool		saved;
	int			rc;

	for (i = 0; i < tupdesc->natts; i++)
	{
		if (!TupleDescAttr(tupdesc, i)->attisdropped)
			ncols++;
	}
	nargs = ncols + 2;			/* + valid_to, deleted_by */

	if (entry->plan_insert == NULL)
	{
		StringInfoData sql;
		StringInfoData params;
		Oid		   *argtypes = palloc(nargs * sizeof(Oid));
		SPIPlanPtr	plan;

		initStringInfo(&sql);
		initStringInfo(&params);
		appendStringInfo(&sql, "INSERT INTO %s (", history_qualname(entry));

		argno = 0;
		for (i = 0; i < tupdesc->natts; i++)
		{
			Form_pg_attribute att = TupleDescAttr(tupdesc, i);

			if (att->attisdropped)
				continue;
			if (argno > 0)
			{
				appendStringInfoString(&sql, ", ");
				appendStringInfoString(&params, ", ");
			}
			appendStringInfoString(&sql, quote_identifier(NameStr(att->attname)));
			appendStringInfo(&params, "$%d", argno + 1);
			argtypes[argno] = att->atttypid;
			argno++;
		}
		appendStringInfo(&sql, ", valid_to, deleted_by) VALUES (%s, $%d, $%d)",
						 params.data, ncols + 1, ncols + 2);
		argtypes[ncols] = TIMESTAMPTZOID;
		argtypes[ncols + 1] = TEXTOID;

		plan = SPI_prepare(sql.data, nargs, argtypes);
		if (plan == NULL)
			elog(ERROR, "SPI_prepare failed: %s", SPI_result_code_string(SPI_result));
		SPI_keepplan(plan);
		entry->plan_insert = plan;
	}

	values = palloc(nargs * sizeof(Datum));
	nulls = palloc(nargs + 1);

	argno = 0;
	for (i = 0; i < tupdesc->natts; i++)
	{
		Form_pg_attribute att = TupleDescAttr(tupdesc, i);
		bool		isnull;

		if (att->attisdropped)
			continue;
		values[argno] = heap_getattr(old_tup, att->attnum, tupdesc, &isnull);
		nulls[argno] = isnull ? 'n' : ' ';
		argno++;
	}
	values[ncols] = TimestampTzGetDatum(tx_ts);
	nulls[ncols] = ' ';
	if (deleted_by)
	{
		values[ncols + 1] = CStringGetTextDatum(deleted_by);
		nulls[ncols + 1] = ' ';
	}
	else
	{
		values[ncols + 1] = (Datum) 0;
		nulls[ncols + 1] = 'n';
	}
	nulls[nargs] = '\0';

	saved = temporal_in_internal_write;
	temporal_in_internal_write = true;
	PG_TRY();
	{
		rc = SPI_execute_plan(entry->plan_insert, values, nulls, false, 0);
	}
	PG_FINALLY();
	{
		temporal_in_internal_write = saved;
	}
	PG_END_TRY();

	if (rc != SPI_OK_INSERT)
		elog(ERROR, "history insert failed: %s", SPI_result_code_string(rc));

	pfree(values);
	pfree(nulls);
}

/* SQL Server raises error 13535 in the equivalent situation. */
static void
check_transaction_not_older_than_row(TimestampTz old_vf, TimestampTz tx_ts,
									 Relation rel)
{
	if (old_vf > tx_ts)
		ereport(ERROR,
				(errcode(ERRCODE_T_R_SERIALIZATION_FAILURE),
				 errmsg("data modification failed on temporal table \"%s\" because the transaction start time is earlier than the row's valid_from",
						RelationGetRelationName(rel)),
				 errhint("A concurrent transaction modified this row after this transaction started. Retry the transaction.")));
}

/* ─────────────────────────────────────────────────────────────────────────
 * BEFORE INSERT OR UPDATE: stamp valid_from / changed_by and decide the
 * combine outcome for updates.
 * ───────────────────────────────────────────────────────────────────────── */
Datum
temporal_row_stamp(PG_FUNCTION_ARGS)
{
	TriggerData *trigdata = (TriggerData *) fcinfo->context;
	Relation	rel;
	TupleDesc	tupdesc;
	TemporalTableEntry *entry;
	HeapTuple	new_tup;
	TimestampTz tx_ts;
	TimestampTz new_vf;
	const char *cur_user;
	Datum		values[2];
	bool		nulls[2];
	int			columns[2];

	if (!CALLED_AS_TRIGGER(fcinfo))
		elog(ERROR, "temporal_row_stamp: not called as trigger");
	if (!TRIGGER_FIRED_BEFORE(trigdata->tg_event) ||
		!TRIGGER_FIRED_FOR_ROW(trigdata->tg_event))
		elog(ERROR, "temporal_row_stamp: must be a BEFORE ROW trigger");

	rel = trigdata->tg_relation;
	tupdesc = RelationGetDescr(rel);

	entry = lookup_with_partition_root(rel);
	if (entry == NULL)
		return PointerGetDatum(trigdata->tg_trigtuple);

	check_supported_table_shape(rel);
	check_require_user(rel);

	tx_ts = GetCurrentTransactionStartTimestamp();
	cur_user = current_user_id();
	new_vf = tx_ts;

	if (TRIGGER_FIRED_BY_UPDATE(trigdata->tg_event))
	{
		HeapTuple	old_tup = trigdata->tg_trigtuple;
		TimestampTz old_vf = old_valid_from(entry, old_tup, tupdesc);
		char	   *old_user;

		new_tup = trigdata->tg_newtuple;

		check_transaction_not_older_than_row(old_vf, tx_ts, rel);

		/* updates invisible to history keep the old stamps */
		if (only_excluded_columns_changed(rel, old_tup, new_tup, entry))
		{
			Datum		d;
			bool		isnull;

			columns[0] = entry->valid_from_attnum;
			values[0] = TimestampTzGetDatum(old_vf);
			nulls[0] = false;

			d = heap_getattr(old_tup, entry->changed_by_attnum, tupdesc, &isnull);
			columns[1] = entry->changed_by_attnum;
			values[1] = d;
			nulls[1] = isnull;

			new_tup = heap_modify_tuple_by_cols(new_tup, tupdesc, 2,
												columns, values, nulls);
			return PointerGetDatum(new_tup);
		}

		old_user = old_changed_by(entry, old_tup, tupdesc);
		if (combine_eligible(entry, tx_ts, old_vf, old_user))
		{
			bool		chain_exists;

			if (SPI_connect() != SPI_OK_CONNECT)
				elog(ERROR, "SPI_connect failed");
			chain_exists = history_chain_row_exists(entry, rel, old_tup, old_vf);
			SPI_finish();

			/*
			 * No chain row means the previous change was the INSERT (or the
			 * start of a collapsed burst): this update melts into it and the
			 * row keeps its original valid_from.
			 */
			if (!chain_exists)
				new_vf = old_vf;
		}
		if (old_user)
			pfree(old_user);
	}
	else
		new_tup = trigdata->tg_trigtuple;	/* INSERT */

	columns[0] = entry->valid_from_attnum;
	values[0] = TimestampTzGetDatum(new_vf);
	nulls[0] = false;

	columns[1] = entry->changed_by_attnum;
	if (cur_user)
	{
		values[1] = CStringGetTextDatum(cur_user);
		nulls[1] = false;
	}
	else
	{
		values[1] = (Datum) 0;
		nulls[1] = true;
	}

	new_tup = heap_modify_tuple_by_cols(new_tup, tupdesc, 2,
										columns, values, nulls);
	return PointerGetDatum(new_tup);
}

/* ─────────────────────────────────────────────────────────────────────────
 * AFTER UPDATE OR DELETE: write (or extend) the history row.
 * ───────────────────────────────────────────────────────────────────────── */
Datum
temporal_write_history(PG_FUNCTION_ARGS)
{
	TriggerData *trigdata = (TriggerData *) fcinfo->context;
	Relation	rel;
	TupleDesc	tupdesc;
	TemporalTableEntry *entry;
	HeapTuple	old_tup;
	TimestampTz tx_ts;
	TimestampTz old_vf;

	if (!CALLED_AS_TRIGGER(fcinfo))
		elog(ERROR, "temporal_write_history: not called as trigger");
	if (!TRIGGER_FIRED_AFTER(trigdata->tg_event) ||
		!TRIGGER_FIRED_FOR_ROW(trigdata->tg_event))
		elog(ERROR, "temporal_write_history: must be an AFTER ROW trigger");

	rel = trigdata->tg_relation;
	tupdesc = RelationGetDescr(rel);
	old_tup = trigdata->tg_trigtuple;

	entry = lookup_with_partition_root(rel);
	if (entry == NULL)
		return PointerGetDatum(NULL);

	check_supported_table_shape(rel);

	tx_ts = GetCurrentTransactionStartTimestamp();
	old_vf = old_valid_from(entry, old_tup, tupdesc);

	if (TRIGGER_FIRED_BY_UPDATE(trigdata->tg_event))
	{
		HeapTuple	new_tup = trigdata->tg_newtuple;
		char	   *old_user;

		if (only_excluded_columns_changed(rel, old_tup, new_tup, entry))
			return PointerGetDatum(NULL);

		old_user = old_changed_by(entry, old_tup, tupdesc);

		if (SPI_connect() != SPI_OK_CONNECT)
			elog(ERROR, "SPI_connect failed");

		if (combine_eligible(entry, tx_ts, old_vf, old_user))
		{
			/* extend the chain row; zero rows = insert-collapse, no-op */
			history_extend(entry, rel, old_tup, old_vf, tx_ts);
		}
		else
			history_insert(entry, rel, old_tup, tx_ts, NULL);

		SPI_finish();
		if (old_user)
			pfree(old_user);
	}
	else if (TRIGGER_FIRED_BY_DELETE(trigdata->tg_event))
	{
		check_require_user(rel);
		check_transaction_not_older_than_row(old_vf, tx_ts, rel);

		/* a row born and deleted in the same transaction leaves no trace */
		if (old_vf == tx_ts)
			return PointerGetDatum(NULL);

		if (SPI_connect() != SPI_OK_CONNECT)
			elog(ERROR, "SPI_connect failed");
		history_insert(entry, rel, old_tup, tx_ts, current_user_id());
		SPI_finish();
	}

	return PointerGetDatum(NULL);
}

Datum
temporal_block_truncate(PG_FUNCTION_ARGS)
{
	TriggerData *trigdata = (TriggerData *) fcinfo->context;

	if (!CALLED_AS_TRIGGER(fcinfo))
		elog(ERROR, "temporal_block_truncate: not called as trigger");

	ereport(ERROR,
			(errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
			 errmsg("TRUNCATE is not allowed on temporal table \"%s\"",
					RelationGetRelationName(trigdata->tg_relation)),
			 errhint("Use DELETE to remove rows (history is preserved), or "
					 "temporal.disable() first to stop tracking.")));
	PG_RETURN_NULL();
}

Datum
temporal_protect_history(PG_FUNCTION_ARGS)
{
	TriggerData *trigdata = (TriggerData *) fcinfo->context;

	if (!CALLED_AS_TRIGGER(fcinfo))
		elog(ERROR, "temporal_protect_history: not called as trigger");

	if (temporal_in_internal_write)
	{
		/* our own write path — let it through unchanged */
		if (TRIGGER_FIRED_BY_UPDATE(trigdata->tg_event))
			return PointerGetDatum(trigdata->tg_newtuple);
		return PointerGetDatum(trigdata->tg_trigtuple);
	}

	ereport(ERROR,
			(errcode(ERRCODE_INSUFFICIENT_PRIVILEGE),
			 errmsg("direct modification of temporal history table \"%s\" is not allowed",
					RelationGetRelationName(trigdata->tg_relation)),
			 errhint("History is maintained automatically. Use temporal.prune() "
					 "for retention cleanup or temporal.disable() to stop tracking.")));
	PG_RETURN_NULL();
}
