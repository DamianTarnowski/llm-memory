# Skills subsystem — implementation plan

Auto-created, auto-updated, cross-agent **skills** synthesized from real work sessions.
The memory server watches what Claude Code / Codex actually did, distills reusable
procedural knowledge into spec-compliant `SKILL.md` skills, and delivers them back to
**every** connected agent — files on disk first, `skill://` MCP resources later.

Status: **M1 + M2 + M3 SHIPPED** (2026-07-10) — skill store + MCP CRUD + `memory
skills sync` + harvest (SecretScanner, hooks, `ClaudeTranscriptParser`) + synthesis
(`synthesize_skills`: prefilter → Reflector → Curator → Drafter → gates → Suggest/
AutoExecute). See [SKILLS.md](SKILLS.md) for the user-facing docs. Deviation from
this plan: the M3 trigger is the `synthesize_skills` MCP tool + REST endpoint rather
than a CLI verb (the stdio/HTTP hosts already carry the full DI stack; a CLI verb
would need its own config bootstrap — deferred). Remaining: M4 (SessionStart
watermark hook, `CodexRolloutParser`, `--take-local`), M5 (Web review UI +
per-project AutomationMode), M6 (hosted-service wrapper, SEP-2640 resources, evals,
hygiene). Plan written 2026-07-10 after a codebase + literature survey and an
adversarial review pass; references at the bottom.

---

## 1. Vision & north star

Today the system already stores *proto-skills*: notes with `MemoryType.Procedural` +
`NoteKind.Pattern` (written by `save_coding_pattern`), surfaced read-only through
`memory://core/procedures`. What's missing is the full loop:

```
        ┌──────────────────────────────────────────────────────────────┐
        │                       THE SKILL LOOP                          │
        │                                                               │
        │  1. CAPTURE     hooks + transcripts from Claude Code & Codex  │
        │  2. SYNTHESIZE  Reflector → Curator → Drafter → quality gates │
        │  3. DELIVER     files: ~/.claude/skills + ~/.agents/skills    │
        │                 (later: skill:// MCP resources, SEP-2640)     │
        │  4. MEASURE     skill_feedback, helpful/harmful counters      │
        │  5. IMPROVE     update / merge / deprecate — back to (2)      │
        └──────────────────────────────────────────────────────────────┘
```

North-star scenario: you debug a gnarly AGE/Cypher issue in a Codex session. When you
end the session, the harvest hook ships the transcript to the memory store; synthesis
runs (on session end, on a schedule, or on demand) and drafts a skill. **Your next
Claude Code session** opens with a one-line note *"1 new skill: debugging-age-cypher"*
— reviewed first (or auto-published, your choice), available as `/debugging-age-cypher`
in Claude Code and `$debugging-age-cypher` in Codex. **Inter-LLM by construction**:
experience from any agent teaches all agents, whatever model synthesized it.

Why this is the right bet in mid-2026:

- **SKILL.md is an open standard** (agentskills.io, published by Anthropic Dec 2025).
  Codex CLI, Cursor, Gemini CLI and GitHub Copilot all read the converged cross-agent
  directories `~/.agents/skills/` + `<repo>/.agents/skills/`; Claude Code reads its own
  `~/.claude/skills/` + `.claude/skills/` (it does **not** read `.agents/` — hence the
  dual render in §7). One format, every agent, no per-client renderers.
- **Skills-over-MCP is becoming official**: SEP-2640 serves skills as MCP resources
  under `skill://<name>/SKILL.md` + a `skill://index.json` catalog — pure Resources,
  no new protocol methods. We defer this channel (no remote file-less client in daily
  use today) but the design stays compatible with it.
- **Both CLIs have compatible hooks**: Codex shipped a deliberately Claude-Code-
  compatible hooks engine (stable v0.124) — same event names, same stdin JSON envelope
  with `transcript_path`, same exit-code semantics. One capture script serves both,
  with one asymmetry: **Codex parses but skips `async: true` hooks**, so on Codex the
  script must self-background (spawn detached, exit 0) — see §6.
