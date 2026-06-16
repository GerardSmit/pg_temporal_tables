\echo Use "CREATE EXTENSION pg_temporal_tables" to load this file. \quit

-- ─────────────────────────────────────────────────────────────────────────────
-- Catalog
-- ─────────────────────────────────────────────────────────────────────────────

-- Column order matters: temporal_catalog.c reads this table by attribute
-- number. Never reorder or insert columns; append only (and update the C
-- Anum_* constants).
CREATE TABLE temporal.tracked_tables (
    table_oid         regclass    PRIMARY KEY,
    history_table_oid regclass    NOT NULL UNIQUE,
    versions_view_oid regclass    NOT NULL UNIQUE,
    excluded_columns  name[]      NOT NULL DEFAULT '{}',
    combine_interval  interval    NOT NULL DEFAULT '0',
    include_indexes   boolean     NOT NULL DEFAULT false,
    enabled_at        timestamptz NOT NULL DEFAULT now(),
    track_lineage     boolean     NOT NULL DEFAULT false
);
GRANT SELECT ON temporal.tracked_tables TO PUBLIC;

CREATE TABLE temporal.mirrored_indexes (
    table_oid         regclass NOT NULL REFERENCES temporal.tracked_tables ON DELETE CASCADE,
    base_index_oid    regclass PRIMARY KEY,
    history_index_oid regclass NOT NULL UNIQUE
);
GRANT SELECT ON temporal.mirrored_indexes TO PUBLIC;

SELECT pg_catalog.pg_extension_config_dump('temporal.tracked_tables', '');
SELECT pg_catalog.pg_extension_config_dump('temporal.mirrored_indexes', '');

-- ─────────────────────────────────────────────────────────────────────────────
-- C functions
-- ─────────────────────────────────────────────────────────────────────────────

CREATE FUNCTION temporal.invalidate(p_table regclass)
RETURNS void
AS 'MODULE_PATHNAME', 'temporal_invalidate'
LANGUAGE C STRICT VOLATILE;

COMMENT ON FUNCTION temporal.invalidate(regclass) IS
    'Broadcast a relcache invalidation so all backends reload tracked-table metadata.';

CREATE FUNCTION temporal.row_stamp()
RETURNS trigger
AS 'MODULE_PATHNAME', 'temporal_row_stamp'
LANGUAGE C VOLATILE;

CREATE FUNCTION temporal.write_history()
RETURNS trigger
AS 'MODULE_PATHNAME', 'temporal_write_history'
LANGUAGE C VOLATILE SECURITY DEFINER;

CREATE FUNCTION temporal.block_truncate()
RETURNS trigger
AS 'MODULE_PATHNAME', 'temporal_block_truncate'
LANGUAGE C VOLATILE;

CREATE FUNCTION temporal.protect_history()
RETURNS trigger
AS 'MODULE_PATHNAME', 'temporal_protect_history'
LANGUAGE C VOLATILE;

CREATE FUNCTION temporal.prune(p_table regclass, p_older_than timestamptz)
RETURNS bigint
AS 'MODULE_PATHNAME', 'temporal_prune'
LANGUAGE C STRICT VOLATILE;

COMMENT ON FUNCTION temporal.prune(regclass, timestamptz) IS
    'Retention cleanup: delete history rows whose validity ended before the cutoff. Returns rows deleted.';

-- ─────────────────────────────────────────────────────────────────────────────
-- Internal helpers
-- ─────────────────────────────────────────────────────────────────────────────

-- Mirror one base-table index onto the history table (unique → non-unique).
-- Returns the OID of the created history index.
CREATE FUNCTION temporal._mirror_one_index(p_base_index regclass, p_history regclass)
RETURNS regclass
LANGUAGE plpgsql
AS $$
DECLARE
    v_def        text;
    v_tail       text;        -- everything from USING onwards (method, columns, predicate)
    v_pos        int;
    v_name       text;
    v_hist_schema name;
    v_n          int := 0;
BEGIN
    v_def := pg_get_indexdef(p_base_index);

    v_pos := position(' USING ' IN v_def);
    IF v_pos = 0 THEN
        RAISE EXCEPTION 'cannot mirror index %: unexpected definition %', p_base_index, v_def;
    END IF;
    v_tail := substring(v_def FROM v_pos + 1);   -- 'USING btree (...) [WHERE ...]'
    -- History mirrors are intentionally non-unique. PostgreSQL prints
    -- NULLS [NOT] DISTINCT on unique indexes, but that clause is not valid
    -- once CREATE UNIQUE INDEX has been reduced to CREATE INDEX.
    v_tail := regexp_replace(
        v_tail,
        '[[:space:]]+NULLS[[:space:]]+(NOT[[:space:]]+)?DISTINCT([[:space:]]+WHERE|$)',
        '\2',
        'i');

    SELECT n.nspname INTO v_hist_schema
    FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE c.oid = p_history;

    -- unique index name on the history table, capped to NAMEDATALEN
    SELECT left(c.relname, 50) || '__hist' INTO v_name
    FROM pg_class c WHERE c.oid = p_base_index;
    WHILE EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                  WHERE c.relname = v_name AND n.nspname = v_hist_schema) LOOP
        v_n := v_n + 1;
        v_name := left(v_name, 55) || v_n::text;
    END LOOP;

    EXECUTE format('CREATE INDEX %I ON %s %s', v_name, p_history::text, v_tail);

    RETURN format('%I.%I', v_hist_schema, v_name)::regclass;
END;
$$;

-- Mirror all not-yet-mirrored base indexes onto the history table.
CREATE FUNCTION temporal._mirror_indexes(p_table regclass)
RETURNS void
LANGUAGE plpgsql
AS $$
DECLARE
    v_history regclass;
    r         record;
BEGIN
    SELECT history_table_oid INTO v_history
    FROM temporal.tracked_tables WHERE table_oid = p_table;

    FOR r IN
        SELECT i.indexrelid
        FROM pg_index i
        WHERE i.indrelid = p_table
          AND NOT EXISTS (SELECT 1 FROM temporal.mirrored_indexes m
                          WHERE m.base_index_oid = i.indexrelid)
        ORDER BY i.indexrelid
    LOOP
        INSERT INTO temporal.mirrored_indexes (table_oid, base_index_oid, history_index_oid)
        VALUES (p_table, r.indexrelid,
                temporal._mirror_one_index(r.indexrelid, v_history));
    END LOOP;
END;
$$;

-- Comma-separated quoted column list of a relation (ordinary, non-dropped cols).
CREATE FUNCTION temporal._column_list(p_table regclass)
RETURNS text
LANGUAGE sql STABLE
AS $$
    SELECT string_agg(quote_ident(attname), ', ' ORDER BY attnum)
    FROM pg_attribute
    WHERE attrelid = p_table AND attnum > 0 AND NOT attisdropped;
$$;

-- (Re)create the versions view for a tracked table. Used by enable() and by
-- the ADD COLUMN propagation event trigger.
CREATE FUNCTION temporal._create_versions_view(p_table regclass, p_history regclass, p_view_name name, p_schema name)
RETURNS regclass
LANGUAGE plpgsql
AS $$
DECLARE
    v_cols text := temporal._column_list(p_table);
BEGIN
    EXECUTE format(
        'CREATE OR REPLACE VIEW %I.%I AS '
        'SELECT %s, valid_to, deleted_by, false AS is_current FROM %s '
        'UNION ALL '
        'SELECT %s, NULL::timestamptz, NULL::text, true AS is_current FROM %s',
        p_schema, p_view_name,
        v_cols, p_history::text,
        v_cols, p_table::text);
    RETURN format('%I.%I', p_schema, p_view_name)::regclass;
END;
$$;

-- Copy SELECT grants (including PUBLIC) from one relation to another.
CREATE FUNCTION temporal._copy_select_grants(p_from regclass, p_to regclass)
RETURNS void
LANGUAGE plpgsql
AS $$
DECLARE
    r record;
BEGIN
    FOR r IN
        SELECT a.grantee
        FROM pg_class c, aclexplode(c.relacl) a
        WHERE c.oid = p_from AND a.privilege_type = 'SELECT'
    LOOP
        IF r.grantee = 0 THEN
            EXECUTE format('GRANT SELECT ON %s TO PUBLIC', p_to::text);
        ELSE
            EXECUTE format('GRANT SELECT ON %s TO %I', p_to::text,
                           (SELECT rolname FROM pg_roles WHERE oid = r.grantee));
        END IF;
    END LOOP;
END;
$$;

-- ─────────────────────────────────────────────────────────────────────────────
-- Public API: enable / disable / settings
-- ─────────────────────────────────────────────────────────────────────────────

CREATE FUNCTION temporal.enable(
    p_table          regclass,
    excluded_columns name[]   DEFAULT '{}',
    combine_interval interval DEFAULT '5 seconds',
    include_indexes  boolean  DEFAULT false,
    history_table    regclass DEFAULT NULL,
    track_lineage    boolean  DEFAULT false
) RETURNS void
LANGUAGE plpgsql
AS $$
DECLARE
    v_schema     name;
    v_relname    name;
    v_relkind    "char";
    v_relpersistence "char";
    v_relispartition boolean;
    v_history    regclass;
    v_hist_name  name;
    v_view_name  name;
    v_view       regclass;
    v_col        name;
    v_pk_cols    name[];
    v_atttype    regtype;
    v_seq        name;
