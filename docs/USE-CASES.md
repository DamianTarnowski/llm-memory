# Use cases

Practical patterns for what to put in this memory and how to wire it. The
common shape is: one project per "domain", a tagging convention, and a
`.mcp.json` per environment.

## Pattern 1 — Programming / technical practices

**What goes in:**
- Decisions: "We chose X over Y because…"
- Learnings: "I learned that Postgres FORCE RLS still bypasses for superusers"
- Errors: "Bug: Blazor scoped CSS ::deep is descendant combinator — silent fail"
- Patterns: "When using EF + value-converted typed IDs, avoid List.Contains"
- Stack inventory: "Project X uses .NET 10 + EF + Aspire + Postgres+AGE"

**Setup:**
- One project per codebase or per "tech domain"
- `.mcp.json` in each repo points the memory MCP server at the right tenant
- Enable `SaveFilter` (filters out chat noise)
- Enable `Reranker` + `GraphRetrieval` (tech notes have rich entity overlap)

**Idiomatic agent prompt:**
> "Before answering, search memory for relevant prior decisions on this topic.
> If the search abstains, say so — don't fabricate. After we agree on a
> direction, save the decision as an episode marked source=`decision`."

**Tagging convention** (kinds are auto-detected; tags are for facets):
- `tech-stack`, `architecture`, `migration`, `incident`, `code-review`
- Per-project tags: `llm-memory`, `aiocr`, `legalassistant`, etc.

**Habits that work:**
- After a debugging session: `save_episode` with the bug description and the
  fix. The pipeline classifies it as `Error`, indexes the surrounding entities
  (technologies, repos), and links it to the related notes that mentioned the
  same components.
- Before starting work on a familiar area: `search_memory("X tech-stack")` to
  surface prior decisions and gotchas. The graph retrieval pulls in adjacent
  notes via shared entities, even if the query doesn't match the note text
  directly.

---

## Pattern 2 — Health log

**What goes in:**
- Symptoms with timestamps: "Headache started around 3 PM, stopped after dinner"
- Sleep notes: bedtime, wake-up, perceived quality
- Workout logs
- Doctor visits, prescriptions, dosages (read **PRIVACY.md** first)
- Mood / energy observations

**Setup:**
- A dedicated `health` project — never mixed with work or technical projects
- For privacy: route this project's LLM calls to a local model (Ollama,
  LM Studio) — see PRIVACY.md. The architecture supports per-project provider
  routing.
- Disable `SaveFilter` here — health observations are often "low-signal" by the
  agentic judge's general standards but matter to you (e.g. "felt foggy
  after lunch" might be discarded as chitchat but is exactly what you want
  to retrieve later when correlating with food logs).
- Enable `TimeDecay` with a long half-life (180-365 days) so old data stays
  retrievable; recent gets a slight boost.

**Useful queries:**
- `"headaches in the past month"` — temporal facets via `since`
- `"how did I feel after eating X?"` — graph retrieval from food entity
  pulls related observations

**Idiomatic agent prompt:**
> "When I say 'log:', save the rest of the message as a health observation.
> When I ask analytical questions about my health log, ALWAYS search memory
> first — don't speculate from training data. If memory abstains, say so.
> Never offer medical advice — only summarize what's logged."

**A-MEM helps here** — auto-links observations that share entities (the same
medication, same kind of food, same activity) without you having to manually
tag them.

---

## Pattern 3 — Personal life / journal

**What goes in:**
- Daily reflections, what went well / what didn't
- Conversations with people you want to remember
- Plans, intentions, things you're looking forward to
- Small wins, frustrations
- Books / podcasts / shows + your take on them

**Setup:**
- A `personal` project. Same privacy considerations as health.
- Schedule periodic `reflect` calls (`ReflectionSchedule:Enabled=true`,
  `Interval=24:00:00` for a daily summary, or weekly).
- Use `meta:weekly` / `meta:monthly` scope to roll up daily reflections into
  bigger-picture patterns.
- The bi-temporal graph captures relationship changes ("Alice moved from
  Krakow to Warsaw") — useful for tracking life events that supersede each
  other.

**Useful queries:**
- `"what was I anxious about last month?"` — abstains if no real signal
- `"books I planned to read"` — entity-driven retrieval pulls related notes

---

## Pattern 4 — Cross-tool research

**What goes in:**
- Articles you want to remember, paste in raw
- Slack messages worth keeping (via the `/api/webhooks/slack` connector)
- Notion page exports (via `memory md import`)
- Email digests (manually pasted or via Gmail webhook later)

**Setup:**
- A `research` project; tag aggressively (`paper`, `talk`, `thread`)
- Markdown folder watcher pointing at a `~/notes` folder you also sync with
  Obsidian — every saved .md auto-ingests
- Enable `QueryExpansion` — research queries are often vague enough to
  benefit from LLM rephrasing

---

## Pattern 5 — Shared with collaborators

**What goes in:**
- Team decisions that everyone needs to find
- Postmortems
- "Why we do X this way" — the institutional knowledge usually lost to
  Slack scroll

**Setup:**
- One organization, one project per team domain
- Mint API keys per collaborator (`memory api-key create --name "alice"`)
- Each user runs their own MCP server with their own bearer token; all
  resolve to the same tenant.
- Audit log: `memory api-key list` shows last-used time per key.

**Privacy:** anything saved is visible to anyone with a key for that
tenant. There's no per-user view-filter inside a project. If you need that,
use separate projects per user and `find_related_notes` to chain.

---

## Multi-domain agent ergonomics

When your daily-driver agent (Claude Code, Codex CLI) needs access to MULTIPLE
domains, you have two options:

**A. Per-domain `.mcp.json` profiles.** One `memory` server per project, all
listed in your client's MCP config. Tools have prefixed names per server in
some clients; in others they're flat (last-loaded wins). Clear scope, easy
to reason about.

**B. One "personal" tenant for everything.** A single project called `me`
that sees all your notes — work, personal, health. Much easier UX. Requires
trusting the privacy story (see PRIVACY.md) and using `kinds` + tags to
filter at search time.

I run (B) personally, with `SaveFilter` on so chat-noise gets dropped, and
provider-route to local model for sensitive content categories.