- **The research recipe has converged** (ACE, ExpeL, Voyager, AWM, Letta, Devin, Mem0):
  typed delta updates + helpful/harmful counters + embedding dedup + escalating quality
  gates + review queue for autonomous writes. We copy it, we don't invent it.

---

## 2. Deployment topology (decide first — everything else hangs off this)

The three existing deployments have **separate databases** (local WSL2 PG, R620 PG,
Azure `damian-sharedpg`). A skill synthesized in one store does not exist in the others,
and local stdio MCP servers talk straight to WSL2 PG with no Memory.Api running.

**v1 is local-first.** The laptop's WSL2 PG — the DB your daily stdio sessions already
write to — is the source of truth for skills. Consequences:

- Synthesis is a **pure service** (`ISkillSynthesizer.RunOnceAsync(tenant)`) invoked by
  a CLI verb `memory skills synthesize [--project …]`, not (only) a hosted service.
  Runners, cheapest first: (a) the SessionEnd harvest hook chains a synthesize run
  after submitting the transcript; (b) Windows Task Scheduler / cron entry; (c) the
  `SkillSynthesisService : BackgroundService` wrapper — only where Memory.Api actually
  runs 24/7 (R620/Azure), added late (M6), options-gated default-off like
  `ReflectionSchedule`.
- Harvest transport is **CLI/direct-to-PG locally** (same trust model stdio already
  uses), `POST /api/skills/harvest` for remote setups.
- "Overnight synthesis" is the **R620 variant** (always-on Memory.Api + its PG as the
  skill hub; local `.mcp.json` can point stdio at R620 PG over LAN). Documented, not
  default. **Azure prod is never a default harvest target** — full transcripts on the
  shared prod PG is a privacy + cost decision to make explicitly (PRIVACY.md note).

---

## 3. What we build on (existing assets)

| Existing asset | Reused as |
|---|---|
| `MemoryType.Procedural` / `NoteKind.Pattern` notes | raw material for later notes→skill synthesis (backlog) |
| `SimpleReflectionPipeline` + `ReflectionBackgroundService` | template for the (late, optional) synthesis hosted-service wrapper |
| `MarkdownFolderWatcher` sidecar-state pattern | watermark files for sync/harvest/SessionStart |
| `LlmGateway` + per-stage `ModelOverride` pattern | cheap model for Reflector, stronger for Drafter |
| Structured output `GetResponseAsync<T>` | `SkillDeltaBatch`, `SkillDraft`, curator judgments |
| pgvector 3072-dim + hybrid search | skill dedup + skill retrieval |
| RLS + `TenantScope` + typed IDs | skill isolation, same invariants |
| Bi-temporal supersession philosophy | skills are versioned + deprecated, never deleted |
| MCP assembly scanning (`AddMemoryMcpTools`) | `SkillTools` auto-register in both transports |
| Eval harness pattern (`memory eval …`) | later `memory eval skills` (backlog) |
| Blazor admin UI + `/api/*` | review UI (late milestone; CLI review comes first) |

The subsystem is **additive** — no changes to ingest/search pipelines.

---

## 4. Data model (Memory.Domain + Memory.Storage)

New tables, schema `memory`. **Every table carries a denormalized `project_id` and gets
the standard `ENABLE/FORCE ROW LEVEL SECURITY` + project-GUC policy** (the
`note_relations` migration is the model — project_id is denormalized even where
derivable via FK). The migration also includes an idempotent
`GRANT SELECT,INSERT,UPDATE,DELETE … TO memory_app` (default privileges normally cover
new tables, but they bind to the role that ran `AddMemoryAppRole` — defensive grant
costs nothing).

