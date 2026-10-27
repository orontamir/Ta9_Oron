#!/usr/bin/env bash
# End-to-end smoke test against a running stack (docker compose up --build). Requires curl and jq.
#   ./scripts/smoke-test.sh
set -euo pipefail

JOBS_URL="${JOBS_URL:-http://localhost:5001}"
AUDIT_URL="${AUDIT_URL:-http://localhost:5003}"
# Operator key: $TA9_OPERATOR_KEY, else the key in .env (scripts/keys.sh).
ENV_FILE="$(cd "$(dirname "$0")/.." && pwd)/.env"
API_KEY="${TA9_OPERATOR_KEY:-$( [ -f "$ENV_FILE" ] && grep -E '^TA9_OPERATOR_KEY=' "$ENV_FILE" | cut -d= -f2- )}"
[ -n "$API_KEY" ] || { echo "No API key: run ./scripts/keys.sh first" >&2; exit 1; }

job_id=$(curl -fsS -H "X-Api-Key: $API_KEY" -X POST "$JOBS_URL/api/jobs" -H 'Content-Type: application/json' -d '{
  "name": "smoke",
  "documents": [
    { "name": "english.txt", "content": "The quick brown fox jumps over the lazy dog. The dog sleeps." },
    { "name": "hebrew.txt",  "content": "שלום עולם. שלום לכולם, שלום!" },
    { "name": "empty.txt",   "content": "" }
  ]
}' | jq -r .id)
echo "Created job $job_id"

for _ in $(seq 1 120); do
  status=$(curl -fsS -H "X-Api-Key: $API_KEY" "$JOBS_URL/api/jobs/$job_id" | jq -r .status)
  echo "  status: $status"
  [[ "$status" =~ ^(Completed|Failed|Cancelled)$ ]] && break
  sleep 0.5
done

echo "Results:"
curl -fsS -H "X-Api-Key: $API_KEY" "$JOBS_URL/api/jobs/$job_id/results" | jq '.items[] | {index, name, words: .result.wordCount, unique: .result.uniqueWordCount, top: .result.topWords}'

sleep 6 # audit publisher interval
echo "Audit:"
curl -fsS -H "X-Api-Key: $API_KEY" "$AUDIT_URL/api/audit/$job_id" | jq .
