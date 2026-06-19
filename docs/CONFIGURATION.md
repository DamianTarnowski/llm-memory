# Configuration reference

Every config section recognized by `Memory.Api` and `Memory.Mcp.Stdio`. Defaults
are the value that lands when you skip the section entirely. Set via
`appsettings.Local.json` (gitignored) or environment variables (use `__` as
section separator: `Llm__AzureOpenAi__ApiKey`).

`Memory.Api` also pulls from Azure Key Vault and OpenBao when configured —
see the secret-source chain at the bottom of this doc.

---

## ConnectionStrings

| Key | Default | Notes |
|---|---|---|
| `ConnectionStrings:memorydb` | (none) | Postgres connection. Runtime should use `Username=memory_app;Password=memory_app` (NOBYPASSRLS). Migrations need a superuser; pass that via `MEMORY_DESIGN_CONNSTR` for `dotnet ef`. |

## Storage

| Key | Default |
|---|---|
| `Storage:GraphName` | `memory_graph` |
| `Storage:Schema` | `memory` |

## Tenant (Memory.Mcp.Stdio + dev fallback in Memory.Web)

| Key | Default |
|---|---|
| `Tenant:OrganizationId` | (must be set) |
| `Tenant:UserId` | (must be set) |
| `Tenant:ProjectId` | (must be set) |

For `Memory.Api` the tenant is resolved per-request (bearer key OR X-Memory-*
headers); these statics aren't used.

---

## Llm — chat + text-embedding providers

| Key | Default | Notes |
|---|---|---|
| `Llm:ChatProvider` | `AzureOpenAI` | `AzureOpenAI` / `OpenAI` / `Anthropic` / `AwsBedrock` / `GoogleVertex` |
| `Llm:ChatModel` | `gpt-5-mini` | model id passed to the chat client |
| `Llm:EmbeddingProvider` | `OpenAI` | `OpenAI` or `AzureOpenAI` (Bedrock + Vertex embeddings not wired) |
| `Llm:EmbeddingModel` | `text-embedding-3-large` | |
| `Llm:EmbeddingDimensions` | `3072` | must match the model + table column |

### Provider-specific blocks

#### `Llm:AzureOpenAi`
| Key | Notes |
|---|---|
| `Endpoint` | e.g. `https://foundrypolska.services.ai.azure.com` |
| `ApiKey` | Foundry / Azure OpenAI key |
| `ChatDeployment` | model deployment name |
| `EmbeddingDeployment` | typically `text-embedding-3-large` |

#### `Llm:OpenAi`
| Key | Notes |
|---|---|
| `ApiKey` | `sk-…` |

#### `Llm:Anthropic`
| Key | Notes |
|---|---|
| `ApiKey` | `sk-ant-…` |
| `ChatModelId` | e.g. `claude-haiku-4-5-20251001` |

#### `Llm:AwsBedrock`
| Key | Notes |
|---|---|
| `Region` | e.g. `eu-central-1` |
| `ChatModelId` | e.g. `anthropic.claude-haiku-4-5-20251001-v1:0` |
| `EmbeddingModelId` | (not wired yet) |

#### `Llm:GoogleVertex`
| Key | Notes |
|---|---|
| `ProjectId` | GCP project id |
| `Location` | `global` for chat models (Gemini 3 preview); regional for embeddings |
| `AdcCredentialsPath` | Application Default Credentials json path |
| `ChatModelId` | e.g. `gemini-3-flash-preview` |
| `EmbeddingModelId` | (not wired) |
| `ImageEmbeddingEnabled` | default false; opt-in cross-modal image embedding |
| `ImageEmbeddingRegion` | default `us-central1` (model not at global) |

---

## Pipeline knobs

All optional. Each section's `Enabled` defaults to false unless noted.

### `Linking` — A-MEM auto-linker
| Key | Default |
|---|---|
| `Enabled` | false (dev typically true) |
| `NeighborCount` | 5 |
| `MinSimilarity` | 0.30 |
| `MinConfidence` | 0.60 |

