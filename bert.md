# BERT Explorer Notes

Last updated: March 12, 2026

## What this adds

This repo now has a standalone BERT inspection page at:

- `/bert.html`

It is modeled after the existing `trace.html` pattern: a direct static page under `VibeRails/wwwroot` backed by minimal API endpoints.

## Files added or changed

- `VibeRails/wwwroot/bert.html`
- `VibeRails/Routes/BertRoutes.cs`
- `VibeRails/Services/Bert/IBertExplorerService.cs`
- `VibeRails/Services/Bert/BertExplorerService.cs`
- `VibeRails/DTOs/ResponseRecords.cs`
- `VibeRails/Routes/Routes.cs`
- `VibeRails/MapRegisterServices.cs`

## Endpoints

- `GET /api/v1/bert/status`
- `GET /api/v1/bert/captures?skip=0&take=50`
- `GET /api/v1/bert/captures/{documentId}`
- `POST /api/v1/bert/search`

Example search body:

```json
{
  "query": "terminal multitab",
  "mode": "semantic",
  "topK": 10
}
```

Valid modes:

- `semantic`
- `text`

## Where the data lives locally

Default paths resolved by the explorer:

- BERT data directory: `C:\Users\robst\.vibe_rails\vector\bert`
- BERT SQLite DB: `C:\Users\robst\.vibe_rails\vector\bert\bert_input_vectors.db`
- VibeRails state DB: `C:\Users\robst\.vibe_rails\state.db`
- Model directory: usually `AppContext.BaseDirectory\Models` in build output, falling back to `C:\Users\robst\.vibe_rails\models\bert`

The page uses the BERT DB for stored capture text and enriches metadata from `state.db`:

- `UserInputs`
- `Sessions`
- `InputFileChanges`

## Data shape

BERT documents are stored in `bert_input_documents` with ids shaped like:

- `{sessionId}:{userInputId}`

The raw stored text looks like:

- `session_id: ...`
- `user_input_id: ...`
- `git_commit: ...`
- `user_text:`
- `file_changes:`

That means the page can still show raw capture text even if the metadata join back to `state.db` fails.

## What I observed locally on March 12, 2026

Using the local DB on this machine:

- `bert_input_documents` existed
- document count was `173`
- the newest document ids looked like `27428744-8c4d-4c2a-bf80-2c6cd5bb4d57:1129`

That confirms capture data is already being written locally.

## How the page behaves

- Left column shows status and the most recent captures.
- Top query bar supports semantic or text search.
- Search results auto-load the first hit into the detail panel.
- Detail view shows:
  - session metadata
  - prompt text
  - structured file changes
  - raw stored capture payload

## Semantic search requirements

Semantic mode needs all of these:

- BERT capture enabled in `appsettings.json`
- local BERT DB present
- `model.onnx` present
- `vocab.txt` present
- `vec0` native extension available in the app runtime

If semantic mode is unavailable, the page falls back to text mode only.

## Verification done

Build run completed successfully:

```powershell
dotnet build VibeRails\VibeRails.csproj
```

## Useful next steps if this needs more work

- Add pagination controls to recent captures instead of the fixed `take=50` load.
- Add filters for CLI, environment, session id, or date range.
- Add highlighting for text-mode substring hits.
- If we want richer diff rendering, pull `DiffContent` into a separate endpoint instead of only showing the raw stored payload.
