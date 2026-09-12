[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$InstallPath,
    [string]$ServiceName = 'InactivePDF',
    [string]$DisplayName = 'InactivePDF Conversion Service',
    [string]$DataRoot = 'C:\ProgramData\InactivePDF',
    [string]$WatchRoot = 'C:\InactivePDF\WatchFolders\Default',
    [string]$LibreOfficePath,
    [string]$ServiceUrl = 'http://127.0.0.1:5080',
    [PSCredential]$Credential,
    [switch]$SkipServiceStart
)

$ErrorActionPreference = 'Stop'

function Resolve-ExistingPath([string]$Path, [string]$Description) {
    $resolved = [System.IO.Path]::GetFullPath($Path)
    if (-not (Test-Path -LiteralPath $resolved)) { throw "$Description does not exist: $resolved" }
    $item = Get-Item -LiteralPath $resolved -Force
    if ($item.Attributes.HasFlag([System.IO.FileAttributes]::ReparsePoint)) { throw "$Description cannot be a symlink or reparse point: $resolved" }
    return $resolved
}

function Ensure-SafeDirectory([string]$Path, [string]$Description) {
    $resolved = [System.IO.Path]::GetFullPath($Path)
    if (Test-Path -LiteralPath $resolved) {
        $item = Get-Item -LiteralPath $resolved -Force
        if (-not $item.PSIsContainer) { throw "$Description is not a directory: $resolved" }
        if ($item.Attributes.HasFlag([System.IO.FileAttributes]::ReparsePoint)) { throw "$Description cannot be a symlink or reparse point: $resolved" }
    } else {
        New-Item -ItemType Directory -Path $resolved -Force | Out-Null
    }
    return $resolved
}

function Find-LibreOffice([string]$ConfiguredPath) {
    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($ConfiguredPath)) { $candidates += $ConfiguredPath }
    if (-not [string]::IsNullOrWhiteSpace($env:INACTIVEPDF_LIBREOFFICE_PATH)) { $candidates += $env:INACTIVEPDF_LIBREOFFICE_PATH }
    if ($env:ProgramFiles) { $candidates += (Join-Path $env:ProgramFiles 'LibreOffice\program\soffice.exe') }
    if (${env:ProgramFiles(x86)}) { $candidates += (Join-Path ${env:ProgramFiles(x86)} 'LibreOffice\program\soffice.exe') }
    foreach ($commandName in @('soffice.exe', 'soffice.com')) {
        $command = Get-Command $commandName -ErrorAction SilentlyContinue
        if ($command) { $candidates += $command.Source }
    }
    foreach ($candidate in ($candidates | Select-Object -Unique)) {
        if ([string]::IsNullOrWhiteSpace($candidate)) { continue }
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            $item = Get-Item -LiteralPath $candidate -Force
            if (-not $item.Attributes.HasFlag([System.IO.FileAttributes]::ReparsePoint)) { return $item.FullName }
        }
    }
    $searched = ($candidates | Select-Object -Unique) -join '; '
    throw "LibreOffice was not found. Install the Windows desktop package or pass -LibreOfficePath with the full path to soffice.exe. Searched: $searched"
}

function Grant-ServiceAccess([string]$Path, [string]$Identity) {
    if ([string]::IsNullOrWhiteSpace($Identity) -or $Identity -eq 'LocalSystem') { return }
    $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
        $Identity,
        'Modify',
        'ContainerInherit,ObjectInherit',
        'None',
        'Allow')
    $acl = Get-Acl -LiteralPath $Path
    $acl.SetAccessRule($rule)
    Set-Acl -LiteralPath $Path -AclObject $acl
}