BEGIN
    SELECT n.nspname, c.relname, c.relkind, c.relpersistence, c.relispartition
      INTO v_schema, v_relname, v_relkind, v_relpersistence, v_relispartition
    FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE c.oid = p_table;

    IF v_relkind NOT IN ('r', 'p') THEN
        RAISE EXCEPTION 'temporal.enable: "%" is not an ordinary or partitioned table', p_table;
    END IF;
    IF v_relpersistence = 't' THEN
        RAISE EXCEPTION 'temporal.enable: temporary tables are not supported';
    END IF;
    IF v_relispartition THEN
        RAISE EXCEPTION 'temporal.enable: "%" is a partition; enable the root partitioned table instead', p_table;
    END IF;
    -- Declarative partitioning is supported (relkind 'p'); legacy table
    -- inheritance and TimescaleDB hypertables (relkind 'r' with inheritance
    -- children) are not.
    IF v_relkind = 'r' AND EXISTS (SELECT 1 FROM pg_inherits WHERE inhparent = p_table OR inhrelid = p_table) THEN
        RAISE EXCEPTION 'temporal.enable: inheritance tables and TimescaleDB hypertables are not supported';
    END IF;
    IF EXISTS (SELECT 1 FROM temporal.tracked_tables WHERE table_oid = p_table) THEN
        RAISE EXCEPTION 'temporal.enable: "%" is already tracked', p_table;
    END IF;
    IF EXISTS (SELECT 1 FROM temporal.tracked_tables WHERE history_table_oid = p_table) THEN
        RAISE EXCEPTION 'temporal.enable: "%" is a temporal history table', p_table;
    END IF;

    SELECT array_agg(a.attname ORDER BY o.ord)
      INTO v_pk_cols
    FROM pg_constraint con
    CROSS JOIN LATERAL unnest(con.conkey) WITH ORDINALITY AS o(attnum, ord)
    JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = o.attnum
    WHERE con.conrelid = p_table AND con.contype = 'p';

    IF v_pk_cols IS NULL THEN
        RAISE EXCEPTION 'temporal.enable: "%" must have a PRIMARY KEY', p_table;
    END IF;

    IF combine_interval < interval '0'
       OR EXTRACT(YEAR FROM combine_interval) <> 0
       OR EXTRACT(MONTH FROM combine_interval) <> 0 THEN
        RAISE EXCEPTION 'temporal.enable: combine_interval must be a non-negative day/time interval (no months/years)';
    END IF;

    -- the history-only column names must be free on the base table
    FOR v_col IN
        SELECT attname FROM pg_attribute
        WHERE attrelid = p_table AND attnum > 0 AND NOT attisdropped
          AND attname IN ('valid_to', 'deleted_by')
    LOOP
        RAISE EXCEPTION 'temporal.enable: column name "%" on % conflicts with a managed history column', v_col, p_table;
    END LOOP;

    FOREACH v_col IN ARRAY excluded_columns LOOP
        IF v_col IN ('valid_from', 'changed_by', 'valid_to', 'deleted_by', 'temporal_row_id') THEN
            RAISE EXCEPTION 'temporal.enable: column "%" is managed by the extension and cannot be excluded', v_col;
        END IF;
        IF v_col = ANY (v_pk_cols) THEN
            RAISE EXCEPTION 'temporal.enable: primary key column "%" cannot be excluded', v_col;
        END IF;
        IF NOT EXISTS (SELECT 1 FROM pg_attribute
                       WHERE attrelid = p_table AND attname = v_col
                         AND attnum > 0 AND NOT attisdropped) THEN
            RAISE EXCEPTION 'temporal.enable: excluded column "%" does not exist on %', v_col, p_table;
        END IF;
    END LOOP;

    -- Managed columns. If they pre-exist, the types must be right.
    SELECT atttypid::regtype INTO v_atttype FROM pg_attribute
    WHERE attrelid = p_table AND attname = 'valid_from' AND NOT attisdropped;
    IF v_atttype IS NOT NULL AND v_atttype <> 'timestamptz'::regtype THEN
        RAISE EXCEPTION 'temporal.enable: existing column valid_from must be timestamptz, found %', v_atttype;
    END IF;
    SELECT atttypid::regtype INTO v_atttype FROM pg_attribute
    WHERE attrelid = p_table AND attname = 'changed_by' AND NOT attisdropped;
    IF v_atttype IS NOT NULL AND v_atttype <> 'text'::regtype THEN
        RAISE EXCEPTION 'temporal.enable: existing column changed_by must be text, found %', v_atttype;
    END IF;

    EXECUTE format(
        'ALTER TABLE %s '
        'ADD COLUMN IF NOT EXISTS valid_from timestamptz NOT NULL DEFAULT transaction_timestamp(), '
        'ADD COLUMN IF NOT EXISTS changed_by text',
        p_table::text);

    -- Lineage column: a stable per-row id, assigned once by the sequence default
    -- at INSERT and never re-applied on UPDATE, so it survives primary-key
    -- changes and lets the viewers stitch a continuous chain.
    IF track_lineage THEN
        v_seq := left(v_relname, 40) || '__lineage_seq';
        IF EXISTS (SELECT 1 FROM pg_attribute
                   WHERE attrelid = p_table AND attname = 'temporal_row_id'
                     AND attnum > 0 AND NOT attisdropped) THEN
            -- The column is already present (e.g. an ORM mapped it as a shadow
            -- column). Accept it: require bigint, and attach our own sequence
            -- default only if it has no value generator of its own.
            SELECT atttypid::regtype INTO v_atttype FROM pg_attribute
            WHERE attrelid = p_table AND attname = 'temporal_row_id' AND NOT attisdropped;
            IF v_atttype <> 'bigint'::regtype THEN
                RAISE EXCEPTION 'temporal.enable: existing column temporal_row_id on % must be bigint, found %', p_table, v_atttype;
            END IF;
            IF NOT EXISTS (SELECT 1 FROM pg_attribute
                           WHERE attrelid = p_table AND attname = 'temporal_row_id'
                             AND (atthasdef OR attidentity <> '')) THEN
                EXECUTE format('CREATE SEQUENCE IF NOT EXISTS %I.%I', v_schema, v_seq);
                EXECUTE format('ALTER TABLE %s ALTER COLUMN temporal_row_id SET DEFAULT nextval(%L)',
                               p_table::text, format('%I.%I', v_schema, v_seq));
                EXECUTE format('ALTER SEQUENCE %I.%I OWNED BY %s.temporal_row_id',
                               v_schema, v_seq, p_table::text);
            END IF;
        ELSE
            EXECUTE format('CREATE SEQUENCE IF NOT EXISTS %I.%I', v_schema, v_seq);
            EXECUTE format(
                'ALTER TABLE %s ADD COLUMN temporal_row_id bigint NOT NULL DEFAULT nextval(%L)',
                p_table::text, format('%I.%I', v_schema, v_seq));
            EXECUTE format('ALTER SEQUENCE %I.%I OWNED BY %s.temporal_row_id',
                           v_schema, v_seq, p_table::text);
        END IF;
    END IF;

    -- History table
    IF history_table IS NOT NULL THEN
        v_history := history_table;
        -- shape validation: every base column must exist with the same type
        FOR v_col, v_atttype IN
            SELECT attname, atttypid::regtype FROM pg_attribute
            WHERE attrelid = p_table AND attnum > 0 AND NOT attisdropped
        LOOP
            IF NOT EXISTS (SELECT 1 FROM pg_attribute
                           WHERE attrelid = v_history AND attname = v_col
                             AND atttypid = v_atttype::regtype::oid AND NOT attisdropped) THEN
                RAISE EXCEPTION 'temporal.enable: history table % is missing column % %', v_history, v_col, v_atttype;
            END IF;
        END LOOP;
        IF NOT EXISTS (SELECT 1 FROM pg_attribute
                       WHERE attrelid = v_history AND attname = 'valid_to'
                         AND atttypid = 'timestamptz'::regtype::oid) THEN
            RAISE EXCEPTION 'temporal.enable: history table % must have a valid_to timestamptz column', v_history;
        END IF;
        IF NOT EXISTS (SELECT 1 FROM pg_attribute
                       WHERE attrelid = v_history AND attname = 'deleted_by'
                         AND atttypid = 'text'::regtype::oid) THEN
            RAISE EXCEPTION 'temporal.enable: history table % must have a deleted_by text column', v_history;
        END IF;
        SELECT relname INTO v_hist_name FROM pg_class WHERE oid = v_history;
    ELSE
        v_hist_name := left(v_relname, 51) || '__history';
        IF EXISTS (SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                   WHERE c.relname = v_hist_name AND n.nspname = v_schema) THEN
            RAISE EXCEPTION 'temporal.enable: relation %.% already exists; pass history_table to reuse it',
                v_schema, v_hist_name;
        END IF;
        EXECUTE format('CREATE TABLE %I.%I (LIKE %s, valid_to timestamptz NOT NULL, deleted_by text)',
                       v_schema, v_hist_name, p_table::text);
        v_history := format('%I.%I', v_schema, v_hist_name)::regclass;
    END IF;

    -- Managed history indexes: combine lookup + AS OF scans
    EXECUTE format('CREATE INDEX IF NOT EXISTS %I ON %s (%s, valid_to)',
                   left(v_hist_name, 55) || '_pk_idx', v_history::text,
                   (SELECT string_agg(quote_ident(c), ', ') FROM unnest(v_pk_cols) c));
    EXECUTE format('CREATE INDEX IF NOT EXISTS %I ON %s (valid_from, valid_to)',
                   left(v_hist_name, 51) || '_period_idx', v_history::text);

    -- Protect the history table against direct modification
    EXECUTE format('CREATE TRIGGER temporal_protect BEFORE INSERT OR UPDATE OR DELETE ON %s '
                   'FOR EACH ROW EXECUTE FUNCTION temporal.protect_history()', v_history::text);
    EXECUTE format('CREATE TRIGGER temporal_protect_truncate BEFORE TRUNCATE ON %s '
                   'FOR EACH STATEMENT EXECUTE FUNCTION temporal.protect_history()', v_history::text);
    EXECUTE format('ALTER TABLE %s ENABLE ALWAYS TRIGGER temporal_protect', v_history::text);
    EXECUTE format('ALTER TABLE %s ENABLE ALWAYS TRIGGER temporal_protect_truncate', v_history::text);

    -- Versions view
    v_view_name := left(v_relname, 51) || '__versions';
    v_view := temporal._create_versions_view(p_table, v_history, v_view_name, v_schema);

    PERFORM temporal._copy_select_grants(p_table, v_history);
    PERFORM temporal._copy_select_grants(p_table, v_view);

    -- Row triggers on the base table
    EXECUTE format('CREATE TRIGGER temporal_stamp BEFORE INSERT OR UPDATE ON %s '
                   'FOR EACH ROW EXECUTE FUNCTION temporal.row_stamp()', p_table::text);
    EXECUTE format('CREATE TRIGGER temporal_history AFTER UPDATE OR DELETE ON %s '
                   'FOR EACH ROW EXECUTE FUNCTION temporal.write_history()', p_table::text);
    EXECUTE format('CREATE TRIGGER temporal_truncate BEFORE TRUNCATE ON %s '
                   'FOR EACH STATEMENT EXECUTE FUNCTION temporal.block_truncate()', p_table::text);
    EXECUTE format('ALTER TABLE %s ENABLE ALWAYS TRIGGER temporal_stamp', p_table::text);
    EXECUTE format('ALTER TABLE %s ENABLE ALWAYS TRIGGER temporal_history', p_table::text);
    EXECUTE format('ALTER TABLE %s ENABLE ALWAYS TRIGGER temporal_truncate', p_table::text);

    INSERT INTO temporal.tracked_tables
        (table_oid, history_table_oid, versions_view_oid,
         excluded_columns, combine_interval, include_indexes, track_lineage)
    VALUES (p_table, v_history, v_view,
            excluded_columns, combine_interval, include_indexes, track_lineage);

    IF include_indexes THEN
        PERFORM temporal._mirror_indexes(p_table);
    END IF;

    PERFORM temporal.invalidate(p_table);
