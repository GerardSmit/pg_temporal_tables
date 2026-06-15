\set ON_ERROR_STOP on
CREATE EXTENSION pg_temporal_tables;
CREATE TABLE users (id int PRIMARY KEY, name text, email text);
CREATE UNIQUE INDEX users_email_idx ON users(email);
SELECT temporal.enable('users', include_indexes => true);

-- TRUNCATE blocked
DO $$ BEGIN
  TRUNCATE users;
  RAISE EXCEPTION 'truncate not blocked';
EXCEPTION WHEN feature_not_supported THEN RAISE NOTICE 'OK: truncate blocked'; END $$;

-- DROP history blocked
DO $$ BEGIN
  DROP TABLE users__history CASCADE;
  RAISE EXCEPTION 'history drop not blocked';
EXCEPTION WHEN raise_exception THEN RAISE NOTICE 'OK: history drop blocked'; END $$;

-- DROP versions view blocked
DO $$ BEGIN
  DROP VIEW users__versions;
  RAISE EXCEPTION 'view drop not blocked';
EXCEPTION WHEN raise_exception THEN RAISE NOTICE 'OK: view drop blocked'; END $$;

-- DROP managed column blocked
DO $$ BEGIN
  ALTER TABLE users DROP COLUMN changed_by CASCADE;
  RAISE EXCEPTION 'column drop not blocked';
EXCEPTION WHEN raise_exception THEN RAISE NOTICE 'OK: column drop blocked'; END $$;

-- rename column blocked
DO $$ BEGIN
  ALTER TABLE users RENAME COLUMN name TO name2;
  RAISE EXCEPTION 'rename not blocked';
EXCEPTION WHEN raise_exception THEN RAISE NOTICE 'OK: rename blocked'; END $$;

-- ALTER TYPE blocked
-- ALTER TYPE blocked (the versions view dependency rejects it before our trigger)
DO $$ BEGIN
  ALTER TABLE users ALTER COLUMN name TYPE varchar(10);
  RAISE EXCEPTION 'alter type not blocked';
EXCEPTION WHEN raise_exception OR feature_not_supported THEN RAISE NOTICE 'OK: alter type blocked'; END $$;

-- ADD COLUMN propagates + view rebuilt + versioning works for new column
SET temporal.user_id = 'alice';
INSERT INTO users (id, name) VALUES (1, 'a');
ALTER TABLE users ADD COLUMN phone text;
SELECT 'propagated' AS step, count(*) AS hist_has_phone
FROM pg_attribute WHERE attrelid = 'users__history'::regclass AND attname = 'phone';
SELECT count(*) AS view_has_phone
FROM pg_attribute WHERE attrelid = 'users__versions'::regclass AND attname = 'phone';
SET temporal.user_id = 'bob';
UPDATE users SET phone = '12345' WHERE id = 1;
SELECT name, phone FROM users__history;  -- old version: phone NULL

-- CREATE INDEX auto-mirrors
CREATE INDEX users_name_idx ON users(name);
SELECT 'index-sync' AS step, count(*) AS mirrors FROM temporal.mirrored_indexes;  -- pkey, email, name = 3

-- DROP INDEX removes mirror
DROP INDEX users_name_idx;
SELECT count(*) AS mirrors_after_drop FROM temporal.mirrored_indexes;  -- 2
SELECT count(*) AS hist_name_idx FROM pg_class WHERE relname LIKE 'users_name_idx%';  -- 0

-- DROP base table cleans catalog, history preserved
CREATE TABLE temp1 (id int PRIMARY KEY, v text);
SELECT temporal.enable('temp1');
DROP TABLE temp1 CASCADE;
SELECT 'base-drop' AS step,
       (SELECT count(*) FROM temporal.tracked_tables WHERE table_oid::text LIKE '%temp1%') AS catalog_rows,
       (SELECT count(*) FROM pg_class WHERE relname = 'temp1__history') AS history_kept;
