/*
 * temporal_catalog.c
 *
 * Per-backend metadata cache for tracked tables, populated from
 * temporal.tracked_tables via direct catalog scans (no SPI, so the lookup is
 * safe from planner hooks as well as triggers). Entries — including negative
 * ones — are invalidated through the relcache callback; temporal.enable()
 * and friends broadcast a relcache invalidation for the affected table.
 */
#include "postgres.h"

#include "access/heapam.h"
#include "access/htup_details.h"
#include "access/relation.h"
#include "access/table.h"
#include "access/tableam.h"
#include "catalog/indexing.h"
#include "catalog/namespace.h"
#include "catalog/pg_index.h"
#include "catalog/pg_type.h"
#include "executor/spi.h"
#include "fmgr.h"
#include "lib/stringinfo.h"
#include "utils/array.h"
#include "utils/builtins.h"
#include "utils/catcache.h"
#include "utils/fmgroids.h"
#include "utils/hsearch.h"
#include "utils/inval.h"
#include "utils/lsyscache.h"
#include "utils/memutils.h"
#include "utils/rel.h"
#include "utils/snapmgr.h"
#include "utils/typcache.h"
#include "utils/syscache.h"
#include "utils/timestamp.h"

#include "temporal_catalog.h"

bool		temporal_in_internal_write = false;

static HTAB *temporal_cache = NULL;
static MemoryContext temporal_cache_cxt = NULL;

/* Attribute numbers in temporal.tracked_tables — keep in sync with the SQL script. */
#define Anum_tracked_table_oid			1
#define Anum_tracked_history_table_oid	2
#define Anum_tracked_versions_view_oid	3
#define Anum_tracked_excluded_columns	4
#define Anum_tracked_combine_interval	5

/*
 * SPI plans evicted from the cache. Freeing a cached plan can take locks, so
 * it must not happen inside the invalidation callback; we park plans here and
 * release them at the next temporal_lookup() in a normal execution context.
 */
#define MAX_PENDING_PLANS 64
static SPIPlanPtr pending_free_plans[MAX_PENDING_PLANS];
static int	n_pending_free_plans = 0;

static void
park_plan(SPIPlanPtr plan)
{
	if (plan == NULL)
		return;
	if (n_pending_free_plans < MAX_PENDING_PLANS)
		pending_free_plans[n_pending_free_plans++] = plan;
	/* overflow: leak the plan; bounded by churn between lookups */
}

static void
free_parked_plans(void)
{
	while (n_pending_free_plans > 0)
		SPI_freeplan(pending_free_plans[--n_pending_free_plans]);
}

static void
temporal_cache_remove(Oid relid)
{
	TemporalTableEntry *entry;

	entry = hash_search(temporal_cache, &relid, HASH_FIND, NULL);
	if (entry)
	{
		bms_free(entry->excluded_attnums);
		park_plan(entry->plan_check);
		park_plan(entry->plan_extend);
		park_plan(entry->plan_insert);
		hash_search(temporal_cache, &relid, HASH_REMOVE, NULL);
	}
}

static void
temporal_relcache_callback(Datum arg, Oid relid)
{
	if (temporal_cache == NULL)
		return;

	if (OidIsValid(relid))
		temporal_cache_remove(relid);
	else
	{
		HASH_SEQ_STATUS status;
		TemporalTableEntry *entry;

		hash_seq_init(&status, temporal_cache);
		while ((entry = hash_seq_search(&status)) != NULL)
		{
			bms_free(entry->excluded_attnums);
			park_plan(entry->plan_check);
			park_plan(entry->plan_extend);
			park_plan(entry->plan_insert);
			hash_search(temporal_cache, &entry->base_relid, HASH_REMOVE, NULL);
		}
	}
}

void
temporal_catalog_init(void)
{
	HASHCTL		ctl;

	temporal_cache_cxt = AllocSetContextCreate(TopMemoryContext,
											   "pg_temporal_tables metadata cache",
											   ALLOCSET_SMALL_SIZES);

	ctl.keysize = sizeof(Oid);
	ctl.entrysize = sizeof(TemporalTableEntry);
	ctl.hcxt = temporal_cache_cxt;
	temporal_cache = hash_create("pg_temporal_tables tracked tables",
								 32, &ctl,
								 HASH_ELEM | HASH_BLOBS | HASH_CONTEXT);

	CacheRegisterRelcacheCallback(temporal_relcache_callback, (Datum) 0);
}

/*
 * Resolve the OID of temporal.tracked_tables, or InvalidOid when the
 * extension's objects don't exist in this database.
 */
