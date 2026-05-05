# MCP integration guide

How to wire LLM Memory into the MCP clients you use day-to-day. The same memory
store backs every client — what one writes, the others can read.

## Tools registered

Both `Memory.Mcp.Stdio` (stdio transport) and `Memory.Api` at `/mcp` (HTTP+SSE
transport) expose:

| Tool | Effect |
|---|---|
| `save_episode(source, content, occurredAt?)` | Save a raw episode. Pipeline runs save filter (optional) → extraction → embedding → AGE upserts → A-MEM linking. Returns episode id, note ids, entity ids, and `skipped`/`skipReason` when the filter dropped the input. |
| `search_memory(query, maxResults?)` | Hybrid search across vector + BM25 + graph PPR + (optional) image-vector. Returns ranked notes with related entity ids. |
| `get_entity(name, includeRelations?)` | Fetch an entity by canonical name + optionally its 1-hop neighbors with bi-temporal validity. |
| `reflect(scope, since?, until?, maxNotes?)` | Generate a reflection over notes (or other reflections, when scope starts with `meta:`). |
| `find_related_notes(noteId, maxResults?)` | A-MEM links — show what similar notes were auto-linked at ingest time. |

Resources (read-only context that LLMs can pull on demand):

| URI | Returns |
|---|---|
| `memory://episodes` | Recent episodes (raw inputs) |
| `memory://notes` | Recent atomic notes |
| `memory://reflections` | Recent reflections |

---

## Claude Code (CLI / desktop)

Two paths: stdio (preferred for local) or HTTP (for remote / shared).

### stdio — preferred, no server required

In any project where you want memory-aware Claude:

`.mcp.json` in the project root:
```json
{
  "mcpServers": {
    "memory": {
      "command": "dotnet",
      "args": [
        "C:/Users/me/source/repos/LLM Memory/src/Memory.Mcp.Stdio/bin/Debug/net10.0/Memory.Mcp.Stdio.dll"
      ],
      "env": {
        "Tenant__OrganizationId": "<org-uuid>",
        "Tenant__UserId":         "<user-uuid>",
        "Tenant__ProjectId":      "<project-uuid>",
        "Storage__ConnectionString": "Host=localhost;Port=5435;Database=llm_memory;Username=memory_app;Password=memory_app",
        "Llm__ChatProvider":      "AzureOpenAI",
        "Llm__ChatModel":         "gpt-5-mini",
        "Llm__AzureOpenAi__Endpoint": "https://….services.ai.azure.com",
        "Llm__AzureOpenAi__ApiKey":   "${AZURE_OPENAI_KEY}",
        "Llm__AzureOpenAi__ChatDeployment":      "gpt-5-mini",
        "Llm__AzureOpenAi__EmbeddingDeployment": "text-embedding-3-large"
      }
    }
  }
}
```

Then in Claude Code:
```
/mcp
# memory  ──────  ✓ connected
#   tools: save_episode, search_memory, get_entity, reflect, find_related_notes
#   resources: memory://episodes, memory://notes, memory://reflections
```

**Per-project tip:** different projects can use different `Tenant__ProjectId`
values pointing at the same database, so memories stay scoped per project.
Or share one project across everything for a "personal" knowledge base.

### HTTP — when stdio doesn't fit

If you're running Memory.Api on a different machine (or want one host serving
multiple clients), point Claude Code at the HTTP MCP transport:

```json
{
  "mcpServers": {
    "memory": {
      "type": "http",
      "url": "https://your-host/mcp",
      "headers": {
        "Authorization": "Bearer memk_…"
      }
    }
  }
}
```

The bearer token (mint via `memory api-key create`) carries the tenant scope
on its own — no need to set `X-Memory-*` headers separately.

---

## Codex CLI

Same `.mcp.json` shape. Codex CLI reads it from the project root:
```
codex
> /mcp
```
Tools and resources appear as for Claude Code.

---

## Cursor

Add to `~/.cursor/mcp.json` (global) or `.cursor/mcp.json` (project):
```json
{
  "mcpServers": {
    "memory": { "command": "dotnet", "args": ["…Memory.Mcp.Stdio.dll"], "env": {…} }
  }
}
```
Cursor's chat picks up the tools automatically.

