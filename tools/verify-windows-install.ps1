[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$FixturePath,
    [string]$BaseUrl = 'http://127.0.0.1:5080',
    [string]$WatchRoot = 'C:\InactivePDF\WatchFolders\Default',
    [string]$LibreOfficePath,
    [string]$BearerToken,
    [int]$TimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Get-Json([string]$Uri, [hashtable]$Headers = @{}) {
    $response = Invoke-WebRequest -UseBasicParsing -Uri $Uri -Headers $Headers -TimeoutSec 30
    return ($response.Content | ConvertFrom-Json)
}

function Get-PdfBytes([string]$Uri, [hashtable]$Headers = @{}) {
    $client = New-Object System.Net.Http.HttpClient
    try {
        foreach ($header in $Headers.GetEnumerator()) { $client.DefaultRequestHeaders.TryAddWithoutValidation($header.Key, $header.Value) | Out-Null }
        return $client.GetByteArrayAsync($Uri).GetAwaiter().GetResult()
    } finally { $client.Dispose() }
}

function Invoke-MultipartConversion([string]$Path, [hashtable]$Headers) {
    $client = New-Object System.Net.Http.HttpClient
    $stream = [System.IO.File]::OpenRead($Path)
    $fileContent = New-Object System.Net.Http.StreamContent($stream)
    $form = New-Object System.Net.Http.MultipartFormDataContent
    try {
        $form.Add((New-Object System.Net.Http.StringContent('async')), 'mode')
        $form.Add((New-Object System.Net.Http.StringContent('archive')), 'profile')
        $form.Add($fileContent, 'file', [System.IO.Path]::GetFileName($Path))
        foreach ($header in $Headers.GetEnumerator()) { $client.DefaultRequestHeaders.TryAddWithoutValidation($header.Key, $header.Value) | Out-Null }
        $response = $client.PostAsync("$BaseUrl/v1/conversions", $form).GetAwaiter().GetResult()
        $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        return @{ StatusCode = [int]$response.StatusCode; Body = ($body | ConvertFrom-Json) }
    } finally {
        $form.Dispose(); $fileContent.Dispose(); $stream.Dispose(); $client.Dispose()
    }
}

$fixture = [System.IO.Path]::GetFullPath($FixturePath)
Assert-True (Test-Path -LiteralPath $fixture -PathType Leaf) "Fixture does not exist: $fixture"
$fixtureItem = Get-Item -LiteralPath $fixture -Force
Assert-True (-not $fixtureItem.Attributes.HasFlag([System.IO.FileAttributes]::ReparsePoint)) 'Fixture cannot be a symlink or reparse point.'
$resolvedBaseUrl = $BaseUrl.TrimEnd('/')
$headers = @{}
if (-not [string]::IsNullOrWhiteSpace($BearerToken)) { $headers.Authorization = "Bearer $BearerToken" }

$tempRoot = Join-Path $env:TEMP ("inactivepdf-verify-" + [Guid]::NewGuid().ToString('N'))
$directOutput = Join-Path $tempRoot 'direct'
$directProfile = Join-Path $tempRoot 'profile'
New-Item -ItemType Directory -Path $directOutput, $directProfile -Force | Out-Null
try {
    $lo = $LibreOfficePath
    if ([string]::IsNullOrWhiteSpace($lo)) { $lo = $env:INACTIVEPDF_LIBREOFFICE_PATH }
    if ([string]::IsNullOrWhiteSpace($lo)) { $lo = Join-Path ${env:ProgramFiles} 'LibreOffice\program\soffice.exe' }
    Assert-True (Test-Path -LiteralPath $lo -PathType Leaf) "LibreOffice executable was not found: $lo"
    $profileUri = ([System.Uri]$directProfile).AbsoluteUri
    $directProcess = Start-Process -FilePath $lo -ArgumentList @('--headless', '--invisible', '--nodefault', '--nologo', '--nolockcheck', '--norestore', '--nofirststartwizard', "-env:UserInstallation=$profileUri", '--convert-to', 'pdf', '--outdir', $directOutput, $fixture) -Wait -PassThru -NoNewWindow
    Assert-True ($directProcess.ExitCode -eq 0) "Direct LibreOffice headless conversion failed with exit code $($directProcess.ExitCode)."
    $directPdf = Join-Path $directOutput ([System.IO.Path]::GetFileNameWithoutExtension($fixture) + '.pdf')
    Assert-True (Test-Path -LiteralPath $directPdf -PathType Leaf) "LibreOffice did not create the expected PDF: $directPdf"
    $directBytes = [System.IO.File]::ReadAllBytes($directPdf)
    Assert-True ($directBytes.Length -ge 5 -and [System.Text.Encoding]::ASCII.GetString($directBytes, 0, 5) -eq '%PDF-') 'Direct LibreOffice output is not a PDF.'
    Write-Host 'PASS direct LibreOffice headless conversion'

    $ready = Get-Json "$resolvedBaseUrl/ready" $headers
    Write-Host 'PASS service readiness'

    $apiResult = Invoke-MultipartConversion $fixture $headers
    Assert-True ($apiResult.StatusCode -eq 202) "API conversion was not accepted; HTTP $($apiResult.StatusCode)."
    $jobId = $apiResult.Body.jobId
    Assert-True (-not [string]::IsNullOrWhiteSpace($jobId)) 'API conversion did not return a job id.'
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        Start-Sleep -Milliseconds 500
        $job = Get-Json "$resolvedBaseUrl/v1/jobs/$jobId" $headers
        if ($job.state -in @('Succeeded', 'Failed', 'DeadLettered', 'Cancelled', 'Canceled')) { break }
    } while ((Get-Date) -lt $deadline)
    Assert-True ($job.state -eq 'Succeeded') "API conversion ended in state '$($job.state)': $($job.errorMessage)"
    Assert-True $job.outputAvailable 'API conversion did not publish an output.'
    $pdfBytes = Get-PdfBytes "$resolvedBaseUrl$($job.outputUrl)" $headers
    Assert-True ($pdfBytes.Length -ge 5 -and [System.Text.Encoding]::ASCII.GetString($pdfBytes, 0, 5) -eq '%PDF-') 'API output is not a PDF.'
    Write-Host 'PASS API conversion, polling, and PDF download'

    $watchInput = Join-Path $WatchRoot 'Input'
    $watchOutput = Join-Path $WatchRoot 'Output'
    $watchOriginals = Join-Path $WatchRoot 'Originals'
    $watchErrors = Join-Path $WatchRoot 'Errors'
    foreach ($path in @($watchInput, $watchOutput, $watchOriginals, $watchErrors)) { Assert-True (Test-Path -LiteralPath $path -PathType Container) "Watch-folder directory is missing: $path" }
    $watchName = 'inactivepdf-verification-' + [Guid]::NewGuid().ToString('N') + [System.IO.Path]::GetExtension($fixture)
    $watchInputFile = Join-Path $watchInput $watchName
    Copy-Item -LiteralPath $fixture -Destination $watchInputFile -Force
    $watchOutputFile = Join-Path $watchOutput ([System.IO.Path]::GetFileNameWithoutExtension($watchName) + '.pdf')
    $watchOriginalFile = Join-Path $watchOriginals $watchName
    $watchDeadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        Start-Sleep -Milliseconds 500
        if (Test-Path -LiteralPath $watchOutputFile -PathType Leaf -and Test-Path -LiteralPath $watchOriginalFile -PathType Leaf) { break }
        Assert-True (-not (Test-Path -LiteralPath (Join-Path $watchErrors $watchName))) 'Watch-folder fixture moved to Errors.'
    } while ((Get-Date) -lt $watchDeadline)
    Assert-True (Test-Path -LiteralPath $watchOutputFile -PathType Leaf) 'Watch-folder conversion did not produce Output.'
    Assert-True (Test-Path -LiteralPath $watchOriginalFile -PathType Leaf) 'Watch-folder conversion did not move the source to Originals.'
    $watchBytes = [System.IO.File]::ReadAllBytes($watchOutputFile)
    Assert-True ($watchBytes.Length -ge 5 -and [System.Text.Encoding]::ASCII.GetString($watchBytes, 0, 5) -eq '%PDF-') 'Watch-folder output is not a PDF.'
    Write-Host 'PASS watch-folder conversion and output routing'
    Write-Host 'Windows installation verification passed.'
} finally {
    if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue }
}