static Oid
tracked_tables_relid(void)
{
	Oid			nspoid;

	nspoid = get_namespace_oid("temporal", true);
	if (!OidIsValid(nspoid))
		return InvalidOid;

	return get_relname_relid("tracked_tables", nspoid);
}

/* Fill entry->pk_attnums from the relation's primary key index. */
static bool
load_pk_attnums(Oid relid, TemporalTableEntry *entry)
{
	Relation	rel;
	Oid			pkoid;
	HeapTuple	indtup;
	Form_pg_index indform;
	int			i;

	rel = relation_open(relid, AccessShareLock);
	pkoid = RelationGetPrimaryKeyIndex(rel, false);
	relation_close(rel, AccessShareLock);

	if (!OidIsValid(pkoid))
		return false;

	indtup = SearchSysCache1(INDEXRELID, ObjectIdGetDatum(pkoid));
	if (!HeapTupleIsValid(indtup))
		return false;

	indform = (Form_pg_index) GETSTRUCT(indtup);
	entry->n_pk = indform->indnkeyatts;
	for (i = 0; i < entry->n_pk; i++)
		entry->pk_attnums[i] = indform->indkey.values[i];

	ReleaseSysCache(indtup);
	return entry->n_pk > 0;
}

/*
 * Populate a cache entry from the temporal.tracked_tables row for relid.
 * Returns false when the relation is not tracked (or metadata is broken).
 */
static bool
load_entry(Oid relid, TemporalTableEntry *entry)
{
	Oid			catalog_oid;
	Relation	catalog;
	TableScanDesc scan;
	ScanKeyData key;
	HeapTuple	tup;
	bool		found = false;

	catalog_oid = tracked_tables_relid();
	if (!OidIsValid(catalog_oid))
		return false;

	catalog = table_open(catalog_oid, AccessShareLock);

	ScanKeyInit(&key,
				Anum_tracked_table_oid,
				BTEqualStrategyNumber, F_OIDEQ,
				ObjectIdGetDatum(relid));

	scan = table_beginscan(catalog, GetCatalogSnapshot(catalog_oid), 1, &key);
	tup = heap_getnext(scan, ForwardScanDirection);
	if (HeapTupleIsValid(tup))
	{
		TupleDesc	tupdesc = RelationGetDescr(catalog);
		Datum		datum;
		bool		isnull;

		found = true;

		datum = heap_getattr(tup, Anum_tracked_history_table_oid, tupdesc, &isnull);
		entry->history_relid = isnull ? InvalidOid : DatumGetObjectId(datum);

		datum = heap_getattr(tup, Anum_tracked_versions_view_oid, tupdesc, &isnull);
		entry->versions_view_relid = isnull ? InvalidOid : DatumGetObjectId(datum);

		entry->excluded_attnums = NULL;
		datum = heap_getattr(tup, Anum_tracked_excluded_columns, tupdesc, &isnull);
		if (!isnull)
		{
			ArrayType  *arr = DatumGetArrayTypeP(datum);
			Datum	   *elems;
			int			nelems;
			int			i;
			MemoryContext oldcxt;

			deconstruct_array(arr, NAMEOID, NAMEDATALEN, false, TYPALIGN_CHAR,
							  &elems, NULL, &nelems);

			oldcxt = MemoryContextSwitchTo(temporal_cache_cxt);
			for (i = 0; i < nelems; i++)
			{
				AttrNumber	attnum = get_attnum(relid, NameStr(*DatumGetName(elems[i])));

				if (attnum != InvalidAttrNumber)
					entry->excluded_attnums =
						bms_add_member(entry->excluded_attnums, attnum);
			}
			MemoryContextSwitchTo(oldcxt);
		}

		entry->combine_interval_us = 0;
		datum = heap_getattr(tup, Anum_tracked_combine_interval, tupdesc, &isnull);
		if (!isnull)
		{
			Interval   *iv = DatumGetIntervalP(datum);

			/* enable() rejects month-bearing intervals; clamp defensively */
			entry->combine_interval_us = iv->time
				+ (int64) iv->day * USECS_PER_DAY
				+ (int64) iv->month * (30 * USECS_PER_DAY);
		}
	}

	table_endscan(scan);
	table_close(catalog, AccessShareLock);

	if (!found)
		return false;

	entry->valid_from_attnum = get_attnum(relid, "valid_from");
	entry->changed_by_attnum = get_attnum(relid, "changed_by");
	if (entry->valid_from_attnum == InvalidAttrNumber ||
		entry->changed_by_attnum == InvalidAttrNumber)
		ereport(ERROR,
				(errcode(ERRCODE_DATA_CORRUPTED),
				 errmsg("tracked table %u is missing its managed temporal columns", relid)));

	if (!load_pk_attnums(relid, entry))
		ereport(ERROR,
				(errcode(ERRCODE_DATA_CORRUPTED),
				 errmsg("tracked table %u has no primary key", relid)));

	return true;
}