END;
$$;

CREATE FUNCTION temporal.disable(p_table regclass)
RETURNS void
LANGUAGE plpgsql
AS $$
DECLARE
    v_history regclass;
    v_view    regclass;
BEGIN
    SELECT history_table_oid, versions_view_oid
      INTO v_history, v_view
    FROM temporal.tracked_tables WHERE table_oid = p_table;

    IF v_history IS NULL THEN
        RAISE EXCEPTION 'temporal.disable: "%" is not tracked', p_table;
    END IF;

    -- Catalog rows go first so DDL-protection event triggers see "not tracked".
    DELETE FROM temporal.mirrored_indexes WHERE table_oid = p_table;
    DELETE FROM temporal.tracked_tables WHERE table_oid = p_table;

    EXECUTE format('DROP TRIGGER temporal_stamp ON %s', p_table::text);
    EXECUTE format('DROP TRIGGER temporal_history ON %s', p_table::text);
    EXECUTE format('DROP TRIGGER temporal_truncate ON %s', p_table::text);
    EXECUTE format('DROP TRIGGER temporal_protect ON %s', v_history::text);
    EXECUTE format('DROP TRIGGER temporal_protect_truncate ON %s', v_history::text);
    EXECUTE format('DROP VIEW %s', v_view::text);

    -- History table, its data, and the managed columns are preserved.
    PERFORM temporal.invalidate(p_table);
END;
$$;

CREATE FUNCTION temporal.set_excluded_columns(p_table regclass, p_excluded name[])
RETURNS void
LANGUAGE plpgsql
AS $$
DECLARE
    v_col name;
    v_pk_cols name[];
BEGIN
    IF NOT EXISTS (SELECT 1 FROM temporal.tracked_tables WHERE table_oid = p_table) THEN
        RAISE EXCEPTION 'temporal.set_excluded_columns: "%" is not tracked', p_table;
    END IF;

    SELECT array_agg(a.attname)
      INTO v_pk_cols
    FROM pg_constraint con
    JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = ANY (con.conkey)
    WHERE con.conrelid = p_table AND con.contype = 'p';

    FOREACH v_col IN ARRAY p_excluded LOOP
        IF v_col IN ('valid_from', 'changed_by', 'valid_to', 'temporal_row_id') OR v_col = ANY (v_pk_cols) THEN
            RAISE EXCEPTION 'temporal.set_excluded_columns: column "%" cannot be excluded', v_col;
        END IF;
        IF NOT EXISTS (SELECT 1 FROM pg_attribute
                       WHERE attrelid = p_table AND attname = v_col
                         AND attnum > 0 AND NOT attisdropped) THEN
            RAISE EXCEPTION 'temporal.set_excluded_columns: column "%" does not exist on %', v_col, p_table;
        END IF;
    END LOOP;

    UPDATE temporal.tracked_tables SET excluded_columns = p_excluded WHERE table_oid = p_table;
    PERFORM temporal.invalidate(p_table);
END;
$$;

CREATE FUNCTION temporal.set_combine_interval(p_table regclass, p_interval interval)
RETURNS void
LANGUAGE plpgsql
AS $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM temporal.tracked_tables WHERE table_oid = p_table) THEN
        RAISE EXCEPTION 'temporal.set_combine_interval: "%" is not tracked', p_table;
    END IF;
    IF p_interval < interval '0'
       OR EXTRACT(YEAR FROM p_interval) <> 0
       OR EXTRACT(MONTH FROM p_interval) <> 0 THEN
        RAISE EXCEPTION 'temporal.set_combine_interval: interval must be a non-negative day/time interval (no months/years)';
    END IF;

    UPDATE temporal.tracked_tables SET combine_interval = p_interval WHERE table_oid = p_table;
    PERFORM temporal.invalidate(p_table);
END;
$$;

-- ─────────────────────────────────────────────────────────────────────────────
-- Query-level AS OF marker
-- ─────────────────────────────────────────────────────────────────────────────

-- Usage: SELECT ... FROM tracked t JOIN other o ON ... WHERE temporal.as_of('2026-06-01');
-- The planner hook removes the marker and time-travels the ENTIRE statement
-- to the given moment — a per-query alternative to SET temporal.as_of.
-- The body only runs when the hook did not intercept it (extension missing
-- from shared_preload_libraries), where silently returning present-day data
-- would be a correctness bug — so it fails loudly instead.
CREATE FUNCTION temporal.as_of(p_ts timestamptz)
RETURNS boolean
LANGUAGE plpgsql IMMUTABLE
AS $$
BEGIN
    RAISE EXCEPTION 'temporal.as_of() was not intercepted by pg_temporal_tables'
        USING HINT = 'Add pg_temporal_tables to shared_preload_libraries and restart PostgreSQL.';
END;
$$;

COMMENT ON FUNCTION temporal.as_of(timestamptz) IS
    'Query-level time travel marker: WHERE temporal.as_of(''2026-06-01'') makes the whole statement read that moment.';

-- ─────────────────────────────────────────────────────────────────────────────
-- History viewer & analytics
-- ─────────────────────────────────────────────────────────────────────────────

CREATE FUNCTION temporal.mod_date(rec anyelement)
RETURNS timestamptz
AS 'MODULE_PATHNAME', 'temporal_mod_date'
LANGUAGE C STRICT STABLE;

COMMENT ON FUNCTION temporal.mod_date(anyelement) IS
    'When this row version was made. Works on rows of tracked tables, history tables and versions views.';

CREATE FUNCTION temporal.mod_user(rec anyelement)
RETURNS text
AS 'MODULE_PATHNAME', 'temporal_mod_user'
LANGUAGE C STRICT STABLE;

COMMENT ON FUNCTION temporal.mod_user(anyelement) IS
    'Who made this row version (temporal.user_id at write time). Works on rows of tracked tables, history tables and versions views.';

-- Resolve base/history/versions-view regclass to the tracked_tables row.
CREATE FUNCTION temporal._resolve_tracked(p_rel regclass)
RETURNS temporal.tracked_tables
LANGUAGE plpgsql STABLE
AS $$
DECLARE
    t temporal.tracked_tables;
BEGIN
    SELECT * INTO t FROM temporal.tracked_tables
    WHERE table_oid = p_rel OR history_table_oid = p_rel OR versions_view_oid = p_rel;
    IF t.table_oid IS NULL THEN
        RAISE EXCEPTION '"%" is not a temporal-tracked table (nor its history table or versions view)', p_rel;
    END IF;
    RETURN t;
END;
$$;

CREATE FUNCTION temporal._reject_as_of()
RETURNS void
LANGUAGE plpgsql STABLE
AS $$
BEGIN
    IF COALESCE(current_setting('temporal.as_of', true), '') <> '' THEN
        RAISE EXCEPTION 'temporal viewer functions cannot run while temporal.as_of is set'
            USING HINT = 'RESET temporal.as_of first; the functions always read the full history.';
    END IF;
END;
$$;

-- Change events (INSERT/UPDATE/DELETE) for rows of a tracked table within
-- [p_from, p_to). old_row/new_row contain the application columns (managed
-- and excluded columns removed). p_pk filters by primary key, e.g. '{"id": 1}'.
CREATE FUNCTION temporal.changes(
    p_table regclass,
    p_from  timestamptz,
    p_to    timestamptz,
    p_pk    jsonb DEFAULT NULL
) RETURNS TABLE (
    pk              jsonb,
    changed_at      timestamptz,
    changed_by      text,
    operation       text,
    old_row         jsonb,
    new_row         jsonb,
    changed_columns text[]
)
LANGUAGE plpgsql STABLE
AS $$
DECLARE
    t         temporal.tracked_tables;
    v_strip   text[];
    v_pkcols  text[];
    v_partexpr text;
    v_filter  text;
