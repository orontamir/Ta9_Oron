#!/usr/bin/env bash
# End-to-end smoke test against a running stack (docker compose up --build). Requires curl and jq.
#   ./scripts/smoke-test.sh
set -euo pipefail

JOBS_URL="${JOBS_URL:-http://localhost:5001}"
AUDIT_URL="${AUDIT_URL:-http://localhost:5003}"

job_id=$(curl -fsS -X POST "$JOBS_URL/api/jobs" -H 'Content-Type: application/json' -d '{
  "name": "smoke",
  "documents": [
    { "name": "english.txt", "content": "The quick brown fox jumps over the lazy dog. The dog sleeps." },
    { "name": "hebrew.txt",  "content": "שלום עולם. שלום לכולם, שלום!" },
    { "name": "empty.txt",   "content": "" }
  ]
}' | jq -r .id)
echo "Created job $job_id"

for _ in $(seq 1 120); do
  status=$(curl -fsS "$JOBS_URL/api/jobs/$job_id" | jq -r .status)
  echo "  status: $status"
  [[ "$status" =~ ^(Completed|Failed|Cancelled)$ ]] && break
  sleep 0.5
done

echo "Results:"
curl -fsS "$JOBS_URL/api/jobs/$job_id/results" | jq '.items[] | {index, name, words: .result.wordCount, unique: .result.uniqueWordCount, top: .result.topWords}'

sleep 6 # audit publisher interval
echo "Audit:"
curl -fsS "$AUDIT_URL/api/audit/$job_id" | jq .
