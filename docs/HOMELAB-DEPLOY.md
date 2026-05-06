# Homelab deploy (R620, native systemd, nginx, Cloudflare Tunnel)

Recipe for deploying Memory.Api as a non-prod side-instance on the user's
homelab Dell R620. The host is on mobile (Plus) internet via Cloudflare
Tunnel — fine for personal use and exploratory work, **not** for serving
external production traffic. The Azure App Service deploy at
`llmmemory-api.azurewebsites.net` remains the production target.

This doc lives on the `homelab-deploy` branch; merging it back to `master`
when the deploy stabilizes is fine.

---

## What gets deployed where

| Piece | Location |
|---|---|
| Memory.Api binaries | `/opt/apps/llm-memory/releases/<TS>/` + `current` symlink |
| systemd unit | `/etc/systemd/system/llm-memory.service` |
| Env file (DB pw, Azure key) | `/etc/llm-memory/llm-memory.env` (mode 0640, root:app-llm-memory) |
| Runtime data | `/var/lib/llm-memory/` |
| Logs | `journalctl -u llm-memory` |
| App user | `app-llm-memory` (system, no-login) |
| Local port | `127.0.0.1:5001` (Kestrel) |
| nginx vhost | `/etc/nginx/sites-available/llm-memory` |
| Public hostname | `memory.aidamian.uk` |
| Postgres | local `127.0.0.1:5432`, db `llm_memory`, owner `llm_memory_owner`, runtime `memory_app` |

## Prereqs (one-time per host)

- Ubuntu 24.04 + .NET 10 + PostgreSQL 17 + nginx + cloudflared (already installed on R620)
- AGE 1.7.0 + pgvector 0.8.2 in `pg_available_extensions`
- `shared_preload_libraries='age'` in `postgresql.conf`
- nginx homelab proxy snippet at `/etc/nginx/snippets/homelab-proxy.conf`
  (see `~/serwer/deploy-apps.md`)

## DB provisioning

Run as the `postgres` superuser (peer auth via `sudo -u postgres psql`):

```sql
-- 1. Owner role + DB + extensions
CREATE ROLE llm_memory_owner LOGIN PASSWORD '<OWNER_PW>';
CREATE DATABASE llm_memory OWNER llm_memory_owner;

\c llm_memory
CREATE EXTENSION IF NOT EXISTS vector;
CREATE EXTENSION IF NOT EXISTS age;
CREATE SCHEMA IF NOT EXISTS memory AUTHORIZATION llm_memory_owner;

-- 2. AGE graph (search_path needed so create_graph finds graphid_ops)
SET search_path = ag_catalog, "$user", public;
SELECT create_graph('memory_graph');

-- 3. Transfer ownership of all memory_graph objects to owner role
DO $$
DECLARE r record;
BEGIN
  FOR r IN SELECT relname, relkind FROM pg_class c JOIN pg_namespace n ON c.relnamespace = n.oid
           WHERE n.nspname = 'memory_graph' AND c.relkind IN ('r','S','v','i')
  LOOP
    EXECUTE format('ALTER %s memory_graph.%I OWNER TO llm_memory_owner',
                   CASE r.relkind WHEN 'r' THEN 'TABLE' WHEN 'S' THEN 'SEQUENCE'
                                  WHEN 'v' THEN 'VIEW' WHEN 'i' THEN 'INDEX' END,
                   r.relname);
  END LOOP;
END $$;
ALTER SCHEMA memory_graph OWNER TO llm_memory_owner;
ALTER DEFAULT PRIVILEGES IN SCHEMA memory_graph GRANT ALL ON TABLES TO llm_memory_owner;
ALTER DEFAULT PRIVILEGES IN SCHEMA memory_graph GRANT ALL ON SEQUENCES TO llm_memory_owner;

-- 4. ag_catalog visibility for owner (migrations need this)
GRANT ALL ON ALL TABLES IN SCHEMA ag_catalog TO llm_memory_owner;
GRANT ALL ON ALL SEQUENCES IN SCHEMA ag_catalog TO llm_memory_owner;
GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA ag_catalog TO llm_memory_owner;
GRANT USAGE ON SCHEMA ag_catalog TO llm_memory_owner;

-- 5. EF migrations need to CREATE the memory_app role themselves
ALTER ROLE llm_memory_owner CREATEROLE;
```

## EF migrations

From a workstation, with an SSH tunnel open
(`ssh -L 5532:127.0.0.1:5432 hdtdtr@192.168.18.6`):

```bash
MEMORY_DESIGN_CONNSTR="Host=localhost;Port=5532;Database=llm_memory;Username=llm_memory_owner;Password=<OWNER_PW>" \
  dotnet ef database update --project src/Memory.Storage
```

## Post-migration grants (AGE access for runtime + drop CREATEROLE)