BEGIN
    PERFORM temporal._reject_as_of();
    t := temporal._resolve_tracked(p_table);

    v_strip := ARRAY['valid_from', 'changed_by', 'valid_to', 'deleted_by', 'is_current', 'temporal_row_id']
               || COALESCE((SELECT array_agg(c::text) FROM unnest(t.excluded_columns) c), '{}');

    -- With lineage tracking, partition by the stable row id so a primary-key
    -- change reads as one continuous chain (UPDATE) rather than delete+insert.
    IF t.track_lineage THEN
        v_partexpr := 'v.temporal_row_id';
        v_filter := format(
            '($3::jsonb IS NULL OR v.temporal_row_id IN (SELECT w.temporal_row_id FROM %s w WHERE to_jsonb(w) @> $3))',
            t.versions_view_oid::text);
    ELSE
        v_partexpr := '(SELECT jsonb_object_agg(c, to_jsonb(v) -> c) FROM unnest($5) c)';
        v_filter := '($3::jsonb IS NULL OR to_jsonb(v) @> $3)';
    END IF;

    SELECT array_agg(a.attname::text ORDER BY o.ord) INTO v_pkcols
    FROM pg_constraint con
    CROSS JOIN LATERAL unnest(con.conkey) WITH ORDINALITY AS o(attnum, ord)
    JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = o.attnum
    WHERE con.conrelid = t.table_oid AND con.contype = 'p';

    RETURN QUERY EXECUTE format($sql$
        WITH versions AS (
            SELECT to_jsonb(v) - $4 AS row_data,
                   (SELECT jsonb_object_agg(c, to_jsonb(v) -> c) FROM unnest($5) c) AS pk,
                   %2$s AS part,
                   v.valid_from, v.valid_to, v.changed_by, v.deleted_by, v.is_current
            FROM %1$s v
            WHERE %3$s
        ), ordered AS (
            SELECT *,
                   LAG(row_data)    OVER w AS prev_row,
                   LAG(valid_to)    OVER w AS prev_valid_to,
                   LEAD(valid_from) OVER w AS next_valid_from
            FROM versions
            WINDOW w AS (PARTITION BY part ORDER BY valid_from)
        )
        SELECT pk,
               valid_from AS changed_at,
               changed_by,
               CASE WHEN prev_valid_to = valid_from THEN 'UPDATE' ELSE 'INSERT' END,
               CASE WHEN prev_valid_to = valid_from THEN prev_row END,
               row_data,
               CASE WHEN prev_valid_to = valid_from THEN
                   (SELECT array_agg(key ORDER BY key)
                    FROM (SELECT COALESCE(n.key, o.key) AS key, n.value AS nv, o.value AS ov
                          FROM jsonb_each(row_data) n
                          FULL JOIN jsonb_each(prev_row) o ON o.key = n.key) d
                    WHERE d.nv IS DISTINCT FROM d.ov)
               END
        FROM ordered
        WHERE valid_from >= $1 AND valid_from < $2
        UNION ALL
        SELECT pk, valid_to, deleted_by, 'DELETE', row_data, NULL, NULL
        FROM ordered
        WHERE NOT is_current
          AND (next_valid_from IS NULL OR next_valid_from <> valid_to)
          AND valid_to >= $1 AND valid_to < $2
        ORDER BY changed_at
        $sql$, t.versions_view_oid::text, v_partexpr, v_filter)
    USING p_from, p_to, p_pk, v_strip, v_pkcols;
END;
$$;

-- Value timeline of one column of one row: only the versions where the value
-- actually changed, up to p_until (NULL = everything).
CREATE FUNCTION temporal.column_history(
    p_table  regclass,
    p_pk     jsonb,
    p_column name,
    p_until  timestamptz DEFAULT NULL
) RETURNS TABLE (
    changed_at timestamptz,
    changed_by text,
    value      jsonb
)
LANGUAGE plpgsql STABLE
AS $$
DECLARE
    t        temporal.tracked_tables;
    v_filter text;
BEGIN
    PERFORM temporal._reject_as_of();
    t := temporal._resolve_tracked(p_table);

    IF p_column = ANY (t.excluded_columns) THEN
        RAISE EXCEPTION 'column "%" is excluded from history tracking on %', p_column, t.table_oid
            USING HINT = 'Old values of excluded columns were never captured.';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_attribute
                   WHERE attrelid = t.table_oid AND attname = p_column
                     AND attnum > 0 AND NOT attisdropped) THEN
        RAISE EXCEPTION 'column "%" does not exist on %', p_column, t.table_oid;
    END IF;

    -- With lineage, follow the row across primary-key changes by id.
    IF t.track_lineage THEN
        v_filter := format(
            'v.temporal_row_id IN (SELECT w.temporal_row_id FROM %s w WHERE to_jsonb(w) @> $1)',
            t.versions_view_oid::text);
    ELSE
        v_filter := 'to_jsonb(v) @> $1';
    END IF;

    RETURN QUERY EXECUTE format($sql$
        WITH versions AS (
            SELECT v.valid_from, v.changed_by, to_jsonb(v) -> $2 AS val
            FROM %1$s v
            WHERE %2$s
              AND ($3::timestamptz IS NULL OR v.valid_from <= $3)
        )
        SELECT valid_from, changed_by, val
        FROM (SELECT *, LAG(val) OVER (ORDER BY valid_from) AS prev_val,
                     row_number() OVER (ORDER BY valid_from) AS rn
              FROM versions) x
        WHERE rn = 1 OR val IS DISTINCT FROM prev_val
        ORDER BY valid_from
        $sql$, t.versions_view_oid::text, v_filter)
    USING p_pk, p_column::text, p_until;
END;
$$;

-- ─────────────────────────────────────────────────────────────────────────────
-- Maintenance: stats, restore, compression, compaction
-- ─────────────────────────────────────────────────────────────────────────────

-- Set the column compression method (pglz / lz4 / default) on every varlena
-- column of the history table. Only affects values stored after the change;
-- existing history keeps its prior compression until rewritten.
CREATE FUNCTION temporal.set_history_compression(p_table regclass, p_method text)
RETURNS void
LANGUAGE plpgsql
AS $$
DECLARE
    t   temporal.tracked_tables;
    r   record;
BEGIN
    t := temporal._resolve_tracked(p_table);
    IF lower(p_method) NOT IN ('pglz', 'lz4', 'default') THEN
        RAISE EXCEPTION 'temporal.set_history_compression: method must be pglz, lz4, or default';
    END IF;

    FOR r IN
        SELECT a.attname
        FROM pg_attribute a
        JOIN pg_type ty ON ty.oid = a.atttypid
        WHERE a.attrelid = t.history_table_oid AND a.attnum > 0 AND NOT a.attisdropped
          AND ty.typlen = -1 AND ty.typstorage IN ('x', 'e', 'm')
    LOOP
        EXECUTE format('ALTER TABLE %s ALTER COLUMN %I SET COMPRESSION %s',
                       t.history_table_oid::text, r.attname, lower(p_method));
    END LOOP;
END;
$$;

-- Storage / version-count summary for a tracked table. Accepts the base table,
-- its history table, or its versions view.
CREATE FUNCTION temporal.table_stats(p_table regclass)
RETURNS TABLE (
    base_rows          bigint,
    history_rows       bigint,
    version_count      bigint,
    base_size_bytes    bigint,
    history_size_bytes bigint,
    oldest_valid_from  timestamptz,
    newest_valid_from  timestamptz,
    avg_chain_length   numeric
)
LANGUAGE plpgsql STABLE
AS $$
DECLARE
    t        temporal.tracked_tables;
    v_base   bigint;
    v_hist   bigint;
    v_oldest timestamptz;
    v_newest timestamptz;
    h_oldest timestamptz;
    h_newest timestamptz;
BEGIN
    PERFORM temporal._reject_as_of();
    t := temporal._resolve_tracked(p_table);

    EXECUTE format('SELECT count(*), min(valid_from), max(valid_from) FROM %s',
                   t.table_oid::text)
        INTO v_base, v_oldest, v_newest;
    EXECUTE format('SELECT count(*), min(valid_from), max(valid_from) FROM %s',
                   t.history_table_oid::text)
        INTO v_hist, h_oldest, h_newest;

    RETURN QUERY SELECT
        v_base,
        v_hist,
        v_base + v_hist,
        pg_total_relation_size(t.table_oid),
        pg_total_relation_size(t.history_table_oid),
        LEAST(v_oldest, h_oldest),
        GREATEST(v_newest, h_newest),
        CASE WHEN v_base > 0
             THEN round((v_base + v_hist)::numeric / v_base, 2) END;
END;
$$;

-- Roll a row back to the state it had at p_as_of (UPSERT through the normal
-- triggers, so the restore itself becomes a new audited current version).
-- p_pk filters by primary key, e.g. '{"id": 1}'.
CREATE FUNCTION temporal.restore(p_table regclass, p_pk jsonb, p_as_of timestamptz)
RETURNS boolean
LANGUAGE plpgsql
AS $$
DECLARE
    t         temporal.tracked_tables;
    v_pkcols  text[];
    v_appcols text[];
    v_setlist text;
    v_pklist  text;
    v_row     jsonb;
    v_strip   text[] := ARRAY['valid_from','changed_by','valid_to','deleted_by','is_current','temporal_row_id'];