### `skills`
| Column | Type | Notes |
|---|---|---|
| `id` | uuid PK | typed `SkillId` |
| `project_id` | uuid | RLS scope |
| `name` | varchar(64) | spec slug `^[a-z0-9]+(-[a-z0-9]+)*$`, unique per project |
| `description` | varchar(1024) | drives auto-invocation; third person; triggers included |
| `when_to_use` | text? | extra trigger context — **Claude-Code-only frontmatter**; folded into `description` (≤1024) or `metadata.*` in the `.agents/` render (§7) |
| `body` | text | SKILL.md markdown body (<500 lines by convention) |
| `frontmatter_extra` | jsonb | nested pass-through (`paths`, `argument-hint`, `metadata.*`, trigger probes). **Not** `JsonbDictionaryConverter` (flat string map) — needs a raw-string/JsonDocument-backed mapping; add it once, reuse for `stats`. |
| `status` | smallint | `Draft=0, Candidate=1, Published=2, Deprecated=3, Rejected=4` |
| `origin` | smallint | `Manual=0, Synthesized=1, Imported=2` |
| `current_version` | int | bumps on every publish |
| `helpful_count` / `harmful_count` / `usage_count` | int | ExpeL/ACE counters |
| `last_used_at`, `created_at`, `updated_at`, `deprecated_at` | timestamptz | |
| `generator_model` | varchar(100)? | which LLM drafted it |
| `untrusted_input` | bool | provenance includes untrusted web/tool content (§10) |

**Personal (cross-project) library**: there is **no `scope` column**. RLS is strictly
project-GUC; a user-scope row would be invisible outside its project. Instead the
personal library is a dedicated **"personal" project** (projects are cheap; per-project
API keys exist) that `Skills:Sync:Targets` maps to `~/.claude/skills` +
`~/.agents/skills`. Zero new tenancy machinery.

### `skill_versions`
`(skill_id, version)` PK + **`project_id`** (RLS) — full frontmatter+body snapshot per
publish, `created_at`, `change_summary`, `created_by` (`synthesis` / `mcp:<tool>` /
`web:<user>` / `cli`). Enables **1-click rollback**.

### `skill_provenance`
`(skill_id, version, source_kind, source_id)` + **`project_id`** (RLS) — links to
`episodes.id`, `notes.id`, `harvested_sessions.id`. Every skill answers "which
conversations taught you this?" (audit trail for the poisoning threat model).

### `skill_embeddings`
Same shape as `note_embeddings` (3072-dim, model + dimensions recorded, project_id,
RLS). Embedded text = `name + description + when_to_use` — what dedup and retrieval
match against.

### `skill_usage_events`
`id, skill_id, project_id, source, session_ref, outcome (Unknown/Helpful/Harmful),
detail?, occurred_at`. v1 feeds this **only** from the explicit `skill_feedback` tool;
transcript-mined usage detection is backlog.

### `harvested_sessions`
`id, project_id, source (claude-code/codex), session_id, content_hash (unique —
idempotent re-submits), transcript text (gzip-compressed by app; nullable when
`transcript_path` is stored instead — local same-filesystem case), transcript_path?,
status (Pending/Processing/Processed/Skipped/Failed), stats jsonb, submitted_at,
processed_at, error`. The synthesis inbox. **Workers claim rows with
`FOR UPDATE SKIP LOCKED` + status→Processing** so a concurrent CLI run + hosted service
(or a scaled-out API) can never double-synthesize the same session. Retention: prune
transcript bodies N days after Processed (default 7), keep row + stats.

### Config
```jsonc
"Skills": {
  "Enabled": false,
  "AutomationMode": "Suggest",        // Suggest | AutoExecute — applies to SYNTHESIZED skills only (§10)
  "Security": { "UntrustedInputForcesReview": true },   // settable, default safe
  "Synthesis": {
    "ReflectorModelOverride": "gpt-5-mini",
    "DrafterModelOverride": null,
    "MinSignalScore": 0.5,
    "MaxSessionsPerRun": 5,
    "MaxTokensPerRun": 200000
  },
  "Schedule": { "Enabled": false, "Interval": "01:00:00", "Tenants": [ … ] },  // hosted-service wrapper, M6
  "Sync": { "Targets": [ { "project": "<guid>", "claudeDir": "~/.claude/skills", "agentsDir": "~/.agents/skills", "repoDir": null } ] }
}
```

`AutomationMode` starts in config; a later increment makes it a per-project settings row
editable in the Web UI (house rule: AI write actions settable per tenant — Suggest
default, AutoExecute first-class, both with audit + rollback).

