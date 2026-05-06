# Privacy & safety

If you're putting personal, health, or otherwise sensitive content into this
memory, this is the doc you have to read end-to-end. The architecture is
private-friendly but it's not private *by default* — there are knobs to
turn, and you should turn them deliberately.

## What leaves your machine, by default

With the dev configuration most people start with (Azure OpenAI for chat +
embeddings, local PG, local OpenBao, local Memory.Api):

| Action | Data egress |
|---|---|
| `save_episode` | Episode text → Azure OpenAI (extraction prompt → response) → Azure OpenAI (embedding) → 1 paid request × 2 calls. Your text is in the request body. |
| `save_episode` with image | Image bytes (base64) → Azure OpenAI vision (chat completion) for caption. Optionally → Vertex multimodalembedding when enabled. |
| `search_memory` | Query string → Azure OpenAI (query expansion, optional) → Azure OpenAI (embedding) → Azure OpenAI (reranker, optional). |
| `reflect` | All N notes in the window → Azure OpenAI (single chat completion). |
| `/api/chat` | Query + top-N hit contents → Azure OpenAI (streaming completion). |
| `find_related_notes` | Note id only → DB only. No LLM call. |

**Crucially: every `save_episode` and every `search_memory` round-trips your
content through whatever LLM provider you've configured.** Embeddings are
generated server-side at the provider, so the embedded vector that ends up
in your local Postgres is derived from data they processed.

What stays local in the dev config:
- The Postgres data (notes, embeddings, edges, reflections)
- The OpenBao secret store (when used)
- The configuration files
- The MCP stdio process — runs entirely on your machine; only the LLM calls
  it makes go outbound

## Data retention by your provider

What "data leaves your machine" actually means depends on the provider's
retention policy. Quick read of the major ones (verify against current
contracts before relying):

- **Azure OpenAI** — by default retains prompts/completions for 30 days for
  abuse monitoring. **Zero-retention** SKU available on request for paid
  tiers; recommend asking for it before piping personal data through.
- **OpenAI direct API** — opt-out of training via the API; retention for
  abuse monitoring is "up to 30 days" per their docs.
- **Anthropic API** — does not train on API customer data by default;
  retention for safety review.
- **AWS Bedrock** — does not train on customer data; tenant isolation per
  account.
- **Google Vertex** — does not train on customer data; same.
- **Local model (Ollama, LM Studio, llama.cpp)** — zero egress. Slower,
  generally weaker output quality, sometimes worth it.

## Recommended setups

### Setup A — "personal but not paranoid"
Default Azure OpenAI / OpenAI provider, paid tier, ask for zero-retention SKU
on the deployment. Save filter ON so casual chitchat doesn't get embedded.
Use this for: technical notes, work decisions, low-stakes personal log.

### Setup B — "health & private"
Per-project provider routing. Run a local model (Ollama with `qwen2.5-72b`
or `llama-3.3-70b`) for the `health` and `personal` projects. Keep Azure
OpenAI for `work` and `tech` projects.

How: `Llm__ChatProvider`, `Llm__EmbeddingProvider`, etc are env vars in the
`.mcp.json` per project. Spin up Ollama on `localhost:11434`, point an
`OpenAI`-compatible adapter at it, configure that as the chat provider for
the sensitive projects only.

A future iteration of `LlmGateway` should expose a per-tenant override (open
issue) — currently it's per-process. For now, run two Memory.Mcp.Stdio
processes if you need both.

### Setup C — "fully air-gapped"
Local model + local Postgres in WSL2 + no Azure KV, no OpenBao remote, no
webhooks, no Memory.Api at all. Just `Memory.Mcp.Stdio` per project. Zero
egress. Slowest but everything stays on disk.

## What's stored, in plaintext, locally

- `notes.content` — the extracted atomic note text (English-language
  paraphrase of your input, plus any direct quotes the LLM kept)
