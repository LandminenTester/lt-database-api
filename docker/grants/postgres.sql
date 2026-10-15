-- PostgreSQL: one data account and one schema account.
-- Run as a superuser, once. Replace the passwords.
CREATE ROLE ldb_schema LOGIN PASSWORD 'change-me-schema';
CREATE ROLE ldb_data   LOGIN PASSWORD 'change-me-data';

CREATE DATABASE ldb OWNER ldb_schema;
\connect ldb

-- The schema account owns every table it creates.
REVOKE ALL ON SCHEMA public FROM PUBLIC;
GRANT ALL ON SCHEMA public TO ldb_schema;
GRANT USAGE ON SCHEMA public TO ldb_data;

-- The data account may only read and write rows, including in tables created later.
ALTER DEFAULT PRIVILEGES FOR ROLE ldb_schema IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO ldb_data;
ALTER DEFAULT PRIVILEGES FOR ROLE ldb_schema IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO ldb_data;
