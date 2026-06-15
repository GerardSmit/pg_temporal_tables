# pg_temporal_tables

SQL Server-style **system-versioned temporal tables** for PostgreSQL — with the two things SQL Server doesn't give you: **who changed what**, and **change combining** for chatty ORMs.

```sql
SELECT temporal.enable('public.users');          -- start tracking
SET temporal.user_id = 'gerard@example.com';     -- who is acting

UPDATE users SET name = 'Alice B' WHERE id = 1;  -- old version archived automatically

SET temporal.as_of = '2026-06-01';               -- now every query reads the past
SELECT * FROM users;                             -- ...as it was on 2026-06-01
```

Targets **PostgreSQL 18+**. Requires `shared_preload_libraries`. C extension + a dedicated **EF Core** library.

---

## What's supported

- **System-time versioning** — every change to a tracked table archives the old row to `<table>__history`; `INSERT`/`UPDATE`/`DELETE` are unchanged in your app.
- **Transparent AS OF time travel** — `SET temporal.as_of = '…'` (or a per-query marker) rewrites *normal* queries — JOINs, views, CTEs, ORM SQL — to read a past moment. No query rewrites, no special syntax.
- **Attribution** — every version carries `valid_from` and `changed_by` (your `temporal.user_id`); deletes stamp `deleted_by`.
- **Combine buckets** — many rapid edits by the same user collapse into one history record.
- **Audit/history views** — `temporal.changes()` (old→new diffs, changed columns), `temporal.column_history()` (per-column timelines), and a `<table>__versions` view.
- **Schema evolution** — `ADD COLUMN` auto-propagates; managed `rename_column` / `drop_column` / `alter_column_type` keep base, history and view in sync.
- **Declarative-partitioned tables** — track the partitioned root; writes across partitions are versioned; AS OF expands partitions.
- **Retention & compaction** — `prune`, partition-drop `drop_history_before`, redundant-version `compact_history`, and declarative policies run by any scheduler (pg_cron).
- **PK-change lineage** (opt-in) — a stable `temporal_row_id` so history follows a row across primary-key changes.
- **Bitemporal** — composes with PostgreSQL 18 native application-time periods (`PRIMARY KEY (…, period WITHOUT OVERLAPS)`).
- **Replication-friendly** — plain trigger writes: physical replication, hot-standby AS OF reads, and transaction poolers (pgdog, PgBouncer) work out of the box.

### Compared to other PostgreSQL temporal extensions

