# Security policy

## Reporting a vulnerability

If you find a security issue in this project — credential leak, injection,
auth bypass, RLS hole, prompt injection that escalates privileges, anything
that materially weakens tenant isolation — please **report it privately**
before opening a public issue.

Email: **hdtdtr@gmail.com** with subject prefix `[security] llm-memory`.

I'll acknowledge within 72 hours and aim to ship a fix or mitigation within
14 days. If the issue affects deployed instances I run, those get patched
first; the public commit lands once the window closes.

## Scope

In scope:
- The code in this repository (Memory.Api, Memory.Mcp.Stdio, Memory.Web,
  Memory.Cli, Memory.Pipeline, Memory.Storage, Memory.Llm, Memory.Tenancy).
- Default configuration shipped in `appsettings.json` and `*.example` files.
- Documentation that recommends a specific deployment shape.

Out of scope:
- Vulnerabilities in upstream dependencies (Postgres, AGE, pgvector, .NET,
  Microsoft.Extensions.AI, model provider SDKs) — please report those to the
  upstream project. I'll bump versions as fixes land.
- Self-inflicted misconfigurations (e.g. running Memory.Api as `postgres`
  superuser, exposing `/api/secrets/*` to the public internet, weak API keys).
  These are documented as anti-patterns; the docs are the authoritative source.
- DoS by sending oversized inputs to the LLM — rate limiting and quota are
  the operator's responsibility.

## What this project does about security

- **Tenant isolation** uses Postgres row-level security with a NOBYPASSRLS
  runtime role (`memory_app`). The migration assumes you do not connect as
  `postgres` in production. There's a smoke test that asserts cross-tenant
  reads return zero rows.
- **API keys** are stored as SHA-256 hashes; the raw `memk_…` token is shown
  exactly once at creation. There is a revoke flow (`memory api-key revoke`).
- **Secrets** go through a chain: Azure Key Vault → OpenBao → JSON file.
  No secret values are committed to the repo (see `.gitignore` and
  `.mcp.json.example`).
- **RLS smoke** is part of the integration suite — a regression that
  re-introduces the superuser bypass would fail tests.

## Responsible disclosure

I treat this as a personal project and respond on best-effort. Critical issues
that affect the publicly-deployed instance (`https://llmmemory-api.azurewebsites.net`)
are prioritized over feature work.
