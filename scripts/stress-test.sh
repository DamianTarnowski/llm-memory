#!/bin/bash
# Stress + edge-case test for Memory.Mcp.Stdio.
# Exercises: Polish UTF-8, single-quote injection in Cypher, long content,
# find_related_notes tool, search with tags, reflect on a custom scope.

set -e

REPO="$(cd "$(dirname "$0")/.." && pwd)"
DLL_WIN="$(cygpath -w "$REPO/src/Memory.Mcp.Stdio/bin/Debug/net10.0/Memory.Mcp.Stdio.dll" 2>/dev/null || echo "$REPO/src/Memory.Mcp.Stdio/bin/Debug/net10.0/Memory.Mcp.Stdio.dll")"
OUT="$REPO/scripts/.stress-output.jsonl"
ERR="$REPO/scripts/.stress-stderr.log"
OUT_WIN="$(cygpath -w "$OUT" 2>/dev/null || echo "$OUT")"
rm -f "$OUT" "$ERR"

POLISH='Damian Tarnowski jest programistą z Polski. Pracuje nad projektem AIObrońca — systemem AI do automatyzacji obsługi prawnej. Używa również LLM Memory do trzymania kontekstu.'
TRICKY="The note's content has tricky chars: it's got 'single quotes', \"double quotes\", \\backslashes\\, and even \$dollar signs\$. Should not break Cypher."
LONG_CONTENT=$(python -c "print('A long-form research note about temporal knowledge graphs and their applications in agent memory systems. ' * 30)")

python -c "
import json
content_polish = '''$POLISH'''
content_tricky = '''$TRICKY'''
content_long   = '''$LONG_CONTENT'''
msgs = [
    {'jsonrpc':'2.0','id':1,'method':'initialize','params':{'protocolVersion':'2024-11-05','capabilities':{},'clientInfo':{'name':'stress','version':'1'}}},
    {'jsonrpc':'2.0','method':'notifications/initialized'},
    {'jsonrpc':'2.0','id':10,'method':'tools/call','params':{'name':'save_episode','arguments':{'source':'pl-test','content':content_polish}}},
    {'jsonrpc':'2.0','id':11,'method':'tools/call','params':{'name':'save_episode','arguments':{'source':'special-chars','content':content_tricky}}},
    {'jsonrpc':'2.0','id':12,'method':'tools/call','params':{'name':'save_episode','arguments':{'source':'long-form','content':content_long}}},
    {'jsonrpc':'2.0','id':20,'method':'tools/call','params':{'name':'search_memory','arguments':{'query':'kto pracuje nad sztuczną inteligencją','maxResults':3}}},
    {'jsonrpc':'2.0','id':21,'method':'tools/call','params':{'name':'search_memory','arguments':{'query':'temporal knowledge graphs','maxResults':3}}},
    {'jsonrpc':'2.0','id':22,'method':'tools/call','params':{'name':'search_memory','arguments':{'query':'tricky','maxResults':3}}},
    {'jsonrpc':'2.0','id':30,'method':'tools/call','params':{'name':'get_entity','arguments':{'name':'damian'}}},
    {'jsonrpc':'2.0','id':31,'method':'tools/call','params':{'name':'get_entity','arguments':{'name':'nonexistent-entity-xyz'}}},
    {'jsonrpc':'2.0','id':40,'method':'tools/call','params':{'name':'reflect','arguments':{'scope':'edge-case-tests','maxNotes':10}}},
]
for m in msgs:
    print(json.dumps(m, separators=(',',':')))
" > "$REPO/scripts/.stress-msgs.jsonl"

