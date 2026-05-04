import json, sys
path = sys.argv[1]
with open(path, encoding='utf-8') as f:
    rows = [json.loads(l) for l in f if l.strip().startswith('{')]
for r in rows:
    rid = r.get('id')
    if rid not in (2, 3):
        continue
    txt = r.get('result', {}).get('content', [{}])[0].get('text', '{}')
    payload = json.loads(txt)
    labels = {2: 'AIObrońca legal automation', 3: 'co buduje Damian'}
    print(f"[query={labels.get(rid)}] total={payload.get('totalCandidates')}")
    for h in payload.get('hits', []):
        score = h.get('score')
        content = h.get('content', '')[:100]
        print(f"  rrf={score:.5f}  {content!r}")