| Feature | **pg_temporal_tables** | [periods](https://github.com/xocolatl/periods) | [temporal_tables](https://github.com/arkhipov/temporal_tables) | [nearform](https://github.com/nearform/temporal_tables) | [pg_bitemporal](https://github.com/hettie-d/pg_bitemporal) |
|---|---|---|---|---|---|
| System-time versioning | ✅ | ✅ | ✅ | ✅ | ⚠️ asserted-time only |
| Application-time periods (SQL:2011) | ⚠️ via PG18 native | ✅ full | ❌ | ❌ | ✅ |
| **Transparent AS OF** (planner rewrite) | ✅ | ⚠️ helper funcs | ❌ manual | ❌ | ❌ |
| Change attribution (who) | ✅ | ❌ | ⚠️ DIY trigger | ❌ | ⚠️ created-at only |
| Combine rapid changes | ✅ | ❌ | ⚠️ same-txn only | ❌ | ❌ |
| Diff / audit views | ✅ | ⚠️ raw history | ❌ | ❌ | ❌ |
| Schema evolution propagated | ✅ | ❌ | ❌ | ⚠️ manual | ❌ |
| Declarative partitions | ✅ | ⚠️ manual | ❌ | ❌ | ❌ |
| Retention + auto policies | ✅ | ⚠️ manual | ✅ strategies | ❌ | ❌ |
| PK-change lineage | ✅ opt-in | ❌ | ❌ | ❌ | ❌ |
| History compaction | ✅ | ❌ | ❌ | ❌ | ❌ |
| EF Core / ORM library | ✅ | ❌ | ❌ | ❌ | ❌ |
| Implementation | C | PL/pgSQL+C | C | PL/pgSQL | PL/pgSQL |
| `shared_preload_libraries` | required | no | no | no | no |
| PostgreSQL | 18+ | 9.5–15 | 9.2+ | any | — |

In short: `periods` is the most standards-complete (full SQL:2011 PERIODs) and `pg_bitemporal` is a dedicated bitemporal modeller, but neither offers transparent AS OF, attribution, schema-evolution, compaction, or an ORM layer. `temporal_tables` (Arkhipov) is the trigger-based predecessor this builds on. `pg_temporal_tables` trades cloud-managed-DB compatibility (it needs `shared_preload_libraries` + PG18) for the broadest operational feature set and planner-hook time travel. Source: [PostgreSQL wiki — Temporal Extensions](https://wiki.postgresql.org/wiki/Temporal_Extensions).

---

## Install

```sh
# build deps (Debian/Ubuntu)
sudo apt install postgresql-server-dev-18 build-essential
make && sudo make install
```

```ini
# postgresql.conf
shared_preload_libraries = 'pg_temporal_tables'
```

```sql
CREATE EXTENSION pg_temporal_tables;
```

## Quick start

```sql
-- Track a table. History for all columns EXCEPT the noisy ones.
SELECT temporal.enable('public.users',
    excluded_columns => ARRAY['updated_at','updated_by','locked_at'],
    combine_interval => interval '5 seconds',
    include_indexes  => true);   -- mirror base indexes on history (unique → non-unique)

-- Tell the extension who is acting (any string: user id, email, service name).
-- Set this per connection — e.g. in an EF Core connection interceptor.
SET temporal.user_id = 'gerard@example.com';

-- Use the table exactly as before. Versioning is invisible.
INSERT INTO users (id, name, role_id) VALUES (1, 'Alice', 10);
UPDATE users SET name = 'Alice B' WHERE id = 1;   -- history row written
UPDATE users SET role_id = 20 WHERE id = 1;       -- within 5 s, same user → combined

-- enable() added two columns to the BASE table (like SQL Server's period
-- columns): valid_from = when this current row was last changed, changed_by =
-- who changed it. They show up in SELECT * too.
SELECT name, valid_from AS modified_at, changed_by AS modified_by FROM users;

-- See the full history of row 1 — every version, oldest first.
-- <table>__versions = history rows + the current row, with valid_from/valid_to/
-- changed_by/deleted_by/is_current.
SELECT name, role_id, valid_from, valid_to, changed_by, is_current
FROM users__versions
WHERE id = 1
ORDER BY valid_from;
```

---

## Usage

### Time travel

Per query — PostgreSQL has no per-statement OPTION clause, so the extension provides one. The planner consumes the marker and the **whole statement** (all joins) reads that moment:

```sql
SELECT u.name, r.name AS role_name,
       u.valid_from AS modified_at,    -- when THIS version was made
       u.changed_by AS modified_by     -- who made THIS version
FROM users u
JOIN roles r ON r.id = u.role_id
WHERE temporal.as_of('2026-06-01');
```

Or per session:

```sql
SET temporal.as_of = '2026-06-01';
SELECT ...;                            -- every query reads that moment
RESET temporal.as_of;                  -- back to the present
```

A query-level marker overrides the session setting. The marker timestamp must be a plan-time value (a literal, or a bound parameter outside a generic plan).

While `temporal.as_of` is set the session is read-only with respect to tracked tables: `INSERT`/`UPDATE`/`DELETE` and table `COPY` raise errors (`COPY (SELECT …) TO` works and honors the timestamp). Statements writing only to untracked tables (e.g. `CREATE TABLE AS SELECT`) may read the historical snapshot.

### History & audit views

```sql
-- Change events with old + new values:
SELECT * FROM temporal.changes('public.users', '2026-05-01', '2026-06-12',
                               p_pk => '{"id": 1}');
--  pk        | changed_at | changed_by | operation | old_row | new_row | changed_columns
--  {"id":1}  | 2026-05-05 | gerard@…   | UPDATE    | {…}     | {…}     | {name,role_id}

-- One column's value timeline (only the points where it actually changed):
SELECT * FROM temporal.column_history('public.users', '{"id": 1}', 'name');

-- Heatmaps / timelines / anything custom: plain SQL over the versions view.
-- users__versions = every row version (history + current) with
-- valid_from / valid_to / changed_by / deleted_by / is_current columns.
SELECT date_bin('1 day', valid_from, TIMESTAMPTZ '2026-05-01') AS bucket,
       count(DISTINCT id) AS changed_rows,
       count(*)           AS total_changes
FROM users__versions
WHERE changed_by = 'gerard@example.com'
  AND valid_from >= '2026-05-01' AND valid_from < '2026-06-12'
GROUP BY 1 ORDER BY 1;
```

`temporal.mod_date(row)` / `temporal.mod_user(row)` are sugar that work on rows of the base table, the history table and the versions view alike.

### Restore / undelete

```sql
-- Roll a row back to a past state (audited — creates a new current version):
SELECT temporal.restore('public.users', '{"id": 1}', '2026-05-01'::timestamptz);
-- Bring back a deleted row (its last version):
SELECT temporal.restore_deleted('public.users', '{"id": 1}');
```

### Schema changes

```sql
ALTER TABLE users ADD COLUMN nickname text;                    -- auto-propagated to history
ALTER TABLE users RENAME COLUMN nickname TO handle;            -- auto-propagated to history
SELECT temporal.drop_column('public.users', 'handle');         -- managed (keeps history column)
SELECT temporal.alter_column_type('public.users', 'name', 'varchar(200)');
```

`DROP COLUMN` / `ALTER TYPE` go through the managed functions because they collide with the versions-view dependency if done raw. See [DDL on tracked tables](#ddl-on-tracked-tables).

### Retention & compaction

```sql
-- One-off:
SELECT temporal.prune('public.users', now() - interval '2 years');  -- delete old history
SELECT temporal.compact_history('public.users');                    -- merge redundant versions

-- Or declare policies and let a scheduler run them — no background worker:
SELECT temporal.add_retention_policy('public.users', interval '2 years');
SELECT temporal.add_compaction_policy('public.users');
SELECT cron.schedule('temporal-policies', '*/15 * * * *',           -- pg_cron (or any scheduler)
                     $$SELECT temporal.run_due_policies()$$);
```

`run_due_policies()` is idempotent, advisory-locked per table, time-budgeted, and a no-op on read-only standbys. For partitioned history, `temporal.drop_history_before()` drops whole sub-cutoff partitions before pruning the rest.

### Stop tracking

```sql
SELECT temporal.disable('public.users');   -- the history table and its data are preserved
```

### Lineage across primary-key changes (opt-in)

By default a row's identity is its primary key — correct only when the PK is immutable. To follow a row across PK changes, enable lineage:

```sql
SELECT temporal.enable('public.invoices', track_lineage => true);  -- adds a stable temporal_row_id
```

`temporal.changes()` / `column_history()` / the versions view then stitch one continuous chain across a PK change. See [Limitations](#limitations) for the full identity model.

### Bitemporal (application-time periods)

System-time versioning composes with PostgreSQL 18's native application-time periods — track a table that already has a range PK:

```sql
CREATE EXTENSION IF NOT EXISTS btree_gist;   -- needed for a scalar + range WITHOUT OVERLAPS key
CREATE TABLE price (
    sku   text,
    valid daterange,
    cents int,
    PRIMARY KEY (sku, valid WITHOUT OVERLAPS)
);
SELECT temporal.enable('public.price');      -- system-time history of the application-time rows
```

The application-time range is ordinary data to the extension; system-time AS OF time-travels the whole row, range included.

---

## EF Core integration

`dotnet/PgTemporalTables.EntityFrameworkCore` (EF Core 10 / Npgsql.EntityFrameworkCore.PostgreSQL 10).

```csharp
// 1. Wire it up once (ASP.NET Core). The user-id provider runs per connection
//    open; its parameters are injected from DI (minimal-API style):
builder.Services.AddHttpContextAccessor();
builder.Services.AddDbContext<AppDbContext>(options => options
    .UseNpgsql(connectionString)
    .UseTemporalTables(o => o.UseUserIdProvider(
        (IHttpContextAccessor hca) => hca.HttpContext?.User?.Identity?.Name)));

// 2. Model — same shape as SQL Server's IsTemporal():
//    Model-wide defaults applied to every IsTemporal() table that doesn't set its own
//    (a per-table value always wins). Lineage stays per-table — it adds a column.
modelBuilder.UseTemporalDefaults(
    combineInterval:    TimeSpan.FromSeconds(5),
    historyCompression: "lz4",
    includeIndexes:     true);

modelBuilder.Entity<User>().ToTable("users", t => t.IsTemporal(tt => tt
    .ExcludeColumns(u => new { u.UpdatedAt, u.LockedAt })   // typed; resolves to column names
    .TrackLineage()                                          // optional: temporal_row_id
    .WithRetentionPolicy(TimeSpan.FromDays(730))));          // optional per-table policy
```

**Migrations / EnsureCreated** emit `CREATE EXTENSION IF NOT EXISTS` + `temporal.enable(...)` after creating the table, `temporal.disable()` before drops, and `set_*()` / `add_*_policy()` / `set_history_compression()` calls as settings change. Shadow properties `ValidFrom` / `ChangedBy` (and `TemporalRowId` when `TrackLineage()` is set) are added — store-generated, populated via `RETURNING`, never written by `SaveChanges`.

**Attribution** — `temporal.user_id` is set on every connection open from the provider; `context.SetTemporalUserId("...")` overrides per context.

**Per-query time travel** — translates to the `temporal.as_of()` marker, so the whole statement (joins, `Include()`) reads one moment:

```csharp
ctx.Users.Include(u => u.Role).TemporalAsOf(t)            // any IQueryable
ctx.Users.Where(u => u.Active && PgTemporal.AsOf(t))      // raw marker form
```

**Version metadata in LINQ:**

```csharp
ctx.Users.Where(u => PgTemporal.ModUser(u) == "gerard@example.com")
ctx.Users.Select(u => new { u.Name, At = PgTemporal.ModDate(u), By = PgTemporal.ModUser(u) })
```

**SQL Server-style range operators** (versions view, multiple versions per row, always no-tracking):

```csharp
ctx.Users.TemporalAll()                   // every version
ctx.Users.TemporalFromTo(a, b)            // active in (a, b), boundaries exclusive
ctx.Users.TemporalBetween(a, b)           // like FromTo, upper bound inclusive
ctx.Users.TemporalContainedIn(a, b)       // whole lifetime within [a, b]
ctx.Users.TemporalAll().OrderBy(u => PgTemporal.ModDate(u))   // metadata composes
```

**Heatmaps / aggregates over history** — group the versions view server-side:

```csharp
var heat = await ctx.Users
    .TemporalAll()                                    // the __versions view
    .Where(u => PgTemporal.ModUser(u) == currentUser)
    .GroupBy(u => PgTemporal.ModDate(u).Date)         // bucket by day
    .Select(g => new
    {
        Day          = g.Key,
        ChangedRows  = g.Select(u => u.Id).Distinct().Count(),
        TotalChanges = g.Count(),
    })
    .OrderBy(x => x.Day)
    .ToListAsync();
// → GROUP BY date_trunc('day', …) with COUNT(DISTINCT id) / COUNT(*), all in PostgreSQL.
// For non-day buckets, use a raw Database.SqlQuery<T>(...) with date_bin().
```

**Change events** — a composable `IQueryable<TemporalChange>` (old→new diffs + changed columns):

```csharp
var updates = await context.Temporal<User>()
    .Changes(from, to, userId)                    // single key; or [a, b] composite; or omit for the whole table
    .Where(c => c.Operation == "UPDATE")          // Where/OrderBy/Take/Count compose to SQL
    .OrderByDescending(c => c.ChangedAt)
    .Take(50)
    .ToListAsync();
foreach (var c in updates)
    Console.WriteLine($"{c.ChangedAt:o} {c.ChangedBy}: " + string.Join(", ", c.ChangedColumns ?? []));
// c.OldRow / c.NewRow are JSON text — JsonSerializer.Deserialize<User>(c.NewRow!) to rehydrate.
```

`Where`/`OrderBy`/`Take`/`Count` on the event columns (`Operation`, `ChangedBy`, `ChangedAt`, `Pk`) translate to SQL; `GetChangesAsync(...)` is the materialized shortcut.

**Maintenance handle** — imperative ops grouped per entity:

```csharp
await context.Temporal<User>().CompactHistoryAsync();
await context.Temporal<User>().PruneAsync(DateTimeOffset.UtcNow.AddYears(-2));
await context.Temporal<User>().DropHistoryBeforeAsync(cutoff);
await context.Temporal<User>().RestoreAsync(userId, asOf);     // roll a row back
await context.Temporal<User>().RestoreDeletedAsync(userId);    // undelete
var stats = await context.Temporal<User>().GetStatsAsync();
await context.Temporal<User>().SetHistoryCompressionAsync("lz4");
await context.RunDueTemporalPoliciesAsync();                   // for a scheduler
```

---

## API reference

| Function | Purpose |
|---|---|
| `temporal.enable(table, excluded_columns, combine_interval, include_indexes, history_table, track_lineage)` | Start tracking. Adds `valid_from`/`changed_by` columns to the base table, and creates `<table>__history`, `<table>__versions`, triggers, indexes. Table must have a PRIMARY KEY. |
| `temporal.disable(table)` | Stop tracking. History table, `valid_from`, `changed_by` are preserved. |
| `temporal.set_excluded_columns(table, name[])` | Change the excluded set. |
| `temporal.set_combine_interval(table, interval)` | Change the combine bucket (0 = same-transaction collapsing only). |
| `temporal.set_include_indexes(table, bool)` | Mirror base indexes onto history (auto-synced on CREATE/DROP INDEX) or drop the mirrors. |
| `temporal.changes(table, from, to, pk)` | Change events: operation, old/new row JSON, changed columns, attribution. |
| `temporal.column_history(table, pk, column, until)` | Distinct-value timeline of one column of one row. |
| `temporal.mod_date(row)` / `temporal.mod_user(row)` | Modification timestamp / author of any row version. |
| `temporal.table_stats(table)` | Row/version counts, history & base size, oldest/newest version, avg chain length. |
| `temporal.restore(table, pk, as_of)` | Roll a row back to its state at `as_of` (audited: creates a new current version). |
| `temporal.restore_deleted(table, pk)` | Re-insert the last version of a currently-deleted row (keeps its lineage). |
| `temporal.rename_column(table, old, new)` | Rename a column on base, history, and versions view together. |
| `temporal.drop_column(table, column, drop_from_history, compact)` | Drop a base column; keeps the history column by default. |
| `temporal.alter_column_type(table, column, type, using)` | Change a column's type on base and history together. |
| `temporal.prune(table, older_than)` | Delete history whose validity ended before the cutoff. Returns rows deleted. |
| `temporal.compact_history(table)` | Merge redundant adjacent history versions (e.g. after a column drop). Returns rows merged. |
| `temporal.drop_history_before(table, cutoff)` | Retention: drop whole history partitions older than the cutoff (if `RANGE(valid_to)`-partitioned), then prune the rest. |
| `temporal.set_history_compression(table, method)` | Set column compression (`pglz`/`lz4`/`default`) on the history table's varlena columns. |
| `temporal.add_retention_policy(table, drop_after, run_every)` / `add_compaction_policy(table, run_every)` | Declare a policy (stored in `temporal.policies`). |
| `temporal.remove_retention_policy(table)` / `remove_compaction_policy(table)` | Drop a policy. |
| `temporal.run_due_policies(max_duration)` | Run all due policies. Call periodically from pg_cron or any scheduler. Returns count run. |

| GUC | Purpose |
|---|---|
| `temporal.user_id` | Identity stamped into `changed_by`. **No fallback** — unset means `NULL`. |
| `temporal.require_user` | When on, writes to tracked tables fail unless `temporal.user_id` is set. |
| `temporal.as_of` | Timestamptz. When set, SELECTs on tracked tables time-travel transparently. |

---

## Semantics & guarantees

Modeled on SQL Server system-versioned temporal tables:

- All period timestamps are the **transaction start time** (`transaction_timestamp()`), stored as `timestamptz`.
- AS OF returns rows where `valid_from <= t AND valid_to > t` (start inclusive, end exclusive).
- `INSERT` writes no history; `UPDATE` copies the old version to history; `DELETE` copies the final version and stamps `deleted_by`.
- Changes touching **only excluded columns** (or changing nothing) are invisible — no history, stamps unchanged.
- **Same transaction always collapses**: a row inserted+updated+deleted within one transaction leaves no trace; multiple updates yield one change.
- **Combine bucket**: another change by the *same non-NULL user* within `combine_interval` of the previous change extends the previous history record instead of adding one. The window slides with each edit (an active editing session stays one record). A different user — or no user — always splits.
- Versioning starts at `temporal.enable()`: AS OF earlier than that returns nothing (same as SQL Server).
- Concurrent writers: if your transaction began before another transaction's committed change to the same row, the write fails with SQLSTATE `40001` (retry), mirroring SQL Server error 13535.

### DDL on tracked tables

- `ADD COLUMN` propagates to the history table automatically (nullable, no default) — EF Core migrations keep working.
- `RENAME COLUMN` is auto-propagated: a raw `ALTER TABLE ... RENAME COLUMN` is mirrored onto the history table and the versions view rebuilt. (Use `temporal.rename_column()` for an explicit, unambiguous rename.)
- `DROP COLUMN` / `ALTER TYPE` collide with the versions-view dependency if done raw, so use `temporal.drop_column()` / `temporal.alter_column_type()`. `drop_column` keeps the history column (and its data) by default; pass `compact => true` to compact afterward. The disable → alter both tables → `temporal.enable(..., history_table => ...)` cycle still works for anything more involved.
- `TRUNCATE` is blocked; the history table rejects direct writes (use `temporal.prune()`).
- Managed temporal triggers cannot be dropped, disabled or reconfigured while tracking is active; use `temporal.disable()` first.
- Dropping the history table or versions view is blocked while tracking; dropping the base table releases everything and keeps the orphaned history.

### Sessions, transactions, poolers

- GUCs are per connection. Npgsql resets them on pooled-connection reuse (`DISCARD ALL`), so set `temporal.user_id` in a connection-open interceptor.
- Behind transaction-mode poolers (pgdog, PgBouncer) use `SET LOCAL temporal.user_id = ...` inside each transaction, or `set_config('temporal.user_id', $1, true)`.
- `ROLLBACK` reverts both the data changes *and* any `SET` made in the transaction — attribution can never outlive the data it attributed.
- Hot standby: `temporal.as_of` reads work on replicas (the extension must be in `shared_preload_libraries` there too).

## Performance & storage notes

- History writes are one extra index-assisted INSERT (or UPDATE when combining) per changed row, via cached SPI plans.
- Plans under `temporal.as_of` push predicates into both union branches (index scans on base and history). Prepared statements re-plan automatically when `as_of` changes.
- History stores **full row versions**. There is no cross-row deduplication: a large unchanged `text` value is physically duplicated per version (TOAST-compressed individually; consider `default_toast_compression = lz4`, or `temporal.set_history_compression(table, 'lz4')` per table). Your levers are `excluded_columns`, `combine_interval`, and `temporal.compact_history()`.
- `include_indexes => false` (default) matches SQL Server: history gets only `(pk, valid_to)` and `(valid_from, valid_to)` indexes. Add workload-specific indexes directly on `__history`, or set `include_indexes => true`.

## Limitations

- Tracked tables must be ordinary (`relkind = 'r'`) or declarative-partitioned (`relkind = 'p'`) tables with a primary key. Track the partitioned **root**, not a leaf. Legacy table inheritance and TimescaleDB hypertables are not supported.
- For a partitioned table the auto-created history table is a plain table; to get partition-drop retention, pass a `RANGE (valid_to)`-partitioned history table via `history_table =>` and call `temporal.drop_history_before()`.
- Unique constraints are not enforced across history (impossible by design — many versions per key); mirrored unique indexes become non-unique.
- **Row identity over time**: by default the **primary key** is the identity, correct only when the PK is immutable and never reused. `track_lineage => true` switches to **object identity** — a stable `temporal_row_id` carried for the row's life. Under lineage: a PK change keeps one chain; a `DELETE` ends the object and a later bare `INSERT` reusing the key is a *new* object (new lineage, gap shown); `temporal.restore_deleted()` is an explicit resurrection and keeps the **original** lineage (deleted interval shown as a gap). Object identity is the theoretically correct model; PK-as-identity is the cheaper default for immutable keys.
- `temporal.changes()` / `column_history()` / the versions views are for present-time sessions; they refuse to run while `temporal.as_of` is set.
- Logical replication of tracked tables replicates the history table's rows as ordinary inserts; the subscriber should not also enable tracking on the same tables.

## Development

Tests are .NET 10 + xunit.v3 + Testcontainers (Docker required):

```sh
dotnet test tests/pg_temporal_tables.sln
```

The test image is built and cached automatically from the extension sources (`PG_TEMPORAL_PG_VERSION` selects the PostgreSQL major, default 18). Quick manual loop: `scripts/smoke*.sql` against a container built from `Dockerfile.pg_temporal_test`.
