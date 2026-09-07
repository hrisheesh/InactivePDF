# InactivePDF

InactivePDF is a Windows-first, self-hosted document-to-PDF service built with C# and .NET 10. It exposes an HTTP API, a typed .NET client, a watch-folder workflow, durable asynchronous jobs, and isolated conversion workers.

It is designed for applications that need document conversion without embedding the conversion engines in the calling process.

> Status: early beta. The repository is suitable for development, integration, and controlled single-server evaluation. Windows acceptance, broad document-fidelity comparison, security review, and sustained production-load validation are still required before a general production release.

## What it does

- Converts supported office documents, images, markup, text, and CSV files to PDF.
- Creates PDFs from plain text.
- Passes through existing PDFs without rasterizing them.
- Merges PDFs structurally while preserving page order and geometry.
- Processes jobs synchronously or asynchronously.
- Provides a durable single-server job queue with retries, idempotency, leases, and dead-letter replay.
- Runs expensive conversion work in short-lived worker processes.
- Provides a watch-folder workflow for file-based integrations.
- Exposes operator and machine-readable conversion logs.

The service is intentionally single-node in this release. It does not claim horizontal high availability, built-in authentication, or byte-identical output with another PDF engine.

## Supported formats

The service publishes the authoritative list at `GET /v1/capabilities`. The current catalog includes:

| Category | Extensions |
| --- | --- |
| PDF | `.pdf` |
| Images | `.png`, `.jpg`, `.jpeg`, `.gif`, `.bmp`, `.tif`, `.tiff` |
| Microsoft Office | `.doc`, `.docx`, `.dot`, `.xls`, `.xlsx`, `.ppt`, `.pptx` |
| OpenDocument | `.odt`, `.ods`, `.odp` |
| Markup and text | `.html`, `.htm`, `.rtf`, `.txt`, `.csv` |

Office, OpenDocument, HTML, RTF, and CSV conversion requires LibreOffice. Image conversion uses Magick.NET through an isolated worker. Plain text and PDF operations use the service's own code paths. Microsoft Office is not required.

Unsupported extensions and invalid file signatures return structured validation errors. Extension matching is case-insensitive. Output names use a safe version of the input base name and the `.pdf` extension.

## PDF profiles

Every conversion uses one explicit output profile:

| Profile | Intended use | Behavior |
| --- | --- | --- |
| `archive` | Fidelity-first output | Keeps source JPEG data when safe, does not downsample images, preserves source metadata where possible, and structurally validates the result. |
| `compact` | Smaller output | Enables stronger compression, uses JPEG quality 75, and downsamples images to 150 DPI. |
| `compatibility` | Older PDF consumers | Produces PDF 1.4 output, avoids image downsampling, and uses conservative metadata and compression behavior. |

Profiles are configurable in `InactivePDF.settings.json`. A profile is not accepted merely as a label: its policy changes the generated document and is recorded in diagnostics.

Password-protected PDFs are rejected unless password handling is explicitly implemented for the selected operation. The service does not guess or bypass passwords.

## Quick start

### Prerequisites

For development and local evaluation:

1. .NET 10 SDK.
2. LibreOffice for office, OpenDocument, HTML, RTF, and CSV conversion.
3. A writable data directory.

The API and conversion worker must be built together so the worker executable and its managed dependencies are available beside the API executable. The service, build, unit tests, and included C# tools do not require Python. The optional watch-folder stress script uses Python 3 only to generate its report.

For Windows production-like testing:

1. Windows Server 2016 or newer, x64.
2. .NET 10 runtime is included when using the self-contained publish script.
3. LibreOffice installed separately.
4. A dedicated service account with access to the configured data and watch-folder directories.
5. Approved fonts installed on the server.

### Build and test

```bash
dotnet restore InactivePDF.slnx
dotnet build InactivePDF.slnx --no-restore
dotnet test tests/InactivePDF.UnitTests/InactivePDF.UnitTests.csproj --no-restore
```

The unit test suite does not require LibreOffice. Conversion acceptance tests should be run on the target Windows installation with the actual LibreOffice build, fonts, storage, service account, and representative documents.

### Run locally

On macOS or Linux, use `soffice` from your PATH or set its full path:

```bash
export INACTIVEPDF_DATA_PATH="$PWD/.inactivepdf-data"
export INACTIVEPDF_LIBREOFFICE_PATH="/path/to/soffice"
export INACTIVEPDF_WATCH_ROOT="$PWD/WatchFolders/Default"
dotnet run --project src/InactivePDF.Api/InactivePDF.Api.csproj --urls http://127.0.0.1:5080
```