- `notes.context_description` — short situational metadata
- `notes.keywords` / `notes.tags` — lowercase
- `episodes.content` — your raw input verbatim
- `note_embeddings.embedding` — 3072-dim vector (encodes the meaning of the
  note text; reversible-ish via embedding inversion attacks but practically
  useless without the model that produced it)
- `image_embeddings.embedding` — 1408-dim vector when Vertex is on
- AGE entities + edges — entity names and relations (e.g. `alice` →
  `LIVES_IN` → `warsaw`)
- `reflections.summary` — multi-paragraph LLM-generated synthesis of N notes
- `api_keys.key_hash` — SHA-256 of bearer tokens (raw token never stored)

What's NOT stored:
- Image bytes (only their captions + embeddings; original image is discarded
  after ingestion in v1)
- Audio / video (no support yet)
- Anything the SaveFilter dropped

## Right to forget

There's no first-class "forget this episode" command yet. To delete a memory:

```sql
-- runs as memory_app or postgres
DELETE FROM memory.notes WHERE id IN (
  SELECT id FROM memory.notes WHERE source_episode_id = '<episode-id>'
);
DELETE FROM memory.episodes WHERE id = '<episode-id>';
-- AGE entities/edges aren't auto-cleaned; either query and DELETE
-- explicitly, or live with orphan nodes.
```

The cascade on `episodes → notes → note_embeddings → note_entity_mentions →
note_relations` cleans the relational side. AGE has no FK back to relational
tables, so entities/edges live until manually pruned.

For full project wipe: `DROP SCHEMA memory_<orgid> CASCADE` once the
schema-per-org foundation lands and you've migrated to it.

## Audit

`api_keys.last_used_at` is updated per-request; gives you a coarse "when was
this key actively used" log. There's no per-search audit log yet — the system
doesn't record what queries each key issued. If you need that, the simplest
path is HTTP access logs at the reverse proxy level (Nginx / Caddy / Cloudflare).

## Threat model

What this design protects against:
- ✅ Cross-tenant leak inside the API process (RLS via memory_app)
- ✅ Casual on-machine snooping if Postgres data dir is on encrypted disk
- ✅ Rotating provider keys without rebuilding (KV / OpenBao chain)
- ✅ Accidental ingestion of garbage chitchat (SaveFilter)
- ✅ Forgetting that "X used to be true" — bi-temporal supersession keeps
  history queryable

What it does NOT protect against:
- ❌ Compromised LLM provider with retained logs (use zero-retention SKU
  or local model)
- ❌ A malicious actor with shell access on your machine — they can read
  the Postgres data dir and the dev OpenBao unseal key in `~/.config`
- ❌ Token exfiltration — bearer tokens are in `localStorage` (Web UI) and
  in `.mcp.json` env (clients). Treat as plaintext credentials.
- ❌ Embedding-inversion attacks — academically demonstrated for some
  embedding models; practically requires the same model used for embedding,
  significant compute, and willingness to recover only approximate text. Low
  risk for personal use but real for adversarial scenarios.
- ❌ Anything past the LLM you call — once a token is in the model's
  context, the provider's retention policy applies.

## Practical hygiene

Before sharing a memory dump (e.g., `memory backup dump`):
- Strip / anonymize the JSON before sharing
- Or: re-key — provision a new tenant, re-import only the notes you want
  to share, share that subset

Before sending the project to GitHub:
- Confirm `.gitignore` covers `appsettings.Local.json`, `*.PublishSettings`,
  `secrets.json`, `eval-queries.json` (it does, by default)
- `git log -p -- appsettings.Local.json` should return empty (no leaks)

If you suspect a token was exposed:
- `memory api-key revoke --id <id>` immediately
- Mint a new one; update `.mcp.json` everywhere it's referenced
- Rotate the underlying provider keys (Azure OpenAI key, etc) if those
  could've leaked through the same compromise.