```sql
\c llm_memory
ALTER ROLE llm_memory_owner NOCREATEROLE;

GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA ag_catalog TO memory_app;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA ag_catalog TO memory_app;
GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA ag_catalog TO memory_app;
GRANT USAGE ON SCHEMA ag_catalog TO memory_app;

ALTER SCHEMA memory_graph OWNER TO memory_app;
DO $$
DECLARE r record;
BEGIN
  FOR r IN SELECT relname, relkind FROM pg_class c JOIN pg_namespace n ON c.relnamespace = n.oid
           WHERE n.nspname = 'memory_graph' AND c.relkind IN ('r','S','v','i')
  LOOP
    EXECUTE format('ALTER %s memory_graph.%I OWNER TO memory_app',
                   CASE r.relkind WHEN 'r' THEN 'TABLE' WHEN 'S' THEN 'SEQUENCE'
                                  WHEN 'v' THEN 'VIEW' WHEN 'i' THEN 'INDEX' END,
                   r.relname);
  END LOOP;
END $$;
GRANT CREATE ON SCHEMA memory_graph TO memory_app;
ALTER ROLE memory_app PASSWORD '<APP_PW>';
```

## App user + dirs (one-time)

```bash
APP=llm-memory
APPUSER=app-$APP

sudo useradd --system --home /opt/apps/$APP --shell /usr/sbin/nologin $APPUSER
sudo install -d -m 0755 -o root -g root /opt/apps/$APP/releases
sudo install -d -m 0750 -o $APPUSER -g $APPUSER /var/lib/$APP
sudo install -d -m 0750 -o $APPUSER -g $APPUSER /var/log/$APP
sudo install -d -m 0750 -o root -g $APPUSER /etc/$APP
```

## Build + deploy

From the workstation:

```bash
dotnet publish src/Memory.Api/Memory.Api.csproj \
  -c Release -o /tmp/llm-memory-publish \
  --runtime linux-x64 --self-contained false
rm -f /tmp/llm-memory-publish/appsettings.Local.json   # never ship local config
tar -czf /tmp/llm-memory-publish.tar.gz -C /tmp/llm-memory-publish .
scp -i ~/.ssh/r620_ed25519 /tmp/llm-memory-publish.tar.gz hdtdtr@192.168.18.6:/tmp/
```

On the server:

```bash
APP=llm-memory
STAMP=$(date -u +%Y%m%dT%H%M%SZ)
RELEASE=/opt/apps/$APP/releases/$STAMP

sudo install -d -m 0755 -o root -g root "$RELEASE"
sudo tar -xzf /tmp/llm-memory-publish.tar.gz -C "$RELEASE"
sudo chown -R root:root "$RELEASE"
sudo ln -sfn "$RELEASE" /opt/apps/$APP/current.new
sudo mv -Tf /opt/apps/$APP/current.new /opt/apps/$APP/current
sudo rm /tmp/llm-memory-publish.tar.gz

sudo systemctl restart llm-memory   # only after first deploy creates the unit (below)
```

## Env file (`/etc/llm-memory/llm-memory.env`, mode 0640)

```ini
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://127.0.0.1:5001
ASPNETCORE_FORWARDEDHEADERS_ENABLED=true
DOTNET_PRINT_TELEMETRY_MESSAGE=false

ConnectionStrings__memorydb=Host=127.0.0.1;Port=5432;Database=llm_memory;Username=memory_app;Password=<APP_PW>

Llm__ChatProvider=AzureOpenAI
Llm__EmbeddingProvider=AzureOpenAI
Llm__ChatModel=<AOAI_CHAT_DEPLOYMENT>
Llm__EmbeddingModel=<AOAI_EMBED_DEPLOYMENT>
Llm__EmbeddingDimensions=3072
Llm__AzureOpenAi__Endpoint=<AOAI_ENDPOINT>
Llm__AzureOpenAi__ApiKey=<AOAI_API_KEY>
Llm__AzureOpenAi__ChatDeployment=<AOAI_CHAT_DEPLOYMENT>
Llm__AzureOpenAi__EmbeddingDeployment=<AOAI_EMBED_DEPLOYMENT>
```

Install with `sudo install -m 0640 -o root -g app-llm-memory <src> /etc/llm-memory/llm-memory.env`.

## systemd unit (`/etc/systemd/system/llm-memory.service`)

```ini
[Unit]
Description=llm-memory .NET app (Memory.Api)
After=network-online.target postgresql.service
Wants=network-online.target

[Service]
User=app-llm-memory
Group=app-llm-memory
WorkingDirectory=/opt/apps/llm-memory/current
ExecStart=/usr/bin/dotnet /opt/apps/llm-memory/current/Memory.Api.dll
Restart=always
RestartSec=5
KillSignal=SIGINT
SyslogIdentifier=llm-memory
EnvironmentFile=-/etc/llm-memory/llm-memory.env
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=full
ReadWritePaths=/var/lib/llm-memory /var/log/llm-memory

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now llm-memory
sudo systemctl status llm-memory --no-pager
```

## nginx site (`/etc/nginx/sites-available/llm-memory`)

```nginx
server {
    listen 80;
    listen [::]:80;
    server_name memory.aidamian.uk;

    client_max_body_size 50m;

    location / {
        proxy_pass http://127.0.0.1:5001;
        include snippets/homelab-proxy.conf;
    }
}
```