BEGIN
    PERFORM temporal._reject_as_of();
    t := temporal._resolve_tracked(p_table);
    IF p_table <> t.table_oid THEN
        RAISE EXCEPTION 'temporal.restore: pass the tracked base table (not its history table or versions view)';
    END IF;

    SELECT array_agg(a.attname::text ORDER BY o.ord) INTO v_pkcols
    FROM pg_constraint con
    CROSS JOIN LATERAL unnest(con.conkey) WITH ORDINALITY AS o(attnum, ord)
    JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = o.attnum
    WHERE con.conrelid = t.table_oid AND con.contype = 'p';

    SELECT array_agg(attname::text ORDER BY attnum) INTO v_appcols
    FROM pg_attribute
    WHERE attrelid = t.table_oid AND attnum > 0 AND NOT attisdropped
      AND attname NOT IN ('valid_from','changed_by','temporal_row_id')
      AND attname <> ALL (v_pkcols);

    -- the version that was current at p_as_of
    EXECUTE format($q$
        SELECT to_jsonb(v) - $3
        FROM %s v
        WHERE to_jsonb(v) @> $1
          AND v.valid_from <= $2
          AND (v.is_current OR v.valid_to > $2)
        ORDER BY v.valid_from DESC
        LIMIT 1
        $q$, t.versions_view_oid::text)
    INTO v_row USING p_pk, p_as_of, v_strip;

    IF v_row IS NULL THEN
        RAISE EXCEPTION 'temporal.restore: no version of % existed at %', p_pk, p_as_of;
    END IF;

    v_pklist := (SELECT string_agg(quote_ident(c), ', ') FROM unnest(v_pkcols) c);

    IF v_appcols IS NULL THEN
        EXECUTE format(
            'INSERT INTO %1$s SELECT (jsonb_populate_record(NULL::%1$s, $1)).* '
            'ON CONFLICT (%2$s) DO NOTHING',
            t.table_oid::text, v_pklist)
        USING v_row;
    ELSE
        SELECT string_agg(format('%I = EXCLUDED.%I', c, c), ', ') INTO v_setlist
        FROM unnest(v_appcols) c;
        EXECUTE format(
            'INSERT INTO %1$s SELECT (jsonb_populate_record(NULL::%1$s, $1)).* '
            'ON CONFLICT (%2$s) DO UPDATE SET %3$s',
            t.table_oid::text, v_pklist, v_setlist)
        USING v_row;
    END IF;

    RETURN true;
END;
$$;

-- Re-insert the last version of a row that is currently deleted.
CREATE FUNCTION temporal.restore_deleted(p_table regclass, p_pk jsonb)
RETURNS boolean
LANGUAGE plpgsql
AS $$
DECLARE
    t       temporal.tracked_tables;
    v_row   jsonb;
    v_exists boolean;
    -- temporal_row_id is intentionally NOT stripped: restoring a deleted row is
    -- an explicit resurrection of the same object, so it keeps its original
    -- lineage id (the explicit value overrides the sequence default on insert).
    v_strip text[] := ARRAY['valid_from','changed_by','valid_to','deleted_by','is_current'];
BEGIN
    PERFORM temporal._reject_as_of();
    t := temporal._resolve_tracked(p_table);
    IF p_table <> t.table_oid THEN
        RAISE EXCEPTION 'temporal.restore_deleted: pass the tracked base table';
    END IF;

    EXECUTE format('SELECT EXISTS (SELECT 1 FROM %s v WHERE to_jsonb(v) @> $1)',
                   t.table_oid::text)
        INTO v_exists USING p_pk;
    IF v_exists THEN
        RAISE EXCEPTION 'temporal.restore_deleted: % is not deleted (still present)', p_pk;
    END IF;

    EXECUTE format($q$
        SELECT to_jsonb(v) - $2
        FROM %s v
        WHERE to_jsonb(v) @> $1
        ORDER BY v.valid_from DESC
        LIMIT 1
        $q$, t.versions_view_oid::text)
    INTO v_row USING p_pk, v_strip;

    IF v_row IS NULL THEN
        RAISE EXCEPTION 'temporal.restore_deleted: no history found for %', p_pk;
    END IF;

    EXECUTE format('INSERT INTO %1$s SELECT (jsonb_populate_record(NULL::%1$s, $1)).*',
                   t.table_oid::text)
    USING v_row;

    RETURN true;
END;
$$;

-- Merge adjacent, contiguous history versions that are equal on all comparable
-- columns (managed, excluded, and dropped columns ignored). The earliest row of
-- each run survives with its valid_to extended over the run; the rest are
-- deleted. Equality uses the column types' own operators (IS DISTINCT FROM).
-- Called through the temporal.compact_history() C wrapper, which sets the
-- internal-write flag so the history protect trigger allows the writes.
CREATE FUNCTION temporal._compact_history_impl(p_table regclass)
RETURNS bigint
LANGUAGE plpgsql
AS $$
DECLARE
    t         temporal.tracked_tables;
    v_hist    text;
    v_pkcols  name[];
    v_cmpcols name[];
    v_pklist  text;
    v_cmprow  text;
    v_deleted bigint := 0;
BEGIN
    t := temporal._resolve_tracked(p_table);
    v_hist := t.history_table_oid::text;

    SELECT array_agg(a.attname ORDER BY o.ord) INTO v_pkcols
    FROM pg_constraint con
    CROSS JOIN LATERAL unnest(con.conkey) WITH ORDINALITY AS o(attnum, ord)
    JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = o.attnum
    WHERE con.conrelid = t.table_oid AND con.contype = 'p';

    -- comparable columns: history columns that are still application columns of
    -- the base table, minus managed, excluded, and PK columns.
    SELECT array_agg(h.attname ORDER BY h.attnum) INTO v_cmpcols
    FROM pg_attribute h
    WHERE h.attrelid = t.history_table_oid AND h.attnum > 0 AND NOT h.attisdropped
      AND h.attname NOT IN ('valid_from','changed_by','valid_to','deleted_by','temporal_row_id')
      AND h.attname <> ALL (v_pkcols)
      AND NOT (h.attname = ANY (t.excluded_columns))
      AND EXISTS (SELECT 1 FROM pg_attribute b
                  WHERE b.attrelid = t.table_oid AND b.attname = h.attname
                    AND b.attnum > 0 AND NOT b.attisdropped);

    v_pklist := (SELECT string_agg(quote_ident(c), ', ') FROM unnest(v_pkcols) c);
    IF v_cmpcols IS NULL THEN
        -- no comparable columns left: every contiguous version is "equal"
        v_cmprow := 'NULL::boolean';
    ELSE
        v_cmprow := 'ROW(' || (SELECT string_agg(quote_ident(c), ', ')
                               FROM unnest(v_cmpcols) c) || ')';
    END IF;

    EXECUTE format($q$
        WITH ordered AS (
            SELECT ctid, %3$s, valid_from, valid_to,
                   %2$s AS r,
                   LAG(valid_to) OVER w AS prev_to,
                   LAG(%2$s)     OVER w AS prev_r
            FROM %1$s
            WINDOW w AS (PARTITION BY %3$s ORDER BY valid_from)
        ),
        marked AS (
            SELECT ctid, %3$s, valid_from, valid_to,
                   SUM(CASE WHEN prev_to IS NULL
                                 OR prev_to <> valid_from
                                 OR r IS DISTINCT FROM prev_r
                            THEN 1 ELSE 0 END)
                       OVER (PARTITION BY %3$s ORDER BY valid_from
                             ROWS UNBOUNDED PRECEDING) AS grp
            FROM ordered
        ),
        isl AS (
            SELECT ctid, valid_to,
                   first_value(ctid) OVER (PARTITION BY %3$s, grp ORDER BY valid_from
                       ROWS BETWEEN UNBOUNDED PRECEDING AND UNBOUNDED FOLLOWING) AS keep_ctid,
                   max(valid_to) OVER (PARTITION BY %3$s, grp) AS new_to
            FROM marked
        ),
        upd AS (
            UPDATE %1$s h SET valid_to = i.new_to
            FROM isl i
            WHERE h.ctid = i.keep_ctid AND h.valid_to <> i.new_to
            RETURNING 1
        ),
        del AS (
            DELETE FROM %1$s h
            USING isl i
            WHERE h.ctid = i.ctid AND i.ctid <> i.keep_ctid
            RETURNING 1
        )
        SELECT (SELECT count(*) FROM del)
        $q$, v_hist, v_cmprow, v_pklist)
    INTO v_deleted;

    PERFORM temporal.invalidate(t.table_oid);
    RETURN v_deleted;
END;
$$;

CREATE FUNCTION temporal.compact_history(p_table regclass)
RETURNS bigint
AS 'MODULE_PATHNAME', 'temporal_compact_history'
LANGUAGE C STRICT VOLATILE;

COMMENT ON FUNCTION temporal.compact_history(regclass) IS
    'Merge redundant adjacent history versions (e.g. after dropping a column). Returns rows merged away. Scan-heavy; run off-peak.';

-- Retention. When the history table is RANGE-partitioned on valid_to, drop any
-- partition whose whole range ended before the cutoff (fast path), then prune
-- the straddling/remaining rows. For a plain history table this is just prune.
CREATE FUNCTION temporal.drop_history_before(p_table regclass, p_cutoff timestamptz)
RETURNS bigint
LANGUAGE plpgsql
AS $$
DECLARE
    t        temporal.tracked_tables;
    v_kind   "char";
    r        record;
    v_to     timestamptz;
    v_dropped int := 0;
BEGIN
    PERFORM temporal._reject_as_of();
    t := temporal._resolve_tracked(p_table);

    SELECT relkind INTO v_kind FROM pg_class WHERE oid = t.history_table_oid;

    IF v_kind = 'p' THEN
        FOR r IN
            SELECT c.oid::regclass AS part,
                   pg_get_expr(c.relpartbound, c.oid) AS bound
            FROM pg_inherits i
            JOIN pg_class c ON c.oid = i.inhrelid
            WHERE i.inhparent = t.history_table_oid
        LOOP
            CONTINUE WHEN r.bound IS NULL OR r.bound = 'DEFAULT';
            BEGIN
                -- upper bound of a RANGE partition: ... TO ('<value>')
                v_to := substring(r.bound FROM $re$TO \('([^']*)'\)$re$)::timestamptz;
            EXCEPTION WHEN others THEN
                v_to := NULL;       -- MAXVALUE or a non-timestamptz key
            END;
            IF v_to IS NOT NULL AND v_to <= p_cutoff THEN
                EXECUTE format('DROP TABLE %s', r.part::text);
                v_dropped := v_dropped + 1;
            END IF;
        END LOOP;
        IF v_dropped > 0 THEN
            RAISE NOTICE 'temporal: dropped % history partition(s) entirely older than %', v_dropped, p_cutoff;
        END IF;
    END IF;

    -- prune any rows in straddling/default partitions (the whole job for a
    -- plain history table). Returns the per-row deletions.
    RETURN temporal.prune(t.table_oid, p_cutoff);
