# Memory evals

Use evals to compare retrieval changes before trusting them. Keep the generated
query file stable; compare profiles against the same file.

## Golden set loop

```bash
export MEMORY_API_URL=http://127.0.0.1:5001
export MEMORY_API_KEY=memk_...

memory eval gen-queries --count 50 --out eval-queries.json
memory eval run --in eval-queries.json --top-k 10 --out baseline.json
memory eval run --in eval-queries.json --top-k 10 --mode graph_rag --out graph.json
memory eval run --in eval-queries.json --top-k 10 --no-reranker --out no-reranker.json
memory eval run --in eval-queries.json --top-k 10 --no-graph --out no-graph.json
memory eval sweep --in eval-queries.json --top-k 10 --out-dir eval-results
memory eval gate --summary eval-results/summary.json --profile memory-light
```

Metrics:

- `Recall@1/3/5/K`: whether the gold note appears in the top K.
- `MRR`: rewards higher rank for the gold note.
- `notFound`: gold note absent from top K.
- `abstained`: retrieval explicitly refused weak results.
- `meanLatencyMs`, `p50LatencyMs`, `p95LatencyMs`: cost of the profile.

`sweep` runs the same stable query file against built-in profiles and writes:

- `<profile>.json`: full `/api/eval/run` response for each profile,
- `summary.json`: machine-readable aggregate table,
- `summary.md`: human-readable comparison table.

Default sweep profiles:

- `baseline`
- `memory-light`
- `memory-medium`
- `heavy-rag`
- `graph-rag`
- `no-reranker`
- `no-graph`

Optional profiles can be selected with:

```bash
memory eval sweep --profiles baseline,bm25-only,vector-only,no-reranker
```

Use `gate` in CI or DevHub deploy/test flows to reject a retrieval change that
misses minimum quality or latency:

```bash
memory eval gate \
  --summary eval-results/summary.json \
  --profile memory-light \
  --min-recall-at-3 0.95 \
  --min-mrr 0.90 \
  --max-p95-ms 1000 \
  --max-abstained 0 \
  --max-not-found 0
```

The command exits with code `2` when a gate fails. Use `off` to disable an
individual threshold, for example `--max-p95-ms off`.

Golden query items may include a typed-memory hint:

```json
{
  "noteId": "9fb65e16-b35f-4948-9bc7-0ae2a48f4a5c",
  "query": "Jakiego pliku lokalnej konfiguracji nie wolno publikować do release Memory.Api?",
  "memoryTypes": ["Procedural"]
}
```

`/api/eval/run` passes `memoryTypes` through to `/api/search`, so typed
golden sets can measure preference/procedure/decision lookup separately.

## What to measure

Start with 50-100 mixed Polish/English/code queries. Include:

- user preferences and corrections,
- project decisions,
- debug findings,
- UI test findings,
- coding patterns,
- relational questions where graph should help,
- vague follow-ups rewritten through smart-caller routing.

Compare at least:

- baseline smart route,
- graph enabled vs disabled,
- reranker enabled vs disabled,
- local reranker vs cloud mini reranker,
- `memory_light`, `memory_medium`, `heavy_rag`, `graph_rag`.

The production gate for shared-agent use should include retrieval quality plus
tenant isolation:

- Recall@3 does not regress materially from baseline.
- P95 latency is acceptable for DevHub agent startup and pre-edit searches.
- Cross-tenant leakage remains zero in live RLS smoke tests.
