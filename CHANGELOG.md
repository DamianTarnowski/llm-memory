# Changelog

All notable changes to this project. Format loosely follows
[Keep a Changelog](https://keepachangelog.com/) — semver where it applies,
"date + intent" where it doesn't.

## [Unreleased]

### Added
- **Skills subsystem (M1)** — cross-agent skill library in the Agent Skills
  (SKILL.md) format, stored in Postgres as the source of truth
  ([docs/SKILLS.md](docs/SKILLS.md), roadmap in
  [docs/SKILLS-PLAN.md](docs/SKILLS-PLAN.md)):
  - schema: `skills`, `skill_versions` (publish snapshots + rollback),
    `skill_provenance`, `skill_embeddings` (3072-dim), `skill_usage_events`,
    `harvested_sessions` (synthesis inbox for M2) — all RLS-protected with
    denormalized `project_id` + defensive `memory_app` grants
    (migration `20260710105405_AddSkills`);
  - MCP tools: `save_skill`, `propose_skill`, `search_skills` (hybrid:
    embedding cosine + text fallback), `get_skill`, `list_skills`,
    `skill_feedback`, `promote_skill`, `deprecate_skill`; agent guidance
    prompt extended with skill usage rules;
  - `memory skills sync|init-dirs|list` CLI — renders published skills to
    `~/.claude/skills` (Claude Code flavor) and `~/.agents/skills` (open
    standard: Codex/Cursor/Gemini/Copilot; `when_to_use` folded into
    description), guarded by an ownership manifest that never touches
    hand-written skills; opt-in in-repo rendering with git-leak defenses
    (`.git/info/exclude`, GitHub-remote warning, dirty-tree refusal);
  - REST: `GET /api/skills`, `GET /api/skills/{name}?flavor=`;
  - body sanitizer strips executable payloads (dynamic-context `` !`cmd` ``
    lines, ```` ```! ```` fences, permission-granting frontmatter extras).
- **Skills subsystem (M2 — harvest)** — transcript capture into the synthesis
  inbox:
  - `SecretScanner` (Memory.Domain): regex redaction of connection-string
    passwords, API keys/tokens (OpenAI/Anthropic/GitHub/Slack/Google/AWS/
    `memk_`), PEM private keys, JWTs, bearer headers — runs at intake before
    any transcript byte is stored;
  - `memory skills harvest` CLI (`--path`/`--stdin`) + `POST /api/skills/harvest`
    (content-only, 10 MB cap): redact → sha256 dedup → gzip →
    `harvested_sessions` (double-fires from PreCompact+SessionEnd are benign);
  - `ClaudeTranscriptParser` → normalized `SessionTrace` (turns, tool calls
    joined to results across lines, error counts) — defensive against format
    drift; fixture mirrors the real transcript structure with redacted content;
  - `scripts/hooks/`: `harvest.ps1` (cwd→project allowlist via `.llm-memory.json`
    marker — unmapped repos are never harvested) + settings snippets for
    Claude Code (`SessionEnd`/`PreCompact`, async) and Codex (sync +
    `-SelfBackground`, since Codex skips async hooks).
- **Skills subsystem (M3 — synthesis)** — automatic skill creation from harvested
  sessions (`synthesize_skills` MCP tool + `POST /api/skills/synthesize`):
  - inbox claiming with `FOR UPDATE SKIP LOCKED` (safe under concurrent runners);
  - `TraceSignals` prefilter (error→fix arcs, PL/EN correction phrases, explicit
    skill requests, tool volume) — sub-threshold sessions cost zero LLM calls;
  - ACE-style Reflector (typed create/update/upvote/downvote deltas with verbatim
    evidence, cheap-model override) → Curator (embedding dedup ≥ 0.85 + LLM
    add/update/noop) → Drafter (full SKILL.md, minimal-delta updates) → gates
    (schema, secret redaction, injection heuristics hard-reject, LLM quality
    judge) → `AutomationMode` publish decision (Suggest → Candidate;
    AutoExecute → direct publish; WebFetch/WebSearch-tainted sessions forced
    into review per `Skills:Security:UntrustedInputForcesReview`);
  - provenance (`harvested_session`) + generator model recorded on every
    synthesized skill; verified end-to-end on a real 2.2 MB session transcript
    (3 accurate Candidates, quality 0.84–0.88).
- `Memory.Api.Tests`, `Memory.Llm.Tests` populated with pure-logic tests:
  RRF fusion math, per-variant vector merge, API-key SHA-256 hashing,
  slug generation, `AdminOnlyEndpointFilter` 401/403/200 paths, LLM
  gateway provider routing + named-setting error messages.
- `RrfFuser` extracted from `HybridSearchPipeline` so the four-stream
  fusion math is isolated and unit-testable.
- `InternalsVisibleTo` for `Memory.Pipeline.Tests` and `Memory.Api.Tests`.

### Security
- `TenantHeaderMiddleware` is hard-gated to `IsDevelopment()`. Production
  requires bearer auth; the previous `X-Memory-*` GUID fallback would have
  let any caller impersonate any tenant. **Backwards-incompatible if you
  relied on header-based auth in prod.**
- CORS no longer uses the dev-grade `SetIsOriginAllowed(true) +
  AllowCredentials()` outside Development. Production reads
  `Cors:AllowedOrigins[]` from config; misconfig defaults to no
  cross-origin access.
- `/api/secrets/*` (the OpenBao proxy) now requires admin-scoped API
  keys. New `ApiKey.IsAdmin` column (migration `20260506171421`); mint
  with `memory api-key create --admin`. Tenant keys get 403 here.
- `ApiKeyAuthMiddleware` stashes the resolved `ApiKey` on
  `HttpContext.Items` so downstream policies can inspect role flags
  without re-querying the database.

### Fixed
- `ReflectionBackgroundService` was a no-op: it checked `tenant.Current`
  in a background context where no AsyncLocal scope is set, so every
  cycle skipped silently. It now reads `ReflectionSchedule:Tenants[]`
  explicitly and fans out reflection runs across configured tenants.

### Changed
- Docs no longer hard-code the author's specific Azure infrastructure
  (vault names, App Service hosts, resource group, alt-subscription
  paths). They read as a generic operations guide with `<your-vault>` /
  `<your-host>` placeholders. Smoke-test scripts use generic personas.
- README rewritten with a hero block, a concrete "what it looks like in
  use" interaction shape, and a feature table that surfaces the
  differentiators on first scan.
- `dotnet format` baseline cleared — `--verify-no-changes` is now green.

### Removed
- 5 stale debug artifacts that had been tracked in `scripts/` (raw MCP
  JSON-RPC traces from a long-past dev session). `.gitignore` now
  ignores all dotfiles in `scripts/` to prevent recurrence.
- `.github/workflows/` removed entirely — CI/CD lives on the homelab,
  not on GitHub Actions.

## [1.0.0] - 2026-05-06 — initial public release

First public commit of LLM Memory at
`https://github.com/DamianTarnowski/llm-memory`. Working hybrid retrieval,
multi-note extraction, bi-temporal supersession, A-MEM auto-linking,
reflection hierarchy, multi-tenant RLS, multi-provider LLM, MCP stdio +
HTTP, Blazor admin UI, secret-source chain (Azure KV → OpenBao → JSON),
backup zip, image embeddings via Vertex multimodalembedding@001, webhook
connectors. 60-commit history scrubbed of secrets via `git-filter-repo`
before publish.
