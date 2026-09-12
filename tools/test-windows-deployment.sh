#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
required=(
  tools/publish-windows.ps1
  tools/install-windows-service.ps1
  tools/setup-windows.ps1
  tools/verify-windows-install.ps1
  tools/uninstall-windows-service.ps1
  README.md
)
for file in "${required[@]}"; do
  [[ -f "$repo_root/$file" ]] || { echo "missing deployment artifact: $file" >&2; exit 1; }
done

rg -q "win-x64" "$repo_root/tools/publish-windows.ps1"
rg -q "LibreOffice" "$repo_root/tools/setup-windows.ps1"
rg -q "INACTIVEPDF_LIBREOFFICE_PATH" "$repo_root/tools/setup-windows.ps1"
rg -q "INACTIVEPDF_CONVERSION_WORKER_PATH" "$repo_root/tools/setup-windows.ps1"
rg -q "/ready" "$repo_root/tools/verify-windows-install.ps1"
rg -q "WatchFolders|Input|Originals|Output" "$repo_root/tools/verify-windows-install.ps1"
rg -q "RemoveData" "$repo_root/tools/uninstall-windows-service.ps1"
rg -q "setup-windows.ps1|verify-windows-install.ps1" "$repo_root/README.md"
echo "Windows deployment contract passed."