---

## 5. MCP surface (Memory.Mcp)

New `SkillTools.cs` following the existing convention (auto-registered by assembly scan
in both stdio and HTTP hosts):

| Tool | Purpose |
|---|---|
| `save_skill(name, description, body, whenToUse?, publish?)` | create/update a skill explicitly. **Explicit saves publish directly when `publish:true`** — AutomationMode gates only autonomous synthesis output, not user/agent-initiated writes (house rule: no forced per-operation confirmations; safety comes from versioning + 1-click rollback + audit episode). |
| `search_skills(query, maxResults?, includeDeprecated?)` | hybrid search over skill embeddings + name/description/body. |
| `get_skill(name)` | full SKILL.md render + counters + provenance summary. |
| `list_skills(status?)` | catalog (name, description, status, version, counters). |
| `skill_feedback(name, outcome, detail?)` | explicit helpful/harmful signal → counters + usage event. |
| `propose_skill(name, description, rationale, evidence?)` | lightweight mid-session proposal → `Draft` with provenance to the current conversation. |
| `promote_skill(name)` / `deprecate_skill(name, reason)` | lifecycle ops with audit episode (mirrors `supersede_note`). |

Prompt: extend `memory_agent_guidance` — when to `search_skills` (before unfamiliar
multi-step work), when to `propose_skill` (hard-won discovery, user correction,
repeated workflow), when to `skill_feedback`.

**Deferred (backlog, design-compatible):** SEP-2640 `skill://index.json` +
`skill://{name}/SKILL.md` resources. Notes from the review pass for when we build it:
the C# SDK (1.2.0) lists templated resources only under `resources/templates/list`, so
per-skill entries won't appear in `resources/list` / @-mention autocomplete — the
catalog resource + `list_skills`/`get_skill` tools are the discovery surface; per-skill
direct resources and `listChanged` need the SDK's mutable runtime primitive collection.
Server `instructions` will advertise the catalog (the Figma-MCP pattern).

---

## 6. Capture — hooks for Claude Code & Codex

`scripts/hooks/` in the repo + setup section in `docs/SKILLS.md`. Design principles:
**hooks are dumb couriers** (all intelligence server-side) and **zero cost when idle**.

### Tenant attribution & allowlist (the make-or-break detail)
Hooks in `~/.claude/settings.json` fire for **every** repo on the machine — including
confidential projects that must never be harvested. The harvest script therefore:

1. Resolves `cwd` → project mapping by reading the repo's own config: the `env` block
   of `.mcp.json` (`Tenant__ProjectId`) or an explicit `.llm-memory.json` marker file.
2. **Exits 0 silently when the repo has no mapping.** Unmapped repos are never
   harvested, never uploaded, never logged. Allowlist by construction.
3. Uses the mapping's connection (local: `memory` CLI direct-to-PG with the same
   connection string stdio uses; remote: per-project bearer token) — attribution can't
   cross tenants because the credential *is* the tenant.

