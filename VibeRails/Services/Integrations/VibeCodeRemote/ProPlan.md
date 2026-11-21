# Pro plan denials

Four hosted feature families need the Pro plan: `remote_boards`, `remote_terminals`,
`session_sharing` and `card_sharing`. Everything local stays free, and so do sign-in and device
linking, API keys, code signing, uploads, backups, summaries, review requests and push. The hosted
service is the only authority. The desktop reads the plan to stop its own loops and to explain a
refusal; it never unlocks anything, and it never blocks on a plan it could not read.

## Wire contract

**Entitlements.** `GET {origin}/api/v1/account/entitlements` with header `X-Api-Key`, where
`{origin}` is the configured frontend origin. A 200 body is:

```json
{ "version": 1, "plan": "free", "access": "free", "enforced": true,
  "features": { "remote_boards": false, "remote_terminals": false, "session_sharing": false, "card_sharing": false },
  "billingPath": "/Billing", "refreshAfterSeconds": 300 }
```

`access` is `free`, `pro` or `inactive` (a subscription exists but is unpaid or paused). While the
service is not enforcing, `enforced` is false and every feature is true. 401 means the key is
invalid; 404 means an older server without the route.

**Denial.** A paid call the plan refuses answers HTTP 403 with header
`X-VibeRails-Denial: pro_required` and a JSON body:

```json
{ "error": "...", "code": "pro_required", "feature": "remote_boards",
  "payer": "account", "upgradePath": "/Billing?feature=remote-boards" }
```

`payer` is `account`, or `board_owner` when the caller is an invited member of a Board whose owner
is not Pro: that caller is never asked to upgrade. A refused `/ws/v1/terminal` handshake is the
same 403 and header (its body cannot be read through `ClientWebSocket`). A live relay socket whose
owner loses Pro is closed with code 1008 and reason exactly `Vibe Rails Pro required`. Listing,
revoking, deleting, the `/remote-access` safety status routes and terminal de-registration are
never denied this way.

## What the desktop does

`AccountEntitlementsClient` (`IAccountEntitlements`) makes the read: one request, 10-second
timeout, 16 KiB cap, no redirects, no retries. The answer is cached in memory per
(origin, account key) for `refreshAfterSeconds` (default 300, clamped to 30 to 3600); only a hash
of the pair is kept, so a changed key or origin is simply another entry. States:

| State | When | Features |
| --- | --- | --- |
| `signed_out` | no account key is configured; nothing is sent | all false |
| `pro`, `free`, `inactive` | the server's `access` | the server's `features` |
| `unknown` | offline, timeout, 401, 404, 429, 5xx, oversized, a `version` other than 1, or malformed (a missing feature key included) | all true |

`unknown` is asked again after 60 seconds and never blocks anything. `AllowsAsync(feature)` is
false only for `signed_out` or a known account whose feature is off. `ProDenial` reads a 403: the
header, plus a bounded read of the body (4 KiB; session sharing keeps its existing 16 KiB error
bound) for `code`, `feature` and `payer`. A missing or unrecognised
feature is the caller's own, and a missing payer is `account`. Remote prose and `upgradePath` are
never used: every message and link is the desktop's own, and the billing link is always
`{origin}/Billing`. A denial with `payer: "account"` calls `NoteDenied(feature)`, which turns that
feature off in the cached answer for at least five minutes; an account that was `pro` or `unknown`
is then reported as `free`. `board_owner` denials never touch the account's own answer.

Each terminal tab is its own process with its own cache. A denial noted in a tab reaches the
dashboard when the root's own answer is next read from the server.

## Local routes

Root backend only, both local credentials, `no-store`. Both always answer 200 with the same body;
an upstream failure is the `unknown` state, never a local error status.

- `GET /api/v1/account/entitlements`
- `POST /api/v1/account/entitlements/refresh` forces a server read (the user came back from Billing).

```json
{ "state": "free", "enforced": true,
  "features": { "remote_boards": false, "remote_terminals": false, "session_sharing": false, "card_sharing": false },
  "billingUrl": "https://viberails.ai/Billing", "checkedUtc": "2026-11-20T12:00:00Z" }
```

`checkedUtc` is null for `signed_out` and `unknown`.

A Board route whose hosted call is refused answers 403 with
`{ "error", "code": "pro_required", "feature": "remote_boards", "payer": "account" | "board_owner" }`.
Other Board sync failures keep `{ "error" }` and their status. `GET .../boards/{boardId}/sync`
adds `pausedCode` (`pro_required` or null) and `pausedPayer`, derived from the stored reason.
Session and card sharing stay HTTP 200 domain results: session sharing has status `pro_required`
with `feature: "session_sharing"`, and a card share result has `code: "pro_required"` with
`feature: "card_sharing"`. Any other 403 keeps its existing account-key wording.

## No-loop rules

- **Board sync tick.** Asked once a board is due. When the account's plan says no, the pause
  reason is written to each owned, published link's `LastError` (once: the stored text is compared)
  and the tick makes no request. A denial in the middle of a sweep stops the sweep. Nothing is
  unpublished or deleted, and queued entries stay queued; the first successful sync clears the reason.
- **A Board whose owner is not Pro.** Only that link is paused, with its own reason. The scheduler
  asks again about every 15 minutes (kept in memory); a manual Sync now always asks.
- **Remote Start work poll.** Skipped while the plan says no. A `pro_required` answer is logged
  once at Information, never as a Warning every ten seconds.
- **Card share refresh.** A due sweep sends nothing while the plan says no, and a refusal ends the
  sweep at that publication.
- **Remote terminals.** A session is not registered and no relay socket is opened while the plan
  says no. A refused registration, a refused handshake and the 1008 close are noted and never
  retried for that terminal. Nothing is written into the PTY; the local terminal keeps running.
- **The account safety pause** (`RemoteSafety.md`) is checked first and keeps its own codes and
  wording wherever both could apply.
- Actions a person starts (publish, Sync now, create a link) are always sent, so an upgrade works
  at once without waiting for the cache.
