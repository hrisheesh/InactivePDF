param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$BaseUrl = 'http://127.0.0.1:5080/',
    [switch]$SkipLiveService
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('inactivepdf-phase10-' + [Guid]::NewGuid().ToString('N'))
$publishRoot = Join-Path $tempRoot 'publish'
$fixtureRoot = Join-Path $tempRoot 'fixtures'
$dataRoot = Join-Path $tempRoot 'data'
$watchRoot = Join-Path $tempRoot 'watch'
$service = $null

try {
    New-Item -ItemType Directory -Path $tempRoot, $publishRoot, $fixtureRoot, $dataRoot, $watchRoot -Force | Out-Null
    Set-Location $repoRoot
    dotnet restore .\InactivePDF.slnx
    dotnet build .\InactivePDF.slnx --configuration $Configuration --no-restore
    dotnet test .\tests\InactivePDF.UnitTests\InactivePDF.UnitTests.csproj --configuration $Configuration --no-restore
    dotnet run --project .\tools\InactivePDF.Fixtures\InactivePDF.Fixtures.csproj --configuration $Configuration --no-restore -- $fixtureRoot

    dotnet restore .\InactivePDF.slnx --runtime $Runtime
    dotnet publish .\src\InactivePDF.Api\InactivePDF.Api.csproj --configuration $Configuration --runtime $Runtime --self-contained true --output $publishRoot --no-restore
    dotnet publish .\src\InactivePDF.ConversionWorker\InactivePDF.ConversionWorker.csproj --configuration $Configuration --runtime $Runtime --self-contained true --output $publishRoot --no-restore
    foreach ($required in @('InactivePDF.Api.exe', 'InactivePDF.ConversionWorker.exe', 'InactivePDF.settings.json', 'wwwroot\index.html', 'wwwroot\console.js', 'wwwroot\console.css', 'wwwroot\openapi.json', 'wwwroot\api-reference.html')) {
        $path = Join-Path $publishRoot $required
        if (-not (Test-Path -LiteralPath $path)) { throw "Published artifact is missing $required" }
    }

    if (-not $SkipLiveService) {
        $port = ([Uri]$BaseUrl).Port
        $env:INACTIVEPDF_DATA_PATH = $dataRoot
        $env:INACTIVEPDF_WATCH_ROOT = $watchRoot
        $service = Start-Process dotnet -ArgumentList @('run', '--project', '.\src\InactivePDF.Api\InactivePDF.Api.csproj', '--configuration', $Configuration, '--no-restore', '--urls', "http://127.0.0.1:$port") -WorkingDirectory $repoRoot -PassThru
        for ($attempt = 0; $attempt -lt 60; $attempt++) {
            try { Invoke-WebRequest -Uri "http://127.0.0.1:$port/ready" -UseBasicParsing | Out-Null; break } catch { Start-Sleep -Seconds 1 }
        }
        Invoke-WebRequest -Uri "http://127.0.0.1:$port/ready" -UseBasicParsing | Out-Null
        dotnet run --project .\tools\InactivePDF.ReleaseCheck\InactivePDF.ReleaseCheck.csproj --configuration $Configuration --no-restore -- --base-url "http://127.0.0.1:$port/" --fixture-root (Join-Path $fixtureRoot 'TestFixtures') --watch-root $watchRoot
    }
    Write-Host 'Phase 10 verification passed.'
}
finally {
    if ($null -ne $service -and -not $service.HasExited) { Stop-Process -Id $service.Id -Force }
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}