### Claude Code (`~/.claude/settings.json`)
| Event | Config | Action |
|---|---|---|
| `SessionEnd` | `async: true` | `harvest.ps1` → submit transcript (gzip; size-capped), then optionally chain `memory skills synthesize --project …` + `memory skills sync`. Fire-and-forget. |
| `PreCompact` | `async: true` | same submit — last full transcript before squash (dedup by content hash makes double-fire safe). |
| `Stop` | off by default | optional mid-session harvest for marathon sessions. |
| `SessionStart` | sync | **zero network, zero dotnet**: read a tiny local watermark file (written by the last sync/harvest for this repo's project); if it shows N>0 new skills, emit one line of `additionalContext` ("N new skills: names") + `reloadSkills: true`, advance the watermark; else exit instantly. Never blocks on WSL2/PG being down. |

### Codex (`~/.codex/hooks.json`, hooks engine v0.124+)
Same script, same events (`Stop`, `PreCompact`, `SessionStart` with `additionalContext`)
— stdin envelope is Claude-Code-compatible (`transcript_path`, `session_id`, `cwd`).
Two asymmetries:
- **No `async` support** — Codex parses but *skips* async hooks. Codex entries run
  synchronously and the script **self-backgrounds** (spawn detached child that does the
  submit, exit 0 immediately).
- **No `SessionEnd` event** — `Stop` + content-hash dedup covers it; the optional
  `memory skills watch-codex` rollout watcher (FileSystemWatcher on
  `~/.codex/sessions/**/*.jsonl`, debounced) is a backlog fallback.

Hook contract details (`async`, `reloadSkills`, Codex envelope) were verified against
current official docs during planning — **re-verify at M2 build time** (both CLIs move
fast); the degradation path if anything changed: `additionalContext` text only +
next-session pickup.

### Transport & safety
- Local (default): `memory skills harvest --stdin` writes directly to PG. Windows→WSL2
  path mismatch is a non-issue because we submit **content**, not paths; the
  `transcript_path`-only variant is allowed exclusively when CLI and PG share a
  filesystem view, with the path canonicalized and **restricted to
  `~/.claude/projects/**` and `~/.codex/sessions/**`**.
- Remote: `POST /api/skills/harvest` accepts **content only** (no server-side path
  reads — a client-supplied path would be an authenticated arbitrary-file-read
  primitive). Gzip body, request size cap, bearer auth.
- Secret scan + redaction runs at intake, before the row is stored (§10).

---

## 7. Delivery — files first, MCP later

### `memory skills sync`
Renders **Published** skills of mapped projects into agent-readable directories:

| Target | Who reads it | Default |
|---|---|---|
| `~/.claude/skills/<name>/` | Claude Code (user scope) | ✅ on |
| `~/.agents/skills/<name>/` | Codex, Cursor, Gemini CLI, Copilot (user scope) | ✅ on |
| `<repo>/.claude/skills/` + `<repo>/.agents/skills/` | project scope, per repo | ⛔ **opt-in per target** |

Render rules:
- `.claude/` render: full frontmatter incl. `when_to_use`. `.agents/` render:
  `when_to_use` folded into `description` (≤1024 cap) or `metadata.*` — top-level
  unknown keys are Claude-specific and don't drive auto-invocation elsewhere.
- Claude Code does not read `.agents/skills`, so the dual render creates no duplicates.
- Real directories + files, no symlinks (Windows friction).

Safety rules (git-leak defenses — repos may be **public**):
- **Ownership manifest** `.llm-memory-sync.json` per target: sync only creates/updates/
  deletes files it owns (hash-tracked). Hand-written skills are never touched; an owned
  file edited by hand flips to *"locally modified — skipped"* (`sync --take-local`
  imports the edit as a new version; `--force-server` overwrites).
- In-repo targets: sync appends its paths to **`.git/info/exclude`** (invisible, no
  diff noise), **warns loudly when `git remote -v` shows a public GitHub remote**, and
  refuses (with prompt) when the target path is dirty in git.
- Installer step: `memory skills init-dirs` pre-creates the top-level skills dirs —
  Claude Code's hot reload only watches dirs that existed at session start (one-time
  restart caveat documented).

Freshness: filesystem **hot reload covers mid-session** changes (Claude Code watches
existing skill dirs); the **SessionStart watermark + `reloadSkills` covers
start/resume/compact**. Codex/Cursor pick changes up next session.

### MCP path (backlog)
`skill://` resources per SEP-2640 (§5 notes) once a remote, file-less client actually
needs it. The DB stays the single source of truth either way.

---

## 8. Synthesis pipeline (Memory.Pipeline/Skills — the heart)

Core is a **pure, callable service**: `ISkillSynthesizer.RunOnceAsync(TenantScope, ct)`
— invoked by CLI verb, hook chain, or (later) the hosted-service wrapper. Roles from
ACE (Reflector/Curator) + Mem0 (op-based updates):

