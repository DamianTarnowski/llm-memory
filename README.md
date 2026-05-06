# LLM Memory

Stateful "second brain" memory system for LLM assistants — exposed via the **Model Context Protocol (MCP)** so Claude Code, OpenAI Codex, and other clients can read and write into it. Built on **.NET 10** with a single **Postgres** instance backing both vectors (pgvector) and a temporal knowledge graph (Apache AGE).

> **Status:** working. Hybrid retrieval, multi-note extraction, bi-temporal supersession, A-MEM auto-linking, reflection hierarchy, multi-tenant RLS, multi-provider LLM, MCP stdio + HTTP, Blazor admin UI, Azure Key Vault → OpenBao → JSON secret-source chain — all live and verified end-to-end. Retrieval baseline on the dev tenant: Recall@1 = 93%, Recall@3 = 100%, MRR = 0.96 across 15 LLM-generated queries.

---

## What it does, in 6 bullets

- **Ingest**: episodes (chat turns, docs, observations) → LLM splits into 1-5 atomic Zettelkasten notes → embeds each (single batched call) → upserts entities and edges into a temporal graph → A-MEM auto-links to prior notes by similarity + LLM judgment.
- **Search**: a query fans out to *three* retrievers — dense vector (pgvector cosine), BM25 (`tsvector`), and personalized PageRank (HippoRAG-2-inspired, seeded from LLM-extracted entity hints). Three lists fuse via Reciprocal Rank Fusion, optional time-decay, then an LLM reranker scores top candidates for relevance. Each hit carries per-stream provenance (`fromVector` / `fromBm25` / `fromGraph` / `rerankerScore`).
- **Reflect**: scheduled background job synthesizes recent notes into a multi-paragraph reflection (Letta sleep-time pattern). Meta-reflections fold *across* prior reflections to surface long-arc themes.
- **Bi-temporal supersede**: when an episode says "X used to … now …", the LLM emits the prior `(from, relation, to)` and the pipeline marks that edge `invalidated_at = now`. Search and graph view honor it; superseded edges show dashed in the Cytoscape viewer.
- **Multi-tenant from the data layer**: Org → User → Project hierarchy, Postgres RLS enforced via a `memory_app` role (NOBYPASSRLS). API key bearer auth (SHA-256-hashed) resolves tenant scope per request.
- **Operations surface**: REST + MCP HTTP at `/mcp`, MCP stdio for Claude Code / Codex CLI, Blazor WASM at `/` with Search / Notes / Entities / Graph (Cytoscape) / Secrets pages, CLI for init / api-key / backup / chat / tenants / eval.

## Architecture

```
Claude Code / Codex CLI    ──stdio──▶  Memory.Mcp.Stdio
Other MCP clients          ──HTTP+SSE──▶  Memory.Api  (also: REST + Blazor)
Web UI (Blazor WASM)       ──HTTPS──▶ Memory.Api

  Memory.Pipeline  ingest, search, reflect, save-filter, query expansion
  Memory.Llm       multi-provider IChatClient + IEmbeddingGenerator
  Memory.Storage   EF Core + AGE Cypher + pgvector
  Memory.Secrets   Azure KV → OpenBao → JSON config-provider chain
  Memory.Tenancy   Org/User/Project AsyncLocal scope

                ┌──────────────────────────────────────┐
                │  PostgreSQL 16                        │
                │   • pgvector (cosine, 3072-dim)       │
                │   • Apache AGE 1.6 (memory_graph)     │
                │   • RLS via memory_app NOBYPASSRLS    │
                │   • bi-temporal edges (valid_from/to, │
                │     recorded_at, invalidated_at)      │
                └──────────────────────────────────────┘
```

`Memory.AppHost` (.NET Aspire 13) orchestrates the API and binds an external connection string for Postgres — local dev uses an existing local Postgres (no Docker, by project rule).

---

## Prerequisites

- **.NET 10 SDK 10.0.101** (pinned in `global.json`).
- **Postgres 16** with both extensions:
  - `pgvector` 0.8+
  - `Apache AGE` 1.6+ (no native Windows build — see "AGE on Windows" below)
- One LLM provider with chat + embeddings. Currently wired:
  Azure OpenAI (Foundry v1), OpenAI direct, Anthropic, AWS Bedrock chat, Google Vertex chat.