TemporalTableEntry *
temporal_lookup(Oid relid)
{
	TemporalTableEntry *entry;
	bool		found;

	if (temporal_cache == NULL)
		return NULL;

	free_parked_plans();

	entry = hash_search(temporal_cache, &relid, HASH_FIND, &found);
	if (found)
		return entry->is_tracked ? entry : NULL;

	/*
	 * Miss: load from catalog into a local struct first, so a partially
	 * filled entry never becomes visible if load_entry() errors out.
	 */
	{
		TemporalTableEntry local;
		bool		tracked;

		memset(&local, 0, sizeof(local));
		local.base_relid = relid;
		tracked = load_entry(relid, &local);
		local.is_tracked = tracked;

		entry = hash_search(temporal_cache, &relid, HASH_ENTER, NULL);
		memcpy(entry, &local, sizeof(local));
	}

	return entry->is_tracked ? entry : NULL;
}

/*
 * temporal.prune(p_table regclass, p_older_than timestamptz) — retention
 * cleanup. Deletes history rows whose validity ended before the cutoff.
 * Runs with the internal-write flag set so the protect trigger lets the
 * DELETE through; this is the only sanctioned way to remove history.
 */
PG_FUNCTION_INFO_V1(temporal_prune);
Datum
temporal_prune(PG_FUNCTION_ARGS)
{
	Oid			relid = PG_GETARG_OID(0);
	TimestampTz cutoff = PG_GETARG_TIMESTAMPTZ(1);
	TemporalTableEntry *entry;
	char	   *history_qualname;
	StringInfoData sql;
	uint64		deleted;
	bool		saved_flag;
	int			rc;
	Oid			argtypes[1] = {TIMESTAMPTZOID};
	Datum		values[1];

	entry = temporal_lookup(relid);
	if (entry == NULL)
		ereport(ERROR,
				(errcode(ERRCODE_UNDEFINED_OBJECT),
				 errmsg("relation \"%s\" is not a temporal-tracked table",
						get_rel_name(relid))));

	history_qualname = quote_qualified_identifier(
		get_namespace_name(get_rel_namespace(entry->history_relid)),
		get_rel_name(entry->history_relid));

	initStringInfo(&sql);
	appendStringInfo(&sql, "DELETE FROM %s WHERE valid_to < $1", history_qualname);

	values[0] = TimestampTzGetDatum(cutoff);

	if (SPI_connect() != SPI_OK_CONNECT)
		elog(ERROR, "SPI_connect failed");

	saved_flag = temporal_in_internal_write;
	temporal_in_internal_write = true;
	PG_TRY();
	{
		rc = SPI_execute_with_args(sql.data, 1, argtypes, values, NULL,
								   false, 0);
	}
	PG_FINALLY();
	{
		temporal_in_internal_write = saved_flag;
	}
	PG_END_TRY();

	if (rc != SPI_OK_DELETE)
		elog(ERROR, "history prune failed: %s", SPI_result_code_string(rc));
	deleted = SPI_processed;

	SPI_finish();

	PG_RETURN_INT64((int64) deleted);
}

/*
 * temporal.compact_history(p_table regclass) — merge adjacent history versions
 * that became identical (e.g. after a column was dropped) into a single row,
 * extending the survivor's valid_to over the merged span. The merge logic lives
 * in the SQL helper temporal._compact_history_impl(); we wrap it here only to
 * set the internal-write flag so the history protect trigger lets the
 * UPDATE/DELETE through — the same sanctioned bypass temporal.prune() uses.
 */
