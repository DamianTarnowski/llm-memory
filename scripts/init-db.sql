-- LLM Memory — environment bootstrap (idempotent).
-- Run once as a Postgres superuser against an empty database, e.g.:
--   psql -h localhost -U postgres -d llm_memory -f scripts/init-db.sql
-- This script only sets up extensions + the AGE graph. Tables are created and
-- maintained by EF Core migrations:
--   dotnet ef database update --project src/Memory.Storage
-- Prerequisites:
--   * pgvector installed and on `shared_preload_libraries`
--   * Apache AGE 1.5 / 1.6 installed and on `shared_preload_libraries`
--     (Windows: build from source against the matching Postgres version, or use
--      a community installer; Linux: distro package or apt repo)

CREATE EXTENSION IF NOT EXISTS vector;
CREATE EXTENSION IF NOT EXISTS age;

LOAD 'age';
SET search_path = ag_catalog, "$user", public;

DO $$
BEGIN
    PERFORM ag_catalog.create_graph('memory_graph');
EXCEPTION
    WHEN duplicate_schema THEN NULL;
    WHEN duplicate_object THEN NULL;
END $$;

CREATE SCHEMA IF NOT EXISTS memory;

-- Tenancy isolation knob — every connection should set this before doing work:
--   SET app.organization_id = '<org-uuid>';
--   SET app.project_id      = '<project-uuid>';
-- Row-level security policies (added by EF Core migrations in v1) will read
-- these GUCs and filter rows accordingly.

-- Tables, indexes, and RLS policies are created by EF Core migration `Initial`
-- (see src/Memory.Storage/Migrations). AGE node/edge labels are created lazily
-- by IGraphContext implementations on first use. This script only ensures the
-- environment (extensions, graph, schema) is ready.
