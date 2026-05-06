# Operations runbook

Common scenarios, in order of how often you'll run into them.

## Daily start-up

```bash
# WSL2 PG idles after some minutes — keep it warm for the session:
wsl -d Ubuntu --exec sleep 7200 &

# OpenBao auto-starts via systemd in WSL2, but it's SEALED after a hard
# reboot. Re-unseal:
wsl -d Ubuntu -- bash -c '
  export BAO_ADDR=http://127.0.0.1:8200
  bao operator unseal $(jq -r .unseal_key ~/.config/openbao-dev-creds.json)
'

# Boot Memory.Api with the secret-source chain you want:
export AZURE_CONFIG_DIR="$HOME/.azure-foundry"
export MEMORY_KV_URI="https://llmmemory-kv.vault.azure.net/"
export MEMORY_BAO_ADDR="http://127.0.0.1:8200"
export MEMORY_BAO_TOKEN=$(jq -r .root_token /mnt/wsl/.../openbao-dev-creds.json)
dotnet run --project src/Memory.Api/Memory.Api.csproj
```

For Web UI:
```bash
dotnet run --project src/Memory.Web/Memory.Web.csproj
# → http://localhost:5570
```

For MCP stdio (manually testing):
```bash
echo '{"jsonrpc":"2.0","id":1,"method":"tools/list"}' | \
  dotnet src/Memory.Mcp.Stdio/bin/Debug/net10.0/Memory.Mcp.Stdio.dll
```

## Healthcheck

```bash
curl http://localhost:5566/api/health -H "Authorization: Bearer memk_…"
```

| Field | Healthy looks like |
|---|---|
| `status` | `"healthy"` |
| `db.status` | `"ok"` |
| `db.latencyMs` | < 100 in dev |
| `llm.status` | `"ok"` |

If degraded — check `/tmp/api.log` for the underlying exception.

## Comprehensive smoke test

```bash
bash scripts/smoke-test-api.sh
# Expected: 20/20 passing
```

Tests: health, list endpoints, 4 search queries with provenance, faceted
filters, query expansion, abstention on irrelevant query, RLS isolation
(no headers / wrong project / invalid bearer), bi-temporal edge counts.

If it fails on a specific search — check that you have a populated dev
tenant. Mint a key, save a few episodes, re-run.

## Mint an API key

```bash
MEMORY_CONNSTR="Host=localhost;Port=5435;Database=llm_memory;Username=postgres;Password=…" \
dotnet run --project src/Memory.Cli -- api-key create \
  --org   <org-uuid> \
  --user  <user-uuid> \
  --project <project-uuid> \
  --name  "claude-code"
# → memk_…  ←  save this; the raw token is never shown again
```

List + revoke:
```bash
memory api-key list
memory api-key revoke --id <api-key-id>
```

## Backup + restore

Two paths depending on whether you have direct DB access or only the HTTP API.

### HTTP path — works against any deploy (incl. cloud)

```bash
# Stream a single .zip for the caller's tenant via the API
memory backup download \
  --api-url https://llmmemory-api.azurewebsites.net \
  --api-key memk_… \
  --out ./memory-backup.zip
```

The zip contains per-entity JSONs (episodes, notes, embeddings, mentions,
relations, reflections, image embeddings, entities, edges) plus a `notes-md/`
folder of one Obsidian-frontmatter Markdown per active note. Slim it with
`--no-embeddings` / `--no-image-embeddings` if you only want the prose.

Same endpoint can be hit directly from a browser / `curl`:
```bash
curl -fSL https://your.host/api/backup/download \
  -H "Authorization: Bearer memk_…" \
  -o memory-backup.zip
```

### DB path — admin-only, requires postgres connection

```bash
# Dump every active tenant entity to JSON
memory backup dump --connection-string "Host=…" --org <org-uuid> --output ./backup.json

# Restore into a fresh project (won't merge into existing — drops first)
memory backup restore --connection-string "Host=…" --input ./backup.json
```

Both paths preserve bi-temporal supersession state. Use `download` for
self-service tenant exports, `dump`/`restore` for cross-deploy migrations
where you control the database directly.

## Markdown round-trip (Obsidian etc)

```bash
memory md export --out-dir ./obsidian-vault --limit 2000
# Each note becomes one .md with YAML frontmatter
# Browse / edit in Obsidian / VS Code

memory md import --in-dir ./obsidian-vault --dry-run   # preview
memory md import --in-dir ./obsidian-vault             # actual
# Each .md body goes through the standard ingestion pipeline
```

Frontmatter is informational on import — the LLM re-extracts notes,
re-embeds, re-links. This is intentional: you get current pipeline behavior
applied to old content (better embeddings, better entity resolution, etc).

## Run the retrieval eval

```bash
# Generate eval queries from the corpus (one LLM call per note)
memory eval gen-queries --count 30 --out eval-queries.json

# Run + report Recall@K + MRR
memory eval run --in eval-queries.json --top-k 10
```

Use this before/after pipeline tweaks (Reranker / GraphRetrieval / TimeDecay
/ QueryExpansion env vars) to measure delta.

## Apply migrations

```bash
# Use the postgres superuser conn string for design-time:
export MEMORY_DESIGN_CONNSTR="Host=localhost;Port=5435;Database=llm_memory;Username=postgres;Password=…"
dotnet ef database update --project src/Memory.Storage
```

