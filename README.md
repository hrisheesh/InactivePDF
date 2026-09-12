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
- Runs expensive conversion work in isolated worker processes; Windows Office conversions can reuse independent, private LibreOffice sessions to avoid repeated engine startup.
- Provides a watch-folder workflow for file-based integrations.
- Exposes operator and machine-readable conversion logs.

The service is intentionally single-node in this release. It does not claim horizontal high availability or byte-identical output with another PDF engine. Private deployments can enable bearer-token authentication.

## Watermark API

The built-in operations console at `/` includes live service metrics, recent durable jobs, all configuration sections, and watermark editing. Settings are reviewed and validated before saving and require a service restart; watermark profiles are available to new conversions immediately. Enter the server bearer token through Connection & access for authenticated deployments.

Watermark profiles are stored under the service data directory and managed with:

```http
GET    /v1/watermark-profiles
GET    /v1/watermark-profiles/{name}
PUT    /v1/watermark-profiles/{name}
DELETE /v1/watermark-profiles/{name}
GET    /v1/watermark-assets
GET    /v1/watermark-assets/{name}
POST   /v1/watermark-assets   (multipart field: file)
```

Example profile:

```json
{
  "kind": "Text", "text": "CONFIDENTIAL", "fontSize": 36,
  "color": "#808080", "opacity": 0.2, "rotation": -35,
  "position": "Center", "layer": "Over", "pages": "all", "tile": false,
  "header": "Example Ltd - {page}/{pages}", "footer": "Internal use only"
}
```

Select a saved profile with the `watermarkProfile` field on `POST /v1/jobs` or the synchronous conversion routes. Synchronous JSON conversion also accepts a direct `watermark` object. Multipart clients send the same object as a JSON form field named `watermark`. Image profiles must use an asset filename from the configured `INACTIVEPDF_WATERMARK_ASSET_PATH`; the browser editor can upload supported image files through the protected asset endpoint. Absolute paths, traversal, symlinks, and reparse points are rejected.

The .NET client exposes `WatermarkProfile` and `WatermarkJson` on `InactivePdfJobRequest`, plus direct-watermark methods for file, merge, and text conversion. The client does not store credentials; add `Authorization: Bearer <administrator-token-or-integration-key>` to the supplied `HttpClient` before constructing `InactivePdfClient`.

Asynchronous jobs submitted with an integration key are owned by that key. The same key needs `jobs:read` to read status and `outputs:read` to download output; another integration key receives `404 Not Found` without job or output metadata. Administrator and watch-folder jobs are server-owned and are available only through administrator operations. Per-key requests, concurrency, queue, file, output, and daily-input limits can be edited in Security & system or through `PUT /v1/admin/api-keys/{id}/limits`. See [API key authentication and ownership](docs/api-keys.md).

The complete global and per-key admission contract, including `413`/`429` responses, retry behavior, quotas, and retention is documented in [API limits and quotas](docs/api-limits.md).

For the shortest integration path, send a multipart file to `POST /v1/conversions`. Set `mode=async` (the default) to receive `202 Accepted` with a job location, or `mode=sync` to receive the generated PDF directly. Optional fields are `profile`, `watermarkProfile`, and a JSON `watermark`; use `Idempotency-Key` for safe retries and `X-Request-Id` for trace correlation. The selected profile is resolved and stored with an asynchronous job at acceptance time.

Set `INACTIVEPDF_API_TOKEN` for administrator authentication. Integration keys are created and managed through the administrator-only routes documented in [`docs/api-keys.md`](docs/api-keys.md). `/health` and `/ready` remain available for probes; the public console shell can load before sign-in, while administrator data and configuration routes require the exact administrator token. Put TLS and rate limiting at the reverse proxy, keep all credentials out of source control, and use a dedicated service account.

## Developer quickstart

