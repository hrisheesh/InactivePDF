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
$requiredAssets = @(
    'InactivePDF.settings.json',
    'wwwroot\index.html',
    'wwwroot\console.js',
    'wwwroot\console.css',
    'wwwroot\openapi.json',
    'wwwroot\api-reference.html'
)
foreach ($asset in $requiredAssets) {
    $assetPath = Join-Path $OutputPath $asset
    if (-not (Test-Path -LiteralPath $assetPath)) { throw "The published deployment is missing $assetPath" }
}

$requiredRuntimeFiles = @(
    'InactivePDF.Api.deps.json',
    'InactivePDF.Api.runtimeconfig.json',
    'InactivePDF.ConversionWorker.deps.json',
    'InactivePDF.ConversionWorker.runtimeconfig.json'
)
foreach ($runtimeFile in $requiredRuntimeFiles) {
    $runtimePath = Join-Path $OutputPath $runtimeFile
    if (-not (Test-Path -LiteralPath $runtimePath)) { throw "The published deployment is missing $runtimePath" }
}

Write-Host "Published API: $api"
Write-Host "Published worker: $worker"
Write-Host "LibreOffice remains an external dependency. Run .\tools\setup-windows.ps1 -InstallPath '$OutputPath' after installing LibreOffice."
