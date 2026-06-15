\set ON_ERROR_STOP on
CREATE EXTENSION pg_temporal_tables;
CREATE TABLE users (id int PRIMARY KEY, name text, email text, updated_at timestamptz);
SELECT temporal.enable('users', excluded_columns => ARRAY['updated_at']::name[], combine_interval => interval '5 seconds');

SET temporal.user_id = 'alice';
INSERT INTO users (id, name) VALUES (1, 'v1');
SELECT 'after-insert' AS step, count(*) AS history_rows FROM users__history;
SELECT changed_by, valid_from IS NOT NULL AS has_vf FROM users WHERE id = 1;

UPDATE users SET name = 'v2' WHERE id = 1;
SELECT 'insert-collapse' AS step, count(*) AS history_rows FROM users__history;  -- expect 0 (melted into insert)
SELECT name, changed_by FROM users WHERE id = 1;

-- force the next change outside the bucket by faking an old valid_from? cannot (protected).
-- instead: different user breaks the combine
SET temporal.user_id = 'bob';
UPDATE users SET name = 'v3' WHERE id = 1;
SELECT 'cross-user' AS step, count(*) AS history_rows, min(changed_by) AS old_author FROM users__history; -- expect 1, alice

UPDATE users SET name = 'v4' WHERE id = 1;  -- bob again, within 5s => combine-extend
SELECT 'combine-extend' AS step, count(*) AS history_rows FROM users__history;   -- still 1
SELECT name AS old_value, changed_by FROM users__history;                        -- v2 (alice's last)

-- excluded column only: invisible
UPDATE users SET updated_at = now() WHERE id = 1;
SELECT 'excluded-only' AS step, count(*) AS history_rows FROM users__history;    -- still 1
SELECT name, changed_by FROM users WHERE id = 1;                                  -- v4, bob

-- no-op update (same values): still a "change"? values identical => excluded-only comparison says nothing changed => invisible
UPDATE users SET name = 'v4' WHERE id = 1;
SELECT 'noop-update' AS step, count(*) AS history_rows FROM users__history;      -- still 1

-- delete: history row with deleted_by
SET temporal.user_id = 'carol';
DELETE FROM users WHERE id = 1;
SELECT 'delete' AS step, count(*) AS history_rows FROM users__history;           -- 2
SELECT name, changed_by, deleted_by FROM users__history ORDER BY valid_to DESC LIMIT 1; -- v4, bob, carol

-- same-transaction collapse
SET temporal.user_id = 'dave';
BEGIN;
INSERT INTO users (id, name) VALUES (2, 'a');
UPDATE users SET name = 'b' WHERE id = 2;
UPDATE users SET name = 'c' WHERE id = 2;
DELETE FROM users WHERE id = 2;
COMMIT;
SELECT 'same-tx-wipe' AS step, count(*) AS history_for_id2 FROM users__history WHERE id = 2;  -- 0

-- unattributed (NULL user) never combines
RESET temporal.user_id;
INSERT INTO users (id, name) VALUES (3, 'x');
UPDATE users SET name = 'y' WHERE id = 3;  -- prev change = insert by NULL... same-tx? no. NULL user => no combine => history row
SELECT 'null-user' AS step, count(*) AS history_for_id3, max(changed_by) IS NULL AS author_null FROM users__history WHERE id = 3; -- 1, true

-- versions view
SELECT 'versions-view' AS step, count(*) FILTER (WHERE is_current) AS current_rows, count(*) AS total FROM users__versions;

-- require_user
SET temporal.require_user = on;
DO $$ BEGIN
  INSERT INTO users (id, name) VALUES (4, 'z');
  RAISE EXCEPTION 'should not get here';
EXCEPTION WHEN insufficient_privilege THEN
  RAISE NOTICE 'require_user correctly blocked unattributed write';
END $$;

-- prune
SELECT 'prune' AS step, temporal.prune('users', now()) AS pruned;
SELECT count(*) AS history_after_prune FROM users__history;
