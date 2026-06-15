/*
 * pg_temporal_tables.c
 *
 * Module entry point: GUC definitions and hook installation.
 */
#include "postgres.h"

#if PG_VERSION_NUM < 180000
#error "pg_temporal_tables requires PostgreSQL 18 or newer"
#endif

#include "fmgr.h"
#include "miscadmin.h"
#include "utils/builtins.h"
#include "utils/date.h"
#include "utils/datetime.h"
#include "utils/elog.h"
#include "utils/guc.h"
#include "utils/memutils.h"
#include "utils/plancache.h"
#include "utils/timestamp.h"

#include "temporal_catalog.h"
#include "temporal_hooks.h"

PG_MODULE_MAGIC;

void		_PG_init(void);

/* GUC storage */
char	   *temporal_user_guc = NULL;
bool		temporal_require_user_guc = false;
char	   *temporal_as_of_guc = NULL;
bool		temporal_internal_ddl_guc = false;

/*
 * Validate that the new temporal.as_of value parses as a timestamptz.
 * An empty string is treated as "unset" and is always valid.
 */
static bool
temporal_as_of_check_hook(char **newval, void **extra, GucSource source)
{
	MemoryContext oldcontext = CurrentMemoryContext;

	if (*newval == NULL || (*newval)[0] == '\0')
		return true;

	PG_TRY();
	{
		(void) DirectFunctionCall3(timestamptz_in,
								   CStringGetDatum(*newval),
								   ObjectIdGetDatum(InvalidOid),
								   Int32GetDatum(-1));
	}
	PG_CATCH();
	{
		ErrorData  *edata;

		MemoryContextSwitchTo(oldcontext);
		edata = CopyErrorData();
		FlushErrorState();
		GUC_check_errdetail("%s", edata->message);
		FreeErrorData(edata);
		return false;
	}
	PG_END_TRY();

	return true;
}

/*
 * Any change to temporal.as_of (including SET/RESET and transaction-abort
 * revert) invalidates cached plans: the AS OF rewrite bakes the timestamp
 * into the plan as a Const, so plans from a different as_of state are stale.
 */
static void
temporal_as_of_assign_hook(const char *newval, void *extra)
{
	ResetPlanCache();
}

void
_PG_init(void)
{
	if (!process_shared_preload_libraries_in_progress)
		ereport(ERROR,
				(errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
				 errmsg("pg_temporal_tables must be loaded via shared_preload_libraries"),
				 errhint("Add 'pg_temporal_tables' to shared_preload_libraries in postgresql.conf and restart.")));

	/*
	 * Named "user_id" rather than "user": USER is a reserved keyword, so
	 * "SET temporal.user = ..." would be a syntax error. The value is any
	 * developer-chosen identity string — a user id, email, service name.
	 */
	DefineCustomStringVariable("temporal.user_id",
							   "Application-provided identity stamped into changed_by on tracked tables.",
							   "Unset means changed_by is NULL; attribution is never inferred from the database role.",
							   &temporal_user_guc,
							   NULL,
							   PGC_USERSET,
							   0,
							   NULL, NULL, NULL);

	DefineCustomBoolVariable("temporal.require_user",
							 "Reject writes to tracked tables when temporal.user_id is unset.",
							 NULL,
							 &temporal_require_user_guc,
							 false,
							 PGC_USERSET,
							 0,
							 NULL, NULL, NULL);

	DefineCustomStringVariable("temporal.as_of",
							   "Timestamp for transparent AS OF time travel.",
							   "When set, SELECTs on tracked tables transparently read the data as it was at this moment.",
							   &temporal_as_of_guc,
							   NULL,
							   PGC_USERSET,
							   0,
							   temporal_as_of_check_hook,
							   temporal_as_of_assign_hook,
							   NULL);

	DefineCustomBoolVariable("temporal.internal_ddl",
							 "Internal flag: suppress DDL protection while the extension rebuilds its own objects.",
							 NULL,
							 &temporal_internal_ddl_guc,
							 false,
							 PGC_USERSET,
							 GUC_NO_SHOW_ALL,
							 NULL, NULL, NULL);

	MarkGUCPrefixReserved("temporal");

	temporal_catalog_init();
	temporal_hooks_init();
}
