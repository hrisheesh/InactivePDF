#!/usr/bin/env bash
set -euo pipefail

BASE_URL="${INACTIVEPDF_URL:-http://127.0.0.1:5080}"
: "${INACTIVEPDF_TOKEN:?Set INACTIVEPDF_TOKEN to an administrator token or scoped integration key}"

curl --fail-with-body "$BASE_URL/health"
curl --fail-with-body -X POST "$BASE_URL/v1/create-text-pdf" \
  -H "Authorization: Bearer $INACTIVEPDF_TOKEN" \
  -H 'Content-Type: application/json' \
  --data '{"text":"Hello from InactivePDF","profile":"archive"}' \
  --output converted.pdf

echo "Wrote converted.pdf"
