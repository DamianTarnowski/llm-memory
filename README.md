# LLM Memory

Stateful "second brain" memory system for LLM assistants — exposed via the **Model Context Protocol (MCP)** so Claude Code, OpenAI Codex, and other clients can read and write into it. Built on **.NET 10**, with a single **Postgres** instance backing both vectors (pgvector) and a temporal knowledge graph (Apache AGE).

> **Status:** alpha skeleton. The solution compiles end-to-end and the project structure is wired, but ingestion, retrieval, sleep-time reflection, and most LLM provider implementations are still stubs that throw `NotImplementedException`. See the roadmap below for what is planned.

---

## What this is

Most current AI memory systems pick one shape: pure vector RAG (Mem0), pure knowledge graph (Graphiti / Zep), or per-agent hierarchical memory (Letta / MemGPT). The strongest 2026 systems combine these ideas. This project takes the same shape:

- **Vectors + temporal knowledge graph in one Postgres** (pgvector for embeddings, Apache AGE for graph nodes/edges/Cypher) — atomic across both within a single transaction.
- **Bitemporal facts** — every edge tracks both when it was true in the world (`valid_from`/`valid_to`) and when the agent learned it (`recorded_at`/`invalidated_at`), inspired by Graphiti.
- **Zettelkasten-style notes** with dynamic linking driven by an LLM (A-MEM, NeurIPS 2025).
- **Sleep-time reflection** — a background process consolidates raw episodes into learned context (Letta).
- **Hybrid retrieval** — vector + graph traversal + keyword + temporal filters; Personalized PageRank (HippoRAG 2) planned for v2.
- **Multi-tenant from day one** — Organization → User → Project, with row-level security in Postgres and a `tenant_id` filter injected into every Cypher query.
- **Multi-provider LLMs** — Microsoft.Extensions.AI `IChatClient` over Azure OpenAI, OpenAI, AWS Bedrock, Google Vertex, Anthropic, with thin wrappers per provider.

---

## Architecture (high level)

```
Claude Code / Codex CLI    ──stdio──▶  Memory.Mcp.Stdio
Other MCP clients / SaaS   ──HTTP+SSE──▶  Memory.Api  (also serves REST + Blazor)
Web UI (Blazor WASM)       ──HTTP──▶    Memory.Api

                                Memory.Pipeline  (ingest, retrieve, reflect)
                                Memory.Llm       (IChatClient, IEmbeddingGenerator)
                                Memory.Storage   (EF Core + AGE + pgvector)
                                Memory.Tenancy   (Org/User/Project scope)

                                ┌──────────────────────────────┐
                                │  PostgreSQL (local or Azure) │
                                │  • pgvector                  │
                                │  • Apache AGE                │
                                │  schema "memory"             │
                                └──────────────────────────────┘
```

`Memory.AppHost` (.NET Aspire 13) orchestrates the API and binds an external connection string for Postgres — **no Docker** for the database; we use the user's existing local Postgres.

---

## Prerequisites