END;
$$;

COMMENT ON FUNCTION temporal.drop_history_before(regclass, timestamptz) IS
    'Retention: drop whole history partitions older than the cutoff (if partitioned by valid_to), then prune the remainder. Returns rows pruned.';

-- ─────────────────────────────────────────────────────────────────────────────
-- Retention & compaction policies (scheduler-agnostic)
-- ─────────────────────────────────────────────────────────────────────────────
-- Declarative policies executed by temporal.run_due_policies(), which an
-- external scheduler (e.g. pg_cron) calls periodically. There is no background
-- worker: the engine is plain SQL, the schedule is whatever calls it.

CREATE TABLE temporal.policies (
    table_oid    regclass NOT NULL REFERENCES temporal.tracked_tables ON DELETE CASCADE,
    kind         text     NOT NULL CHECK (kind IN ('retention', 'compaction')),
    older_than   interval,                       -- retention only: drop history older than this
    run_interval interval NOT NULL DEFAULT '1 day',
    last_run     timestamptz,
    enabled      boolean  NOT NULL DEFAULT true,
    PRIMARY KEY (table_oid, kind)
);
GRANT SELECT ON temporal.policies TO PUBLIC;
SELECT pg_catalog.pg_extension_config_dump('temporal.policies', '');

CREATE FUNCTION temporal.add_retention_policy(
    p_table      regclass,
    p_drop_after interval,
    p_run_every  interval DEFAULT '1 day'
) RETURNS void
LANGUAGE plpgsql
AS $$
DECLARE
    t temporal.tracked_tables;
BEGIN
    t := temporal._resolve_tracked(p_table);
    IF p_drop_after < interval '0' THEN
        RAISE EXCEPTION 'temporal.add_retention_policy: drop_after must be non-negative';
    END IF;
    INSERT INTO temporal.policies (table_oid, kind, older_than, run_interval)
    VALUES (t.table_oid, 'retention', p_drop_after, p_run_every)
    ON CONFLICT (table_oid, kind) DO UPDATE
        SET older_than = EXCLUDED.older_than,
            run_interval = EXCLUDED.run_interval,
            enabled = true;
END;
$$;

CREATE FUNCTION temporal.add_compaction_policy(
    p_table     regclass,
    p_run_every interval DEFAULT '1 day'
) RETURNS void
LANGUAGE plpgsql
AS $$
DECLARE
    t temporal.tracked_tables;
BEGIN
    t := temporal._resolve_tracked(p_table);
    INSERT INTO temporal.policies (table_oid, kind, run_interval)
    VALUES (t.table_oid, 'compaction', p_run_every)
    ON CONFLICT (table_oid, kind) DO UPDATE
        SET run_interval = EXCLUDED.run_interval,
            enabled = true;
END;
$$;

CREATE FUNCTION temporal.remove_retention_policy(p_table regclass)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE t temporal.tracked_tables;
BEGIN
    t := temporal._resolve_tracked(p_table);
    DELETE FROM temporal.policies WHERE table_oid = t.table_oid AND kind = 'retention';
END; $$;

CREATE FUNCTION temporal.remove_compaction_policy(p_table regclass)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE t temporal.tracked_tables;
BEGIN
    t := temporal._resolve_tracked(p_table);
    DELETE FROM temporal.policies WHERE table_oid = t.table_oid AND kind = 'compaction';
END; $$;

-- Run every due policy. Idempotent and safe to call from any scheduler. Skips a
-- table whose policies another session is already running (advisory lock), and
-- stops once the time budget is exhausted (resumes on the next call). Writes, so
-- it is a no-op on a read-only standby.
CREATE FUNCTION temporal.run_due_policies(p_max_duration interval DEFAULT '5 minutes')
RETURNS integer
LANGUAGE plpgsql
AS $$
DECLARE
    r       record;
    v_start timestamptz := clock_timestamp();
    v_ran   integer := 0;
BEGIN
    PERFORM temporal._reject_as_of();

    FOR r IN
        SELECT * FROM temporal.policies
        WHERE enabled
          AND (last_run IS NULL OR clock_timestamp() - last_run >= run_interval)
        ORDER BY last_run NULLS FIRST, table_oid
    LOOP
        EXIT WHEN clock_timestamp() - v_start > p_max_duration;
        CONTINUE WHEN NOT pg_try_advisory_xact_lock(
            hashtext('temporal.policy:' || r.table_oid::text)::bigint);

        IF r.kind = 'retention' THEN
            PERFORM temporal.drop_history_before(r.table_oid, now() - r.older_than);
        ELSIF r.kind = 'compaction' THEN
            PERFORM temporal.compact_history(r.table_oid);
        END IF;

        UPDATE temporal.policies SET last_run = clock_timestamp()
        WHERE table_oid = r.table_oid AND kind = r.kind;
        v_ran := v_ran + 1;
    END LOOP;

    RETURN v_ran;
END;
$$;

COMMENT ON FUNCTION temporal.run_due_policies(interval) IS
    'Execute all due retention/compaction policies. Call periodically from pg_cron or any scheduler.';

-- ─────────────────────────────────────────────────────────────────────────────
-- Managed schema evolution
-- ─────────────────────────────────────────────────────────────────────────────
-- These keep the base table, its history table and the versions view in sync.
-- DROP/ALTER TYPE collide with the versions-view dependency if done raw, so the
-- managed functions drop and rebuild the view around the change. Raw RENAME is
-- harmless and is auto-propagated by the ddl_command_end event trigger.

CREATE FUNCTION temporal.rename_column(p_table regclass, p_old name, p_new name)
RETURNS void
LANGUAGE plpgsql
AS $$
DECLARE
    t         temporal.tracked_tables;
    v_schema  name;
    v_view    name;
    v_newview regclass;
BEGIN
    t := temporal._resolve_tracked(p_table);
    IF p_table <> t.table_oid THEN
        RAISE EXCEPTION 'temporal.rename_column: pass the base table';
    END IF;
    IF p_old IN ('valid_from','changed_by') THEN
        RAISE EXCEPTION 'temporal.rename_column: cannot rename managed column "%"', p_old;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = t.table_oid
                   AND attname = p_old AND attnum > 0 AND NOT attisdropped) THEN
        RAISE EXCEPTION 'temporal.rename_column: column "%" does not exist on %', p_old, t.table_oid;
    END IF;

    SELECT n.nspname, c.relname INTO v_schema, v_view
    FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE c.oid = t.versions_view_oid;

    PERFORM set_config('temporal.internal_ddl', 'on', true);
    EXECUTE format('ALTER TABLE %s RENAME COLUMN %I TO %I', t.table_oid::text, p_old, p_new);
    EXECUTE format('ALTER TABLE %s RENAME COLUMN %I TO %I', t.history_table_oid::text, p_old, p_new);
    EXECUTE format('DROP VIEW %s', t.versions_view_oid::text);
    v_newview := temporal._create_versions_view(t.table_oid, t.history_table_oid, v_view, v_schema);
    PERFORM temporal._copy_select_grants(t.table_oid, v_newview);
    PERFORM set_config('temporal.internal_ddl', 'off', true);

    UPDATE temporal.tracked_tables
       SET versions_view_oid = v_newview,
           excluded_columns  = array_replace(excluded_columns, p_old, p_new)
     WHERE table_oid = t.table_oid;
    PERFORM temporal.invalidate(t.table_oid);
END;
$$;

CREATE FUNCTION temporal.drop_column(
    p_table             regclass,
    p_column            name,
    p_drop_from_history boolean DEFAULT false,
    p_compact           boolean DEFAULT false
) RETURNS void
LANGUAGE plpgsql
AS $$
DECLARE
    t         temporal.tracked_tables;
    v_schema  name;
    v_view    name;
    v_pkcols  name[];
    v_newview regclass;
BEGIN
    t := temporal._resolve_tracked(p_table);
    IF p_table <> t.table_oid THEN
        RAISE EXCEPTION 'temporal.drop_column: pass the base table';
    END IF;
    IF p_column IN ('valid_from','changed_by') THEN
        RAISE EXCEPTION 'temporal.drop_column: cannot drop managed column "%"', p_column;
    END IF;

    SELECT array_agg(a.attname) INTO v_pkcols
    FROM pg_constraint con
    JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = ANY (con.conkey)
    WHERE con.conrelid = t.table_oid AND con.contype = 'p';
    IF p_column = ANY (v_pkcols) THEN
        RAISE EXCEPTION 'temporal.drop_column: cannot drop primary key column "%"', p_column;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = t.table_oid
                   AND attname = p_column AND attnum > 0 AND NOT attisdropped) THEN
        RAISE EXCEPTION 'temporal.drop_column: column "%" does not exist on %', p_column, t.table_oid;
    END IF;

    SELECT n.nspname, c.relname INTO v_schema, v_view
    FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE c.oid = t.versions_view_oid;

    PERFORM set_config('temporal.internal_ddl', 'on', true);
    EXECUTE format('DROP VIEW %s', t.versions_view_oid::text);
    EXECUTE format('ALTER TABLE %s DROP COLUMN %I', t.table_oid::text, p_column);
    IF p_drop_from_history THEN
        EXECUTE format('ALTER TABLE %s DROP COLUMN IF EXISTS %I', t.history_table_oid::text, p_column);
    END IF;
    v_newview := temporal._create_versions_view(t.table_oid, t.history_table_oid, v_view, v_schema);
    PERFORM temporal._copy_select_grants(t.table_oid, v_newview);
    PERFORM set_config('temporal.internal_ddl', 'off', true);

    -- a dropped column may have taken indexes (and their mirrors) with it
    DELETE FROM temporal.mirrored_indexes m
     WHERE m.table_oid = t.table_oid
       AND (NOT EXISTS (SELECT 1 FROM pg_class WHERE oid = m.base_index_oid)
            OR NOT EXISTS (SELECT 1 FROM pg_class WHERE oid = m.history_index_oid));

    UPDATE temporal.tracked_tables
       SET versions_view_oid = v_newview,
           excluded_columns  = array_remove(excluded_columns, p_column)
     WHERE table_oid = t.table_oid;
    PERFORM temporal.invalidate(t.table_oid);

    IF p_compact THEN
        PERFORM temporal.compact_history(t.table_oid);
    END IF;