```
harvested_sessions (Pending → claim FOR UPDATE SKIP LOCKED → Processing)
        │
        ▼
[0] SignalPrefilter (no LLM, pure heuristics)
    error→fix cycles · user-correction phrases ("nie rób", "mówiłem", "zawsze…")
    repeated command sequences · struggle-then-success arcs · session length
    score < MinSignalScore → Skipped ($0 for most sessions)
        │
        ▼
[1] REFLECTOR (cheap model, structured SkillDeltaBatch)
    input:  normalized SessionTrace + digest of existing skills (name+description)
    output: typed deltas {op: Create|Update|Upvote|Downvote|Deprecate,
            targetSkillName?, name, description, whenToUse, bodyOutline,
            evidence[] (verbatim quotes + turn refs), confidence}
    rules: only knowledge that required actual discovery, has a clear trigger,
    and was verified to work in-session (Claudeception rule); placeholders instead
    of concrete values (AWM); deltas only, never rewrites (ACE)
        │
        ▼
[2] CURATOR (deterministic + one LLM assist)
    embedding dedup vs skill_embeddings (cosine ~0.85) → near-match: LLM decides
    ADD / UPDATE / NOOP (Mem0) · counter deltas skip content entirely ·
    contradiction with existing skill → flag both for review, never silent
        │
        ▼
[3] DRAFTER (default chat model, structured SkillDraft)
    Create → full SKILL.md (3rd-person description w/ triggers, <500-line body,
    checklist style); Update → minimal body patch (delta-merge)
    system prompt distills Anthropic skill-authoring best practices
        │
        ▼
[4] GATES (escalating cost; fail → Rejected with reason)
    a. schema: slug regex, description ≤1024, body cap, no XML tags
    b. secret scan (also ran at intake): key/token/connstring regexes + entropy → redact/reject
    c. injection heuristics: "ignore previous instructions"-class content, exfil
       patterns; sessions containing untrusted web/tool content set untrusted_input=true
    d. quality judge (cheap LLM, rubric): actionable? generalizable? non-obvious?
        │
        ▼
[5] PUBLISH DECISION (applies to synthesized skills only)
    Suggest      → Candidate  (review via CLI `memory skills review`, later Web UI)
    AutoExecute  → Published, version++, audit episode, 1-click rollback
    untrusted_input && Security:UntrustedInputForcesReview → Candidate regardless
    (flag is settable — secure default, owner's choice wins)
```

**SessionTrace normalization** — one internal shape for both CLIs:
- `ClaudeTranscriptParser` (M2): `~/.claude/projects/<enc>/<session>.jsonl` — tree via
  `uuid`/`parentUuid`; `text`/`thinking`/`tool_use`/`tool_result` blocks; tolerate
  unknown `type`s (format evolves); ignore `usage` numbers (known-unreliable).
- `CodexRolloutParser` (M4): `~/.codex/sessions/**/rollout-*.jsonl`. Until it ships,
  Codex transcripts are **harvested and stored** (hash-deduped) but sit Pending —
  capture now, parse later, nothing lost.
- Pure logic → unit-tested against **fixtures captured from real sessions** (house
  rule: no synthesized fixtures).

**LLM calls** via existing `ILlmGateway` (multi-provider — synthesis works on whichever
provider the tenant runs). `claude --bare -p --json-schema` documented as an
alternative subscription-cost backend, not built in v1.

---

## 9. Feedback & hygiene (v1-minimal)

v1 keeps measurement deliberately small:
- `skill_feedback` tool + helpful/harmful/usage counters (agents are prompted to call
  it when a skill clearly helped or misled).
- Counters order `list_skills` output and flag candidates for deprecation — a human
  (or AutoExecute policy) acts; we deprecate, never delete.

**Backlog** (build when the library size justifies it): transcript-mined usage
detection, trigger probes (`should_trigger[]`/`should_not_trigger[]`) + `memory eval
skills` hit-rate + A/B utility runs (`claude --bare -p`, env-gated, cost-capped, real
calls per house rule), frozen regression probe set, `list_skill_hygiene`,
NoteClusterScanner (skills from accumulated Procedural notes).

---

## 10. Security & privacy (must-read before building)

