\set ON_ERROR_STOP on
CREATE EXTENSION pg_temporal_tables;
CREATE TABLE roles (id int PRIMARY KEY, name text);
CREATE TABLE users (id int PRIMARY KEY, name text, role_id int);
SELECT temporal.enable('roles', combine_interval => interval '0');
SELECT temporal.enable('users', combine_interval => interval '0');
SET temporal.user_id = 'alice';
INSERT INTO roles VALUES (10, 'Admin');
INSERT INTO users VALUES (1, 'Alice', 10);
SELECT pg_sleep(0.05); SELECT clock_timestamp() AS m1 \gset
SELECT pg_sleep(0.05);
SET temporal.user_id = 'bob';
UPDATE roles SET name = 'Administrator' WHERE id = 10;
UPDATE users SET name = 'Alice B' WHERE id = 1;

-- marker: whole-statement time travel without any session state
SELECT 'marker-join' AS step, u.name, r.name AS role_name
FROM users u JOIN roles r ON r.id = u.role_id
WHERE temporal.as_of(:'m1');            -- expect Alice / Admin

SELECT 'present' AS step, u.name, r.name FROM users u JOIN roles r ON r.id = u.role_id;

-- marker combined with own predicates
SELECT 'marker-where' AS step, name FROM users WHERE id = 1 AND temporal.as_of(:'m1');

-- marker in subquery position
SELECT 'marker-sub' AS step, (SELECT name FROM users WHERE temporal.as_of(:'m1') AND id = 1) AS name;

-- marker wins over GUC
SET temporal.as_of = '2000-01-01';
SELECT 'guc-only' AS step, count(*) AS rows_via_guc FROM users;            -- 0 (pre-data)
SELECT 'marker-overrides' AS step, name FROM users WHERE temporal.as_of(:'m1');  -- Alice
RESET temporal.as_of;

-- DML with marker -> error
DO $$ BEGIN
  UPDATE users SET name = 'x' WHERE temporal.as_of('2026-01-01');
  RAISE EXCEPTION 'marker DML not blocked';
EXCEPTION WHEN read_only_sql_transaction THEN RAISE NOTICE 'OK: marker DML blocked'; END $$;

-- volatile/non-constant arg -> clear error
DO $$ BEGIN
  PERFORM 1 FROM users WHERE temporal.as_of(now());
  RAISE EXCEPTION 'volatile marker arg not rejected';
EXCEPTION WHEN feature_not_supported THEN RAISE NOTICE 'OK: non-constant marker arg rejected'; END $$;

-- prepared statement with parameter (custom plans provide the value)
PREPARE pm(timestamptz) AS SELECT name FROM users WHERE temporal.as_of($1);
EXECUTE pm(:'m1');   -- Alice
EXECUTE pm(now());   -- Alice B