On Windows PowerShell:

```powershell
$env:INACTIVEPDF_DATA_PATH = 'C:\ProgramData\InactivePDF'
$env:INACTIVEPDF_LIBREOFFICE_PATH = 'C:\Program Files\LibreOffice\program\soffice.exe'
$env:INACTIVEPDF_WATCH_ROOT = 'C:\InactivePDF\WatchFolders\Default'
dotnet run --project .\src\InactivePDF.Api\InactivePDF.Api.csproj --urls http://127.0.0.1:5080
```

The default Windows data directory is `C:\ProgramData\InactivePDF`. The default Windows watch-folder directory is `C:\InactivePDF\WatchFolders\Default`.

### Clone and test the watch folder

The repository is ready for a clean local trial. Runtime documents, generated PDFs, database state, and logs are ignored by Git and stay on the machine running the service.

```bash
git clone https://github.com/hrisheesh/InactivePDF.git
cd InactivePDF
dotnet restore InactivePDF.slnx
dotnet build InactivePDF.slnx --no-restore
```

Start the service with a dedicated local data directory and the included watch-folder layout:

```bash
export INACTIVEPDF_DATA_PATH="$PWD/.inactivepdf-data"
export INACTIVEPDF_WATCH_ROOT="$PWD/WatchFolders/Default"
export INACTIVEPDF_LIBREOFFICE_PATH="/path/to/soffice"
dotnet run --project src/InactivePDF.Api/InactivePDF.Api.csproj --urls http://127.0.0.1:5080
```

On Windows PowerShell:

```powershell
$env:INACTIVEPDF_DATA_PATH = "$PWD\.inactivepdf-data"
$env:INACTIVEPDF_WATCH_ROOT = "$PWD\WatchFolders\Default"
$env:INACTIVEPDF_LIBREOFFICE_PATH = 'C:\Program Files\LibreOffice\program\soffice.exe'
dotnet run --project .\src\InactivePDF.Api\InactivePDF.Api.csproj --urls http://127.0.0.1:5080
```

Confirm the service is ready, then copy a complete supported file into `WatchFolders/Default/Input`. The generated PDF appears in `Output`; successful originals move to `Originals`; permanent failures move to `Errors`; local diagnostics appear in `Logs`.

```bash
curl -f http://127.0.0.1:5080/health
curl -f http://127.0.0.1:5080/ready
curl -f http://127.0.0.1:5080/v1/capabilities
```

Only the folder markers and `README.md` are part of the repository. Do not commit files copied into `Input`, `Processing`, `Output`, `Originals`, `Errors`, or `Logs`.

## HTTP API

### Convert one file

```bash
curl -f -X POST http://127.0.0.1:5080/v1/convert-file \
  -F 'profile=archive' \
  -F 'file=@./document.docx' \
  -o document.pdf
```

### Convert and merge files

```bash
curl -f -X POST http://127.0.0.1:5080/v1/convert-and-merge \
  -F 'profile=archive' \
  -F 'file=@./first.docx' \
  -F 'file=@./second.pdf' \
  -o merged.pdf
```

### Create a PDF from text

```bash
curl -f -X POST http://127.0.0.1:5080/v1/create-text-pdf \
  -H 'Content-Type: application/json' \
  --data '{"text":"Hello from InactivePDF","profile":"archive"}' \
  -o text.pdf
```

### Submit an asynchronous job

```bash
curl -f -X POST http://127.0.0.1:5080/v1/jobs \
  -H 'Idempotency-Key: example-job-0001' \
  -F 'operation=ConvertFile' \
  -F 'profile=archive' \
  -F 'file=@./document.docx'
```

Then poll the returned job URL:

```bash
curl -f http://127.0.0.1:5080/v1/jobs/<job-id>
curl -f http://127.0.0.1:5080/v1/jobs/<job-id>/output -o document.pdf
```

The same idempotency key and identical request return the original job. Reusing a key with different request content returns `idempotency_conflict`.

Common response statuses are:

- `200` for a completed synchronous conversion or a successful read.
- `202` when an asynchronous job is accepted.
- `409` for an idempotency conflict or an invalid dead-letter replay.
- `413` when request, file-count, or file-size limits are exceeded.
- `422` when the extension, file signature, profile, or input format is invalid.
- `500` for an unexpected server-side failure.

