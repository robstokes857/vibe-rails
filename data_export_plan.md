# Data Export Plan

## Goal

Add an **Export Data** action to the Settings page. It will:

1. Be available only when a non-whitespace API key has been saved.
2. Create a consistent `copy_state.db` snapshot of the live `state.db`.
3. Brotli-compress the snapshot at maximum quality.
4. Compute the SHA-256 hash of the exact compressed bytes.
5. Delete the uncompressed snapshot.
6. Upload the compressed database and hash to a configurable endpoint.
7. Remove all remaining temporary artifacts after success, failure, or cancellation.

Only `state.db` is in scope. Separate databases such as `proxy_exchanges.db` and the BERT vector database are intentionally excluded.

## Key decisions

### A saved API key controls availability

Treat “API key is filled in” as “a non-whitespace API key has been saved.” An unsaved key is not yet available to the backend.

The Settings API already returns an empty string when no key exists and a masked, non-empty value when one is configured. The frontend can use that response without exposing the real key. The backend must still validate `ParserConfigs.GetApiKey()` before performing any work.

If necessary, update the existing settings masking/presence check to use `string.IsNullOrWhiteSpace` so an all-whitespace key does not enable the export action.

### Use a SQLite snapshot, not `File.Copy`

`state.db` runs in SQLite WAL mode and can be written by multiple connections and processes. Copying only the main file can omit committed pages that still reside in `state.db-wal`.

Use `SqliteConnection.BackupDatabase()` to create the requested `copy_state.db`. This produces a transactionally consistent, standalone snapshot without stopping VibeRails or forcing a checkpoint.

### Stream large files

The state database can be large. Snapshot compression, hashing, and upload must use file streams rather than reading the complete database into memory.

## Implementation plan

### 1. Add the Settings UI

Update `VibeRails/wwwroot/index.html`:

- Add a hidden wrapper immediately below the API-key field.
- Add a `type="button"` button labeled exactly **Export Data**.
- Keep the button outside the Settings form's submit behavior.
- Existing Bootstrap classes should be sufficient; add custom CSS only if required by the finished layout.

Update `VibeRails/wwwroot/js/modules/settings-controller.js`:

- Show the wrapper only when the server reports a saved API key.
- Keep it hidden for a newly entered but unsaved key.
- Hide or disable it while the API-key field differs from its saved/masked value so the export cannot unexpectedly use an older saved key.
- Re-evaluate visibility after a successful Settings save.
- On click, call:

  ```text
  POST /api/v1/settings/export-data
  ```

- Send no API key or database content from the browser. The local backend owns both.
- Disable the button and change its text to `Exporting…` while the request runs.
- Use `{ showLoading: false }` so the button communicates progress instead of displaying a long-running global overlay.
- Display the structured backend result through the existing toast system.
- Restore the button state in `finally`.

### 2. Add the placeholder export URL

Add one search-friendly configuration value under the `VibeRails` section of `VibeRails/appsettings.json`:

```json
"ExportUrl": "EXPORT_URL_HERE"
```

Read the full configured value as the destination endpoint. Do not append an implicit path.

If the value is missing, blank, invalid, or still exactly `EXPORT_URL_HERE`, return a clear `not_configured` result before creating a snapshot or making a network request.

### 3. Add the backend service

Create a dedicated export service under `VibeRails/Services/Integrations/VibeCodeRemote/`:

```text
IDataExportService
DataExportService
```

Keep it separate from `DebugBundleService`: debug bundles contain selected session data and add encryption, while this feature exports the complete `state.db` using the explicitly requested format.

Register the service and its typed `HttpClient` in `VibeRails/MapRegisterServices.cs`. Configure a deliberate upload timeout suitable for a large database and propagate the local request's cancellation token.

Add a process-wide single-export gate. If another export is already running, return a structured `busy` result. Unique temporary directories are still required for defensive isolation and reliable cleanup.

### 4. Create a safe database snapshot

Inside `DataExportService.ExportAsync`:

1. Validate the saved API key.
2. Validate the configured export URL and placeholder.
3. Resolve the source with `ParserConfigs.GetStatePath()`.
4. Verify the source database exists.
5. Create a unique, owner-only temporary directory.
6. Use the exact temporary filenames:

   ```text
   copy_state.db
   copy_state.db.br
   ```

7. Apply `PrivateFilePermissions` to the temporary directory and files where supported.
8. Open source and destination `SqliteConnection` instances with `Pooling=false`.
9. Open the source read-only and call:

   ```csharp
   sourceConnection.BackupDatabase(destinationConnection);
   ```

10. Dispose both database connections before compression or deletion so Windows file handles are released.

Do not run `wal_checkpoint`, stop database writers, or use `File.Copy`.

### 5. Compress at maximum Brotli quality

Stream `copy_state.db` into `copy_state.db.br` with:

```csharp
new BrotliCompressionOptions { Quality = 11 }
```

Pass the options to `BrotliStream` and use asynchronous stream copying with the request cancellation token. Dispose the Brotli stream before hashing so all compressed bytes and final framing have been flushed.

### 6. Hash and remove the database copy

Open `copy_state.db.br` for reading and compute:

```csharp
await SHA256.HashDataAsync(compressedStream, cancellationToken)
```

