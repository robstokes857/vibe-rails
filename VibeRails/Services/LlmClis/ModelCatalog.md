# Remote model names

`ModelCatalogClient` reads the public, versioned catalog from the fixed HTTPS
endpoint `https://viberails.ai/public/api/v1/models`. No user account, credentials,
cookies or redirect are involved. Reads have a five-second total timeout and a
1 MiB response limit. A malformed/version-mismatched snapshot never replaces a
good one. Known CLI names and safe model argument shapes remain locally validated.

The service is a singleton. `IMemoryCache` fronts `IGlobalCache` at
`llm-model-catalog:v1`; one coalesced request refreshes at most hourly per active
backend. The persistent refresh deadline avoids another request on restart.
Failures retain the last good snapshot and retry after five minutes, including
across restarts. Concurrent backends can each refresh once when the shared entry
expires; this is intentionally not a machine-wide request lock. No new database
tables or diagnostic data are needed.

The ordinary authenticated `GET /api/v1/llm-picker/models` endpoint serves this
catalog to the UI. The existing session/tab middleware still applies. It returns
503 only when no good snapshot exists; the UI then keeps its shipped list.
Board, Agent and legacy environment editors share `llm-model-catalog.js` and
coalesce UI reads for one minute. Updates preserve the user's current selection.
An empty managed list intentionally offers only Default; saved names absent from
the list stay selectable as custom values. No background polling is added.

This is a names-only contract. Model-specific speed/effort rules, new option
vocabularies and fixed pseudo-CLI model pins still require a code release. Valid
new model IDs already pass `BaseLlmOptionsBuilder` as discrete argv without a
catalog-membership check, including IDs received from a newer hosted Board.
Unsupported portable options keep the existing sync validation behavior. No
untrusted model text is interpreted by a shell.

The hosted API returns `{ version: 1, revision, models: [{ cli, value, label }] }`.
VibeTies owns the source and CRUD, publishes committed revisions to Valkey, and
the hosted endpoint has a 30-second memory / five-minute shared cache. Old
desktop releases continue using their bundled list. Release the API and admin
catalog first when convenient; this client works with fallback during rollout.
