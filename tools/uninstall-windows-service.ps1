[CmdletBinding()]
param(
    [string]$ServiceName = 'InactivePDF',
    [string]$DataRoot = 'C:\ProgramData\InactivePDF',
    [string]$WatchRoot = 'C:\InactivePDF\WatchFolders\Default',
    [switch]$RemoveData
)

$ErrorActionPreference = 'Stop'

function Assert-SafeRemovalPath([string]$Path, [string]$Description) {
    $resolved = [System.IO.Path]::GetFullPath($Path)
    $root = [System.IO.Path]::GetPathRoot($resolved)
    if ([string]::IsNullOrWhiteSpace($resolved) -or $resolved -eq $root -or $resolved.Length -lt 8) { throw "Refusing to remove unsafe $Description path: $resolved" }
    if (Test-Path -LiteralPath $resolved) {
        $item = Get-Item -LiteralPath $resolved -Force
        if (-not $item.PSIsContainer -or $item.Attributes.HasFlag([System.IO.FileAttributes]::ReparsePoint)) { throw "Refusing to remove unsafe $Description path: $resolved" }
    }
    return $resolved
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') { Stop-Service -Name $ServiceName -Force }
    sc.exe delete $ServiceName | Out-Null
    Write-Host "Removed Windows service: $ServiceName"
} else {
    Write-Host "Windows service was not installed: $ServiceName"
}

foreach ($name in @('INACTIVEPDF_SETTINGS_PATH', 'INACTIVEPDF_DATA_PATH', 'INACTIVEPDF_STATE_PATH', 'INACTIVEPDF_JOBS_PATH', 'INACTIVEPDF_WATCH_ROOT', 'INACTIVEPDF_LIBREOFFICE_PATH', 'INACTIVEPDF_CONVERSION_WORKER_PATH', 'ASPNETCORE_URLS')) {
    [Environment]::SetEnvironmentVariable($name, $null, 'Machine')
}

if ($RemoveData) {
    $safeDataRoot = Assert-SafeRemovalPath $DataRoot 'data root'
    $safeWatchRoot = Assert-SafeRemovalPath $WatchRoot 'watch root'
    Remove-Item -LiteralPath $safeDataRoot -Recurse -Force
    Remove-Item -LiteralPath $safeWatchRoot -Recurse -Force
    Write-Host "Removed data root: $safeDataRoot"
    Write-Host "Removed watch root: $safeWatchRoot"
} else {
    Write-Host 'Conversion data and watch-folder files were preserved. Use -RemoveData only when permanent deletion is intended.'
}