## Typed .NET client

The `InactivePDF.Client` project targets `netstandard2.0`. It can be used from modern .NET, .NET Framework, C#, and VB.NET applications. The client communicates over HTTP; callers do not need to load the .NET 10 conversion engine in their own process.

```csharp
using InactivePDF.Client;

using var client = new InactivePdfClient(
    new Uri("http://127.0.0.1:5080/"),
    new InactivePdfClientOptions
    {
        RequestTimeout = TimeSpan.FromMinutes(5)
    });

await using var input = File.OpenRead("document.docx");
await using var output = File.Create("document.pdf");

await client.ConvertFileWithProfileAsync(
    input,
    "document.docx",
    "archive",
    output);
```

The client also supports PDF merge, text PDF creation, asynchronous job submission, status polling, output streaming, queue inspection, dead-letter inspection, replay, retries, and cancellation.

Input streams remain owned by the caller. The client does not dispose streams supplied by the caller.

The client is currently included as a source project. To consume it from another .NET solution, add a project reference to `src/InactivePDF.Client/InactivePDF.Client.csproj`. Its package metadata targets future NuGet publication and the project can be packed with:

```bash
dotnet pack src/InactivePDF.Client/InactivePDF.Client.csproj -c Release
```

## Watch folder

The repository contains a directory template at [`WatchFolders/Default`](WatchFolders/Default):

```text
WatchFolders/Default/
  Input/       # drop complete input files here
  Processing/  # claimed files; do not edit
  Output/      # generated PDFs
  Originals/   # successfully processed originals
  Errors/      # permanently failed inputs
  Logs/        # operator and diagnostic logs
```

Workflow:

1. Copy a complete file into `Input`.
2. InactivePDF waits until the file is stable.
3. The file is moved to `Processing`.
4. A bounded worker converts it.
5. Success writes a PDF to `Output` and moves the original to `Originals`.
6. Permanent failure moves the input to `Errors`.
7. Files left in `Processing` during a restart are recovered to `Input`.

Useful settings:

```text
INACTIVEPDF_WATCH_ROOT=C:\InactivePDF\WatchFolders\Default
INACTIVEPDF_WATCH_CONCURRENCY=2
INACTIVEPDF_WATCH_HEAVY_CONCURRENCY=1
INACTIVEPDF_WATCH_MARKUP_CONCURRENCY=1
INACTIVEPDF_WATCH_RETRIES=1
INACTIVEPDF_WATCH_SCAN_INTERVAL_SECONDS=2
INACTIVEPDF_WATCH_FILE_STABILITY_SECONDS=2
```

One service instance should own a watch-folder root. Multiple instances must not share the same folder until shared-storage coordination has been validated.

## Configuration

All supported settings are in [`InactivePDF.settings.json`](InactivePDF.settings.json). The file is copied beside the API and worker during build and publish. Restart the service after changing it.

Environment variables override values from the JSON file, which is useful for Windows Service deployment and emergency changes. Do not put secrets in the settings file.

Important configuration groups:

- `Paths`: data, state, job, watch-folder, LibreOffice, and optional fidelity-tool paths.
- `Api`: request size, file size, and file-count limits.
- `Resources`: output size, image pixels, disk space, timeouts, and copy buffers.
- `Workers`: queue capacity, attempts, leases, polling, retry delay, and worker memory.
- `Concurrency`: independent office, image, PDF, and text gates.
- `WatchFolder`: scan, stability, retry, concurrency, resource admission, and opt-in retention behavior.
- `Conversion`: default operation, default profile, and profile policies.

The conservative defaults are intended for a small single-server deployment. Increasing concurrency can increase native memory use significantly.

### Watch-folder retention

Retention is disabled by default so the service never deletes generated documents or originals without an explicit operator choice. To enable it, edit `WatchFolder.Retention` in `InactivePDF.settings.json`, restart the service, and set both the limits and the corresponding delete switches:

```json
"Retention": {
  "Enabled": true,
  "SweepIntervalSeconds": 300,
  "MaximumAgeDays": 30,
  "MaximumOutputBytes": 32212254720,
  "MaximumOriginalsBytes": 32212254720,
  "MaximumErrorsBytes": 5368709120,
  "MaximumLogsBytes": 2147483648,
  "MinimumFileAgeSeconds": 300,
  "DeleteOutputFiles": true,
  "DeleteOriginalFiles": true,
  "DeleteErrorFiles": true,
  "DeleteLogFiles": true
}
```