- *(Optional)* Azure Key Vault and/or OpenBao if you want secrets out of `appsettings.Local.json`.

### AGE on Windows (WSL2 path)

```bash
wsl --install -d Ubuntu-22.04
wsl -d Ubuntu-22.04 -- bash -lc '
  sudo apt update &&
  sudo apt install -y postgresql-16 postgresql-server-dev-16 build-essential git &&
  cd /tmp && git clone https://github.com/apache/age.git && cd age &&
  git checkout release/PG16/1.6.0 &&
  make PG_CONFIG=/usr/lib/postgresql/16/bin/pg_config &&
  sudo make PG_CONFIG=/usr/lib/postgresql/16/bin/pg_config install
'
```

Add to `/etc/postgresql/16/main/postgresql.conf` inside WSL:
```
shared_preload_libraries = 'age'
```
restart Postgres, then create the graph from psql:
```sql
CREATE EXTENSION IF NOT EXISTS vector;
CREATE EXTENSION IF NOT EXISTS age;
LOAD 'age';
SET search_path = ag_catalog, "$user", public;
SELECT create_graph('memory_graph');
```

WSL2 forwards localhost ports to Windows automatically — the .NET app on Windows connects to `Host=localhost;Port=5432` (or whatever port you used).

---

## Quick start

```bash
# 1. Create the database
createdb -h localhost -U postgres llm_memory
psql -h localhost -U postgres -d llm_memory -f scripts/init-db.sql

# 2. Apply EF Core migrations as superuser (creates schema, tables, RLS policies,
#    memory_app role with NOBYPASSRLS — postgres bypasses RLS, memory_app does not)
export MEMORY_DESIGN_CONNSTR="Host=localhost;Port=5432;Database=llm_memory;Username=postgres;Password=YOUR_PASS"
dotnet ef database update --project src/Memory.Storage

# 3. Seed an Org/User/Project
dotnet run --project src/Memory.Cli -- init \
  --connection-string "$MEMORY_DESIGN_CONNSTR" \
  --org "MyOrg" --user-email "me@example.com" --user-name "Me" \
  --project "default" --embedding-model "text-embedding-3-large"
# → prints three GUIDs (org / user / project)

# 4. Configure Memory.Api appsettings.Local.json (copy from .example, fill in
#    LLM creds and the runtime connection string with Username=memory_app)

# 5. Run
dotnet run --project Memory.AppHost
```

Aspire dashboard prints URLs. Click `memory-api`; visit `/`, `/openapi/v1.json`, `/api/health`. Visit Memory.Web for the admin UI.

For Claude Code / Codex CLI:

```bash
dotnet build src/Memory.Mcp.Stdio
```

