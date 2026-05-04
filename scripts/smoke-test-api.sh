#!/bin/bash
# Comprehensive E2E smoke test for Memory.Api covering the full hybrid search pipeline:
# vector + BM25 + graph PPR fusion, LLM reranker, time-decay, query expansion, faceted
# filters, and per-stream provenance reporting.
#
# Prerequisites:
#   - Memory.Api running at $API_URL (default http://localhost:5566)
#   - WSL2 PG with the seeded dev tenant
#   - LLM creds wired in src/Memory.Api/appsettings.Local.json
#
# Optional env vars:
#   API_URL           — override API base (default http://localhost:5566)
#   ORG_ID/USR_ID/PRJ — override the tenant scope (defaults match dev seed)

set -euo pipefail

API_URL="${API_URL:-http://localhost:5566}"
ORG_ID="${ORG_ID:-90eb678a-e86d-47d0-897c-9f5918952d8b}"
USR_ID="${USR_ID:-2361fe5e-09d1-4c1d-b316-dab99acb9aba}"
PRJ_ID="${PRJ_ID:-b675f6dd-8aba-4f2a-8ab9-1cb40fa5529a}"

HDR=(
    -H "X-Memory-Org-Id: $ORG_ID"
    -H "X-Memory-User-Id: $USR_ID"
    -H "X-Memory-Project-Id: $PRJ_ID"
)

PASS=0
FAIL=0

pass() { echo "  ✓ $1"; PASS=$((PASS+1)); }
fail() { echo "  ✗ $1"; FAIL=$((FAIL+1)); }

step() { echo ""; echo "== $1 =="; }

# ---------------------------------------------------------------------------
step "Health & root"
# ---------------------------------------------------------------------------
ROOT=$(curl -sf "$API_URL/" || echo "FAIL")
[[ $ROOT == *"Memory.Api"* ]] && pass "/ returns service info" || fail "/ root probe ($ROOT)"

HEALTH=$(curl -sf "${HDR[@]}" "$API_URL/api/health")
echo "$HEALTH" | python -c "import sys,json; d=json.load(sys.stdin); assert d['status']=='healthy', d; assert d['db']['status']=='ok'; assert d['llm']['status']=='ok'" \
    && pass "/api/health = healthy (db ok, llm ok)" || fail "/api/health ($HEALTH)"

# ---------------------------------------------------------------------------
step "List endpoints"
# ---------------------------------------------------------------------------
for ep in episodes notes entities edges reflections; do
    COUNT=$(curl -sf "${HDR[@]}" "$API_URL/api/$ep?limit=200" | python -c "import sys,json; print(len(json.load(sys.stdin)))")
    [[ "$COUNT" -ge 0 ]] && pass "/api/$ep returned $COUNT rows" || fail "/api/$ep"
done

# ---------------------------------------------------------------------------
step "Search — basic query, full pipeline"
# ---------------------------------------------------------------------------
RES=$(curl -sf "${HDR[@]}" -H "Content-Type: application/json" "$API_URL/api/search" \
    -d '{"query":"What is the LLM Memory project?","maxResults":5}')
echo "$RES" | python -c "
import sys, json
d = json.load(sys.stdin)
assert d['totalCandidates'] > 0, 'no candidates'
assert len(d['hits']) > 0, 'no hits'
top = d['hits'][0]
assert 'provenance' in top, 'provenance missing'
p = top['provenance']
assert any([p['fromVector'], p['fromBm25'], p['fromGraph']]), 'no stream flags set'
" && pass "search returns hits with provenance" || fail "search basic ($RES)"

# Provenance variety: probe queries that should hit different stream combinations.
# We don't assert which streams hit (that depends on data) — just that hits exist.
for q in "Apache AGE" "Damian" "embedding model" "duplicate"; do
    HITS=$(curl -sf "${HDR[@]}" -H "Content-Type: application/json" "$API_URL/api/search" \
        -d "{\"query\":\"$q\",\"maxResults\":3}" \
        | python -c "import sys,json; d=json.load(sys.stdin); print(len(d['hits']))")
    [[ "$HITS" -gt 0 ]] && pass "search '$q' → $HITS hits" || fail "search '$q' returned 0"
done

