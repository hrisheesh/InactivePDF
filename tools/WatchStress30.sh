#!/usr/bin/env bash
set -u

if ! command -v python3 >/dev/null 2>&1; then
    echo 'WatchStress30.sh requires Python 3 for report generation. The service itself does not require Python.' >&2
    exit 1
fi

repo_root="$(cd "$(dirname "$0")/.." && pwd)"
watch_root="${INACTIVEPDF_STRESS_WATCH_ROOT:-$repo_root/WatchFolders/Default}"
input_dir="$watch_root/Input"
processing_dir="$watch_root/Processing"
output_dir="$watch_root/Output"
originals_dir="$watch_root/Originals"
errors_dir="$watch_root/Errors"
logs_dir="$watch_root/Logs"
duration_seconds="${INACTIVEPDF_STRESS_DURATION_SECONDS:-1800}"
stamp="$(date -u +%Y%m%dT%H%M%SZ)"
run_dir="$watch_root/Stress30-$stamp"
controller_log="$run_dir/controller.jsonl"

mkdir -p "$run_dir"

log_controller() {
    local event="$1"
    local cycle="${2:-0}"
    local count="${3:-0}"
    printf '{"utc":"%s","event":"%s","cycle":%s,"count":%s}\n' \
        "$(date -u +%Y-%m-%dT%H:%M:%S.%3NZ)" "$event" "$cycle" "$count" >> "$controller_log"
}

cleanup_stress_files() {
    local directory
    for directory in "$input_dir" "$processing_dir" "$output_dir" "$originals_dir" "$errors_dir"; do
        find "$directory" -maxdepth 1 -type f -name 'stress30*' -delete 2>/dev/null || true
    done
}

wait_for_drain() {
    local deadline=$(( $(date +%s) + 1800 ))
    while true; do
        local input_count processing_count
        input_count=$(find "$input_dir" -maxdepth 1 -type f -name 'stress30*' | wc -l | tr -d ' ')
        processing_count=$(find "$processing_dir" -maxdepth 1 -type f ! -name '.DS_Store' | wc -l | tr -d ' ')
        if [ "$input_count" -eq 0 ] && [ "$processing_count" -eq 0 ]; then return 0; fi
        if [ "$(date +%s)" -ge "$deadline" ]; then return 1; fi
        sleep 2
    done
}

copy_fixture_set() {
    local source_dir="$1"
    local category="$2"
    local cycle="$3"
    local index=0
    local source extension target
    while IFS= read -r source; do
        [ -n "$source" ] || continue
        index=$((index + 1))
        extension=".${source##*.}"
        target="$input_dir/stress30c$(printf '%04d' "$cycle")${category}$(printf '%04d' "$index")$extension"
        cp "$source" "$target"
    done < <(find "$source_dir" -maxdepth 1 -type f ! -name '.DS_Store' -print | sort)
    printf '%s\n' "$index"
}

start_epoch="$(date +%s)"
deadline=$((start_epoch + duration_seconds))
cleanup_stress_files
log_controller run_started 0 0

cycle=0
while [ "$(date +%s)" -lt "$deadline" ]; do
    cycle=$((cycle + 1))
    light_count=$(copy_fixture_set "$watch_root/TestFixtures" light "$cycle")
    heavy_count=$(copy_fixture_set "$watch_root/HeavyFixtures" heavy "$cycle")
    total_count=$((light_count + heavy_count))
    log_controller cycle_enqueued "$cycle" "$total_count"
    if wait_for_drain; then
        log_controller cycle_drained "$cycle" "$total_count"
    else
        log_controller cycle_drain_timeout "$cycle" "$total_count"
        break
    fi
    cleanup_stress_files
done

end_epoch="$(date +%s)"
log_controller run_stopped "$cycle" 0

log_file="$logs_dir/InactivePDF-$(date -u +%Y%m%d).jsonl"
report_json="$run_dir/report.json"
report_text="$run_dir/report.log"

python3 - "$controller_log" "$log_file" "$report_json" "$report_text" "$start_epoch" "$end_epoch" "$cycle" <<'PY'
import json
import sys
from collections import defaultdict
from datetime import datetime, timezone
from pathlib import Path