Age and size limits are independent: `0` disables that limit. Cleanup removes the oldest eligible files first and never touches `Input` or `Processing`. The current day's active diagnostic log is protected. Keep `DeleteOutputFiles` and `DeleteOriginalFiles` disabled when those artifacts must be retained permanently.

## Operations and diagnostics

## Watermarks and authentication

Watermark profiles are managed with `GET`, `PUT`, and `DELETE /v1/watermark-profiles/{name}`. A profile is JSON using `WatermarkOptions` (text or image, opacity, rotation, placement, page ranges, tiling, headers, footers, and page numbering). Conversion requests may select a profile with `watermarkProfile`; JSON requests may also provide a direct `watermark` object. Watch-folder deployments can set `INACTIVEPDF_WATCH_WATERMARK_PROFILE`.

Set `INACTIVEPDF_API_TOKEN` on private deployments. When set, every endpoint except `/health` and `/ready` requires `Authorization: Bearer <token>`. Keep the token in the service manager's protected secret store and use HTTPS at the reverse proxy. Watermark image assets must be kept in the configured asset directory and are never accepted from arbitrary filesystem paths.

The built-in browser editor is available at `/` and provides profile selection, editable watermark settings, and a live first-page preview. It is an operator tool, not a replacement for final PDF fidelity validation.

Endpoints:

| Endpoint | Purpose |
| --- | --- |
| `GET /health` | Process liveness |
| `GET /ready` | Storage and conversion dependency readiness |
| `GET /v1/capabilities` | Operations, formats, routes, and profiles |
| `GET /v1/metrics` | Process-local counters and queue depth |
| `GET /v1/queue` | Queue depth and capacity |
| `GET /v1/dead-letters` | Failed jobs requiring operator review |
| `POST /v1/dead-letters/{jobId}/replay` | Controlled dead-letter replay |

Watch-folder logs are split into two files:

- `.log`: concise operator events, timings, sizes, retries, and error reasons.
- `.jsonl`: detailed machine-readable events including CPU, GC, threads, worker metrics, gate wait, and exception information.

The detailed log is intended for analysis tools. Retain and rotate it according to the storage policy for the host running the service.

## Reliability model

Asynchronous jobs use a durable LiteDB data file for single-server persistence. Jobs are claimed with leases, renewed while running, and made available again after an expired lease. Retryable failures use bounded backoff. Terminal failures are stored as dead letters with attempt history.

The service uses short-lived conversion workers so native memory is reclaimed when a worker exits. Worker timeouts, process-tree termination, image limits, disk limits, and conversion gates are independent protections.

This is durable for one service instance. Horizontal scaling requires a shared queue or database design and separate failure testing; it is not currently advertised as a clustered high-availability product.


## Windows self-contained publish

The publish script creates a self-contained `win-x64` deployment containing both the API and conversion worker:

```powershell
.\tools\publish-windows.ps1
```

By default, the output is written to `artifacts\InactivePDF`. LibreOffice remains a separate deployment dependency because it is an external conversion engine.

To install the API as a Windows Service from an elevated PowerShell session:

```powershell
.\tools\install-windows-service.ps1 -InstallPath .\artifacts\InactivePDF
```

The installer configures automatic startup and service recovery. Use a dedicated low-privilege service account and grant it access only to the configured data and watch-folder paths.

The self-contained publish contains the .NET runtime and the service binaries. LibreOffice remains an explicit machine dependency and is not bundled by this repository.

## Architecture

```text
HTTP clients / .NET client / watch folder
                    |
              InactivePDF.Api
          validation and bounded intake
                    |
            durable single-node state
             claims, leases, retries
                    |
       short-lived InactivePDF.ConversionWorker
              /          |          \
          images      text and PDF    LibreOffice
```

Project boundaries:

```text
src/InactivePDF.Domain          stable models and contracts
src/InactivePDF.Application     validation, orchestration, queue abstractions
src/InactivePDF.Infrastructure  storage, files, processes, rendering, workers
src/InactivePDF.Api             HTTP host and background services
src/InactivePDF.Client          netstandard2.0 typed HTTP client
tests/                          unit and behavior tests
tools/                          fixtures, stress, publish, and fidelity utilities
```

The API does not load document bytes into large managed arrays for normal conversion. Conversion work is file-backed and isolated from the API process.