echo "=== feeding $(wc -l < "$REPO/scripts/.stress-msgs.jsonl") messages ==="
{
    head -2 "$REPO/scripts/.stress-msgs.jsonl"
    sleep 1
    sed -n '3p' "$REPO/scripts/.stress-msgs.jsonl"
    sleep 30
    sed -n '4p' "$REPO/scripts/.stress-msgs.jsonl"
    sleep 30
    sed -n '5p' "$REPO/scripts/.stress-msgs.jsonl"
    sleep 35
    sed -n '6,8p' "$REPO/scripts/.stress-msgs.jsonl"
    sleep 12
    sed -n '9,10p' "$REPO/scripts/.stress-msgs.jsonl"
    sleep 6
    sed -n '11p' "$REPO/scripts/.stress-msgs.jsonl"
    sleep 30
} | timeout 250 dotnet "$DLL_WIN" 2>"$ERR" | tee "$OUT" >/dev/null

echo ""
echo "=== results ==="
python -c "
import json
with open(r'$OUT_WIN', 'r', encoding='utf-8') as f:
    rows = [json.loads(l) for l in f if l.strip().startswith('{')]
for r in rows:
    rid = r.get('id')
    if 'error' in r:
        print(f'[id={rid}] ERROR: {json.dumps(r[chr(34)+\"error\"+chr(34).strip(chr(34))])[:300]}')
        continue
    res = r.get('result', {})
    c = res.get('content', [])
    if not c or 'text' not in c[0]:
        if rid == 1: print(f'[id=1] init OK')
        continue
    txt = c[0]['text']
    try: payload = json.loads(txt)
    except: payload = txt
    if rid == 10:
        print(f'[id=10 save Polish] note_count={len(payload.get(chr(34)+\"NoteIds\"+chr(34).strip(chr(34)),[]))} entities={len(payload.get(chr(34)+\"EntityIds\"+chr(34).strip(chr(34)),[]))}')
    elif rid == 11:
        print(f'[id=11 save tricky] note_count={len(payload.get(chr(34)+\"NoteIds\"+chr(34).strip(chr(34)),[]))} entities={len(payload.get(chr(34)+\"EntityIds\"+chr(34).strip(chr(34)),[]))}')
    elif rid == 12:
        print(f'[id=12 save long] note_count={len(payload.get(chr(34)+\"NoteIds\"+chr(34).strip(chr(34)),[]))} entities={len(payload.get(chr(34)+\"EntityIds\"+chr(34).strip(chr(34)),[]))}')
    elif rid in (20, 21, 22):
        hits = payload.get('hits', [])
        labels = {20: 'PL query', 21: 'EN tech', 22: 'tricky'}
        print(f'[id={rid} search {labels[rid]}] hits={len(hits)}')
        for h in hits[:2]:
            print(f'   score={h[chr(34)+\"score\"+chr(34).strip(chr(34))]:.3f} content={h[chr(34)+\"content\"+chr(34).strip(chr(34))][:80]!r}')
    elif rid == 30:
        ent = payload.get('entity')
        if ent:
            print(f'[id=30 get_entity damian] FOUND name={ent.get(chr(34)+\"name\"+chr(34).strip(chr(34)))} kind={ent.get(chr(34)+\"kind\"+chr(34).strip(chr(34)))} out_edges={len(payload.get(chr(34)+\"outgoingEdges\"+chr(34).strip(chr(34)),[]))} in_edges={len(payload.get(chr(34)+\"incomingEdges\"+chr(34).strip(chr(34)),[]))}')
        else:
            print(f'[id=30 get_entity damian] NOT_FOUND')
    elif rid == 31:
        ent = payload.get('entity')
        print(f'[id=31 get_entity nonexistent] entity_is_null={ent is None}')
    elif rid == 40:
        print(f'[id=40 reflect edge-case] notes={payload.get(chr(34)+\"NotesConsidered\"+chr(34).strip(chr(34)))}')
        print(f'   summary[:300]: {payload.get(chr(34)+\"Summary\"+chr(34).strip(chr(34)),\"\")[:300]!r}')
"

echo ""
echo "=== errors in stderr ==="
grep -E "fail|error|Exception" "$ERR" | grep -v "info:" | head -10 || echo "(none)"