Format it with `Convert.ToHexStringLower`, producing a 64-character lowercase hexadecimal value.

After the hash is complete, delete `copy_state.db` and any defensive `copy_state.db-wal` or `copy_state.db-shm` sidecars. Keep `copy_state.db.br` only until the upload finishes.

### 7. Upload the compressed bytes

Use this external request contract:

```http
POST EXPORT_URL_HERE
X-Api-Key: <saved API key>
X-Content-SHA256: <lowercase SHA-256 of the compressed body>
Content-Type: application/octet-stream
Content-Disposition: attachment; filename="copy_state.db.br"

<raw Brotli-compressed copy_state.db.br bytes>
```

Upload `copy_state.db.br` using `StreamContent`.

Do not set `Content-Encoding: br`. Some server middleware automatically decompresses content-encoded requests, which would make it ambiguous whether the reported SHA-256 covers the transported bytes or the decompressed database. The `.br` filename and documented contract identify the body format without that ambiguity.

Receiver behavior:

- Read and hash the raw request body before decompression.
- Compare that hash with `X-Content-SHA256`.
- Reject a mismatch.
- Brotli-decompress only after validating the hash.
- Treat the API key as coming from `X-Api-Key`.

Client behavior:

- Treat any 2xx response as success.
- Map upstream 401/403 to an `invalid_api_key` result rather than returning a local HTTP 401.
- Map other non-2xx responses and transport failures to `upload_failed`.
- Never log or return the API key or the upstream response body.

The final endpoint should use HTTPS because the exported database is sensitive and SHA-256 provides integrity, not confidentiality.

### 8. Add the local route and response DTO

Add `DataExportRoutes.Map(app)` and register it in `VibeRails/Routes/Routes.cs`.

Expose:

```text
POST /api/v1/settings/export-data
```

The route accepts no request body and receives `RequestAborted` as its cancellation token. Existing cookie and per-tab authentication middleware will protect the route.

Return HTTP 200 with a structured domain result so the SPA can display useful errors without confusing an upstream API-key rejection with the local server's authentication flow:

```json
{
  "success": true,
  "status": "ok",
  "message": "Data exported successfully.",
  "sha256": "lowercase-64-character-hash"
}
```

Expected statuses:

- `ok`
- `no_api_key`
- `not_configured`
- `busy`
- `invalid_api_key`
- `upload_failed`
- `failed`

Add the response DTO to `VibeRails/DTOs/ResponseRecords.cs` and register it with the existing Native AOT JSON source-generation context.

### 9. Guarantee cleanup

Use a top-level `try/finally` around all temporary artifacts. Cleanup must run after:

- Successful upload
- Snapshot failure
- Compression failure
- Hashing failure
- Upstream non-success status
- Network exception
- Request cancellation

Best-effort cleanup should remove:

```text
copy_state.db
copy_state.db-wal
copy_state.db-shm
copy_state.db.br
the unique temporary directory
```

Cleanup failures should produce a warning without replacing the primary export result.

## Test plan

### Backend service tests

- Missing or whitespace API key returns `no_api_key`, creates no snapshot, and makes no HTTP request.
- `EXPORT_URL_HERE`, a blank URL, or an invalid URL returns `not_configured` without expensive work.
- Missing `state.db` returns a clear failure.
- A committed row remaining in a live WAL file is present in the exported snapshot. Keep the writer open with WAL auto-checkpoint disabled to ensure this specifically guards against regression to `File.Copy`.
- Capture the uploaded body, Brotli-decompress it, open it as SQLite, and verify:

  ```sql
  PRAGMA integrity_check;
  ```

  returns `ok`.

- Assert Brotli round-trip correctness.
- Recompute SHA-256 over the captured compressed request body and compare it with `X-Content-SHA256`.
- Assert the URL, `X-Api-Key`, content type, content disposition, and filename.
- Assert upstream 401/403 maps to `invalid_api_key`.
- Assert other non-2xx responses, transport exceptions, and cancellation map correctly.
- Assert success and every failure path remove the database copy, compressed file, sidecars, and temporary directory.
- Assert simultaneous exports serialize or return `busy` without filename collisions.
- Restore all static `ParserConfigs` values changed by tests and use the repository's process-environment isolation collection where required.

### Route and serialization tests

- Confirm the local route returns the expected structured response for every service status.
- Confirm the response DTO is handled by the Native AOT JSON source-generation context.
- Confirm an upstream API-key rejection never becomes a local HTTP 401.

### Settings UI tests

- Empty API-key response keeps the button hidden.
- Whitespace-only key does not enable it.
- Masked configured key shows the button.
- Newly typed but unsaved key does not show the button.
- Editing an existing masked key hides or disables the button until Settings are saved.
- Clicking sends exactly one local POST.
- The button is disabled and displays `Exporting…` while pending.
- Success and failure show the correct toast.
- The button is restored after success, failure, or thrown request error.

## Verification commands

After implementation:

```powershell
dotnet build VibeRails\VibeRails.csproj --artifacts-path .codex-test-artifacts
dotnet test Tests\Tests.csproj --artifacts-path .codex-test-artifacts
```

Run the relevant frontend/Playwright tests as required by `UITests/AGENTS.md`, then manually verify the Settings flow with both an empty and configured API key.
