# Skills

Reusable procedural knowledge in the [Agent Skills](https://agentskills.io) (SKILL.md)
format, stored in the memory database as the single source of truth and rendered to
agent-readable skill directories. What one agent learns, every agent can use.

Status: **M1 + M2 + M3 shipped** — skill store, MCP tool family, `memory skills sync`,
transcript harvesting (hooks → redaction → synthesis inbox), and **automatic skill
synthesis** (`synthesize_skills`). Loop closure extras (SessionStart watermark hook,
Codex rollout parser, Web review UI) are the remaining milestones; the full roadmap
lives in [SKILLS-PLAN.md](SKILLS-PLAN.md).

---

## Concepts

- A **skill** is a named slug (`debugging-age-cypher`), a **description** (drives
  auto-invocation — third person, with concrete triggers), an optional
  **when_to_use** (extra trigger context), and a markdown **body** (the how-to).
- **Lifecycle**: `Draft → Candidate → Published → Deprecated` (+ `Rejected`).
  Only Published skills are rendered by sync. Skills are versioned on every
  publish (`skill_versions` snapshot — rollback is trivial) and deprecated, never
  deleted.
- **Counters**: `helpful / harmful / usage` — fed by the `skill_feedback` tool;
  they drive ordering and deprecation decisions.
- **Provenance**: every skill version can link back to the episodes / notes /
  sessions that taught it.
- **Tenancy**: skills tables are RLS-protected by `project_id` like everything
  else. A cross-project "personal" library is just a dedicated project mapped to
  your user-scope directories.
- **Safety**: bodies are sanitized — Claude Code dynamic-context (`` !`cmd` ``,
  ```` ```! ````) is stripped, and permission-granting frontmatter
  (`allowed-tools`, `hooks`, …) is never rendered from stored extras. Synthesized
  skills are text-only by design.

## MCP tools

| Tool | Purpose |
|---|---|
| `save_skill(name, description, body, whenToUse?, publish?, changeSummary?)` | Create/update a skill. `publish:true` goes live immediately (versioned + audited + revertible). |
| `propose_skill(name, description, rationale, evidence?)` | Mid-session draft proposal with provenance; mature it later. |
| `search_skills(query, maxResults?, includeDeprecated?)` | Hybrid search: skill embeddings (cosine) + text fallback. |
| `get_skill(name)` | Full SKILL.md render + counters + metadata. |
| `list_skills(status?, limit?)` | Catalog with status/version/counters. |
| `skill_feedback(name, outcome, detail?)` | Record helpful/harmful after actually using a skill. |
| `promote_skill(name)` | Draft/Candidate → Published (version bump + snapshot). |
| `deprecate_skill(name, reason)` | Retire a stale/wrong skill (kept in history). |

REST (read-only): `GET /api/skills[?status=]`, `GET /api/skills/{name}[?flavor=agents]`.

## Syncing skills to your agents

```bash
# One-time: pre-create the directories (Claude Code only watches dirs that
# existed at session start — restart a running session once afterwards).
memory skills init-dirs

# Render published skills to both agent ecosystems:
memory skills sync \
  --connection-string "Host=localhost;Port=5435;Database=llm_memory;Username=memory_app;Password=..." \
  --org <org-guid> --project <project-guid>
```

Default targets:

| Directory | Flavor | Read by |
|---|---|---|
| `~/.claude/skills/<name>/SKILL.md` | Claude Code (full frontmatter incl. `when_to_use`) | Claude Code |
| `~/.agents/skills/<name>/SKILL.md` | Open standard (`when_to_use` folded into `description`) | Codex CLI, Cursor, Gemini CLI, GitHub Copilot |

After a sync, the skill is `/skill-name` in Claude Code and `$skill-name` in Codex
(or auto-invoked from its description in both).

### Ownership manifest — your hand-written skills are safe

Each target directory gets a `.llm-memory-sync.json` manifest. Sync only ever
creates/updates/deletes files it owns:

- a foreign file with the same name is **never overwritten** (reported as a conflict);
- an owned file you edited by hand is **skipped** and flagged as locally modified
  (`--force-server` overwrites; identical content is silently adopted);
- when a skill is deprecated/unpublished, its pristine file is deleted — an edited
  one is left in place.

`--dry-run` previews the plan without writing.

### In-repo rendering (opt-in)

`--repo-dir <path>` renders into `<repo>/.claude/skills` + `<repo>/.agents/skills`
with git-leak defenses: paths are appended to `.git/info/exclude`, a loud warning
fires when the repo has a GitHub remote, and sync refuses to write into a dirty
target path. Default (no flag) touches only your user-scope directories — nothing
ever lands in a repo unless you ask.

## Harvesting session transcripts (the synthesis inbox)

Skills are ultimately synthesized from what agents actually did. The capture path:

```
Claude Code / Codex session ends (or pre-compacts)
  → hook fires scripts/hooks/harvest.ps1 with the session envelope
  → script resolves cwd → project via a .llm-memory.json marker file
      (no marker = repo is NOT harvested — allowlist by construction)
  → memory skills harvest: secrets REDACTED → sha256 hash (dedup) → gzip
  → row in memory.harvested_sessions (status=Pending), waiting for synthesis
```

Setup:

1. Copy `scripts/hooks/.llm-memory.json.example` as `.llm-memory.json` into the
   root of each repo you want harvested (add it to `.git/info/exclude` — it may
   carry a connection string). Repos without the marker are never touched.
2. Merge `scripts/hooks/claude-settings-snippet.json` into `~/.claude/settings.json`
   (`SessionEnd` + `PreCompact`, `async: true` — fire and forget).
3. For Codex, merge `scripts/hooks/codex-hooks-snippet.json` into
   `~/.codex/hooks.json`. **Codex parses but skips async hooks** — its entries run
   synchronously and the script detaches itself (`-SelfBackground`).
4. Manual submission works too:
   `memory skills harvest --path <transcript.jsonl> --source claude-code --session-id <id> --org … --project …`
   (or `--stdin`), and remote setups can `POST /api/skills/harvest`
   (content-only body; the server never reads client-supplied paths).

Intake guarantees:
- **Secret redaction before storage** (`SecretScanner`: connection-string
  passwords, API keys/tokens — OpenAI/Anthropic/GitHub/Slack/Google/AWS/`memk_`,
  PEM blocks, JWTs, `Authorization: Bearer`, generic `key=value` assignments).
- **Dedup by content hash** — PreCompact + SessionEnd double-fires are harmless.
- 10 MB transcript cap; stats (lines/chars/redactions/cwd/branch) stored as jsonb.

Verify the hook contract against current Claude Code / Codex docs when installing —
both CLIs evolve; the snippets match the contract as of 2026-07.

## Automatic synthesis — skills from your sessions

`synthesize_skills` (MCP tool) or `POST /api/skills/synthesize` drains the inbox:

```
pending harvested session (claimed with FOR UPDATE SKIP LOCKED — concurrency-safe)
  → SignalPrefilter (free heuristics: error→fix arcs, user-correction phrases
    PL/EN, explicit "save that as a skill", tool volume; below threshold = skipped, $0)
  → REFLECTOR (cheap model): typed deltas vs the existing library —
    create / update / upvote / downvote, with verbatim evidence
  → CURATOR: embedding dedup (cosine ≥ 0.85 → LLM add/update/noop verdict);
    upvote/downvote deltas just move counters, no content churn
  → DRAFTER (default model): full SKILL.md draft (checklist style, placeholders,
    pitfalls section) — minimal delta when updating
  → GATES: schema → secret redaction → injection heuristics (hard reject)
    → quality judge (actionable? generalizable? non-obvious? < 0.5 = rejected)
  → PUBLISH DECISION: Suggest → Candidate (review with list_skills status=candidate
    + promote_skill); AutoExecute → published directly. Sessions that touched
    WebFetch/WebSearch are flagged untrusted and forced into review
    (settable: Skills:Security:UntrustedInputForcesReview).
```

Every synthesized skill carries provenance (`harvested_session` ref), the generator
model, and a version snapshot — review, rollback, and deprecation all work the same
as for manual skills. Cost controls: signal threshold, `MaxSessionsPerRun`, cheap
`ReflectorModelOverride`; a typical eligible session costs a few cents, filtered
sessions cost nothing.

## Configuration

```jsonc
"Skills": {
  "Enabled": false,                       // subsystem master switch (synthesis milestones)
  "AutomationMode": "Suggest",            // Suggest | AutoExecute — synthesized skills only
  "Security": { "UntrustedInputForcesReview": true }
}
```

Explicit `save_skill publish:true` is never funneled into a review queue —
`AutomationMode` gates autonomous synthesis output only.
