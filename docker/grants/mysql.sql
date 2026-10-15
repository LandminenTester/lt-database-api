-- MySQL / MariaDB: one data account and one schema account.
-- Run as an administrator, once. Replace the passwords and, if you can, restrict the host part.
CREATE DATABASE ldb CHARACTER SET utf8mb4 COLLATE utf8mb4_bin;

CREATE USER 'ldb_schema'@'%' IDENTIFIED BY 'change-me-schema';
CREATE USER 'ldb_data'@'%'   IDENTIFIED BY 'change-me-data';

GRANT SELECT, INSERT, UPDATE, DELETE, CREATE, ALTER, DROP, INDEX, REFERENCES ON ldb.* TO 'ldb_schema'@'%';
GRANT SELECT, INSERT, UPDATE, DELETE ON ldb.* TO 'ldb_data'@'%';
