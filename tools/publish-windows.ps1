param(
    [string]$OutputPath = (Join-Path (Get-Location) 'artifacts\InactivePDF'),
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$apiProject = Join-Path $repoRoot 'src\InactivePDF.Api\InactivePDF.Api.csproj'
$workerProject = Join-Path $repoRoot 'src\InactivePDF.ConversionWorker\InactivePDF.ConversionWorker.csproj'

if (Test-Path $OutputPath) {
    Remove-Item -LiteralPath $OutputPath -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null

dotnet publish $apiProject -c Release -r $Runtime --self-contained true -o $OutputPath
dotnet publish $workerProject -c Release -r $Runtime --self-contained true -o $OutputPath

$worker = Join-Path $OutputPath 'InactivePDF.ConversionWorker.exe'
$api = Join-Path $OutputPath 'InactivePDF.Api.exe'
if (-not (Test-Path $api)) { throw "The API executable was not published: $api" }
if (-not (Test-Path $worker)) { throw "The conversion worker executable was not published: $worker" }

Write-Host "Published API: $api"
Write-Host "Published worker: $worker"
Write-Host "Set INACTIVEPDF_CONVERSION_WORKER_PATH=$worker when installing the service."