```bash
sudo ln -sfn /etc/nginx/sites-available/llm-memory /etc/nginx/sites-enabled/
sudo nginx -t && sudo systemctl reload nginx
```

## Tenant + admin API key (one-time)

Through SSH tunnel, from workstation:

```bash
dotnet run --project src/Memory.Cli -- init \
  --connection-string "Host=localhost;Port=5532;Database=llm_memory;Username=llm_memory_owner;Password=<OWNER_PW>" \
  --org "homelab" --user-email "you@example.com" --user-name "you" \
  --project "default" --embedding-model "text-embedding-3-large"
# → prints org/user/project GUIDs

dotnet run --project src/Memory.Cli -- api-key create \
  --connection-string "Host=localhost;Port=5532;..." \
  --org <org-guid> --user <user-guid> --project <project-guid> \
  --name "homelab-admin" --admin
# → prints memk_… (save it; never shown again)
```

## Cloudflare Tunnel hostname (manual, one-time)

The `homelab` tunnel is remote-config managed. In Cloudflare Zero Trust dashboard:

1. **Networks → Tunnels → `homelab` → Public Hostnames → Add a public hostname**
2. Subdomain: `memory`, Domain: `aidamian.uk`
3. Type: `HTTP`, URL: `localhost:80`
4. Save.

Verify:
```bash
curl -I https://memory.aidamian.uk/
```

For an admin/private app, gate it behind **Cloudflare Access** before serving:
- Zero Trust → Access → Applications → Add → Self-hosted
- Application domain: `memory.aidamian.uk`
- Policy: `include emails == your@email.com`
- Save.

After Access is active, only authenticated browser sessions or requests with a
valid `CF-Access-Client-Id` / `CF-Access-Client-Secret` service-token pair
reach the origin. For programmatic clients (CLI, MCP HTTP from a non-browser
agent), mint a service token in Access → Service Auth.

## Smoke test

```bash
# Local (on the box)
curl -s http://127.0.0.1:5001/                | jq .
curl -s http://127.0.0.1:5001/api/health      | jq .

# Through nginx
curl -s -H 'Host: memory.aidamian.uk' http://127.0.0.1/api/health | jq .

# Through Cloudflare (after CF hostname is added)
curl -s https://memory.aidamian.uk/api/health | jq .

# With bearer
curl -s https://memory.aidamian.uk/api/notes \
  -H "Authorization: Bearer memk_…" | jq .
```

## Re-deploy procedure

```bash
# workstation
dotnet publish ... -o /tmp/llm-memory-publish --runtime linux-x64 --self-contained false
rm -f /tmp/llm-memory-publish/appsettings.Local.json
tar -czf /tmp/llm-memory-publish.tar.gz -C /tmp/llm-memory-publish .
scp /tmp/llm-memory-publish.tar.gz hdtdtr@192.168.18.6:/tmp/

# server
APP=llm-memory; STAMP=$(date -u +%Y%m%dT%H%M%SZ); RELEASE=/opt/apps/$APP/releases/$STAMP
sudo install -d -m 0755 "$RELEASE"
sudo tar -xzf /tmp/llm-memory-publish.tar.gz -C "$RELEASE"
sudo ln -sfn "$RELEASE" /opt/apps/$APP/current.new
sudo mv -Tf /opt/apps/$APP/current.new /opt/apps/$APP/current
sudo systemctl restart llm-memory
sudo systemctl status llm-memory --no-pager
```

## Rollback

```bash
ls /opt/apps/llm-memory/releases/
sudo ln -sfn /opt/apps/llm-memory/releases/<previous-TS> /opt/apps/llm-memory/current
sudo systemctl restart llm-memory
```

## Gotchas burned through during the first deploy

1. **`SELECT create_graph('memory_graph')` fails with `operator class "graphid_ops" does not exist`** unless `search_path = ag_catalog` is set first. The extension installs the operators in `ag_catalog`; without it on the search path, the function can't find them.

2. **EF migration fails with `permission denied to create role`** because `llm_memory_owner` isn't a superuser. Workaround: `ALTER ROLE llm_memory_owner CREATEROLE` before running migrations, then drop the privilege after.

3. **Migration fails again with `permission denied for table ag_graph`** — `llm_memory_owner` has no rights on `ag_catalog` tables. Fix: `GRANT ALL ON ALL TABLES IN SCHEMA ag_catalog`.

4. **Migration fails on `_label_id_seq`** — AGE creates auto-managed sequences (`_label_id_seq`, `_label_*_props_idx_seq`, etc.) owned by `postgres`. Loop over `pg_class WHERE relnamespace = 'memory_graph'` and ALTER OWNER on each, plus `ALTER DEFAULT PRIVILEGES` to cover future objects.

5. **nginx fails to reload** with `homelab-proxy.conf` not found until you create the snippet file (one-time host prep step from `~/serwer/deploy-apps.md` that wasn't run yet).