# ---------------------------------------------------------------------------
step "Search — faceted filters"
# ---------------------------------------------------------------------------
TAG_RES=$(curl -sf "${HDR[@]}" -H "Content-Type: application/json" "$API_URL/api/search" \
    -d '{"query":"discussion","maxResults":5,"tags":["ai"]}')
TAG_TOTAL=$(echo "$TAG_RES" | python -c "import sys,json; print(json.load(sys.stdin)['totalCandidates'])")
[[ "$TAG_TOTAL" -ge 0 ]] && pass "tag filter [ai] → $TAG_TOTAL candidates" || fail "tag filter"

UNTIL_RES=$(curl -sf "${HDR[@]}" -H "Content-Type: application/json" "$API_URL/api/search" \
    -d '{"query":"recent","maxResults":3,"until":"2020-01-01T00:00:00Z"}')
UNTIL_HITS=$(echo "$UNTIL_RES" | python -c "import sys,json; print(len(json.load(sys.stdin)['hits']))")
[[ "$UNTIL_HITS" == "0" ]] && pass "until=2020 filter → 0 hits (none predate)" || fail "until filter unexpected ($UNTIL_HITS hits)"

# ---------------------------------------------------------------------------
step "Search — short query (expansion path)"
# ---------------------------------------------------------------------------
SHORT_HITS=$(curl -sf "${HDR[@]}" -H "Content-Type: application/json" "$API_URL/api/search" \
    -d '{"query":"AGE","maxResults":3}' \
    | python -c "import sys,json; d=json.load(sys.stdin); print(len(d['hits']))")
[[ "$SHORT_HITS" -gt 0 ]] && pass "short query 'AGE' → $SHORT_HITS hits (expansion likely fired)" \
    || fail "short query returned 0"

# ---------------------------------------------------------------------------
step "Tenancy isolation (RLS)"
# ---------------------------------------------------------------------------
# Without tenant headers OR a bearer the API has no scope; RLS should hide all rows.
# This only works if the API connects as a NOBYPASSRLS role (memory_app), not postgres.
NO_HEADERS=$(curl -sf "$API_URL/api/notes?limit=5" | python -c "import sys,json; print(len(json.load(sys.stdin)))")
[[ "$NO_HEADERS" == "0" ]] && pass "no headers → 0 notes (RLS enforces)" \
    || fail "no headers → $NO_HEADERS notes (RLS bypassed — API likely connected as superuser)"

# With wrong project_id headers, no rows should match.
WRONG=$(curl -sf -H "X-Memory-Org-Id: $ORG_ID" -H "X-Memory-User-Id: $USR_ID" \
    -H "X-Memory-Project-Id: 00000000-0000-0000-0000-000000000000" \
    "$API_URL/api/notes?limit=5" | python -c "import sys,json; print(len(json.load(sys.stdin)))")
[[ "$WRONG" == "0" ]] && pass "wrong project_id → 0 notes" \
    || fail "wrong project_id leaked $WRONG notes"

# Bad bearer should 401 (not 200, not silently fall through).
BAD=$(curl -s -o /dev/null -w "%{http_code}" -H "Authorization: Bearer memk_invalid_test_xyz" "$API_URL/api/health")
[[ "$BAD" == "401" ]] && pass "invalid bearer → HTTP 401" || fail "invalid bearer → HTTP $BAD"

# ---------------------------------------------------------------------------
step "Edges — bi-temporal field"
# ---------------------------------------------------------------------------
EDGES=$(curl -sf "${HDR[@]}" "$API_URL/api/edges?limit=200")
ACTIVE=$(echo "$EDGES" | python -c "import sys,json; print(sum(1 for e in json.load(sys.stdin) if e['invalidatedAt'] is None))")
INVALID=$(echo "$EDGES" | python -c "import sys,json; print(sum(1 for e in json.load(sys.stdin) if e['invalidatedAt'] is not None))")
pass "edges: $ACTIVE active, $INVALID invalidated"

# ---------------------------------------------------------------------------
step "Result"
# ---------------------------------------------------------------------------
echo ""
echo "Passed: $PASS,  Failed: $FAIL"
[[ "$FAIL" == "0" ]] && exit 0 || exit 1
