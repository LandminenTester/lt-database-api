# Schemas and schema sync

A **namespace** owns a set of tables. The caller declares the complete desired structure; the API makes the database match it
and remembers what it applied. Running the same declaration again does nothing; changing it applies exactly the difference.

This is a one-to-one relationship by design: the program that declares a namespace is the only owner of that structure, so
removing something from the declaration removes it from the database (unless you choose `Quarantine` or `Deny`, below).

## Names

* Namespace ≤ 24, table ≤ 36, column and index ≤ 48 characters; `^[a-z][a-z0-9]*(_[a-z0-9]+)*$`.
* Physical table name: `{namespace}__{table}` (MongoDB: the collection of that name). Two namespaces can reuse table names.
* Internal bookkeeping uses `_ldb_schema` (the last applied declaration per namespace), `_ldb_locks` and `_ldb_counters`
  (MongoDB only). Everything starting with `_` is reserved.

## Column types

| Type | Options | PostgreSQL | MySQL | MongoDB (BSON) |
|---|---|---|---|---|
| `int32` | | `INTEGER` | `INT` | int / long |
| `int64` | | `BIGINT` | `BIGINT` | long / int |
| `decimal` | `precision` 1–38 (18), `scale` 0–precision (0) | `NUMERIC(p,s)` | `DECIMAL(p,s)` | decimal128 |
| `float64` | | `DOUBLE PRECISION` | `DOUBLE` | double |
| `bool` | | `BOOLEAN` | `TINYINT(1)` | bool |
| `string` | `length` 1–4000 (255) | `VARCHAR(n)` | `VARCHAR(n)` | string, `maxLength` |
| `text` | | `TEXT` | `LONGTEXT` | string |
| `json` | | `JSONB` | `JSON` | embedded document / array / value |
| `datetime` | UTC, microseconds | `TIMESTAMPTZ` | `DATETIME(6)` | date |
| `date` | | `DATE` | `DATE` | date (midnight UTC) |
| `uuid` | | `UUID` | `CHAR(36)` | string |
| `bytes` | `length` 1–65535 (optional) | `BYTEA` | `VARBINARY(n)` / `LONGBLOB` | binData |

Column options: `nullable` (default `false`, so columns are `NOT NULL`), `default`, `unique`, `autoIncrement`
(`int32`/`int64`, must be the only primary key column), `renamedFrom`.

Defaults are literals matching the type. `datetime` also accepts `"now"`. Defaults are applied by the database on SQL engines
and by the API on insert for MongoDB.

Primary key: `primaryKey` lists one or more columns. In MongoDB a single-column key is stored as `_id`; a composite key is
enforced by a unique index. `autoIncrement` on MongoDB uses an atomic counter collection.

Indexes: `{ "name", "columns": [...], "unique" }`. `json`, `text` and unbounded `bytes` columns cannot be indexed, and
indexed strings are limited to 255 characters, the portable limit that fits MySQL's key length.

Collation: MySQL tables use `utf8mb4_bin`, so comparisons are case sensitive like on PostgreSQL and MongoDB.

## How sync works

1. The declaration is validated and normalised (`unique` becomes an index named `uq_<column>`, sizes get their defaults) and hashed.
2. The namespace is locked (PostgreSQL advisory lock, MySQL `GET_LOCK`, MongoDB lock document) so two syncs never overlap.
3. The previous declaration is read from `_ldb_schema`; the difference between it and the new one becomes an **ordered plan**:
   rename, drop index, add column, alter column, create table, create index, drop column, drop table.
4. The plan is checked against the **real** database. Objects that should exist but do not (someone dropped them, an earlier
   sync stopped half way) are added to the plan as repairs. Steps whose result already exists are skipped, so a sync can
   always be run again.
5. In dry-run mode the plan is returned and nothing is changed.
6. Otherwise the steps are applied (PostgreSQL: inside one transaction) and the new declaration is stored.

If the hash equals the stored hash and nothing needs repair, the result is `unchanged` and the database is not touched.

### Renames

A removed column looks like a drop. To keep the data, declare the new name with `renamedFrom`:

```json
{ "name": "about", "type": "text", "nullable": true, "renamedFrom": "bio" }
```

Tables work the same way. The hint is only used while the old name exists in the stored declaration, so it can stay in the
declaration afterwards.

### What counts as destructive

Removing a column or table, a type change that is not a pure widening (`int32`→`int64` and `string`→`text` are widenings),
shortening a column or reducing decimal precision, and making a nullable column `NOT NULL`. These steps are flagged
`"destructive": true` in the plan.

`Schema:DestructiveMode` decides what the server does with them:

| Mode | Behaviour |
|---|---|
| `Apply` (default) | Do it. The database follows the declaration exactly. |
| `Quarantine` | Never delete: a removed column is renamed to `_dropped_<unixtime>_<name>` (and made nullable so it cannot block inserts), a removed table or collection is renamed the same way. Clean up by hand when you are sure. |
| `Deny` | Refuse any sync that contains a destructive step (`409 destructive_denied`). A dry run reports `denied`. |

Shortening a `string` column first checks that no stored value is longer than the new length (`409 data_does_not_fit`).

### What sync will not do

* **Change a primary key** (`409 unsupported_change`). Create a new table and copy the data.
* **Switch `autoIncrement` on or off** for an existing column.
* **Convert existing values** when a type changes. On SQL engines the database casts (and fails if it cannot, which rolls back
  on PostgreSQL); on MongoDB only the validator changes and old documents keep their stored type until rewritten.
* **Detect manual drift** such as someone changing a column type by hand. Missing tables, columns and indexes are repaired;
  other differences are not looked at.
* **Foreign keys.** Not supported yet; model relations in your program.

### Engine notes

* **PostgreSQL** runs the whole sync in a transaction; a failure leaves nothing half applied.
* **MySQL / MariaDB** commits each DDL statement immediately. A failure can leave a partly applied sync; run it again and the
  repair step completes it. Take a backup before large changes.
* **MongoDB** has no DDL transactions either. Each table's `$jsonSchema` validator (`validationLevel: moderate`,
  `validationAction: error`) enforces types, lengths and required fields for new writes. Added columns with a default are
  back-filled into existing documents.