END;
$$;

CREATE FUNCTION temporal.alter_column_type(
    p_table  regclass,
    p_column name,
    p_type   text,
    p_using  text DEFAULT NULL
) RETURNS void
LANGUAGE plpgsql
AS $$
DECLARE
    t         temporal.tracked_tables;
    v_schema  name;
    v_view    name;
    v_using   text;
    v_newview regclass;
BEGIN
    t := temporal._resolve_tracked(p_table);
    IF p_table <> t.table_oid THEN
        RAISE EXCEPTION 'temporal.alter_column_type: pass the base table';
    END IF;
    IF p_column IN ('valid_from','changed_by') THEN
        RAISE EXCEPTION 'temporal.alter_column_type: cannot change type of managed column "%"', p_column;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_attribute WHERE attrelid = t.table_oid
                   AND attname = p_column AND attnum > 0 AND NOT attisdropped) THEN
        RAISE EXCEPTION 'temporal.alter_column_type: column "%" does not exist on %', p_column, t.table_oid;
    END IF;

    v_using := CASE WHEN p_using IS NULL THEN '' ELSE ' USING ' || p_using END;

    SELECT n.nspname, c.relname INTO v_schema, v_view
    FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
    WHERE c.oid = t.versions_view_oid;

    PERFORM set_config('temporal.internal_ddl', 'on', true);
    EXECUTE format('DROP VIEW %s', t.versions_view_oid::text);
    EXECUTE format('ALTER TABLE %s ALTER COLUMN %I TYPE %s%s',
                   t.table_oid::text, p_column, p_type, v_using);
    EXECUTE format('ALTER TABLE %s ALTER COLUMN %I TYPE %s%s',
                   t.history_table_oid::text, p_column, p_type, v_using);
    v_newview := temporal._create_versions_view(t.table_oid, t.history_table_oid, v_view, v_schema);
    PERFORM temporal._copy_select_grants(t.table_oid, v_newview);
    PERFORM set_config('temporal.internal_ddl', 'off', true);

    UPDATE temporal.tracked_tables SET versions_view_oid = v_newview
     WHERE table_oid = t.table_oid;
    PERFORM temporal.invalidate(t.table_oid);
END;
$$;

-- ─────────────────────────────────────────────────────────────────────────────
-- DDL safety (event triggers)
-- ─────────────────────────────────────────────────────────────────────────────

CREATE FUNCTION temporal._assert_managed_triggers_intact(p_table oid, p_history oid)
RETURNS void
LANGUAGE plpgsql
AS $$
DECLARE
    v_bad text;
BEGIN
    WITH expected(relid, tgname) AS (
        VALUES
            (p_table,   'temporal_stamp'),
            (p_table,   'temporal_history'),
            (p_table,   'temporal_truncate'),
            (p_history, 'temporal_protect'),
            (p_history, 'temporal_protect_truncate')
    )
    SELECT string_agg(format('%I on %s', e.tgname, e.relid::regclass), ', ' ORDER BY e.relid::regclass::text, e.tgname)
      INTO v_bad
    FROM expected e
    LEFT JOIN pg_trigger trg ON trg.tgrelid = e.relid
                            AND trg.tgname = e.tgname
                            AND NOT trg.tgisinternal
    WHERE trg.oid IS NULL
       OR trg.tgenabled <> 'A';

    IF v_bad IS NOT NULL THEN
        RAISE EXCEPTION 'cannot disable, drop, or reconfigure temporal trigger(s): %', v_bad
            USING HINT = 'Call temporal.disable() on the tracked table first.';
    END IF;
END;
$$;

-- sql_drop: protect history tables, versions views and managed columns;
-- clean up the catalog when a tracked base table is dropped; keep the
-- mirrored-index mapping in sync with DROP INDEX.
CREATE FUNCTION temporal.on_sql_drop()
RETURNS event_trigger
LANGUAGE plpgsql
AS $$
DECLARE
    obj record;
    v_mirror text;
    v_relation_identity text;
    v_relation_oid oid;
    v_trigger_name text;
BEGIN
    -- During DROP EXTENSION (or restore) our catalog may already be gone.
    IF to_regclass('temporal.tracked_tables') IS NULL THEN
        RETURN;
    END IF;
    -- The extension rebuilding its own objects (e.g. versions view) is fine.
    IF current_setting('temporal.internal_ddl', true) = 'on' THEN
        RETURN;
    END IF;

    -- Pass 1: tracked base tables being dropped → release their bookkeeping
    -- first, so the cascade-dropped versions view passes the checks below.
    FOR obj IN SELECT * FROM pg_event_trigger_dropped_objects()
               WHERE classid = 'pg_class'::regclass AND objsubid = 0
    LOOP
        IF EXISTS (SELECT 1 FROM temporal.tracked_tables WHERE table_oid = obj.objid) THEN
            DELETE FROM temporal.mirrored_indexes WHERE table_oid = obj.objid;
            DELETE FROM temporal.tracked_tables WHERE table_oid = obj.objid;
            RAISE NOTICE 'temporal: table % was dropped; its history table is kept and no longer protected',
                obj.object_identity;
        END IF;
    END LOOP;

    FOR obj IN SELECT * FROM pg_event_trigger_dropped_objects()
    LOOP
        IF obj.classid = 'pg_class'::regclass AND obj.objsubid = 0 THEN
            -- protected relations of still-tracked tables
            IF EXISTS (SELECT 1 FROM temporal.tracked_tables
                       WHERE history_table_oid = obj.objid) THEN
                RAISE EXCEPTION 'cannot drop temporal history table "%"', obj.object_identity
                    USING HINT = 'Call temporal.disable() on the tracked table first.';
            END IF;
            IF EXISTS (SELECT 1 FROM temporal.tracked_tables
                       WHERE versions_view_oid = obj.objid) THEN
                RAISE EXCEPTION 'cannot drop temporal versions view "%"', obj.object_identity
                    USING HINT = 'Call temporal.disable() on the tracked table first.';
            END IF;

            -- mirrored index dropped on either side → sync the mapping
            IF EXISTS (SELECT 1 FROM temporal.mirrored_indexes
                       WHERE base_index_oid = obj.objid) THEN
                SELECT history_index_oid::text INTO STRICT v_mirror
                FROM temporal.mirrored_indexes WHERE base_index_oid = obj.objid;
                DELETE FROM temporal.mirrored_indexes WHERE base_index_oid = obj.objid;
                EXECUTE format('DROP INDEX IF EXISTS %s', v_mirror);
            END IF;
            DELETE FROM temporal.mirrored_indexes WHERE history_index_oid = obj.objid;
        END IF;

        -- managed temporal triggers must not be removed while tracking is active
        IF obj.classid = 'pg_trigger'::regclass THEN
            v_trigger_name := trim(both '"' from coalesce(obj.object_name, split_part(obj.object_identity, ' on ', 1)));
            IF v_trigger_name IN ('temporal_stamp',
                                  'temporal_history',
                                  'temporal_truncate',
                                  'temporal_protect',
                                  'temporal_protect_truncate') THEN
                v_relation_identity := substring(obj.object_identity from ' on (.*)$');
                v_relation_oid := to_regclass(v_relation_identity);

                IF v_relation_oid IS NOT NULL AND EXISTS (
                    SELECT 1
                    FROM temporal.tracked_tables
                    WHERE table_oid = v_relation_oid OR history_table_oid = v_relation_oid
                ) THEN
                    RAISE EXCEPTION 'cannot drop temporal trigger "%"', obj.object_identity
                        USING HINT = 'Call temporal.disable() on the tracked table first.';
                END IF;
            END IF;
        END IF;

        -- column drops on tracked tables
        IF obj.classid = 'pg_class'::regclass AND obj.objsubid <> 0 THEN
            IF EXISTS (SELECT 1 FROM temporal.tracked_tables WHERE table_oid = obj.objid) THEN
                RAISE EXCEPTION 'cannot drop column on temporal table %', obj.object_identity
                    USING HINT = 'Use temporal.drop_column(table, column), or temporal.disable() first.';
            END IF;
        END IF;
    END LOOP;
END;
$$;

CREATE EVENT TRIGGER temporal_sql_drop
    ON sql_drop
    EXECUTE FUNCTION temporal.on_sql_drop();

-- ddl_command_end: propagate ADD COLUMN to the history table, block
-- type changes / renames, and mirror CREATE INDEX when include_indexes is on.
CREATE FUNCTION temporal.on_ddl_command_end()
RETURNS event_trigger
LANGUAGE plpgsql
AS $$
DECLARE
    cmd record;
    t   record;
    col record;
    v_orphan name;
    v_new    name;
    n_orphan int;
    n_new    int;
    v_schema name;
    v_view_name name;
    v_propagated boolean;
