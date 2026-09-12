#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
configuration="${INACTIVEPDF_VERIFY_CONFIGURATION:-Release}"
runtime="${INACTIVEPDF_VERIFY_RUNTIME:-win-x64}"
base_url="${INACTIVEPDF_RELEASE_BASE_URL:-http://127.0.0.1:5080/}"
temp_root="$(mktemp -d "${TMPDIR:-/tmp}/inactivepdf-phase10.XXXXXX")"
service_pid=""

cleanup() {
  if [[ -n "$service_pid" ]] && kill -0 "$service_pid" 2>/dev/null; then
    kill "$service_pid" 2>/dev/null || true
    wait "$service_pid" 2>/dev/null || true
  fi
  rm -rf "$temp_root"
}
trap cleanup EXIT

cd "$repo_root"
dotnet restore InactivePDF.slnx
dotnet build InactivePDF.slnx --configuration "$configuration" --no-restore
dotnet test tests/InactivePDF.UnitTests/InactivePDF.UnitTests.csproj --configuration "$configuration" --no-restore

fixture_root="$temp_root/fixtures"
dotnet run --project tools/InactivePDF.Fixtures/InactivePDF.Fixtures.csproj --configuration "$configuration" --no-restore -- "$fixture_root"

publish_root="$temp_root/publish"
dotnet restore InactivePDF.slnx --runtime "$runtime"
dotnet publish src/InactivePDF.Api/InactivePDF.Api.csproj --configuration "$configuration" --runtime "$runtime" --self-contained true --output "$publish_root" --no-restore
dotnet publish src/InactivePDF.ConversionWorker/InactivePDF.ConversionWorker.csproj --configuration "$configuration" --runtime "$runtime" --self-contained true --output "$publish_root" --no-restore
for required in InactivePDF.Api.exe InactivePDF.ConversionWorker.exe InactivePDF.settings.json wwwroot/index.html wwwroot/console.js wwwroot/console.css wwwroot/openapi.json wwwroot/api-reference.html; do
  [[ -f "$publish_root/$required" ]] || { echo "Published artifact is missing $required" >&2; exit 1; }
done

port="${base_url##*:}"
port="${port%%/*}"
if [[ "$port" == "$base_url" || -z "$port" ]]; then port=5080; fi
data_root="$temp_root/data"
watch_root="$temp_root/watch"
mkdir -p "$data_root" "$watch_root"

env INACTIVEPDF_DATA_PATH="$data_root" \
    INACTIVEPDF_WATCH_ROOT="$watch_root" \
    INACTIVEPDF_LIBREOFFICE_PATH="${INACTIVEPDF_LIBREOFFICE_PATH:-soffice}" \
    dotnet run --project src/InactivePDF.Api/InactivePDF.Api.csproj --configuration "$configuration" --no-restore --urls "http://127.0.0.1:$port" >"$temp_root/service.log" 2>&1 &
service_pid=$!

for _ in $(seq 1 60); do
  if curl -fsS "http://127.0.0.1:$port/ready" >/dev/null 2>&1; then break; fi
  sleep 1
done
curl -fsS "http://127.0.0.1:$port/ready" >/dev/null

dotnet run --project tools/InactivePDF.ReleaseCheck/InactivePDF.ReleaseCheck.csproj --configuration "$configuration" --no-restore -- \
  --base-url "http://127.0.0.1:$port/" \
  --fixture-root "$fixture_root/TestFixtures" \
  --watch-root "$watch_root"

echo "Phase 10 verification passed."