- **.NET 10 SDK 10.0.101** (pinned in `global.json`).
- **Postgres 16** running locally with both extensions installed:
  - `pgvector` (https://github.com/pgvector/pgvector) — typically prebuilt for Windows in EnterpriseDB installer extras.
  - `Apache AGE` 1.6 (https://age.apache.org/) — see "Installing AGE" below; **no native Windows build** exists.
- For cloud deploy later: **Azure Database for PostgreSQL Flexible Server PG 16** supports AGE 1.6 since January 2026 (allowlist via `azure.extensions` server parameter — managed install, no manual build needed).

### Installing AGE on Windows

Apache AGE has no official Windows binaries. The Docker image is the upstream "supported" path, but per project convention we don't use Docker for local services. Two viable options on Windows:

**Option A — WSL2 + Linux Postgres (recommended for local dev).**

WSL2 ships a Linux kernel via Microsoft's hypervisor; Postgres + AGE compile against it cleanly. The .NET app on Windows connects to `localhost` and WSL2 forwards the port automatically.

```powershell
wsl --install -d Ubuntu-24.04
wsl -d Ubuntu-24.04 -- bash -lc '
  sudo apt update &&
  sudo apt install -y postgresql-16 postgresql-server-dev-16 build-essential git &&
  cd /tmp &&
  git clone https://github.com/apache/age.git &&
  cd age && git checkout release/PG16/1.6.0 &&
  make PG_CONFIG=/usr/lib/postgresql/16/bin/pg_config &&
  sudo make PG_CONFIG=/usr/lib/postgresql/16/bin/pg_config install
'
```

Then add to `/etc/postgresql/16/main/postgresql.conf` inside WSL: `shared_preload_libraries = ''age''`, restart Postgres, allow connections from Windows in `pg_hba.conf` (`host all all 0.0.0.0/0 scram-sha-256`), and use `Host=localhost;Port=5432` from the .NET app.

**Option B — build AGE on native Windows MSVC (advanced).**

Use Postgres install dev headers (`include/server/`), Visual Studio MSVC, and adapt the AGE Makefile — community guides exist but it's not officially supported. Skip unless you specifically want to avoid WSL.

**Option C — defer the graph layer.**

If you want to start without AGE, set `Storage:GraphName` to empty and the storage layer's `IGraphContext` calls will fail loudly. Vector search and Reflection will still work because they don't touch AGE. Entity / Edge persistence and `get_entity` / search-with-related-entities will throw until AGE is set up.

---

## Quick start

```bash
# 1. Create the database and enable extensions/graph (idempotent — extensions + AGE only)
createdb -h localhost -U postgres llm_memory
psql -h localhost -U postgres -d llm_memory -f scripts/init-db.sql

# 2. Apply EF Core migrations (creates schema "memory", tables, indexes, RLS policies)
export MEMORY_DESIGN_CONNSTR="Host=localhost;Port=5432;Database=llm_memory;Username=postgres;Password=YOUR_PASS"
dotnet ef database update --project src/Memory.Storage --context MemoryDbContext

# 3. Configure the runtime connection string for the AppHost
dotnet user-secrets set "ConnectionStrings:memorydb" \
  "Host=localhost;Port=5432;Database=llm_memory;Username=postgres;Password=YOUR_PASS" \
  --project Memory.AppHost

# 4. Run the Aspire dashboard + API
dotnet run --project Memory.AppHost
```

Open the Aspire dashboard URL it prints, click into `memory-api`, you should see the `/` endpoint, OpenAPI at `/openapi/v1.json`, and the MCP HTTP transport at `/mcp`.

After step 2 every connection from the app must set its tenant scope before doing any work — Storage will inject `SET LOCAL app.organization_id` / `SET LOCAL app.project_id` GUCs on each opened connection so RLS policies admit only the active project's rows.

To run the local stdio MCP server (for Claude Code / Codex CLI):

```bash
dotnet run --project src/Memory.Mcp.Stdio
```

For now this expects `appsettings.json` next to the binary with a real Postgres connection string and a tenant scope. Once a project bootstrap CLI lands (`memory init`), you will not have to do this by hand.

---

## Project layout

```
src/
  Memory.Domain/            # entities, value objects, identifiers
  Memory.Tenancy/           # ITenantContext + AmbientTenantContext (AsyncLocal)
  Memory.Storage/           # EF Core DbContext, IGraphContext (AGE wrapper)
  Memory.Llm/               # ILlmGateway over Microsoft.Extensions.AI
  Memory.Pipeline/          # IIngestionPipeline, ISearchPipeline
  Memory.Mcp/               # MCP tools shared across stdio + HTTP
  Memory.Mcp.Stdio/         # console exe — local MCP server for Claude Code
  Memory.Cli/               # `memory` CLI — init, project bootstrap
  Memory.Api/               # ASP.NET Core: REST + MCP HTTP + Aspire ServiceDefaults
  Memory.Web/               # Blazor WebAssembly client (graph viewer, planned)
  Memory.ServiceDefaults/   # Aspire shared OpenTelemetry / resilience
Memory.AppHost/             # .NET Aspire orchestration
tests/
  Memory.{Domain,Storage,Llm,Pipeline,E2E}.Tests/
scripts/
  init-db.sql               # one-time PG setup (extensions + graph + schema)
.mcp.json                   # MCP server config for Claude Code / Codex CLI
```

---

## Hooking up to Claude Code (or Codex CLI)

After step 4 of the quickstart, seed an organization / user / project:

```bash
dotnet run --project src/Memory.Cli -- init \
  --connection-string "Host=localhost;Database=llm_memory;Username=postgres;Password=YOUR_PASS" \
  --org "MyOrg" \
  --user-email "me@example.com" \
  --user-name "Me" \
  --project "default"
```

That prints three GUIDs (org / user / project). Export them plus your LLM creds as environment variables — the project root `.mcp.json` references them via `${VAR}` substitution:

```bash
export MEMORY_DB_CONNSTR="Host=localhost;Database=llm_memory;Username=postgres;Password=YOUR_PASS"
export MEMORY_ORG_ID="<org-uuid>"
export MEMORY_USER_ID="<user-uuid>"
export MEMORY_PROJECT_ID="<project-uuid>"
export MEMORY_LLM_CHAT_PROVIDER="AzureOpenAI"      # or OpenAI
export MEMORY_LLM_CHAT_MODEL="gpt-5-mini"
export MEMORY_LLM_EMBEDDING_PROVIDER="OpenAI"      # or AzureOpenAI
export MEMORY_LLM_EMBEDDING_MODEL="text-embedding-3-large"
# plus the provider-specific block (see .mcp.json) — e.g.
export MEMORY_OPENAI_KEY="sk-..."
```

Build the stdio MCP server once (so the dll path in `.mcp.json` resolves):

```bash
dotnet build src/Memory.Mcp.Stdio
```

Open Claude Code in the repo — it reads `.mcp.json` automatically and launches the `memory` server. Verify with `/mcp` inside Claude Code; you should see `save_episode`, `search_memory`, `get_entity` listed.

For Codex CLI: same `.mcp.json` works — Codex reads it from the project root.

---

## Roadmap

**MVP (in progress).** Solution skeleton, MCP tools wired, real ingest + hybrid search against a real local Postgres.

**v1.** Bitemporal edges, sleep-time reflection job, A-MEM-style auto-linking, Web UI in Blazor (graph + timeline + entity pages), AWS Bedrock and GCP Vertex providers.

**v2.** Personalized PageRank for multi-hop retrieval (HippoRAG 2), cross-encoder reranking, conflict detection in temporal graph, Obsidian import/export, multi-tenant SaaS auth.

---

## Notes on testing

Per project rule (see `~/.claude/CLAUDE.md`), tests against the LLM layer **always use real provider calls** — no mocks. Storage tests run against the user's local Postgres (no Testcontainers). Run live tests gated by an env var pattern that the test fixtures look for, to keep accidental cost / DB writes contained.