1. **Learned skills are a prompt-injection surface** (untrusted tool output →
   transcript → skill → future sessions). Layered mitigations: provenance on every
   version; `untrusted_input` flag + settable forced-review policy; injection
   heuristics gate; review diff; audit episodes for every lifecycle change.
2. **No executable payloads in v1**: synthesized skills are text-only — no `scripts/`,
   no `` !`command` `` dynamic-context blocks, no `hooks` frontmatter, no
   `allowed-tools` grants. The renderer strips/refuses them. (SEP-2640: hosts MUST NOT
   run MCP-served scripts without explicit approval.) Revisit only with per-skill
   human approval.
3. **Secrets**: scan+redact at harvest intake AND at draft gate; transcript bodies
   pruned by retention (default 7 days post-processing); `docs/PRIVACY.md` updated —
   especially the remote-harvest variants (R620/Azure).
4. **Server-side file reads**: the REST harvest endpoint accepts content only; the
   local path-variant is allowlisted to the two transcript roots after
   canonicalization (no authenticated file-read primitive).
5. **Tenancy**: all six tables RLS-protected (denormalized `project_id`); sync renders
   only mapped projects into a target; hooks harvest only allowlisted repos.
   Cross-project leakage impossible at three layers (hook, RLS, sync mapping).
6. **Git leakage**: in-repo render opt-in + `.git/info/exclude` + public-remote warning
   + dirty-tree refusal (§7).
7. **Rollback-first**: every publish is a version; 1-click revert; deprecate never
   delete. This is what makes AutoExecute acceptable.

---

## 11. Cost model & controls

| Stage | Model tier | Est. per session |
|---|---|---|
| SignalPrefilter | none | $0 — filters most sessions out |
| Reflector | cheap (`ReflectorModelOverride`) | ~$0.01–0.05 |
| Curator LLM assist | cheap, only on near-dups | ~$0.005 |
| Drafter | default chat model, only Create/Update | ~$0.05–0.15 per skill |
| Quality judge | cheap | ~$0.01 |

Controls: prefilter threshold, `MaxSessionsPerRun`, `MaxTokensPerRun` hard ceiling,
counter-only deltas skip the Drafter. Steady state for a solo dev: **a few cents/day**.
Batch APIs (50% off) only if volume ever warrants.

---

## 12. Testing strategy (house rules apply)

- **Pure unit**: slug/frontmatter validation, SKILL.md renderer (incl. `when_to_use`
  folding + payload stripping), ownership-manifest sync logic (in-memory FS), git
  safety checks, transcript parsers on **real captured fixtures**, prefilter scoring,
  delta-merge.
- **LivePg**: skills CRUD + RLS isolation (two tenants, zero cross-visibility),
  versioning + rollback, harvest idempotency by hash, `FOR UPDATE SKIP LOCKED`
  claiming under two concurrent workers.
- **LiveLlm** (real calls, no mocks): Reflector on a small real transcript → non-empty
  typed deltas; Drafter → valid SKILL.md; curator dedup judgment. Cheap models,
  per-suite target <$0.50.
- **E2E smoke** (`scripts/smoke-test-skills.sh`): save_skill → sync → file on disk →
  harvest fixture → synthesize → Candidate appears → promote → sync.

---

## 13. Milestones (each independently shippable)

