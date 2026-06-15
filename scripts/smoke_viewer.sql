\set ON_ERROR_STOP on
CREATE EXTENSION pg_temporal_tables;
CREATE TABLE users (id int PRIMARY KEY, name text, email text, updated_at timestamptz);
SELECT temporal.enable('users', excluded_columns => ARRAY['updated_at']::name[], combine_interval => interval '0');

SET temporal.user_id = 'alice';
INSERT INTO users (id, name, email) VALUES (1, 'Alice', 'a@x');
SET temporal.user_id = 'bob';
UPDATE users SET name = 'Alice B' WHERE id = 1;
UPDATE users SET email = 'b@x' WHERE id = 1;
SET temporal.user_id = 'carol';
DELETE FROM users WHERE id = 1;
SET temporal.user_id = 'dave';
INSERT INTO users (id, name) VALUES (2, 'Dan');

-- mod_date / mod_user on base, history, versions view rows
SELECT 'mod-funcs' AS step, temporal.mod_user(u) AS cur_user, temporal.mod_date(u) IS NOT NULL AS has_date FROM users u;
SELECT temporal.mod_user(h) AS hist_user FROM users__history h ORDER BY valid_to LIMIT 1;
SELECT temporal.mod_user(v) AS view_user, v.is_current FROM users__versions v ORDER BY valid_from LIMIT 1;

-- changes(): full event log
SELECT pk, changed_by, operation, old_row->>'name' AS old_name, new_row->>'name' AS new_name, changed_columns
FROM temporal.changes('users', now() - interval '1 hour', now() + interval '1 hour')
ORDER BY changed_at;

-- changes() with pk filter
SELECT operation, changed_by FROM temporal.changes('users', now() - interval '1 hour', now() + interval '1 hour', '{"id": 2}');

-- column_history(): name timeline of row 1 (works via history even though row deleted)
SELECT changed_by, value FROM temporal.column_history('users', '{"id": 1}', 'name');

-- column_history on excluded column → error
DO $$ BEGIN
  PERFORM * FROM temporal.column_history('users', '{"id": 1}', 'updated_at');
  RAISE EXCEPTION 'excluded col not rejected';
EXCEPTION WHEN raise_exception THEN RAISE NOTICE 'OK: excluded column rejected'; END $$;

-- accepts history/view regclass
SELECT count(*) AS via_view FROM temporal.column_history('users__versions', '{"id": 1}', 'name');
SELECT count(*) AS via_hist FROM temporal.column_history('users__history', '{"id": 1}', 'name');

-- viewer functions blocked under as_of
SET temporal.as_of = '2026-01-01';
DO $$ BEGIN
  PERFORM * FROM temporal.changes('users', now() - interval '1 day', now());
  RAISE EXCEPTION 'as_of guard missing';
EXCEPTION WHEN raise_exception THEN RAISE NOTICE 'OK: viewer blocked under as_of'; END $$;
RESET temporal.as_of;