then point your MCP config at the stdio binary (or use the project's `.mcp.json`). The server registers `save_episode`, `search_memory`, `get_entity`, `reflect`, `find_related_notes`.

---

## Configuration knobs (Memory.Api)

All optional, all opt-in via `appsettings.Local.json` or env vars.

| Section / env var | Effect |
|---|---|
| `Linking:Enabled=true` | A-MEM auto-link new notes to similar prior notes |
| `Reranker:Enabled=true` | LLM reranker scores top fused candidates (off → RRF order) |
| `GraphRetrieval:Enabled=true` | PPR over the entity graph as a 3rd RRF stream |
| `TimeDecay:Enabled=true` (HalfLifeDays=30) | Recency boost on hits before reranking |
| `QueryExpansion:Enabled=true` (MaxQueryWords=4) | LLM rewrites short queries into 2-3 variants |
| `SaveFilter:Enabled=true` (MinScore=0.30) | LLM judges importance pre-ingest; drops noise |
| `MEMORY_KV_URI` | Pull secrets from Azure Key Vault (DefaultAzureCredential) |
| `MEMORY_BAO_ADDR` + `MEMORY_BAO_TOKEN` | Pull secrets from OpenBao / HashiCorp Vault |

The secret-source chain is **JSON < OpenBao < Azure KV** (later wins); each layer is opt-in by setting its env vars. Skip a layer entirely by leaving its env vars unset.

---

## Project layout

```
src/
  Memory.Domain/            entities, typed IDs, NoteKind enum
  Memory.Tenancy/           ITenantContext + AmbientTenantContext (AsyncLocal)
  Memory.Storage/           EF Core DbContext, AGE Cypher wrapper, RLS interceptor
  Memory.Llm/               5-provider gateway over Microsoft.Extensions.AI
  Memory.Pipeline/          ingest, search (vector+BM25+PPR+rerank), reflect,
                              save-filter, query-expansion, A-MEM linker
  Memory.Mcp/               MCP tools shared across stdio + HTTP
  Memory.Mcp.Stdio/         console exe — local MCP for Claude Code / Codex CLI
  Memory.Cli/               `memory` CLI — init, api-key, backup, chat, tenants, eval
  Memory.Api/               ASP.NET Core: REST + MCP HTTP + secrets admin
  Memory.Web/               Blazor WASM — Search, Notes, Entities, Graph, Secrets
  Memory.Secrets/           IConfigurationSource chain: Azure KV / OpenBao / JSON
  Memory.ServiceDefaults/   Aspire shared OpenTelemetry / resilience
Memory.AppHost/             .NET Aspire orchestration
tests/                      Domain / Storage / Pipeline / E2E
scripts/                    init-db.sql, smoke-test-api.sh, smoke-test-mcp.sh
```

---

## Retrieval evaluation

Built-in eval harness for measuring search quality:

```bash
# 1. Sample N recent notes; LLM writes one realistic query per note (gold = note id)
memory eval gen-queries --count 30 --out eval-queries.json

# 2. Replay through /api/search; print Recall@K + MRR
memory eval run --top-k 10
```

Run before/after a pipeline tweak (Reranker / GraphRetrieval / QueryExpansion / TimeDecay env vars) to see the actual delta. Without numbers, every "improvement" is a guess.

---

## CLI reference

```
memory init                Seed an organization / user / project tenant scope.
memory api-key {create,list,revoke}
                           Manage Memory.Api bearer-token API keys.
memory backup {dump,restore,download}
                           Tenant data backup. dump/restore = direct DB JSON;
                           download = HTTP-streamed .zip from any deploy.
memory chat                Conversational REPL against /api/search etc.
memory tenants {provision-schema,list,drop-schema}
                           Schema-per-org tenancy foundation.
memory eval {gen-queries,run}
                           Retrieval evaluation (Recall@K + MRR).
```

`memory help` (or `memory <cmd> help`) prints flags.

---

## Notes on testing

Per project rule (in `~/.claude/CLAUDE.md`), tests against the LLM layer **always use real provider calls** — no mocks. Storage tests run against the user's local Postgres (no Testcontainers). Run live tests gated by `MEMORY_LIVE_LLM_TESTS=1` so they don't fire by accident.

`scripts/smoke-test-api.sh` is the comprehensive E2E probe — health, list endpoints, search variants, faceted filters, expansion, RLS isolation. Currently 19/19 passing.

`scripts/smoke-test-mcp.sh` exercises the stdio MCP path: initialize → save_episode → search_memory → reflect.

---

## Documentation

Detailed docs live under [`docs/`](docs/):

| Doc | What it covers |
|---|---|
| [ARCHITECTURE.md](docs/ARCHITECTURE.md) | Module map, data flow diagrams for `save_episode` and `search_memory`, key invariants, where state lives, operational gotchas. |
| [API.md](docs/API.md) | Every HTTP endpoint with curl examples — health, search, ingest, streaming chat, webhooks, eval, secrets admin, MCP transport. |
| [CONFIGURATION.md](docs/CONFIGURATION.md) | Every config section + env var. Defaults, sources, the secret-source chain (Azure KV → OpenBao → JSON). |
| [MCP-INTEGRATION.md](docs/MCP-INTEGRATION.md) | How to wire to Claude Code, Codex CLI, Cursor, Continue, ChatGPT desktop. Cross-model usage patterns. |
| [USE-CASES.md](docs/USE-CASES.md) | Practical setups for programming notes, health log, personal life, research, shared collaboration. |
| [PRIVACY.md](docs/PRIVACY.md) | What leaves your machine, by default. Per-provider retention. Recommended setups for sensitive content. Threat model. |
| [OPERATIONS.md](docs/OPERATIONS.md) | Daily start-up, healthcheck, mint API keys, backup/restore, Markdown round-trip, eval, migrations, OpenBao + Azure KV ops, troubleshooting. |
