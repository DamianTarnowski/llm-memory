# API reference

Every public HTTP surface on `Memory.Api`, with curl-able examples.

Base URL: `http://localhost:5566` in dev. Authentication via either:
- `Authorization: Bearer memk_<token>` — preferred. Tokens minted by
  `memory api-key create` and resolved through `memory.api_keys` (SHA-256
  hashed). Their tenant scope is bound at creation time.
- `X-Memory-Org-Id`, `X-Memory-User-Id`, `X-Memory-Project-Id` — dev-only
  fallback. Headers must all parse as GUIDs.

When neither auth method is present, RLS hides everything and most endpoints
return empty results (or 401 for invalid bearer).

---

## Health & info

### `GET /`
Returns service banner with auth model info.

```bash
curl http://localhost:5566/
```

### `GET /api/health`
Reports DB + LLM gateway status. 200 healthy / 503 degraded.

```bash
curl http://localhost:5566/api/health \
  -H "Authorization: Bearer memk_…"
```

Response:
```json
{
  "status": "healthy",
  "timestamp": "…",
  "db":  { "status": "ok", "latencyMs": 12 },
  "llm": { "status": "ok" }
}
```

---

## Reads

### `GET /api/episodes?limit=N`
List recent episodes (raw inputs).
- `limit`: 1-500, default 50

### `GET /api/notes?limit=N`
List recent notes (extracted atoms). Each note carries `kind` (Decision,
Learning, Error, Pattern, Observation, General).

### `GET /api/entities?name=X&limit=N`
List entities. Optional `name` filter does case-insensitive substring match.

### `GET /api/edges?limit=N`
List bi-temporal graph edges. Includes `invalidatedAt` (null for active edges).

### `GET /api/reflections?limit=N`
List reflections. Each carries `scope` (e.g. `daily`, `meta`, `meta:weekly`)
and `generatorModel`.

---

## Search

### `POST /api/search`
Hybrid retrieval (vector + BM25 + graph PPR + image-vector + reranker +
abstention).

```bash
curl -X POST http://localhost:5566/api/search \
  -H "Authorization: Bearer memk_…" \
  -H "Content-Type: application/json" \
  -d '{
    "query": "what did we decide about secrets management?",
    "maxResults": 5,
    "tags": ["infrastructure"],
    "since": "2026-01-01T00:00:00Z",
    "kinds": ["Decision"],
    "maxTokens": 2000
  }'
```

Body fields:
| Field | Type | Effect |
|---|---|---|
| `query` | string | required |
| `maxResults` | int | default 20 |
| `tags` | string[] | OR-filter; matches notes with at least one of these tags |
| `since` / `until` | ISO timestamp | created_at range |
| `kinds` | string[] | filter by Note.Kind enum names |
| `maxTokens` | int | when set, packs hits greedily by score until estimated chars/3.8 ≥ budget; takes precedence over `maxResults` |

Response:
```json
{
  "totalCandidates": 17,
  "abstain": false,
  "abstainReason": null,
  "hits": [{
    "noteId": "…",
    "content": "…",
    "score": 0.74,
    "relatedEntityIds": ["…", "…"],
    "provenance": {
      "fromVector": true, "fromBm25": true, "fromGraph": false,
      "vectorScore": 0.0164, "bm25Score": 0.0156, "graphScore": 0,
      "rerankerScore": 0.74
    }
  }]
}
```