Migrations run as superuser (CREATE ROLE / EXTENSION / DDL); runtime should
connect as `memory_app` (NOBYPASSRLS — see ARCHITECTURE.md for why).

To create a new migration after schema change:
```bash
dotnet ef migrations add MyMigration --project src/Memory.Storage
# Inspect the generated .cs file; edit if needed (idempotent SQL helpful)
dotnet ef database update --project src/Memory.Storage
```

## Schema-per-org provisioning (foundation only)

```bash
memory tenants list                                       # show registered
memory tenants provision-schema --org <org-uuid>          # create empty
memory tenants drop-schema      --org <org-uuid>          # remove
```

These commands populate `memory.tenant_schemas` but **don't yet route
connections** — the runtime still uses RLS in the central schema. Foundation
for when you onboard a second org.

## OpenBao operations

```bash
# Status
wsl -d Ubuntu -- bash -c "BAO_ADDR=http://127.0.0.1:8200 bao status"

# Unseal (after PG / WSL reboot)
wsl -d Ubuntu -- bash -c "BAO_ADDR=http://127.0.0.1:8200 bao operator unseal <KEY>"

# Write a secret directly (Web UI at /secrets is easier)
wsl -d Ubuntu -- bash -c '
  export BAO_ADDR=http://127.0.0.1:8200
  export BAO_TOKEN=<root-token>
  bao kv put secret/llm-memory ConnectionStrings__memorydb="Host=…"
'
```

Re-init scenario (lost unseal key etc):
```bash
sudo systemctl stop openbao
sudo rm -rf /opt/openbao/data/*
sudo systemctl start openbao
# → uninitialized + sealed; bao operator init → unseal → reload secrets
```

## Azure Key Vault operations

```bash
# Set up tenant + creds (per ~/.azure-foundry profile)
AZURE_CONFIG_DIR="$HOME/.azure-foundry" az account show

# Write a secret
AZURE_CONFIG_DIR="$HOME/.azure-foundry" az keyvault secret set \
  --vault-name llmmemory-kv \
  --name "Llm--AzureOpenAi--ApiKey" \
  --value "$NEW_KEY"

# Read back (CLI; the API does this automatically through DefaultAzureCredential)
AZURE_CONFIG_DIR="$HOME/.azure-foundry" az keyvault secret show \
  --vault-name llmmemory-kv \
  --name "Llm--AzureOpenAi--ApiKey" \
  --query value -o tsv

# Grant a collaborator read-only
AZURE_CONFIG_DIR="$HOME/.azure-foundry" az role assignment create \
  --role "Key Vault Secrets User" \
  --assignee-object-id <their-object-id> \
  --assignee-principal-type User \
  --scope "/subscriptions/<sub-id>/resourceGroups/zasobyPolska/providers/Microsoft.KeyVault/vaults/llmmemory-kv"
```

The `az role assignment create` CLI sometimes returns a spurious
`MissingSubscription` error in this configuration; the workaround is `az
rest --method put …` to the `roleAssignments/<guid>` API directly — see
`reference_azure_keyvault.md` in the auto-memory.

## Troubleshooting matrix

| Symptom | First check | Likely fix |
|---|---|---|
| `/api/health` shows `db.status: fail: ...connect 5435` | `wsl -d Ubuntu -- pg_isready -p 5435 -h localhost` | `wsl -d Ubuntu -- sudo service postgresql start` |
| `/api/health` shows `llm.status: unconfigured` | `appsettings.Local.json` Llm section | fill in provider creds; restart |
| Search returns 0 hits / 20 candidates | `abstain: true`? | the query was genuinely irrelevant. Or lower `Abstention:MinTopScore`. |
| Search returns rows from another project | runtime conn string Username | switch to `memory_app`. postgres bypasses RLS. |
| `access to library "age"` | runtime user | `shared_preload_libraries = 'age'` in postgresql.conf, restart PG |
| MCP stdio: tool calls hang | stdout corrupted by logs | `Logging:Console:LogToStandardErrorThreshold = Trace` in appsettings |
| Eval Recall@1 dropped after a tweak | run eval again with same queries | tweak introduced regression — revert or adjust thresholds |
| Web UI `/secrets` shows "OpenBao not configured" | env vars on Memory.Api process | export `MEMORY_BAO_ADDR` + `MEMORY_BAO_TOKEN`; restart |
| Web UI `/login` token rejected | `memory api-key list` | token revoked or for different tenant; mint a new one |
| Multi-modal ingest: image silently ignored | API logs around `IImageDescriber` | provider doesn't support vision (Bedrock/Vertex chat are text-only here) |
| `/secrets` page unstyled | `Memory.Web.styles.css` | `dotnet build src/Memory.Web` and refresh; scoped CSS regenerates per-component hash |

## CI

`.github/workflows/ci.yml` runs on push/PR:
- `dotnet restore` + `dotnet build --configuration Release`
- `dotnet test tests/Memory.Domain.Tests/...` (no external deps)
- `dotnet format --verify-no-changes` (warn-level, non-blocking)

`.github/workflows/eval.yml` is `workflow_dispatch` — manual trigger requires
a self-hosted runner with PG+AGE+pgvector + a populated dev tenant + repo
secrets `MEMORY_API_URL`, `MEMORY_API_KEY`. Use this to gate pipeline tweaks
in PRs.
