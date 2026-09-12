#!/usr/bin/env bash
set -euo pipefail

root="${1:-${INACTIVEPDF_BASELINE_ROOT:-$(pwd)/WatchFolders/Default}}"
manifest="${2:-$root/Baseline/baseline-manifest.json}"
mkdir -p "$(dirname "$manifest")"

python3 - "$root" "$manifest" <<'PY'
import hashlib
import json
import sys
from datetime import datetime, timezone
from pathlib import Path

root = Path(sys.argv[1]).resolve()
manifest = Path(sys.argv[2]).resolve()
source_dirs = [root / "TestFixtures", root / "HeavyFixtures"]
references = root / "References"
entries = []

for directory in source_dirs:
    if not directory.is_dir():
        continue
    for path in sorted(directory.iterdir()):
        if not path.is_file() or path.name.startswith('.'):
            continue
        digest = hashlib.sha256()
        with path.open('rb') as stream:
            for block in iter(lambda: stream.read(1024 * 1024), b''):
                digest.update(block)
        reference = references / f"{path.stem}.pdf"
        entries.append({
            "name": path.name,
            "relativePath": str(path.relative_to(root)),
            "extension": path.suffix.lower().lstrip('.'),
            "sizeBytes": path.stat().st_size,
            "sha256": digest.hexdigest(),
            "referenceOutput": str(reference.relative_to(root)),
            "referencePresent": reference.is_file(),
        })

document = {
    "schemaVersion": 1,
    "createdUtc": datetime.now(timezone.utc).isoformat(),
    "root": str(root),
    "entries": entries,
}
manifest.write_text(json.dumps(document, indent=2) + "\n", encoding='utf-8')
print(f"Wrote {len(entries)} baseline entries to {manifest}")
PY
