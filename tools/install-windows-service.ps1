param(
    [Parameter(Mandatory = $true)]
    [string]$InstallPath,
    [string]$ServiceName = 'InactivePDF',
    [string]$DisplayName = 'InactivePDF Conversion Service'
)

$ErrorActionPreference = 'Stop'
$resolvedInstallPath = (Resolve-Path $InstallPath).Path
$api = Join-Path $resolvedInstallPath 'InactivePDF.Api.exe'
$worker = Join-Path $resolvedInstallPath 'InactivePDF.ConversionWorker.exe'
if (-not (Test-Path $api)) { throw "API executable not found: $api" }
if (-not (Test-Path $worker)) { throw "Conversion worker executable not found: $worker" }

[Environment]::SetEnvironmentVariable('INACTIVEPDF_CONVERSION_WORKER_PATH', $worker, 'Machine')

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    if ($existing.Status -ne 'Stopped') { Stop-Service -Name $ServiceName -Force }
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 1
}

New-Service -Name $ServiceName -DisplayName $DisplayName -Description 'Open-source document to PDF conversion service.' -BinaryPathName $api -StartupType Automatic | Out-Null
$failureActions = 'restart/60000/restart/60000/""/0'
sc.exe failure $ServiceName reset= 86400 actions= $failureActions | Out-Null
Start-Service -Name $ServiceName
Write-Host "Installed and started $ServiceName."