| # | Scope | Deliverable / demo | Size* |
|---|---|---|---|
| **M1 — Skill store + MCP CRUD + sync** | §4 tables/migration (RLS + grants), `SkillTools` (save/search/get/list/promote/deprecate/feedback), `memory skills sync` + `init-dirs` (user-scope default, in-repo opt-in with git safety), REST `/api/skills` read endpoints | *"save_skill in Claude Code → `/my-skill` in Claude Code and `$my-skill` in Codex after sync"* — manual skills already deliver the cross-agent value | L (3–5 d) |
| **M2 — Harvest (Claude-first)** | `memory skills harvest` CLI + `POST /api/skills/harvest` (content-only), `harvested_sessions` + claiming, `ClaudeTranscriptParser`, secret-scan intake, hook pack with **cwd→project allowlist attribution**; Codex hooks capture-only (store rollouts) | transcripts flow in, correctly attributed, unmapped repos untouched | M (2–3 d) |
| **M3 — Synthesis v1 (Suggest)** | SignalPrefilter, Reflector, Curator, Drafter, gates a–d, `ISkillSynthesizer.RunOnceAsync` + `memory skills synthesize` + `memory skills review` (CLI approve/edit/reject), SessionEnd hook chains synthesize+sync | real session → Candidate skill with provenance + evidence → approve → next session has it | L (4–6 d) |
| **M4 — Loop closure + Codex parity** | `SessionStart` watermark hook (zero-network), `propose_skill`, `CodexRolloutParser` (drains stored Pending rollouts), sync conflict handling (`--take-local`) | the north-star demo end-to-end, from either CLI | M (2–3 d) |
| **M5 — AutoExecute + Web review UI** | Blazor Skills page (queue, diff, approve/edit/reject, versions, rollback, counters), `/api/skills/*` write endpoints, per-project `AutomationMode` setting | full product; AutoExecute for the brave | L (3–5 d) |
| **M6 — Scale-out & extras (on demand)** | `SkillSynthesisService` hosted wrapper (R620/Azure topology), SEP-2640 `skill://` resources, evals + hygiene + NoteClusterScanner + rollout watcher (from §9 backlog), docs polish (SKILLS.md, MCP-INTEGRATION, PRIVACY, README) | overnight-synthesis variant; remote clients | M–L |

*Solo-dev calendar estimates, Claude-assisted. **M1→M4 ≈ 2 tygodnie** to the north
star; M5/M6 on demand. M1 alone is worth shipping.

---

## 14. Resolved decisions (from the adversarial review)

1. **Topology**: local-first (WSL2 PG source of truth); synthesis as callable service +
   CLI; hosted wrapper + R620 hub variant deferred to M6; Azure never a default
   harvest target.
2. **Personal library**: dedicated "personal" project mapped to user-scope dirs — no
   user-level RLS machinery.
3. **AutomationMode scope**: gates synthesized output only; explicit `save_skill
   publish:true` publishes directly (audited, versioned, revertible).
4. **Codex async hooks**: not supported — sync hook + self-backgrounding script.
5. **SEP-2640 / listChanged / evals / usage-mining / NoteClusterScanner**: backlog, not v1.
6. **Executable skills** (`scripts/`, dynamic context): out entirely until per-skill
   human approval exists.
7. **Directory truth**: Claude Code reads `.claude/skills` only; `.agents/skills` covers
   Codex/Cursor/Gemini/Copilot; dual render, no duplicates.

---

## 15. Key references

- Agent Skills spec + authoring: agentskills.io/specification · anthropic.com/engineering/equipping-agents-for-the-real-world-with-agent-skills · github.com/anthropics/skills (skill-creator)
- Claude Code: code.claude.com/docs/en/skills · /docs/en/hooks · /docs/en/headless · /docs/en/mcp
- Codex: learn.chatgpt.com/docs/build-skills · /docs/hooks (async unsupported note) · /docs/agent-configuration/agents-md · rollout files: github.com/openai/codex/discussions/3827
- Skills over MCP: SEP-2640 — github.com/modelcontextprotocol/experimental-ext-skills · modelcontextprotocol.io/community/skills-over-mcp/charter
- MCP spec 2025-11-25 (+2026-07-28 RC: sampling deprecated — do not build on it)
- Research: ACE arXiv 2510.04618 · ExpeL 2308.10144 · Voyager 2305.16291 · Agent Workflow Memory 2409.07429 · Dynamic Cheatsheet 2504.07952 · SkillWeaver 2504.07079 · CLIN 2310.10134 · "Do Self-Evolving Agents Forget?" 2605.09315 · memory poisoning 2606.04329
- Products: Letta skill-learning · Devin Knowledge (review-queue reference design) · Zep/Graphiti (bi-temporal invalidation) · Mem0 (ADD/UPDATE/DELETE/NOOP) · OpenHands microagents · Claudeception · coleam00/claude-memory-compiler