$resolvedInstallPath = Resolve-ExistingPath $InstallPath 'Install path'
$api = Join-Path $resolvedInstallPath 'InactivePDF.Api.exe'
$worker = Join-Path $resolvedInstallPath 'InactivePDF.ConversionWorker.exe'
$settings = Join-Path $resolvedInstallPath 'InactivePDF.settings.json'
foreach ($required in @(@($api, 'API executable'), @($worker, 'conversion worker'), @($settings, 'settings file'))) {
    if (-not (Test-Path -LiteralPath $required[0] -PathType Leaf)) { throw "$($required[1]) not found: $($required[0])" }
}
foreach ($asset in @('wwwroot\index.html', 'wwwroot\console.js', 'wwwroot\console.css', 'wwwroot\openapi.json', 'wwwroot\api-reference.html')) {
    $assetPath = Join-Path $resolvedInstallPath $asset
    if (-not (Test-Path -LiteralPath $assetPath -PathType Leaf)) { throw "Published asset not found: $assetPath" }
}

$resolvedDataRoot = Ensure-SafeDirectory $DataRoot 'Data root'
$resolvedWatchRoot = Ensure-SafeDirectory $WatchRoot 'Watch root'
$stateRoot = Ensure-SafeDirectory (Join-Path $resolvedDataRoot 'state') 'State root'
$jobRoot = Ensure-SafeDirectory (Join-Path $resolvedDataRoot 'jobs') 'Job root'
foreach ($folder in @('Input', 'Processing', 'Output', 'Originals', 'Errors', 'Logs')) {
    Ensure-SafeDirectory (Join-Path $resolvedWatchRoot $folder) "Watch $folder directory" | Out-Null
}
$resolvedLibreOfficePath = Find-LibreOffice $LibreOfficePath

$serviceIdentity = if ($Credential) { $Credential.UserName } else { 'LocalSystem' }
Grant-ServiceAccess $resolvedDataRoot $serviceIdentity
Grant-ServiceAccess $resolvedWatchRoot $serviceIdentity
Grant-ServiceAccess $resolvedInstallPath $serviceIdentity

$machineValues = @{
    INACTIVEPDF_SETTINGS_PATH = $settings
    INACTIVEPDF_DATA_PATH = $resolvedDataRoot
    INACTIVEPDF_STATE_PATH = $stateRoot
    INACTIVEPDF_JOBS_PATH = $jobRoot
    INACTIVEPDF_WATCH_ROOT = $resolvedWatchRoot
    INACTIVEPDF_LIBREOFFICE_PATH = $resolvedLibreOfficePath
    INACTIVEPDF_CONVERSION_WORKER_PATH = $worker
    ASPNETCORE_URLS = $ServiceUrl
}
foreach ($entry in $machineValues.GetEnumerator()) {
    [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Machine')
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    if ($existing.Status -ne 'Stopped') { Stop-Service -Name $ServiceName -Force -ErrorAction Stop }
    sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 1
}

$binaryPath = '"{0}"' -f $api
$newServiceParameters = @{
    Name = $ServiceName
    DisplayName = $DisplayName
    Description = 'Self-hosted document-to-PDF conversion service with watch-folder processing.'
    BinaryPathName = $binaryPath
    StartupType = 'Automatic'
}
if ($Credential) { $newServiceParameters.Credential = $Credential }
New-Service @newServiceParameters | Out-Null
sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
sc.exe failureflag $ServiceName 1 | Out-Null

if (-not $SkipServiceStart) {
    Start-Service -Name $ServiceName
    $deadline = (Get-Date).AddSeconds(30)
    do {
        Start-Sleep -Seconds 1
        $service = Get-Service -Name $ServiceName
        if ($service.Status -eq 'Stopped') { throw "The service stopped during startup. Check Windows Event Viewer and the service logs." }
        try {
            $ready = Invoke-WebRequest -UseBasicParsing -Uri "$ServiceUrl/ready" -TimeoutSec 3
            if ($ready.StatusCode -eq 200) { break }
        } catch { }
    } while ((Get-Date) -lt $deadline)
    if (-not $ready -or $ready.StatusCode -ne 200) {
        throw "The service started but did not become ready at $ServiceUrl/ready. Verify LibreOffice, the worker executable, service-account permissions, and the Windows service logs."
    }
}

Write-Host "Installed $ServiceName."
Write-Host "Install path: $resolvedInstallPath"
Write-Host "LibreOffice: $resolvedLibreOfficePath"
Write-Host "Data root: $resolvedDataRoot"
Write-Host "Watch root: $resolvedWatchRoot"
Write-Host "Service URL: $ServiceUrl"
