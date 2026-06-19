<div align="center">

# 🧠 LLM Memory

**A real memory system for LLM assistants.**
Cross-model, cross-session, hybrid-retrieval, bi-temporal — exposed via the **Model Context Protocol** so Claude Code, Codex, Cursor, and any future MCP client share the same persistent brain.

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4)](https://dotnet.microsoft.com/)
[![Postgres](https://img.shields.io/badge/Postgres-16%20%2B%20pgvector%20%2B%20AGE-336791)](https://www.postgresql.org/)
[![MCP](https://img.shields.io/badge/MCP-stdio%20%2B%20HTTP-blue)](https://modelcontextprotocol.io/)
[![Status](https://img.shields.io/badge/status-working-brightgreen)](#status)

</div>

---

## Why this exists

LLM context windows are big but **not persistent**. Every new session you re-explain your stack, re-state your preferences, re-paste the same docs, and lose the small observations that build into expertise. Vanilla RAG over a folder of files doesn't fix it — it has no memory of *when* a fact stopped being true, no graph of how things relate, no awareness of the conversation that produced a note.

LLM Memory gives an assistant a real memory:

- **Notes that supersede each other** when reality changes (`X used to … now …` is a first-class operation, not a deletion).
- **A graph that links a fact to the conversation that produced it**, traversable bi-temporally (what did I believe last March? what's still valid now?).
- **A retrieval pipeline that returns the *right* thing**, not the most recent — vector + BM25 + personalized PageRank + LLM rerank, with per-stream provenance attached to every hit.
- **Cross-model on purpose**: Claude, GPT, Gemini, and Anthropic-via-Bedrock all read from the same store. Your memory follows you between assistants.

---

## What it looks like in use

A typical Claude Code session — three calls, three different shapes of memory:

```jsonc
// 1. You hand the assistant something that happened today.
{ "tool": "save_episode", "arguments": {
    "source": "claude-code",
    "content": "Switched the api-keys table from per-tenant role grants to a single \
                NOBYPASSRLS memory_app role. Migration 20260504210400."
} }
// → 1 episode → 2 notes (Decision, Pattern) → 5 entities → 4 graph edges → embedded.

// 2. Two weeks later you ask a question.
{ "tool": "search_memory", "arguments": { "query": "how do we enforce tenant isolation?" } }
// → 3 hits, top score 0.92, provenance:
//   { fromVector: true, fromBm25: true, fromGraph: true, rerankerScore: 0.97 }
//   Content: "Runtime connections must use memory_app (NOBYPASSRLS); postgres
//             role bypasses RLS regardless of FORCE ROW LEVEL SECURITY."

// 3. Once a week, ask for a synthesis across recent notes.
{ "tool": "reflect", "arguments": { "scope": "weekly", "maxNotes": 30 } }
// → multi-paragraph reflection identifying recurring themes, decisions made,
//   open questions; stored as its own searchable note.
```

The same store is also reachable as plain HTTP (`POST /api/search`, `GET /api/notes`, `GET /api/backup/download`), through a Blazor admin UI, or via the `memory` CLI.

---

## What's actually in the box

| | |
|---|---|
| 🧬 **Hybrid retrieval** | Vector (pgvector cosine, 3072-dim) + BM25 (`tsvector`) + Graph PPR (HippoRAG-2-style) + optional cross-modal image vector. Reciprocal Rank Fusion → time decay → LLM rerank. Per-hit provenance. |
| 📝 **Multi-note extraction** | Each ingested episode is split by an LLM into 1-5 atomic Zettelkasten-style notes. Notes carry two axes: `NoteKind` (Decision / Pattern / Observation / Learning / Error) and `MemoryType` (Semantic / Episodic / Procedural / Preference / Document / Reflection). Single batched embedding call. |
| 🕒 **Bi-temporal graph** | AGE Cypher with `valid_from / valid_to / recorded_at / invalidated_at`. Supersession is a first-class operation, not a delete. Cytoscape graph viewer renders dashed edges for invalidated facts. |
| 🔗 **A-MEM auto-linking** | New notes are linked to similar prior notes by similarity + LLM judgment, building a self-organizing knowledge web rather than a flat list. |
| 🪞 **Reflection hierarchy** | Background service synthesizes recent notes into reflections; meta-reflections fold *across* prior reflections to surface long-arc themes (Letta sleep-time pattern). |
| 🔐 **Multi-tenant by RLS** | Org → User → Project hierarchy enforced at the **database** layer via Postgres RLS + a `memory_app` NOBYPASSRLS role. API keys (SHA-256-hashed) resolve tenant scope. Admin keys gate `/api/secrets/*`. |
| 🎛️ **Multi-provider LLM** | Azure OpenAI (Foundry v1), OpenAI direct, Anthropic, AWS Bedrock chat, Google Vertex chat. Switch via config — no code change. Embedding provider is independent from chat. |
| 🧰 **MCP, REST, Web, CLI** | MCP stdio for Claude Code / Codex / Cursor; MCP HTTP at `/mcp` for cloud agents; typed save tools (`save_decision`, `save_coding_pattern`, UI/debug findings), hygiene tools, REST at `/api/*`; Blazor admin UI at `/`; `memory` CLI for ops. |
| 📦 **Backup, eval, ops** | One-click tenant zip (`/api/backup/download`), retrieval eval harness (Recall@K + MRR + latency + route ablations), Markdown round-trip (Obsidian-compatible), Azure Key Vault → OpenBao → JSON secret chain. |

## Status

**Working.** Hybrid retrieval, multi-note extraction, bi-temporal supersession, A-MEM auto-linking, reflection hierarchy, multi-tenant RLS, multi-provider LLM, MCP stdio + HTTP, Blazor admin UI, Azure Key Vault → OpenBao → JSON secret-source chain, backup zip, image embeddings, webhooks, dashboard — all live and verified end-to-end.

**Retrieval baseline** on the dev tenant: Recall@1 = 93%, Recall@3 = 100%, MRR = 0.96 across 15 LLM-generated queries.

**Security**: tenant isolation enforced at the DB layer (NOBYPASSRLS), `/api/secrets/*` requires admin-scoped API keys, header-based tenant fallback is dev-only, CORS allow-listed in production. See [SECURITY.md](SECURITY.md).

---

## Architecture

```
Claude Code / Codex / Cursor    ──stdio──▶  Memory.Mcp.Stdio
Other MCP clients               ──HTTP+SSE──▶  Memory.Api  (also: REST + Blazor)
Web UI (Blazor WASM)            ──HTTPS──▶ Memory.Api

  Memory.Pipeline  ingest, search, reflect, save-filter, query expansion, linker
  Memory.Llm       multi-provider IChatClient + IEmbeddingGenerator
  Memory.Storage   EF Core + AGE Cypher + pgvector
  Memory.Secrets   Azure KV → OpenBao → JSON config-provider chain
  Memory.Tenancy   Org/User/Project AsyncLocal scope

                ┌──────────────────────────────────────┐
                │  PostgreSQL 16                        │
                │   • pgvector (cosine, 3072-dim)       │
                │   • Apache AGE 1.6 (memory_graph)     │
                │   • RLS via memory_app NOBYPASSRLS    │
                │   • bi-temporal edges                 │
                │     (valid_from/to, recorded_at,      │
                │      invalidated_at)                  │
                └──────────────────────────────────────┘
```

`Memory.AppHost` (.NET Aspire 13) orchestrates the API and binds an external connection string for Postgres — local dev uses an existing local Postgres (no Docker, by project rule).

Deep dive: **[docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)** has the full data-flow diagrams for `save_episode` and `search_memory`, the key invariants, and the operational gotchas.

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

# 2. Apply EF Core migrations as superuser. Creates schemas, tables, RLS policies,
#    and the memory_app role with NOBYPASSRLS. (postgres bypasses RLS; memory_app
#    does NOT — your runtime must connect as memory_app.)
export MEMORY_DESIGN_CONNSTR="Host=localhost;Port=5432;Database=llm_memory;Username=postgres;Password=YOUR_PASS"
dotnet ef database update --project src/Memory.Storage

# 3. Seed an Org / User / Project — the tenant scope every request will run under.
dotnet run --project src/Memory.Cli -- init \
  --connection-string "$MEMORY_DESIGN_CONNSTR" \
  --org "MyOrg" --user-email "me@example.com" --user-name "Me" \
  --project "default" --embedding-model "text-embedding-3-large"
# → prints three GUIDs (org / user / project)

# 4. Mint an API key for /api/* and /mcp.
dotnet run --project src/Memory.Cli -- api-key create \
  --connection-string "$MEMORY_DESIGN_CONNSTR" \
  --org <org-guid> --user <user-guid> --project <project-guid> \
  --name "claude-code"
# Add --admin if this key should be allowed to call /api/secrets/*.

# 5. Configure Memory.Api appsettings.Local.json (copy from .example, fill in
#    LLM creds and the runtime connection string with Username=memory_app).

# 6. Run.
dotnet run --project Memory.AppHost
```

The Aspire dashboard prints the URLs. Click `memory-api`; visit `/`, `/openapi/v1.json`, `/api/health`. The Blazor admin UI is on Memory.Web.

For Claude Code / Codex CLI:

```bash
dotnet build src/Memory.Mcp.Stdio
cp .mcp.json.example .mcp.json     # then fill in your bearer token
```

The MCP server registers typed write tools (`save_user_preference`,
`save_decision`, `save_coding_pattern`, `save_ui_test_finding`,
`save_debug_finding`), raw `save_episode`, `search_memory`, `get_entity`,
`reflect`, `find_related_notes`, hygiene tools (`supersede_note`,
`invalidate_graph_edge`, `list_memory_hygiene`), plus the
`memory_agent_guidance` prompt so capable clients can load proactive
memory-use rules into their agent context.

Typed write tools use the fast direct-note path: one curated note, no LLM
extraction/entity extraction, and deferred embedding/A-MEM linking in a
background worker. Raw `save_episode` keeps the full extraction path unless the
caller explicitly sets `directNote=true`.

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
| `Cors:AllowedOrigins:[…]` | Production CORS allowlist (dev allows everything) |
| `ReflectionSchedule:Enabled=true` + `Tenants:[…]` | BG synthesis per tenant on a fixed interval |
| `MEMORY_KV_URI` | Pull secrets from Azure Key Vault (DefaultAzureCredential) |
| `MEMORY_BAO_ADDR` + `MEMORY_BAO_TOKEN` | Pull secrets from OpenBao / HashiCorp Vault |

The secret-source chain is **JSON < OpenBao < Azure KV** (later wins); each layer is opt-in by setting its env vars.

---

## Project layout

```
src/
  Memory.Domain/            entities, typed IDs, NoteKind + MemoryType enums
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
docs/                       focused docs — see Documentation below
```

---

## Retrieval evaluation

Built-in eval harness for measuring search quality:

```bash
# 1. Sample N recent notes; LLM writes one realistic query per note (gold = note id)
memory eval gen-queries --count 30 --out eval-queries.json

# 2. Replay through /api/search; print Recall@K + MRR
memory eval run --top-k 10 --out baseline.json
memory eval run --top-k 10 --mode graph_rag --out graph.json
memory eval run --top-k 10 --no-reranker --out no-reranker.json

# 3. Or compare the standard route profiles in one pass
memory eval sweep --in eval-queries.json --out-dir eval-results
```

Run before/after a pipeline tweak (Reranker / GraphRetrieval / QueryExpansion / TimeDecay env vars) to see the actual delta. Without numbers, every "improvement" is a guess.

---

## CLI reference

```
memory init                Seed an organization / user / project tenant scope.
memory api-key {create,list,revoke}
                           Manage Memory.Api bearer-token API keys.
                           --admin marks a key as eligible for /api/secrets/*.
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

Three layers of coverage, gated so a fresh checkout doesn't need any creds:

- **Pure unit** (`Memory.Domain.Tests`, `Memory.Llm.Tests`, `Memory.Api.Tests`,
  fast tests in `Memory.Pipeline.Tests`): no DB, no LLM, no network. RRF
  fusion math, slug generation, API-key hashing, admin-scope filter, LLM
  gateway provider routing, etc. ~46 tests; runs in seconds.
  ```bash
  dotnet test --filter "FullyQualifiedName!~Live"
  ```
- **Live integration** (`LivePg`-flagged tests in `Memory.Storage.Tests` and
  `Memory.Pipeline.Tests`): hit the user's local Postgres + AGE + pgvector.
  Provision via `MEMORY_TEST_PG_PASSWORD` / `MEMORY_TEST_PG_PORT` env vars.
  By project rule: real DB, no Testcontainers.
- **Live LLM** (`LiveLlm`-flagged tests): real provider calls (no mocks of
  `IChatClient` / `IEmbeddingGenerator`). Gated by `MEMORY_LIVE_LLM_TESTS=1`
  so they never fire by accident — token cost is real.

`scripts/smoke-test-api.sh` is the comprehensive E2E probe — health, list
endpoints, search variants, faceted filters, expansion, RLS isolation.
`scripts/smoke-test-mcp.sh` exercises the stdio MCP path: initialize →
save_episode → search_memory → reflect.

---

## Documentation

Detailed docs live under [`docs/`](docs/):

| Doc | What it covers |
|---|---|
| [ARCHITECTURE.md](docs/ARCHITECTURE.md) | Module map, data flow diagrams for `save_episode` and `search_memory`, key invariants, where state lives, operational gotchas. |
| [API.md](docs/API.md) | Every HTTP endpoint with curl examples — health, search, ingest, streaming chat, webhooks, eval, secrets admin, backup, MCP transport. |
| [CONFIGURATION.md](docs/CONFIGURATION.md) | Every config section + env var. Defaults, sources, the secret-source chain (Azure KV → OpenBao → JSON). |
| [DOCUMENT-RAG-BLOB.md](docs/DOCUMENT-RAG-BLOB.md) | Planned Homelab Blob-backed document RAG: schema, ingest/retrieval flow, route modes, DevHub integration and guardrails. |
| [EVALS.md](docs/EVALS.md) | Retrieval eval runbook: golden sets, profile comparisons, latency and quality gates. |
| [MCP-INTEGRATION.md](docs/MCP-INTEGRATION.md) | How to wire to Claude Code, Codex CLI, Cursor, Continue, ChatGPT desktop. Cross-model usage patterns. |
| [USE-CASES.md](docs/USE-CASES.md) | Practical setups for programming notes, health log, personal life, research, shared collaboration. |
| [PRIVACY.md](docs/PRIVACY.md) | What leaves your machine, by default. Per-provider retention. Recommended setups for sensitive content. Threat model. |
| [OPERATIONS.md](docs/OPERATIONS.md) | Daily start-up, healthcheck, mint API keys, backup/restore, Markdown round-trip, eval, migrations, OpenBao + Azure KV ops, troubleshooting. |

Release notes for each version: [CHANGELOG.md](CHANGELOG.md).

---

## Security

Found a vulnerability? See [SECURITY.md](SECURITY.md). Tenant isolation, key
hashing, admin-scoped secret endpoints, and the secret-source chain are the
load-bearing pieces — please report cleanly before opening a public issue.

## License

[MIT](LICENSE) — © 2026 Damian Tarnowski. Use it, fork it, ship it. No warranty.