When `abstain: true`:
- `hits: []`
- `abstainReason` explains why ("All candidates scored below the reranker
  threshold." / "No candidates matched the query across vector / BM25 /
  graph." / "Reranker filtered all candidates; fallback RRF score X
  indicates no real overlap.")

---

## Ingest

### `POST /api/episodes`
Saves an episode through the standard pipeline. Same surface as the
`save_episode` MCP tool, just over REST.

```bash
curl -X POST http://localhost:5566/api/episodes \
  -H "Authorization: Bearer memk_…" \
  -H "Content-Type: application/json" \
  -d '{
    "content": "Decided to use OpenBao instead of HashiCorp Vault…",
    "source": "meeting-notes",
    "occurredAt": "2026-05-05T09:30:00Z",
    "metadata": { "meeting_id": "abc-123" }
  }'
```

With images (vision-capable provider required):
```bash
curl -X POST http://localhost:5566/api/episodes \
  -H "Authorization: Bearer memk_…" \
  -H "Content-Type: application/json" \
  -d '{
    "content": "Whiteboard photo from the architecture review",
    "images": [{
      "data": "iVBORw0KGgo…(base64)",
      "mimeType": "image/png",
      "caption": "architecture-board"
    }]
  }'
```

Response:
```json
{
  "episodeId": "…",
  "noteIds": ["…"],
  "entityIds": ["…", "…"],
  "skipped": false,
  "skipReason": null,
  "importanceScore": null
}
```

When the save filter rejects:
```json
{
  "episodeId": null,
  "noteIds": [],
  "entityIds": [],
  "skipped": true,
  "skipReason": "Casual chatter with no durable information.",
  "importanceScore": 0.05
}
```

---

## Streaming chat

### `POST /api/chat`
Memory-aware streaming chat. Searches first, builds a numbered context block,
streams an LLM answer as Server-Sent Events.

```bash
curl -N -X POST http://localhost:5566/api/chat \
  -H "Authorization: Bearer memk_…" \
  -H "Content-Type: application/json" \
  -d '{ "query": "what tech stack does the LLM Memory project use?" }'
```

Frame protocol:
```
data: {"type":"context","hits":5,"candidates":17,"abstain":false,"abstainReason":null}

data: {"type":"delta","delta":"The"}

data: {"type":"delta","delta":" LLM"}

…

data: [DONE]
```

Errors mid-stream emit `{"type":"error","message":"…"}` followed by `[DONE]`.

Body fields:
| Field | Default |
|---|---|
| `query` | required |
| `maxHits` | 5 |
| `maxContextTokens` | 2000 (caps the search-result token budget) |

---

## Backup

### `GET /api/backup/download`
Streams a tenant-scoped `.zip` containing every entity for the caller's project.
The zip is human-browsable: per-entity JSON files plus a `notes-md/` folder of
one Markdown per active note (Obsidian-friendly YAML frontmatter).

```bash
curl -fSL https://your.host/api/backup/download \
  -H "Authorization: Bearer memk_…" \
  -o memory-backup.zip
```

Query parameters:
- `includeEmbeddings=false` — skip `note_embeddings.json` (default `true`)
- `includeImageEmbeddings=false` — skip `image_embeddings.json` (default `true`)

Zip layout:
```
manifest.json              schema version, counts, generation timestamp, tenant ids
episodes.json              raw ingestion records
notes.json                 distilled notes (active + superseded)
note_embeddings.json       text embedding vectors (when included)
note_entity_mentions.json  note→entity links
note_relations.json        note→note edges (relation_type, confidence, similarity)
reflections.json           periodic summaries
image_embeddings.json      multimodal embeddings (when included, non-empty)
entities.json              graph nodes from AGE
edges.json                 graph edges from AGE (with bi-temporal validity)
notes-md/                  one .md per active note for Obsidian / plain reading
```

Note embeddings are tied to the embedding model that produced them — restoring
into a project with a different model is effectively a re-ingest.

---

## Webhooks

### `POST /api/webhooks/{name}` — generic
Body's `content` field (when JSON) or raw body becomes episode content.
`source` set to `webhook:{name}`.

```bash
curl -X POST http://localhost:5566/api/webhooks/zapier-rss \
  -H "Authorization: Bearer memk_…" \
  -H "Content-Type: application/json" \
  -d '{ "content": "New paper on cross-modal RAG dropped on arxiv: …" }'
```

### `POST /api/webhooks/slack`
Slack Events API shape. Verifies `X-Slack-Signature` HMAC against
`WebhookConnector:SlackSigningSecret` config. Echoes the URL-verification
challenge automatically. Ingests `message` / `app_mention` events as episodes
with metadata `{ slack_channel, slack_user }`.

Slack manifest snippet:
```yaml
features:
  bot_user:
    display_name: memory
    always_online: false
oauth_config:
  scopes:
    bot:
      - app_mentions:read
      - channels:history
settings:
  event_subscriptions:
    request_url: https://your.host/api/webhooks/slack
    bot_events:
      - app_mention
      - message.channels
```

---

## Eval harness

### `POST /api/eval/queries`
Generates synthetic eval queries from existing notes. For each sampled note,
the LLM writes one realistic question that note answers; the note id is the
gold answer.

```bash
curl -X POST http://localhost:5566/api/eval/queries \
  -H "Authorization: Bearer memk_…" \
  -H "Content-Type: application/json" \
  -d '{ "count": 30 }'
```

Response is what `memory eval gen-queries` saves to `eval-queries.json`.

### `POST /api/eval/run`
Replays queries through `/api/search` and reports retrieval metrics.

```bash
curl -X POST http://localhost:5566/api/eval/run \
  -H "Authorization: Bearer memk_…" \
  -H "Content-Type: application/json" \
  -d @eval-queries.json
```

Response: Recall@1/3/5/10 + MRR + per-query rank.

CLI wrapper: `memory eval gen-queries` then `memory eval run`.

---

## Secrets admin (OpenBao proxy)

All return 503 when `MEMORY_BAO_*` env vars aren't set on the API process.

### `GET /api/secrets/status`
```json
{ "configured": true, "address": "http://127.0.0.1:8200", "mount": "secret" }
```

### `GET /api/secrets/paths?folder=…`
Lists path slugs under the mount.

### `GET /api/secrets/data?path=llm-memory`
Reads the data dict for a path.

### `PUT /api/secrets/data`
Replaces the entire data dict for a path.
```json
{ "path": "llm-memory", "keys": { "OpenAi__ApiKey": "sk-…" } }
```

### `DELETE /api/secrets/data?path=llm-memory`
Destroys the path and every version (KV v2 metadata wipe).

UI for all of the above: `/secrets` in `Memory.Web`.

---

## MCP transport

### `POST /mcp`
JSON-RPC over HTTP+SSE per the MCP spec. Use this from agents that don't have
stdio access (ChatGPT desktop, Cursor, etc).

Tools registered:
- `save_episode(source, content, occurredAt?)`
- `search_memory(query, maxResults?)`
- `get_entity(name, includeRelations?)`
- `reflect(scope, since?, until?, maxNotes?)`
- `find_related_notes(noteId, maxResults?)`

Resources surfaced:
- `memory://episodes` (recent episodes)
- `memory://notes` (recent notes)
- `memory://reflections` (recent reflections)

Memory.Mcp.Stdio offers the same tools over stdio for local CLI clients.
