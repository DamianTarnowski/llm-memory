<#
.SYNOPSIS
  Claude Code / Codex hook: submit the session transcript to the LLM Memory
  synthesis inbox (memory skills harvest).

.DESCRIPTION
  Reads the hook envelope from stdin (both CLIs use the same shape:
  session_id, transcript_path, cwd, hook_event_name), resolves which memory
  project the current repo maps to via a .llm-memory.json marker file, and
  submits the transcript.

  ALLOWLIST BY CONSTRUCTION: repos without a .llm-memory.json marker are
  never harvested — the script exits 0 silently. Confidential projects stay
  untouched unless you explicitly opt them in.

  Marker file (.llm-memory.json in the repo root, or any parent of cwd):
    {
      "orgId":     "<org-guid>",
      "projectId": "<project-guid>",
      "connectionString": "Host=...;Username=memory_app;...",   // optional; falls back to MEMORY_CONNSTR
      "cliPath": "C:/path/to/memory.exe"                        // optional; falls back to 'memory' on PATH
    }

  Codex note: Codex parses but SKIPS async hooks, so its hook entry must run
  this script synchronously — pass -SelfBackground so the submit happens in a
  detached child process and the agent loop is not blocked.
#>
param(
    [switch]$SelfBackground,
    [string]$EnvelopeFile
)

$ErrorActionPreference = 'SilentlyContinue'

# ---- read the hook envelope (stdin, or the temp file a backgrounded copy gets) ----
if ($EnvelopeFile -and (Test-Path $EnvelopeFile)) {
    $raw = Get-Content $EnvelopeFile -Raw
    Remove-Item $EnvelopeFile -Force
} else {
    $raw = [Console]::In.ReadToEnd()
}
if ([string]::IsNullOrWhiteSpace($raw)) { exit 0 }
try { $envelope = $raw | ConvertFrom-Json } catch { exit 0 }

$transcriptPath = $envelope.transcript_path
$sessionId = $envelope.session_id
$cwd = $envelope.cwd
if ([string]::IsNullOrWhiteSpace($transcriptPath) -or -not (Test-Path $transcriptPath)) { exit 0 }
if ([string]::IsNullOrWhiteSpace($cwd)) { $cwd = (Get-Location).Path }

# ---- resolve the repo -> project mapping (allowlist) -----------------------
$marker = $null
$dir = $cwd
while ($dir) {
    $candidate = Join-Path $dir '.llm-memory.json'
    if (Test-Path $candidate) { $marker = $candidate; break }
    $parent = Split-Path $dir -Parent
    if ($parent -eq $dir) { break }
    $dir = $parent
}
if (-not $marker) { exit 0 }   # unmapped repo: never harvested, never logged

try { $map = Get-Content $marker -Raw | ConvertFrom-Json } catch { exit 0 }
if (-not $map.orgId -or -not $map.projectId) { exit 0 }

# ---- self-background (Codex: async hooks are skipped, so detach ourselves) --
if ($SelfBackground) {
    $tmp = [System.IO.Path]::GetTempFileName()
    Set-Content -Path $tmp -Value $raw -Encoding UTF8
    Start-Process -WindowStyle Hidden -FilePath 'pwsh' -ArgumentList @(
        '-NoProfile', '-File', $PSCommandPath, '-EnvelopeFile', $tmp
    ) | Out-Null
    exit 0
}

# ---- submit -----------------------------------------------------------------
$source = if ($transcriptPath -like '*codex*') { 'codex' } else { 'claude-code' }
$branch = ''
try { $branch = (git -C $cwd rev-parse --abbrev-ref HEAD 2>$null) } catch {}

$cli = if ($map.cliPath) { $map.cliPath } else { 'memory' }
$connArgs = @()
if ($map.connectionString) { $connArgs = @('--connection-string', $map.connectionString) }

& $cli skills harvest --quiet `
    --path $transcriptPath `
    --source $source `
    --session-id ($sessionId ?? 'unknown') `
    --cwd $cwd `
    --git-branch ($branch ?? '') `
    --org $map.orgId `
    --project $map.projectId `
    @connArgs 2>$null | Out-Null

exit 0
