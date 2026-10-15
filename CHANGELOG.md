# Changelog

## 0.1.1

Update client documentation to use the lt_database bundle identity and its LtDatabase / LtDatabaseClient C# namespaces.
The HTTP protocol and runtime behavior are unchanged.

## 0.1.0

Initial public release: signed, namespace-authorized HTTP API for PostgreSQL, MySQL and MongoDB; portable schema sync
and CRUD; optional journaled write-behind with interval/fixed-time scheduling, explicit flush, immediate writes,
recovery, queue limits and dead-letter handling.

Release hardening blocks operations whose prerequisite flush halted, serializes buffered namespace requests around
schema changes, refreshes schema validation after sync, and avoids merging separate MongoDB insert receipts because
ordered MongoDB bulk inserts can partially commit. Native archives, checksums and a container image are built in CI.
