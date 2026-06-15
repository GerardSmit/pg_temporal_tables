/*
 * temporal_catalog.h
 *
 * Per-backend metadata cache for tracked tables.
 */
#ifndef TEMPORAL_CATALOG_H
#define TEMPORAL_CATALOG_H

#include "postgres.h"
#include "access/attnum.h"
#include "access/genam.h"
#include "executor/spi.h"
#include "nodes/bitmapset.h"
#include "pg_config_manual.h"

/* Cached metadata for one tracked table (or a negative entry). */
typedef struct TemporalTableEntry
{
	Oid			base_relid;		/* hash key — must be first */
	bool		is_tracked;		/* false = negative cache entry */

	Oid			history_relid;
	Oid			versions_view_relid;

	AttrNumber	valid_from_attnum;	/* on base table */
	AttrNumber	changed_by_attnum;	/* on base table */

	int			n_pk;
	AttrNumber	pk_attnums[INDEX_MAX_KEYS];	/* base-table attnums of PK cols */

	Bitmapset  *excluded_attnums;	/* base-table attnums of excluded cols */

	int64		combine_interval_us;	/* microseconds; 0 = same-tx only */

	/* SPI plans against the history table, prepared lazily (SPI_keepplan) */
	SPIPlanPtr	plan_check;		/* chain-row existence check */
	SPIPlanPtr	plan_extend;	/* combine: extend valid_to */
	SPIPlanPtr	plan_insert;	/* history row insert */
} TemporalTableEntry;

/*
 * Look up tracked-table metadata for a relation. Returns NULL when the
 * relation is not tracked. Uses direct catalog scans (no SPI), so it is safe
 * from both trigger and planner-hook contexts. The returned pointer is owned
 * by the cache and valid until the next cache invalidation — do not keep it
 * across CommandCounterIncrement boundaries.
 */
extern TemporalTableEntry *temporal_lookup(Oid relid);

extern void temporal_catalog_init(void);

/* Set while our own writes touch history; checked by the protect trigger. */
extern bool temporal_in_internal_write;

#endif							/* TEMPORAL_CATALOG_H */
