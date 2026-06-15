\set ON_ERROR_STOP on
CREATE EXTENSION pg_temporal_tables;
CREATE TABLE roles (id int PRIMARY KEY, name text);
CREATE TABLE users (id int PRIMARY KEY, name text, role_id int, legacy text);
ALTER TABLE users DROP COLUMN legacy;  -- dropped column BEFORE enable
SELECT temporal.enable('roles', combine_interval => interval '0');
SELECT temporal.enable('users', combine_interval => interval '0');

SET temporal.user_id = 'alice';
INSERT INTO roles VALUES (10, 'Admin');
INSERT INTO users (id, name, role_id) VALUES (1, 'Alice', 10);

SELECT pg_sleep(0.05);
SELECT clock_timestamp() AS m1 \gset
SELECT pg_sleep(0.05);

SET temporal.user_id = 'bob';
UPDATE roles SET name = 'Administrator' WHERE id = 10;
UPDATE users SET name = 'Alice B' WHERE id = 1;

-- ── time travel: simple + join ──
SET temporal.as_of = :'m1';
SELECT 'asof-join' AS step, u.name, r.name AS role_name, u.changed_by, u.valid_from IS NOT NULL AS has_vf
FROM users u JOIN roles r ON r.id = u.role_id;     -- expect Alice / Admin / alice

-- present
RESET temporal.as_of;
SELECT 'present' AS step, u.name, r.name AS role_name, u.changed_by
FROM users u JOIN roles r ON r.id = u.role_id;     -- Alice B / Administrator / bob

-- ── through a regular view ──
CREATE VIEW v_users AS SELECT u.id, u.name, r.name AS role_name FROM users u JOIN roles r ON r.id = u.role_id;
SET temporal.as_of = :'m1';
SELECT 'via-view' AS step, * FROM v_users;          -- Alice / Admin

-- ── CTE + sublink ──
WITH x AS (SELECT name FROM users WHERE id = 1)
SELECT 'via-cte' AS step, name FROM x;              -- Alice
SELECT 'via-sublink' AS step, (SELECT name FROM users WHERE id = 1) AS name;  -- Alice

-- ── DML blocked ──
DO $$ BEGIN
  UPDATE users SET name = 'x' WHERE id = 1;
  RAISE EXCEPTION 'DML not blocked';
EXCEPTION WHEN read_only_sql_transaction THEN RAISE NOTICE 'OK: DML blocked under as_of'; END $$;

-- ── COPY table TO blocked, COPY (SELECT) allowed ──
DO $$ BEGIN
  EXECUTE 'COPY users TO ''/dev/null''';
  RAISE EXCEPTION 'COPY not blocked';
EXCEPTION WHEN feature_not_supported THEN RAISE NOTICE 'OK: COPY table blocked under as_of'; END $$;
COPY (SELECT name FROM users) TO '/dev/null';

-- ── as_of before any data ──
SET temporal.as_of = '2000-01-01';
SELECT 'before-enable' AS step, count(*) AS rows FROM users;   -- 0

-- ── prepared statements + plan cache reset ──
RESET temporal.as_of;
PREPARE p AS SELECT name FROM users WHERE id = 1;
EXECUTE p;  -- Alice B
EXECUTE p; EXECUTE p; EXECUTE p; EXECUTE p; EXECUTE p;  -- drive towards generic plan
SET temporal.as_of = :'m1';
SELECT 'prepared-asof' AS step;
EXECUTE p;  -- must be Alice (plan cache was reset)
RESET temporal.as_of;
SELECT 'prepared-present' AS step;
EXECUTE p;  -- Alice B again

-- ── EXPLAIN sanity ──
SET temporal.as_of = :'m1';
EXPLAIN (COSTS OFF) SELECT name FROM users WHERE id = 1;

-- ── versions view & mod columns under as_of? versions view is for present-time use; just ensure simple select on history table still works
RESET temporal.as_of;
SELECT 'history-direct' AS step, count(*) FROM users__history;