controller_path, service_log_path, report_json_path, report_text_path, start_epoch, end_epoch, cycles = sys.argv[1:]
final_events = []
retry_count = 0

if Path(service_log_path).exists():
    for line in Path(service_log_path).open():
        try:
            event = json.loads(line)
        except json.JSONDecodeError:
            continue
        path = event.get("path", "")
        if not path.startswith("stress30"):
            continue
        if event.get("eventName") in ("converted", "error"):
            final_events.append(event)
        elif event.get("eventName") == "retry":
            retry_count += 1

def category(path):
    return "heavy" if "heavy" in path.lower() else "light"

def fmt(path):
    return Path(path).suffix.lower().lstrip(".").upper()

def summarize(events):
    durations = [int(e.get("durationMs", 0)) for e in events]
    successes = sum(1 for e in events if e.get("eventName") == "converted" and e.get("success") is True)
    failures = len(events) - successes
    physical = [int(e.get("resource", {}).get("PhysicalFootprintBytes", 0)) for e in events]
    workers = [int(e.get("resource", {}).get("ConversionWorkerPeakPhysicalFootprintBytes", 0)) for e in events]
    return {
        "processed": len(events),
        "succeeded": successes,
        "failed": failures,
        "successRatePercent": round(successes * 100 / len(events), 2) if events else 0,
        "averageDurationMs": round(sum(durations) / len(durations), 1) if durations else 0,
        "minDurationMs": min(durations) if durations else 0,
        "maxDurationMs": max(durations) if durations else 0,
        "totalConversionMs": sum(durations),
        "maxParentPhysicalFootprintBytes": max(physical, default=0),
        "maxWorkerPhysicalFootprintBytes": max(workers, default=0),
    }

by_category = defaultdict(list)
by_format = defaultdict(list)
errors = defaultdict(int)
for event in final_events:
    by_category[category(event["path"])].append(event)
    by_format[(category(event["path"]), fmt(event["path"]))].append(event)
    if event.get("eventName") == "error":
        errors[event.get("errorType") or "UnknownError"] += 1

report = {
    "startedUtc": datetime.fromtimestamp(int(start_epoch), timezone.utc).isoformat(),
    "stoppedUtc": datetime.fromtimestamp(int(end_epoch), timezone.utc).isoformat(),
    "durationSeconds": int(end_epoch) - int(start_epoch),
    "cyclesCompleted": int(cycles),
    "serviceLog": service_log_path,
    "controllerLog": controller_path,
    "retryCount": retry_count,
    "overall": summarize(final_events),
    "byCategory": {key: summarize(value) for key, value in sorted(by_category.items())},
    "byFormat": {f"{key[0]}:{key[1]}": summarize(value) for key, value in sorted(by_format.items())},
    "errorReasons": dict(sorted(errors.items())),
}

Path(report_json_path).write_text(json.dumps(report, indent=2) + "\n")
lines = [
    f"Stress duration seconds {report['durationSeconds']}",
    f"Cycles completed {report['cyclesCompleted']}",
    f"Processed {report['overall']['processed']}",
    f"Succeeded {report['overall']['succeeded']}",
    f"Failed {report['overall']['failed']}",
    f"Retries {report['retryCount']}",
    "",
    "Category",
]
for key, value in report["byCategory"].items():
    lines.append(f"{key} processed={value['processed']} succeeded={value['succeeded']} failed={value['failed']} averageMs={value['averageDurationMs']} totalMs={value['totalConversionMs']}")
lines += ["", "Format"]
for key, value in report["byFormat"].items():
    lines.append(f"{key} processed={value['processed']} succeeded={value['succeeded']} failed={value['failed']} averageMs={value['averageDurationMs']} minMs={value['minDurationMs']} maxMs={value['maxDurationMs']} totalMs={value['totalConversionMs']}")
lines += ["", "Errors"]
for key, value in report["errorReasons"].items():
    lines.append(f"{key} count={value}")
Path(report_text_path).write_text("\n".join(lines) + "\n")
print(json.dumps(report, indent=2))
PY

printf 'Stress report: %s\n' "$report_text"