PG_FUNCTION_INFO_V1(temporal_compact_history);
Datum
temporal_compact_history(PG_FUNCTION_ARGS)
{
	Oid			relid = PG_GETARG_OID(0);
	TemporalTableEntry *entry;
	bool		saved_flag;
	int			rc;
	volatile int64 deleted = 0;	/* modified in PG_TRY, read after: avoid clobber */
	Oid			argtypes[1] = {REGCLASSOID};
	Datum		values[1];

	entry = temporal_lookup(relid);
	if (entry == NULL)
		ereport(ERROR,
				(errcode(ERRCODE_UNDEFINED_OBJECT),
				 errmsg("relation \"%s\" is not a temporal-tracked table",
						get_rel_name(relid))));

	values[0] = ObjectIdGetDatum(relid);

	if (SPI_connect() != SPI_OK_CONNECT)
		elog(ERROR, "SPI_connect failed");

	saved_flag = temporal_in_internal_write;
	temporal_in_internal_write = true;
	PG_TRY();
	{
		rc = SPI_execute_with_args("SELECT temporal._compact_history_impl($1)",
								   1, argtypes, values, NULL, false, 0);
		if (rc == SPI_OK_SELECT && SPI_processed == 1)
		{
			bool		isnull;
			Datum		d = SPI_getbinval(SPI_tuptable->vals[0],
										  SPI_tuptable->tupdesc, 1, &isnull);

			if (!isnull)
				deleted = DatumGetInt64(d);
		}
	}
	PG_FINALLY();
	{
		temporal_in_internal_write = saved_flag;
	}
	PG_END_TRY();

	SPI_finish();

	PG_RETURN_INT64(deleted);
}

/*
 * Shared implementation of temporal.mod_date()/mod_user(): extract a managed
 * attribute by name from any composite value (base-table, history-table or
 * versions-view row), with a type check so a stray same-named column of a
 * different type errors instead of returning garbage.
 */
static Datum
record_attribute_by_name(HeapTupleHeader rec, const char *attname,
						 Oid expected_type, bool *isnull)
{
	Oid			tupType = HeapTupleHeaderGetTypeId(rec);
	int32		tupTypmod = HeapTupleHeaderGetTypMod(rec);
	TupleDesc	tupdesc;
	HeapTupleData tuple;
	int			i;
	AttrNumber	attnum = InvalidAttrNumber;
	Datum		result = (Datum) 0;

	tupdesc = lookup_rowtype_tupdesc(tupType, tupTypmod);
	for (i = 0; i < tupdesc->natts; i++)
	{
		Form_pg_attribute att = TupleDescAttr(tupdesc, i);

		if (!att->attisdropped &&
			strcmp(NameStr(att->attname), attname) == 0)
		{
			if (att->atttypid != expected_type)
				ereport(ERROR,
						(errcode(ERRCODE_DATATYPE_MISMATCH),
						 errmsg("attribute \"%s\" is not of type %s",
								attname, format_type_be(expected_type))));
			attnum = att->attnum;
			break;
		}
	}
	if (attnum == InvalidAttrNumber)
		ereport(ERROR,
				(errcode(ERRCODE_UNDEFINED_COLUMN),
				 errmsg("record has no \"%s\" attribute", attname),
				 errhint("temporal.mod_date()/mod_user() take a row of a temporal-tracked table, its history table, or its versions view.")));

	tuple.t_len = HeapTupleHeaderGetDatumLength(rec);
	ItemPointerSetInvalid(&(tuple.t_self));
	tuple.t_tableOid = InvalidOid;
	tuple.t_data = rec;

	result = heap_getattr(&tuple, attnum, tupdesc, isnull);
	ReleaseTupleDesc(tupdesc);
	return result;
}

PG_FUNCTION_INFO_V1(temporal_mod_date);
Datum
temporal_mod_date(PG_FUNCTION_ARGS)
{
	HeapTupleHeader rec = PG_GETARG_HEAPTUPLEHEADER(0);
	bool		isnull;
	Datum		d;

	d = record_attribute_by_name(rec, "valid_from", TIMESTAMPTZOID, &isnull);
	if (isnull)
		PG_RETURN_NULL();
	PG_RETURN_DATUM(d);
}

PG_FUNCTION_INFO_V1(temporal_mod_user);
Datum
temporal_mod_user(PG_FUNCTION_ARGS)
{
	HeapTupleHeader rec = PG_GETARG_HEAPTUPLEHEADER(0);
	bool		isnull;
	Datum		d;

	d = record_attribute_by_name(rec, "changed_by", TEXTOID, &isnull);
	if (isnull)
		PG_RETURN_NULL();
	PG_RETURN_DATUM(PointerGetDatum(PG_DETOAST_DATUM_COPY(d)));
}

/*
 * temporal.invalidate(regclass) — broadcast a relcache invalidation so all
 * backends drop their cached metadata for this table. Called by the SQL-side
 * configuration functions after they change temporal.tracked_tables.
 */
PG_FUNCTION_INFO_V1(temporal_invalidate);
Datum
temporal_invalidate(PG_FUNCTION_ARGS)
{
	Oid			relid = PG_GETARG_OID(0);
	Relation	rel;

	rel = relation_open(relid, AccessShareLock);
	CacheInvalidateRelcache(rel);
	relation_close(rel, AccessShareLock);

	PG_RETURN_VOID();
}
