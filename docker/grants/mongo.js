// MongoDB: one data account and one schema account. Run with mongosh as an administrator, once.
// Replace the passwords.
const ldb = db.getSiblingDB("ldb");

ldb.createUser({ user: "ldb_data", pwd: "change-me-data", roles: [{ role: "readWrite", db: "ldb" }] });
ldb.createUser({ user: "ldb_schema", pwd: "change-me-schema", roles: [{ role: "dbAdmin", db: "ldb" }, { role: "readWrite", db: "ldb" }] });