### `Reranker` — LLM-as-reranker after RRF fusion
| Key | Default |
|---|---|
| `Enabled` | false |
| `TopN` | 20 |
| `MinRelevance` | 0.10 (filter below this) |
| `ModelOverride` | (gateway's chat model) |
| `MaxCharsPerCandidate` | 800 |

### `GraphRetrieval` — HippoRAG-2-style PPR
| Key | Default |
|---|---|
| `Enabled` | true |
| `Iterations` | 8 |
| `Alpha` | 0.15 |
| `MaxSeeds` | 8 |
| `MaxResults` | 30 |
| `QueryExtractorModel` | (gateway's chat model) |

### `TimeDecay`
| Key | Default |
|---|---|
| `Enabled` | false |
| `HalfLifeDays` | 30 |
| `MinMultiplier` | 0.05 |

### `QueryExpansion`
| Key | Default |
|---|---|
| `Enabled` | false |
| `MaxQueryWords` | 4 |
| `VariantCount` | 3 |
| `ModelOverride` | (gateway's chat model) |

### `QueryRouting` — cheap control-plane routing before retrieval
| Key | Default |
|---|---|
| `Enabled` | false |
| `ModelOverride` | (gateway's chat model) |
| `MaxRecentTurns` | 6 |
| `MaxVariants` | 3 |
| `MaxPromptChars` | 6000 |
| `MaxResultsCap` | 50 |
| `AllowDocumentRag` | false |

When enabled, a small chat model rewrites vague follow-ups into standalone
queries and selects a retrieval mode such as `memory_light`, `memory_medium`,
`heavy_rag`, `graph_rag`, `document_rag`, `write_memory`, or `no_rag`.
This is a fallback for simple REST/UI clients. Smart MCP callers such as Codex,
Claude Code, or DevHub/Opus should normally pass route parameters directly in
`search_memory` / `/api/search` instead of paying for another routing LLM call.

`AllowDocumentRag` should stay false until a Blob/document chunk retriever is
configured. With the default false value, `document_rag` routes are downgraded
to heavy memory search and surfaced in the search route trace.

### `SaveFilter` — agentic importance gate
| Key | Default |
|---|---|
| `Enabled` | false |
| `MinScore` | 0.30 |
| `ModelOverride` | (gateway's chat model) |

### `Abstention`
| Key | Default |
|---|---|
| `Enabled` | true |
| `MinTopScore` | 0.20 |
| `MinFusedScore` | 0.025 |

### `ReflectionSchedule`
| Key | Default |
|---|---|
| `Enabled` | false |
| `InitialDelay` | `00:05:00` |
| `Interval` | `06:00:00` |

### `MarkdownConnector` — folder watcher
| Key | Default |
|---|---|
| `Enabled` | false |
| `Path` | (must set) |
| `OrganizationId` / `UserId` / `ProjectId` | (must set — fixed scope for the BackgroundService) |
| `PollIntervalSeconds` | 60 |
| `StateFile` | `%TEMP%/memory/md-watcher-state.json` |

### `WebhookConnector`
| Key | Default |
|---|---|
| `SlackSigningSecret` | (none — verification skipped when absent, fine for loopback) |

---

## Logging

Standard `Microsoft.Extensions.Logging` shape:
```json
"Logging": {
  "LogLevel": { "Default": "Information", "Microsoft.Hosting.Lifetime": "Warning" },
  "Console": { "LogToStandardErrorThreshold": "Trace" }
}
```
For `Memory.Mcp.Stdio` the `LogToStandardErrorThreshold: Trace` is required —
stdout is the JSON-RPC channel; any logger output on stdout corrupts MCP
frames.

---

## Environment variables (special)

These aren't read from `appsettings`; they short-circuit code paths directly.

| Var | Purpose |
|---|---|
| `MEMORY_DESIGN_CONNSTR` | Picked up by `MemoryDesignTimeDbContextFactory` for `dotnet ef`. Use the postgres superuser conn string. |
| `MEMORY_API_URL` | Default API base for `memory chat` / `memory eval` / `memory md`. |
| `MEMORY_API_KEY` | Default bearer for the same CLI commands. |
| `MEMORY_CONNSTR` | Default conn string for `memory init` / `memory api-key` / `memory tenants`. Use the superuser. |
| `MEMORY_KV_URI` | Engages the Azure Key Vault provider in the Memory.Secrets chain. e.g. `https://<your-vault>.vault.azure.net/` |
| `AZURE_CONFIG_DIR` | Forwarded to the AzureCli leg of DefaultAzureCredential. Point at the alternate `~/.azure-*` directory if your KV lives on a different subscription than your default `az login`. |
| `MEMORY_BAO_ADDR` | Engages the OpenBao provider. e.g. `http://127.0.0.1:8200` |
| `MEMORY_BAO_TOKEN` | OpenBao bearer (or use AppRole). |
| `MEMORY_BAO_ROLE_ID` / `MEMORY_BAO_SECRET_ID` | OpenBao AppRole credentials (preferred over root token in non-dev). |
| `MEMORY_BAO_KV_MOUNT` | default `secret` |
| `MEMORY_BAO_KV_PATHS` | comma-separated, default `llm-memory` |
| `MEMORY_LIVE_LLM_TESTS` | gate for the few live-LLM xUnit tests (`=1` to run). |

---

## Secret-source chain (Memory.Api only)

Memory.Api wires three opt-in IConfigurationSource layers via `Memory.Secrets`.
Order in `Program.cs` is **fallback → primary**; later providers win .NET's
configuration merge:

```csharp
builder.Configuration
    .AddSecretsJsonFile("appsettings.Local.json")  // baseline
    .AddSecretsOpenBao()                            // secondary
    .AddSecretsAzureKeyVault();                     // primary
```

Each is internally `optional: true` — missing creds contribute zero keys, the
chain falls through. Drop a layer entirely by leaving its env vars unset.

Secret-key naming: Azure KV and OpenBao both disallow `:` in names. Use `--`
or `__` and the connectors rewrite back to `.NET ":"` form on load.
- KV: `Llm--AzureOpenAi--ApiKey` → `Llm:AzureOpenAi:ApiKey`
- OpenBao: `Llm__AzureOpenAi__ApiKey` → same

For setup steps see [`OPERATIONS.md`](OPERATIONS.md).

Memory.Mcp.Stdio intentionally skips this chain — stdio is local-only and
boot latency matters more than centralized secrets for a CLI process.