---

## Continue.dev

`~/.continue/config.json`:
```json
{
  "experimental": {
    "modelContextProtocolServers": [
      {
        "transport": { "type": "stdio", "command": "dotnet", "args": ["…Memory.Mcp.Stdio.dll"] }
      }
    ]
  }
}
```

---

## ChatGPT desktop / agents

Use the HTTP transport. ChatGPT's Plugins / Agents need a public URL or a
tunnel (cloudflared, ngrok). Bearer auth as above.

```json
{
  "mcpServers": {
    "memory": { "type": "http", "url": "https://your-public-host/mcp", "headers": {"Authorization":"Bearer memk_…"} }
  }
}
```

---

## Direct API consumers (no MCP)

For agents that don't speak MCP yet (Anthropic SDK, Vercel AI SDK), call the
REST endpoints directly. See [`API.md`](API.md). The shape is intentionally
closer to a typical RAG-with-memory API:
- `POST /api/episodes` to save
- `POST /api/search` to retrieve
- `POST /api/chat` to ask (memory-aware streaming)

---

## Cross-model usage patterns

The point of this design — same store, many models. Some setups that work:

**1. One project, multiple clients**
- Claude Code reads/writes during coding work
- Codex CLI reads the same memories during separate sessions
- ChatGPT desktop occasionally queries via HTTP for general questions
- All three see decisions, learnings, errors made anywhere

**2. Project-scoped memory partitions**
- `project-a` for client A's codebase, `project-b` for personal notes
- Same database, different tenant scope per `.mcp.json` env block
- Search, reflection, A-MEM linking all stay within scope

**3. Provider-flavored memory**
- Most queries hit gpt-5.5 / Claude — fast, capable
- Reranker on a flash-tier (`gpt-5.4-mini` / `claude-haiku-4-5`) — cheap, plenty for relevance scoring
- Reflection on Claude (longer context, better prose)
- All three use the same memory.api_keys row + same tenant; the LLM
  routing happens in `Memory.Llm` config, not at the MCP layer.

**4. Sensitive vs general split** *(see [`PRIVACY.md`](PRIVACY.md))*
- Two projects: `personal` and `work`
- Personal project's `Llm:ChatProvider` set to a local model (Ollama, LM Studio)
- Work project on Azure OpenAI
- Both visible from Claude Code, but only work memories ever leave the machine

---

## Tool design notes (for prompt engineering)

When asking an agent to use these tools, calibrate expectations:

- `save_episode` is **deliberately conservative** when SaveFilter is on. If
  your agent expects every save to succeed, disable the filter or surface
  `skipped`/`skipReason` in the agent prompt so it can adapt.
- `search_memory` can return `abstain: true` with empty hits. Agents should
  handle that as "no relevant memory" rather than retrying or fabricating —
  that's the whole point of the abstention signal.
- `reflect` is slow (60-90 s for a meta-reflection) because it reads N notes
  through one LLM call. Don't put it in tight loops.
- `find_related_notes` returns curated A-MEM links, not arbitrary similar
  notes. Empty result means linker hasn't run yet or no neighbors crossed
  the LLM-judged confidence threshold.

---

## Troubleshooting MCP connection

| Symptom | Likely cause |
|---|---|
| Client shows "memory: not connected" | wrong dll path; use absolute path; verify with `dotnet bin/.../Memory.Mcp.Stdio.dll` directly |
| Tool calls return "tenant scope not set" | env vars not picked up; check `Tenant__OrganizationId` etc are present in the `.mcp.json` `env` block |
| `Failed to connect to 127.0.0.1:5435` | WSL2 PG idled out; `wsl -d Ubuntu -- sudo service postgresql start` |
| `access to library "age" is not allowed` | runtime connecting as a non-superuser without AGE preloaded; ensure `shared_preload_libraries = 'age'` in postgresql.conf |
| Bearer auth always 401 | check `memory api-key list` — token may have been revoked; or you're hitting a different tenant than the key was minted for |
| Cross-tenant data leaks visible | runtime connecting as `postgres` (superuser bypasses RLS) — switch connection string to `Username=memory_app;Password=memory_app` |
