-- Post-deploy grants needed when running on Azure Database for PostgreSQL Flexible Server
-- (or any host where the runtime user is NOT a Postgres superuser). The EF migration
-- AddMemoryAppRole grants USAGE/CRUD on memory_graph tables, but AGE's cypher() does
-- two more things at query time:
--   1. CALL functions in ag_catalog (cypher itself, agtype helpers, etc)
--   2. ALTER TABLE memory_graph._ag_label_* to add columns when a new node label / property
--      first appears — which requires being the table OWNER, not just having grants.
--
-- Run this AFTER `dotnet ef database update` and AFTER `create_graph('memory_graph')`,
-- as the postgres / pgadmin migrations user.

BEGIN;

-- 1. Function-execute on ag_catalog
GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA ag_catalog TO memory_app;
GRANT EXECUTE ON ALL PROCEDURES IN SCHEMA ag_catalog TO memory_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA ag_catalog GRANT EXECUTE ON FUNCTIONS TO memory_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA ag_catalog GRANT EXECUTE ON ROUTINES TO memory_app;

-- 2. Schema + bootstrap-table ownership transfer so dynamic ALTER TABLE works.
--    cypher() introduces new label columns on demand; this requires owner perms,
--    not just GRANTed perms. Once memory_app owns the bootstrap tables, future
--    label tables that cypher() creates while running as memory_app are auto-owned
--    by it and stay coherent.
ALTER SCHEMA memory_graph OWNER TO memory_app;
ALTER TABLE memory_graph._ag_label_vertex OWNER TO memory_app;
ALTER TABLE memory_graph._ag_label_edge   OWNER TO memory_app;
ALTER SEQUENCE memory_graph._ag_label_vertex_id_seq OWNER TO memory_app;
ALTER SEQUENCE memory_graph._ag_label_edge_id_seq   OWNER TO memory_app;
ALTER SEQUENCE memory_graph._label_id_seq           OWNER TO memory_app;

-- 3. CREATE on memory_graph for cypher()'s dynamic table creation
GRANT CREATE ON SCHEMA memory_graph TO memory_app;
ALTER DEFAULT PRIVILEGES IN SCHEMA memory_graph GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO memory_app;

COMMIT;
