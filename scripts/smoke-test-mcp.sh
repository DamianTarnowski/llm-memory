#!/bin/bash
# Smoke test the Memory.Mcp.Stdio server end-to-end via JSON-RPC.

set -e

REPO="$(cd "$(dirname "$0")/.." && pwd)"
DLL_WIN="$(cygpath -w "$REPO/src/Memory.Mcp.Stdio/bin/Debug/net10.0/Memory.Mcp.Stdio.dll" 2>/dev/null || echo "$REPO/src/Memory.Mcp.Stdio/bin/Debug/net10.0/Memory.Mcp.Stdio.dll")"
OUT="$REPO/scripts/.smoke-output.jsonl"
ERR="$REPO/scripts/.smoke-stderr.log"
OUT_WIN="$(cygpath -w "$OUT" 2>/dev/null || echo "$OUT")"
rm -f "$OUT" "$ERR"

CONTENT='Damian is building LLM Memory, a second-brain memory system for AI assistants. The project uses .NET 10, PostgreSQL with pgvector, and Apache AGE knowledge graph. He uses Anthropic Claude for chat and OpenAI for embeddings.'

echo "=== feeding 5 messages with delays for LLM calls ==="
{
    cat <<EOF
{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"smoke","version":"1"}}}
{"jsonrpc":"2.0","method":"notifications/initialized"}
EOF
    sleep 1
    python -c "
import json
print(json.dumps({'jsonrpc':'2.0','id':2,'method':'tools/call','params':{'name':'save_episode','arguments':{'source':'smoke-test','content':'''$CONTENT'''}}}, separators=(',',':')))
"
    sleep 30
    cat <<'EOF'
{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"search_memory","arguments":{"query":"what is Damian building","maxResults":5}}}
EOF
    sleep 12
    cat <<'EOF'
{"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"reflect","arguments":{"scope":"smoke","maxNotes":5}}}
EOF
    sleep 25
} | timeout 120 dotnet "$DLL_WIN" 2>"$ERR" | tee "$OUT" >/dev/null

echo ""
echo "=== summary ==="
python -c "
import json
out_path = r'$OUT_WIN'
with open(out_path, 'r', encoding='utf-8') as f:
    lines = []
    for line in f:
        s = line.strip()
        if s.startswith('{'):
            try: lines.append(json.loads(s))
            except: pass

for r in lines:
    rid = r.get('id')
    if 'error' in r:
        print(f'[id={rid}] ERROR:', json.dumps(r['error'])[:600])
        continue
    res = r.get('result', {})
    if rid == 1:
        info = res.get('serverInfo', {})
        print(f'[id=1 initialize]  protocol={res.get(chr(34)+\"protocolVersion\"+chr(34).strip(chr(34))) or res.get(\"protocolVersion\")} server={info.get(\"name\")}')
    elif rid == 2:
        c = res.get('content', [])
        if c and 'text' in c[0]:
            try: payload = json.loads(c[0]['text'])
            except: payload = c[0]['text']
            print(f'[id=2 save_episode]')
            print(json.dumps(payload, indent=2)[:1500])
    elif rid == 3:
        c = res.get('content', [])
        if c and 'text' in c[0]:
            try: payload = json.loads(c[0]['text'])
            except: payload = {'raw': c[0]['text']}
            print(f'[id=3 search_memory] totalCandidates={payload.get(\"totalCandidates\")}')
            for h in payload.get('hits', [])[:3]:
                print(f'   score={h.get(\"score\"):.4f} content={h.get(\"content\", \"\")[:100]!r}')
                print(f'   relatedEntityIds={h.get(\"relatedEntityIds\", [])}')
    elif rid == 4:
        c = res.get('content', [])
        if c and 'text' in c[0]:
            try: payload = json.loads(c[0]['text'])
            except: payload = {'raw': c[0]['text']}
            print(f'[id=4 reflect] notesConsidered={payload.get(\"notesConsidered\")}')
            print(f'   summary[:500]: {payload.get(\"summary\", \"\")[:500]!r}')
"

echo ""
echo "=== stderr tail (15 lines) ==="
tail -15 "$ERR" 2>/dev/null
