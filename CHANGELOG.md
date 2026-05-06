# Changelog

All notable changes to this project. Format loosely follows
[Keep a Changelog](https://keepachangelog.com/) — semver where it applies,
"date + intent" where it doesn't.

## [Unreleased]

### Added
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
