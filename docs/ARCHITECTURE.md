# Architecture

How the system actually works, end-to-end. Read this when you want to understand
why a request lands where it does, what each module owns, and which seams to cut
along when extending.

## Module map

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ Client side                                                                   │
│   Claude Code CLI / Codex CLI ──stdio──┐                                      │
│   Cursor / Continue / others   ──stdio──┤                                      │
│   ChatGPT desktop / agents     ──HTTP──┐│                                      │
│   Memory.Web (Blazor WASM)     ──HTTP──┤│                                      │
│                                         ││                                      │
└─────────────────────────────────────────┼┼──────────────────────────────────────┘
                                          ▼▼
┌─────────────────────────────────────────────────────────────────────────────┐
│ Memory.Mcp.Stdio (console exe) │ Memory.Api (ASP.NET Core)                  │
│                                │                                              │
│  Tools shared from Memory.Mcp: │  REST + MCP HTTP at /mcp + Blazor static    │
│  • save_user_preference        │  + /api/secrets/* (OpenBao admin)           │
│  • typed saves + hygiene       │  + /api/eval/* (retrieval eval)             │
│  • save_episode                │  + /api/episodes (REST ingest)              │
│  • search_memory               │  + /api/chat (SSE streaming)                │
│  • get_entity / reflect        │  + /api/webhooks/{name,slack}               │
│  • find_related_notes          │                                              │
│  Prompt: memory_agent_guidance │                                              │
└────────────────────────────────┴─────────────────────────────────────────────┘
                                            │
        ┌───────────────────────────────────┼─────────────────────────────────┐
        │                                   ▼                                  │
        │   Memory.Pipeline                                                     │
        │     • Ingestion: LlmImportanceJudge → LlmExtractor → embedding →     │
        │                  AGE entity/edge upsert → A-MEM linker (best-effort) │
        │     • Search: query routing/rewrite → query expansion → vector +     │
        │              BM25 + graph PPR + image-vector → weighted RRF fuse →   │
        │              time-decay → LLM reranker → token-budget pack →         │
        │              abstention check                                        │
        │     • Reflect: notes/window → LLM → reflection (or meta-reflection)  │
        │                                                                       │
        │   Memory.Llm                                                          │
        │     • LlmGateway over Microsoft.Extensions.AI                         │
        │     • Providers: AzureOpenAI / OpenAI / Anthropic / Bedrock / Vertex │
        │     • IImageDescriber (caption) + IImageEmbedder (cross-modal)       │
        │                                                                       │
        │   Memory.Storage                                                      │
        │     • EF Core 10 + Npgsql + pgvector                                 │
        │     • TenantConnectionInterceptor sets app.organization_id /         │
        │       app.project_id GUCs per request → RLS enforces                 │
        │     • AgeGraphContext: Cypher over Apache AGE (cypher() function)    │
        │                                                                       │
        │   Memory.Tenancy                                                      │
        │     • ITenantContext (AsyncLocal in Api, Fixed in Mcp.Stdio)         │
        │     • Org → User → Project hierarchy                                 │
        │                                                                       │
        │   Memory.Secrets                                                      │
        │     • IConfigurationSource chain: JSON < OpenBao < Azure KV          │
        │                                                                       │
        └───────────────────────────────────┬─────────────────────────────────┘
                                            ▼
        ┌───────────────────────────────────────────────────────────────────┐
        │ PostgreSQL 16 (local WSL2 / Azure Flex Server)                    │
        │  schema "memory":                                                  │
        │    organizations, users, memberships, projects                     │
        │    episodes, notes, note_embeddings (3072 dim vector)              │
        │    note_entity_mentions, note_relations (A-MEM)                    │
        │    image_embeddings (1408 dim vector, Vertex multimodal)           │
        │    reflections, api_keys, tenant_schemas                           │
        │    note.content_tsv (tsvector trigger for BM25)                    │
        │  RLS policies on every tenant-scoped table; runtime as memory_app  │
        │  (NOBYPASSRLS).                                                    │
        │                                                                    │
        │  Apache AGE graph "memory_graph":                                  │
        │    Entity nodes (id, project_id, name, kind, attributes,           │
        │                  first_seen_at, last_seen_at)                      │
        │    Edge edges  (id, project_id, relation, properties,              │
        │                  recorded_at, valid_from, valid_to,                │
        │                  invalidated_at, source_episode)                   │
        │  Bi-temporal: every edge tracks both event time + ingestion time. │
        │                                                                    │
        │  pgvector cosine search on note_embeddings + image_embeddings.    │
        └───────────────────────────────────────────────────────────────────┘
```

## Data flow — `save_episode`

```
                                        ┌───────────────────────────────────────┐
                                        │ Optional input: text + images[]       │
                                        └───────────────────┬───────────────────┘
                                                            ▼
1. SaveFilter (optional)         LlmImportanceJudge: score 0-1, reason
                                 ───────────────────► drop if < MinScore
                                                            │ (kept)
2. Multimodal v1                 IImageDescriber (vision LLM):                │
                                 image bytes → 100-300 word description        │
                                 prepend "[image: …]\n<desc>" to content       │
                                                            ▼
3. Extraction                    Default: LlmExtractor w/ schema →             │
                                 ExtractionResult:                             │
                                    notes[]: 1-5 atomic Zettelkasten,          │
                                            kind ∈ {General, Observation,     │
                                            Decision, Learning, Error,        │
                                            Pattern}                          │
                                            memoryType ∈ {Semantic, Episodic, │
                                            Procedural, Preference, Document, │
                                            Reflection}                       │
                                    entities[]: name + kind + attributes       │
                                    relationships[]: from → to via relation    │
                                    supersedesPriorEdges[]: bi-temporal hints  │
                                 Direct-note mode: typed tools / callers with  │
                                 directNote=true create one explicit note and  │
                                 skip LLM/entity extraction. With              │
                                 deferEmbedding=true, embedding/A-MEM happens │
                                 in EmbeddingBackfillService after response.   │
                                                             ▼
4. Batched embedding             IEmbeddingGenerator.GenerateAsync(notes)     │
                                 → 3072-dim vectors per note (one API call)   │
                                                            ▼
5. DB insert (transactional)     INSERT episodes, notes, note_embeddings,     │
                                 note_entity_mentions                          │
                                                            ▼
6. AGE upserts                   MERGE Entity nodes (idempotent on name)      │
                                 CREATE Edge with valid_from = now            │
                                                            ▼
7. Bi-temporal supersede         For each supersedesPriorEdges entry:         │
                                 MATCH old edge → SET invalidated_at = now    │
                                                            ▼
8. (optional) Image embed        IImageEmbedder.EmbedImageAsync(bytes)        │
                                 → 1408-dim vector → image_embeddings row     │
                                 keyed to first emitted note                  │
                                                            ▼
9. A-MEM auto-link (post-commit) For each new note:                           │
                                  • cosine top-K similar notes (≥ MinSim)     │
                                  • LLM judges {duplicates, supports, …}      │
                                  • INSERT note_relations (best-effort)       │
                                  • if duplicates@high confidence:            │
                                      mark new note as superseding old one    │
                                                            ▼
                                 Returns IngestionResult { episodeId, noteIds,│
                                 entityIds, skipped, skipReason }             │
```

## Data flow — `search_memory`

```
                  Query string
                       │
                       ▼
1. Query routing       Smart caller RouteOverride OR LlmQueryRouter fallback:
   (optional)            caller/cheap chat model decides no_rag / memory_light /
                         memory_medium / heavy_rag / graph_rag / document_rag /
                         write_memory, rewrites follow-ups into standalone
                         queries, selects retrieval stream weights, and emits
                         route trace metadata.
                       │
                       ▼
2. Query expansion     LlmQueryExpander.ExpandAsync(standalone_q):
   (optional)            short queries (≤4 words) → [original, variant1, …]
                         long queries → [original]
                       │
                       ▼
3. Embed variants      One IEmbeddingGenerator call across all variants
                       → 3072-dim vectors (1 per variant)
                       │
                       ▼
4. Per-variant vector  pgvector cosine on note_embeddings, top-K per variant
                       │
                       ▼
5. Merge variant       RRF inside the vector stream → unified ranked list
   streams
                       │
                       ▼
6. Other retrievers    BM25 (tsvector + plainto_tsquery) on note.content_tsv
   (sequential, share  Graph PPR (LlmQueryEntityExtractor → seed PPR over
   same connection)      active edges → score notes by entity-mention mass)
                       Image-vector (when Vertex configured): IImageEmbedder.
                         EmbedTextAsync(q) → cosine vs image_embeddings
                       │
                       ▼
7. Weighted RRF fuse   score(note) = Σ stream_weight / (60 + rank_in_stream)
                       Provenance flags FromVector/FromBm25/FromGraph set
                       │
                       ▼
8. Time-decay (opt)    score *= exp(-ln2 · age_days / HalfLifeDays)
                       floored at MinMultiplier
                       │
                       ▼
9. LLM reranker        Top-N in single LLM call → relevance scores
   (optional)          Filter < MinRelevance, sort desc.
                       │
                       ▼
10. Token-budget pack  When MaxTokens set: greedy fill top-by-score until
   (optional)          Σ chars/3.8 + 32/hit > budget
                       │
                       ▼
11. Abstention check   If top.Count == 0 OR top reranker score < MinTopScore
                       OR (reranker fell back AND fused score < MinFusedScore)
                       → return Abstain=true with empty hits + reason
                       │
                       ▼
                   SearchResult { hits, totalCandidates, abstain, abstainReason,
                                  route }
```

## Module boundaries (what depends on what)

```
Memory.Domain           pure types (typed-id structs, NoteKind, MemoryType,
                        entities)
                        ↑
Memory.Tenancy          ITenantContext + AsyncLocal scope
                        ↑
Memory.Storage          EF Core, Npgsql, AGE Cypher wrapper
                        ↑
Memory.Llm              IChatClient/IEmbeddingGenerator over MEAI
                        ↑
Memory.Pipeline         Ingest, search, reflect orchestration
                        ↑
Memory.Mcp              Tools (depends on Pipeline + Storage)
                        ↑                            ↑
Memory.Mcp.Stdio        Memory.Api                 (host)
                        ↑
Memory.Web              Blazor WASM (depends only on ApiClient HTTP)
```

Cross-cutting:
- **Memory.Secrets** — IConfigurationSource chain (Azure KV → OpenBao → JSON);
  used by `Memory.Api` only. Memory.Mcp.Stdio reads JSON directly to keep
  startup latency low.
- **Memory.ServiceDefaults** — Aspire's shared OpenTelemetry / resilience
  configuration.
- **Memory.AppHost** — .NET Aspire 13 orchestration entry point.

## Key invariants

1. **Every connection sets tenant GUCs.** `TenantConnectionInterceptor` runs on
   `ConnectionOpened` and emits `SET app.organization_id = …; SET app.project_id
   = …`. RLS policies key off these. Without scope, GUCs are
   `00000000-0000-0000-0000-000000000000` and RLS hides every row.
2. **Runtime never connects as superuser.** Postgres bypasses RLS for
   superusers regardless of FORCE. Migrations use `postgres`; runtime uses
   `memory_app` (NOBYPASSRLS).
3. **AGE is preloaded.** `shared_preload_libraries = 'age'` in postgresql.conf;
   the code does NOT issue `LOAD 'age'` (would require superuser).
4. **Embeddings are write-once.** A note's embedding is generated once at ingest
   time. Re-embedding is not currently supported — to switch embedding model
   you must re-ingest from raw episodes.
5. **Bi-temporal edges are never deleted.** Supersession sets
   `invalidated_at` instead of dropping the row, so historical state can always
   be queried with `validAt: <past-timestamp>`.
6. **Save filter fail-open.** When the importance judge errors (network, model
   hiccup), the episode is saved rather than dropped. Better to keep too much
   than to silently drop signal.
7. **A-MEM linking is best-effort.** Any failure in the post-commit linker
   logs a warning but doesn't tank the ingest. The episode + notes + entities
   already committed.
8. **NoteKind and MemoryType are separate.** `NoteKind` describes the statement
   shape (Decision, Pattern, Error). `MemoryType` describes how an agent should
   use it (Semantic, Episodic, Procedural, Preference, Document, Reflection).
   Typed MCP tools force both axes and bypass the optional save filter.

## Where state lives

- **Postgres `memory` schema** — all relational + vector data
- **Postgres `memory_graph` schema** — AGE graph storage
- **OpenBao file backend** — `/opt/openbao/data` (WSL2-side); persisted across
  reboots, **must be re-unsealed manually after a hard restart**
- **Azure Key Vault** — your KV instance, accessed via DefaultAzureCredential
- **WSL2 home** — `~/.config/openbao-dev-creds.json` (mode 600) holds dev
  unseal key + root token
- **Windows AppData** — `%APPDATA%\gcloud\application_default_credentials.json`
  for Vertex ADC
- **In-memory** — `AmbientTenantContext` (AsyncLocal) tenant scope per request

## Operational gotchas (in one place)

- WSL2 idles after some minutes → PG stops responding → arm a keepalive
  with `wsl -d Ubuntu --exec sleep 7200 &` before long sessions.
- DefaultAzureCredential picks `~/.azure` by default. If your Key Vault lives
  on a different subscription than your default `az login`, set
  `AZURE_CONFIG_DIR` to the alternate config directory (e.g.
  `AZURE_CONFIG_DIR=$HOME/.azure-other`).
- AGE Cypher rejects `:` in JSONB property keys; use `--` or `__` and the
  connectors rewrite back to `:` for IConfiguration.
- Postgres generated columns reject `to_tsvector` (STABLE not IMMUTABLE) —
  `notes.content_tsv` is maintained by a trigger instead.
- EF Core can't translate `List<TypedId>.Contains(...)` over value-converted
  IDs. Workaround: project to `Id.Value` array and use raw SQL with `id =
  ANY(@ids)`, or fetch then filter in-memory.
- `::deep` in Blazor scoped CSS compiles to a descendant combinator — does
  not match the top-level element of the same .razor file. Use plain selectors
  for top-level elements.