The interactive API reference is available at [`/api-reference`](http://127.0.0.1:5080/api-reference) when the service is running. It loads the machine-readable [`/openapi.json`](http://127.0.0.1:5080/openapi.json), lists the available routes, supports authenticated read-only requests, and links back to this console. The repository also includes a ready-to-import [Postman collection](examples/InactivePDF.postman_collection.json), a [curl quickstart](examples/curl/quickstart.sh), and working [JavaScript](examples/javascript/convert.mjs) and [Python](examples/python/convert.py) polling examples.

The shortest path is:

1. Create an integration key with the administrator token: `POST /v1/admin/api-keys`.
2. Convert one file with `POST /v1/conversions`, using `mode=sync` for an immediate PDF or `mode=async` for a durable job.
3. For async mode, poll the returned `jobId` with `GET /v1/jobs/{jobId}` until the state is `Succeeded` or a terminal failure.
4. Download the result from `GET /v1/jobs/{jobId}/output`.
5. Set `profile=archive`, `compact`, or `compatibility`; add `watermarkProfile` or direct JSON `watermark` when needed.
6. Handle structured errors using their `code`, `message`, `requestId`, `retryable`, and `Retry-After` fields.
7. Review the global limits and key-specific limits before sending large or parallel workloads.

For safe client retries, send the same `Idempotency-Key` with the same request. The service returns the original job for a repeat submission and returns `409 idempotency_conflict` if the key is reused for different input. There is no webhook in the self-hosted V1 API; polling the job resource is the supported completion callback.

Common error codes are `unauthorized`, `forbidden`, `invalid_mode`, `file_required`, `invalid_operation`, `invalid_watermark`, `request_bytes_exceeded`, `file_count_exceeded`, `file_bytes_exceeded`, `output_bytes_exceeded`, `requests_per_minute_exceeded`, `concurrent_conversions_exceeded`, `queued_jobs_exceeded`, `daily_input_quota_exceeded`, `idempotency_conflict`, and conversion format codes returned by input validation. `401` means the credential is missing or invalid; `403` means it lacks permission; `413` means the request is too large; `422` means the input or requested conversion is invalid; `429` means admission is temporarily limited; and `5xx` means the service or engine failed.

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

The public profile catalog keeps output policy separate from watermark content:

```http
GET /v1/profiles
GET /v1/profiles/{name}
```

`GET /v1/profiles` returns `pdfOutput` (the configured `archive`, `compact`, and `compatibility` policies) and `watermark` (saved watermark names with their complete options). The name lookup returns the matching `pdfOutput` and/or `watermark` member, so the two profile types remain unambiguous even when names overlap. Both endpoints require the `profiles:read` scope or administrator authentication. The typed .NET client exposes these as `GetProfilesAsync` and `GetProfileAsync`.

Watch-folder conversions use `INACTIVEPDF_WATCH_PROFILE` for the PDF output profile and `INACTIVEPDF_WATCH_WATERMARK_PROFILE` for an optional watermark profile. The selected watermark options are resolved when a file is claimed and carried in the worker request, so edits made afterward do not change that conversion. API requests follow the same snapshot rule: direct watermark options take precedence over a named watermark profile, and the resolved options travel with an accepted asynchronous job.

Administrator watermark management is also available through the generic profile routes:

```http
POST   /v1/admin/profiles
PUT    /v1/admin/profiles/{name}
DELETE /v1/admin/profiles/{name}?type=watermark
```

Create and update requests use `{ "type": "watermark", "name": "internal", "options": { ... } }`. PDF output profiles remain configuration-backed and are edited under `Conversion.Profiles` in `InactivePDF.settings.json`; they are intentionally not mutable through the watermark profile store.

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

### Execution modes

`Performance.ExecutionMode` controls optional diagnostic work without changing the required conversion safety boundary. `Production` is the normal product path: it performs validation, conversion, watermarking, output checks, atomic publishing, timeout, cancellation, retry, cleanup, authentication, and workspace protection while omitting development-only resource sampling and stage trace files. `Development` preserves the detailed investigation path with verbose watch-folder resource logs, process observations, and per-stage timings. Set `INACTIVEPDF_EXECUTION_MODE` to override the file setting; environment values take precedence and the service must be restarted after changing the settings file.

On Windows, `Performance.WindowsPersistentOffice` enables the optimized Office path by default. It starts independent LibreOffice sessions only when Office work arrives; each session has a private profile, a separate process endpoint, and a Windows Job Object. The macOS and Linux paths remain short-lived and unchanged. Set `INACTIVEPDF_WINDOWS_PERSISTENT_OFFICE=false` to use the isolated cold path for a compatibility comparison or rollback.

The mode does not reduce the configured worker count and does not change export filters, PDF profiles, fonts, locale, or page geometry. Use Development when investigating fidelity or resource behavior; use Production for throughput measurements and deployment.

```bash
dotnet restore InactivePDF.slnx
dotnet build InactivePDF.slnx --no-restore
dotnet test tests/InactivePDF.UnitTests/InactivePDF.UnitTests.csproj --no-restore
```

The unit test suite does not require LibreOffice. Conversion acceptance tests should be run on the target Windows installation with the actual LibreOffice build, fonts, storage, service account, and representative documents.

### Phase 10 release verification

The release gate combines the full unit/API/security/watch-folder suite, a self-contained Windows publish check, OpenAPI contract checks, and live conversions for every supported V1 format. It also verifies profile selection, direct watermark options, async polling and output download, idempotent retries, concurrent requests, and an actual watch-folder conversion. The check uses a temporary data directory and never modifies the repository fixtures.

On Windows PowerShell, run the complete gate from the repository root:

```powershell
.\tools\run-phase10-verification.ps1
```

On macOS or Linux, the same gate can cross-publish the Windows artifact when the required runtime pack is installed:

```bash
./tools/run-phase10-verification.sh
```

The live checker is also available for an already running service:

```bash
dotnet run --project tools/InactivePDF.ReleaseCheck -- \
  --base-url http://127.0.0.1:5080/ \
  --fixture-root ./fixtures/TestFixtures \
  --watch-root ./WatchFolders/Default
```

The checker exits non-zero on any failed contract, conversion, output, profile, idempotency, concurrency, or watch-folder assertion. Set `INACTIVEPDF_API_TOKEN` only through the process environment when the target service is protected; the token is never printed.

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
INACTIVEPDF_WATCH_PROFILE=archive
INACTIVEPDF_WATCH_WATERMARK_PROFILE=internal
```

One service instance should own a watch-folder root. Multiple instances must not share the same folder until shared-storage coordination has been validated.

## Configuration

All supported settings are in [`InactivePDF.settings.json`](InactivePDF.settings.json). The file is copied beside the API and worker during build and publish. Restart the service after changing it.

Environment variables override values from the JSON file, which is useful for Windows Service deployment and emergency changes. Do not put secrets in the settings file.

Important configuration groups:

- `Paths`: data, state, job, watch-folder, LibreOffice, and optional fidelity-tool paths.
- `Performance`: execution mode, shared swarm worker ceiling, estimated memory budget, admission backlog, and fairness threshold. The console's Performance & profiles section offers Conservative, Balanced, and Swarm presets plus named complete service configurations.
- `Api`: request size, file size, and file-count limits.
- `Resources`: output size, image pixels, disk space, timeouts, and copy buffers.
- `Workers`: queue capacity, attempts, leases, polling, retry delay, and worker memory.
- `Concurrency`: independent office, image, PDF, and text gates.
- `WatchFolder`: scan, stability, retry, concurrency, resource admission, output/watermark profile selection, and opt-in retention behavior.
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

The browser console at `/` is a live operations dashboard. It receives a server-pushed snapshot every two seconds and shows the connection state, last update time, stale/disconnected status, queue depth, active workers, throughput, failures, retries, dead letters, average/minimum/maximum timings, per-format timings, watch-folder counts, and process resources. Resource values are reported by the host; unavailable platform counters are shown as unavailable rather than as zero.

Use the single search field in the console to search settings, service and watermark profiles, jobs, conversion observations, structured logs, watch-folder metadata, and documentation metadata. Results navigate to the relevant console section and briefly highlight the destination. Search returns operator metadata only; document contents, engine stderr, tokens, and absolute filesystem paths are not indexed.

Overview analytics can be filtered to all sources, watch-folder conversions, synchronous API conversions, or queued jobs. Timing metrics describe conversion execution time and exclude upload and queue wait unless explicitly labelled. Watch-folder input, processing, output, originals, errors, and logs remain separate so a file waiting for stability is not mistaken for an active worker.

The overview also exposes durable usage for each integration key: request and conversion totals, success/failure counts, input/output bytes, bytes saved, compression percentage, conversion and queue timing, retry rate, rate-limit rejections, format breakdown, and last-activity timestamps. Global analytics include the same source split plus queue wait, retry, timing, format, and process-resource summaries. Audit events record administrator changes and rejected API requests as method/path/status metadata only; bearer tokens, request bodies, document contents, query values, and absolute paths are excluded.

The dashboard's editable controls read and save the same persisted settings and profile APIs documented below. Saved startup settings require a service restart; live telemetry and watermark profiles update without restarting.

The performance baseline workflow and telemetry field definitions are documented in [`docs/performance-baseline.md`](docs/performance-baseline.md). Use it to create a checksum manifest before comparing macOS and Windows runs.

## Watermarks and authentication

Watermark profiles are managed with `GET`, `PUT`, and `DELETE /v1/watermark-profiles/{name}`. A profile is JSON using `WatermarkOptions` (text or image, opacity, rotation, placement, page ranges, tiling, headers, footers, and page numbering). Conversion requests may select a profile with `watermarkProfile`; JSON requests may also provide a direct `watermark` object. Watch-folder deployments can set `INACTIVEPDF_WATCH_PROFILE` and `INACTIVEPDF_WATCH_WATERMARK_PROFILE`.

Set `INACTIVEPDF_API_TOKEN` on private deployments. When set, administrator endpoints require `Authorization: Bearer <administrator-token>`. Integration applications should use a scoped key created through the administrator API; their default scopes allow conversion submission, job/output reads, and profile/asset reads without administrator access. Health probes and the public console shell are available before sign-in. See [`docs/api-keys.md`](docs/api-keys.md) for creation, rotation, revocation, expiry, scope, and error behavior. Keep credentials in the service manager's protected secret store and use HTTPS at the reverse proxy. Watermark image assets must be kept in the configured asset directory and are never accepted from arbitrary filesystem paths.

The security deployment baseline is documented in [`docs/security.md`](docs/security.md). In short: use TLS at the network boundary, run under a dedicated least-privilege account, keep state and document roots private, do not use symlinks or reparse points inside service roots, and treat the administrator token and integration-key secrets as deployment credentials.

The built-in browser editor is available at `/` and provides profile selection, asset browsing/upload, editable watermark settings, and a live preview. The preview makes layer behavior explicit and reflects nine placement anchors, opacity, rotation, page selection, headers, footers, page numbers, and tiling. It is an operator tool, not a replacement for final PDF fidelity validation.

Endpoints:

| Endpoint | Purpose |
| --- | --- |
| `GET /health` | Process liveness |
| `GET /ready` | Storage and conversion dependency readiness |
| `GET /v1/capabilities` | Operations, formats, routes, and profiles |
| `GET /v1/profiles` | Separate PDF output and watermark profile catalog |
| `GET /v1/profiles/{name}` | Look up a named output and/or watermark profile |
| `POST /v1/admin/profiles` | Create an administrator-managed watermark profile |
| `PUT /v1/admin/profiles/{name}` | Replace an administrator-managed watermark profile |
| `DELETE /v1/admin/profiles/{name}` | Delete an administrator-managed watermark profile |
| `POST /v1/conversions` | Unified sync/async file conversion endpoint |
| `GET /v1/jobs` | Owned job listing with filters and cursor pagination |
| `GET /v1/jobs/{id}` | Owned job status and progress |
| `POST /v1/jobs/{id}/cancel` | Cancel an owned active job |
| `POST /v1/jobs/{id}/retry` | Retry an owned failed or interrupted job |
| `GET /v1/jobs/{id}/output` | Download an owned completed PDF |
| `DELETE /v1/jobs/{id}` | Remove an owned terminal job and workspace |
| `GET /v1/metrics` | Process-local counters and queue depth |
| `GET /v1/queue` | Queue depth and capacity |
| `GET /v1/dead-letters` | Failed jobs requiring operator review |
| `POST /v1/dead-letters/{jobId}/replay` | Controlled dead-letter replay |
| `GET /v1/admin/live` | Authenticated server-sent live dashboard snapshots |
| `GET /v1/admin/search?q=...` | Bounded universal operator search |
| `GET /v1/admin/usage` | All API-key usage summaries and aggregate totals |
| `GET /v1/admin/api-keys/{id}/usage` | Usage summary for one API key |
| `GET /v1/admin/audit-events` | Recent safe operator audit events |

Watch-folder logs are split into two files:

- `.log`: concise operator events, timings, sizes, retries, and error reasons.
- `.jsonl`: detailed machine-readable events including CPU, GC, threads, worker metrics, gate wait, and exception information.

The detailed log is intended for analysis tools. Retain and rotate it according to the storage policy for the host running the service.

## Reliability model

Asynchronous jobs use a durable LiteDB data file for single-server persistence. Jobs are claimed with leases, renewed while running, and made available again after an expired lease. Retryable failures use bounded backoff. Terminal failures are stored as dead letters with attempt history.

Job lifecycle operations are available through `GET /v1/jobs`, `GET /v1/jobs/{id}`, `POST /v1/jobs/{id}/cancel`, `POST /v1/jobs/{id}/retry`, `GET /v1/jobs/{id}/output`, and `DELETE /v1/jobs/{id}`. Listing supports state, source, format, creation-time, and opaque cursor filters. Responses include progress, active worker and lane, queue wait, processing time, retry count, input/output bytes, compression percentage, and safe output metadata without exposing server paths. See [Jobs and reliability](docs/jobs.md).

The service uses short-lived conversion workers so native memory is reclaimed when a worker exits. Worker timeouts, process-tree termination, image limits, disk limits, and conversion gates are independent protections.

This is durable for one service instance. Horizontal scaling requires a shared queue or database design and separate failure testing; it is not currently advertised as a clustered high-availability product.


## Windows self-contained publish

The supported Windows installation is a self-contained `win-x64` deployment containing the API, isolated conversion worker, console assets, OpenAPI contract, and settings. LibreOffice is intentionally installed separately so its version, fonts, and licensing remain under the operator's control.

### Install from a clean Windows machine

Open **Windows PowerShell as Administrator**. Install the current LibreOffice Windows package first, then confirm that one of these files exists:

```text
C:\Program Files\LibreOffice\program\soffice.exe
C:\Program Files (x86)\LibreOffice\program\soffice.exe
```

LibreOffice must be able to run headless and the Windows service identity must have write access to its private temporary profile. The service does not use an interactive LibreOffice window or the user's existing profile.

From a cloned repository, publish the service:

```powershell
.\tools\publish-windows.ps1
```

By default, the output is written to `artifacts\InactivePDF`. Copy that directory to the target machine if publishing elsewhere. Then run the complete setup from an elevated PowerShell session:

```powershell
.\tools\setup-windows.ps1 `
  -InstallPath 'C:\InactivePDF\app' `
  -DataRoot 'C:\ProgramData\InactivePDF' `
  -WatchRoot 'C:\InactivePDF\WatchFolders\Default' `
  -LibreOfficePath 'C:\Program Files\LibreOffice\program\soffice.exe' `
  -ServiceUrl 'http://127.0.0.1:5080'
```

The setup script validates the API, worker, settings, console, OpenAPI assets, LibreOffice executable, data paths, and watch-folder paths before it registers the service. It configures the worker and LibreOffice paths at machine scope, creates `Input`, `Processing`, `Output`, `Originals`, `Errors`, and `Logs`, configures automatic restart after failure, and checks `/ready` before returning success. It automatically searches both `Program Files` locations when `-LibreOfficePath` is omitted.

The default service identity is `LocalSystem` for a simple local installation. For a locked-down installation, pass a dedicated service credential:

```powershell
$credential = Get-Credential 'DOMAIN\InactivePDFService'
.\tools\setup-windows.ps1 -InstallPath 'C:\InactivePDF\app' -Credential $credential
```

The setup grants that identity Modify access to the configured install, data, and watch roots. Keep the service URL bound to `127.0.0.1` unless a reverse proxy, TLS, firewall rule, and bearer authentication are deliberately configured. Set `INACTIVEPDF_API_TOKEN` through the service manager's protected environment before exposing the API remotely.

### Verify the Windows installation

Use a real `.docx`, `.xlsx`, `.ods`, `.pptx`, `.html`, `.csv`, or other supported fixture. The verification command tests LibreOffice directly, then tests the service API, asynchronous polling, PDF download, watch-folder pickup, output routing, and source archival:

```powershell
.\tools\verify-windows-install.ps1 `
  -FixturePath 'C:\InactivePDF\test-fixtures\sample.docx' `
  -WatchRoot 'C:\InactivePDF\WatchFolders\Default' `
  -LibreOfficePath 'C:\Program Files\LibreOffice\program\soffice.exe'
```

Expected results are five `PASS` lines and `Windows installation verification passed.`. If direct LibreOffice conversion passes but API conversion fails, inspect the worker path and service-account permissions. If API conversion passes but watch-folder conversion does not, check the configured `INACTIVEPDF_WATCH_ROOT` and the service identity's access. `/ready` returns `503` when the LibreOffice executable or isolated worker is unavailable.

### Upgrade, rollback, and uninstall

To upgrade, publish the new version to a new directory, run setup against that directory, and run the verification command. The setup preserves the existing data and watch-folder roots. If verification fails, run setup against the previous publish directory and verify again.

Remove only the service and machine-level configuration while preserving converted documents:

```powershell
.\tools\uninstall-windows-service.ps1
```

Permanent removal of conversion data requires an explicit switch and exact roots:

```powershell
.\tools\uninstall-windows-service.ps1 -RemoveData
```

The uninstall script refuses root/reparse-point paths and never deletes data by default. Existing input, output, originals, errors, and logs are preserved unless `-RemoveData` is supplied.

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
