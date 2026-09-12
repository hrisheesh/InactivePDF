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
$setup = Join-Path $PSScriptRoot 'setup-windows.ps1'
& $setup -InstallPath $InstallPath -ServiceName $ServiceName -DisplayName $DisplayName -DataRoot $DataRoot -WatchRoot $WatchRoot -LibreOfficePath $LibreOfficePath -ServiceUrl $ServiceUrl -Credential $Credential -SkipServiceStart:$SkipServiceStart