BEGIN
    IF to_regclass('temporal.tracked_tables') IS NULL THEN
        RETURN;
    END IF;
    IF current_setting('temporal.internal_ddl', true) = 'on' THEN
        RETURN;
    END IF;

    -- A tracked table must not become a child/partition of something else, and
    -- legacy (non-declarative) inheritance is unsupported. Declarative
    -- partitions hanging off a tracked partitioned root (relkind 'p') are fine.
    IF EXISTS (
        SELECT 1
        FROM temporal.tracked_tables tt
        JOIN pg_inherits i ON i.inhrelid = tt.table_oid
    ) THEN
        RAISE EXCEPTION 'cannot make a temporal table a child or partition of another table'
            USING HINT = 'Track the root table instead.';
    END IF;
    IF EXISTS (
        SELECT 1
        FROM temporal.tracked_tables tt
        JOIN pg_inherits i ON i.inhparent = tt.table_oid
        JOIN pg_class p ON p.oid = tt.table_oid
        WHERE p.relkind <> 'p'
    ) THEN
        RAISE EXCEPTION 'cannot add legacy inheritance to a temporal table'
            USING HINT = 'Only declarative partitioning is supported.';
    END IF;

    FOR cmd IN SELECT * FROM pg_event_trigger_ddl_commands()
    LOOP
        IF cmd.command_tag = 'ALTER TABLE' THEN
            SELECT * INTO t
            FROM temporal.tracked_tables
            WHERE table_oid = cmd.objid OR history_table_oid = cmd.objid;
            CONTINUE WHEN t IS NULL;

            PERFORM temporal._assert_managed_triggers_intact(t.table_oid, t.history_table_oid);
            CONTINUE WHEN cmd.objid = t.history_table_oid;

            v_propagated := false;

            -- A history column (other than the managed ones) that no longer
            -- exists on the base table is the *old* name of a renamed column.
            -- When exactly one column was renamed (one orphan history column +
            -- one new base column with no history match), propagate the rename
            -- to the history table. Anything ambiguous defers to the managed
            -- temporal.rename_column().
            SELECT h.attname INTO v_orphan
            FROM pg_attribute h
            WHERE h.attrelid = t.history_table_oid AND h.attnum > 0 AND NOT h.attisdropped
              AND h.attname NOT IN ('valid_to', 'deleted_by')
              AND NOT EXISTS (SELECT 1 FROM pg_attribute b
                              WHERE b.attrelid = t.table_oid AND b.attname = h.attname
                                AND b.attnum > 0 AND NOT b.attisdropped)
            LIMIT 1;
            IF v_orphan IS NOT NULL THEN
                SELECT count(*) INTO n_orphan
                FROM pg_attribute h
                WHERE h.attrelid = t.history_table_oid AND h.attnum > 0 AND NOT h.attisdropped
                  AND h.attname NOT IN ('valid_to', 'deleted_by')
                  AND NOT EXISTS (SELECT 1 FROM pg_attribute b
                                  WHERE b.attrelid = t.table_oid AND b.attname = h.attname
                                    AND b.attnum > 0 AND NOT b.attisdropped);
                SELECT count(*), min(b.attname) INTO n_new, v_new
                FROM pg_attribute b
                WHERE b.attrelid = t.table_oid AND b.attnum > 0 AND NOT b.attisdropped
                  AND b.attname NOT IN ('valid_from', 'changed_by')
                  AND NOT EXISTS (SELECT 1 FROM pg_attribute h
                                  WHERE h.attrelid = t.history_table_oid AND h.attname = b.attname
                                    AND h.attnum > 0 AND NOT h.attisdropped);

                IF n_orphan = 1 AND n_new = 1 THEN
                    PERFORM set_config('temporal.internal_ddl', 'on', true);
                    EXECUTE format('ALTER TABLE %s RENAME COLUMN %I TO %I',
                                   t.history_table_oid::text, v_orphan, v_new);
                    PERFORM set_config('temporal.internal_ddl', 'off', true);
                    UPDATE temporal.tracked_tables
                       SET excluded_columns = array_replace(excluded_columns, v_orphan, v_new)
                     WHERE table_oid = t.table_oid;
                    v_propagated := true;   -- forces the versions-view rebuild below
                    RAISE NOTICE 'temporal: column rename "%" → "%" propagated to history table %',
                        v_orphan, v_new, t.history_table_oid;
                ELSE
                    RAISE EXCEPTION 'cannot disambiguate column rename on temporal table % (orphan history column "%")',
                        t.table_oid, v_orphan
                        USING HINT = 'Use temporal.rename_column(table, old, new), or temporal.disable() first.';
                END IF;
            END IF;

            -- type changes must stay in sync (raw ALTER TYPE on a column the
            -- versions view exposes is blocked by the view dependency before it
            -- reaches here; this is a safety net for manual divergence)
            FOR col IN
                SELECT b.attname,
                       format_type(b.atttypid, b.atttypmod) AS base_type,
                       format_type(h.atttypid, h.atttypmod) AS hist_type
                FROM pg_attribute b
                JOIN pg_attribute h ON h.attrelid = t.history_table_oid AND h.attname = b.attname
                WHERE b.attrelid = t.table_oid AND b.attnum > 0
                  AND NOT b.attisdropped AND NOT h.attisdropped
                  AND (b.atttypid <> h.atttypid OR b.atttypmod <> h.atttypmod)
            LOOP
                RAISE EXCEPTION 'cannot change the type of column "%" on temporal table % (history has %, table has %)',
                    col.attname, t.table_oid, col.hist_type, col.base_type
                    USING HINT = 'Use temporal.alter_column_type(table, column, type), or temporal.disable() first.';
            END LOOP;

            -- ADD COLUMN propagation: new base columns appear on history too
            FOR col IN
                SELECT b.attname,
                       format_type(b.atttypid, b.atttypmod) AS coltype,
                       CASE WHEN b.attcollation <> 0
                                 AND b.attcollation <> (SELECT typcollation FROM pg_type WHERE oid = b.atttypid)
                            THEN (SELECT format('COLLATE %I.%I', cn.nspname, c.collname)
                                  FROM pg_collation c JOIN pg_namespace cn ON cn.oid = c.collnamespace
                                  WHERE c.oid = b.attcollation)
                            ELSE '' END AS coll
                FROM pg_attribute b
                WHERE b.attrelid = t.table_oid AND b.attnum > 0 AND NOT b.attisdropped
                  AND NOT EXISTS (SELECT 1 FROM pg_attribute h
                                  WHERE h.attrelid = t.history_table_oid AND h.attname = b.attname
                                    AND h.attnum > 0 AND NOT h.attisdropped)
                ORDER BY b.attnum
            LOOP
                -- history rows predate the column: always nullable, no default
                EXECUTE format('ALTER TABLE %s ADD COLUMN %I %s %s',
                               t.history_table_oid::text, col.attname, col.coltype, col.coll);
                RAISE NOTICE 'temporal: column "%" propagated to history table %', col.attname, t.history_table_oid;
                v_propagated := true;
            END LOOP;

            IF v_propagated THEN
                -- the versions view freezes its column list: rebuild it
                SELECT n.nspname, c.relname INTO v_schema, v_view_name
                FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE c.oid = t.versions_view_oid;

                PERFORM set_config('temporal.internal_ddl', 'on', true);
                EXECUTE format('DROP VIEW %s', t.versions_view_oid::text);
                PERFORM temporal._create_versions_view(t.table_oid, t.history_table_oid, v_view_name, v_schema);
                PERFORM set_config('temporal.internal_ddl', 'off', true);

                UPDATE temporal.tracked_tables
                   SET versions_view_oid = format('%I.%I', v_schema, v_view_name)::regclass
                 WHERE table_oid = t.table_oid;
                PERFORM temporal._copy_select_grants(t.table_oid, format('%I.%I', v_schema, v_view_name)::regclass);
            END IF;

            -- ALTER TABLE can create indexes through constraints (for example
            -- ADD CONSTRAINT ... UNIQUE). Mirror any new base-table indexes
            -- that were not exposed as a standalone CREATE INDEX command.
            IF t.include_indexes THEN
                PERFORM temporal._mirror_indexes(t.table_oid);
            END IF;

            PERFORM temporal.invalidate(t.table_oid);

        ELSIF cmd.command_tag = 'CREATE INDEX' THEN
            -- auto-mirror new base-table indexes when include_indexes is on
            SELECT tt.* INTO t
            FROM pg_index i
            JOIN temporal.tracked_tables tt ON tt.table_oid = i.indrelid
            WHERE i.indexrelid = cmd.objid AND tt.include_indexes;
            CONTINUE WHEN t IS NULL;

            IF NOT EXISTS (SELECT 1 FROM temporal.mirrored_indexes
                           WHERE base_index_oid = cmd.objid) THEN
                INSERT INTO temporal.mirrored_indexes (table_oid, base_index_oid, history_index_oid)
                VALUES (t.table_oid, cmd.objid,
                        temporal._mirror_one_index(cmd.objid, t.history_table_oid));
            END IF;
        END IF;
    END LOOP;
END;
$$;

CREATE EVENT TRIGGER temporal_ddl_command_end
    ON ddl_command_end
    WHEN TAG IN ('ALTER TABLE', 'CREATE INDEX', 'CREATE TABLE')
    EXECUTE FUNCTION temporal.on_ddl_command_end();

CREATE FUNCTION temporal.set_include_indexes(p_table regclass, p_include boolean)
RETURNS void
LANGUAGE plpgsql
AS $$
DECLARE
    r record;
BEGIN
    IF NOT EXISTS (SELECT 1 FROM temporal.tracked_tables WHERE table_oid = p_table) THEN
        RAISE EXCEPTION 'temporal.set_include_indexes: "%" is not tracked', p_table;
    END IF;

    UPDATE temporal.tracked_tables SET include_indexes = p_include WHERE table_oid = p_table;

    IF p_include THEN
        PERFORM temporal._mirror_indexes(p_table);
    ELSE
        FOR r IN SELECT history_index_oid FROM temporal.mirrored_indexes
                 WHERE table_oid = p_table
        LOOP
            EXECUTE format('DROP INDEX %s', r.history_index_oid::text);
        END LOOP;
        DELETE FROM temporal.mirrored_indexes WHERE table_oid = p_table;
    END IF;

    PERFORM temporal.invalidate(p_table);
END;
$$;
