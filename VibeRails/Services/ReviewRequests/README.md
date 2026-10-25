# Review requests before a commit (VB-50)

A review step before the commit: the developer asks a named person to look at the staged changes
and the AI sessions that produced them, and to sign off. Unlike a pull request, the reviewer sees
the whole conversation, not just the diff. This document is the component contract for the parts
that exist (slice A, 2026-10-23: the desktop core; slice C, 2026-10-24: sending to viberails.ai and
collecting decisions; slice D, 2026-10-24: the reviewer's desktop and the approval popup) and records
the design decisions for the rest.

## What exists

- **Capture.** `ReviewBundleCapture` turns the staged snapshot (`IGitStagedSnapshotProvider`, the
  same reader the commit hook uses) into a `ReviewBundle`: the repository name, the staged tree id
  and its base, every changed file with its change kind, line count and base/staged text, the
  attached sessions, the reviewers, an optional Board card key and note. Text is bounded to 256 KiB
  per file and 4 MiB per bundle, in path order; a file over either bound is listed without text and
  says why (`binary`, `oversized`, `bundle-limit`), so the reviewer knows what they are not seeing.
  The hook's snapshot carries no baseline for a deleted path and flags it binary, so deleted files
  are read from the frozen base tree (`GitStagedSnapshotProvider.TryReadTreeTextAsync`) under the
  same bounds: a deleted text file shows what was removed; a deleted binary stays `binary`; a
  deleted text file over the per-file bound is `oversized` and is not read at all.
- **Ledger.** `IReviewRequestStore` / `ReviewRequestStore` in state.db, additive component
  `review-requests/1`: `ReviewRequests` (id, project, scope, tree id, base commit, card key, status,
  bundle JSON, timestamps) and `ReviewDecisions` (reviewer, decision, comment, the verified trailer
  fields, decided and verified times). Statuses: `open`, `approved`, `rejected`, `cancelled`. Older
  versions ignore both tables.
- **Service.** `ReviewRequestService` creates a request from the project's staged index (at least one
  reviewer email, at most ten; at most ten attached session ids, each looked up through
  `ISessionStore` and named), lists and reads requests for one project, cancels and deletes them, and
  records decisions. A rejection names a listed reviewer and carries a comment. **An approval is the
  reviewer's `vibe-signature:` line**: it must cover this request's tree, viberails.ai must confirm
  the signature and name the signer's verified email (`ICommitSignatureVerifier`, the hook's
  verifier), and that email must be one of the request's reviewers. Only then is it stored, with
  the verified time. The store records the decision and recomputes the status in one `BEGIN
  IMMEDIATE` transaction (`RecordDecisionAsync`): approved by any verified approval, otherwise
  rejected by any rejection, otherwise open; a request that was cancelled while an approval was out
  for verification refuses the decision (`not_open`) and stores nothing, so a cancelled request can
  never be revived into a hook-eligible approval.
- **Commit hook.** `VcaHookRunner` (commit-msg) asks `IStoredCommitApprovalSource` for a verified
  approval over the staged tree from one of the rule's approvers (any verified signer for
  `Human verified`) before it offers the signing popup. A hit is appended to the message with the
  popup's own trailer writer and the commit is rechecked, which verifies it with viberails.ai like
  any other trailer. So an agent's commit passes once a person has approved exactly that tree, and
  never without one. `ReviewRequestApprovalSource` resolves the store only when a signature finding
  is pending (building the hook runner opens no database), reads the ledger within three seconds
  and is quiet on any failure: the ledger is a convenience, never a gate. The ledger pass runs once,
  before and outside the three popup rounds, and appends every fitting approval (one per pending
  finding, each signature once) followed by a single recheck, so four rules with four approvals
  pass and an unreachable viberails.ai cannot loop. Both hook test
  harnesses (`VcaRegressionRepository`, `VcaHookEndToEndTests`) replace the store with the same
  per-repository file they give the job store, so no hook test opens the user's state.db.
- **Routes.** `/api/v1/review-requests` on the active root only, both local credentials, no-store:
  `GET` list, `POST` create (16 KiB body: `{ scope?, reviewers, sessions?, cardKey?, note? }`, 201),
  `GET /{id}` detail with the bundle, `POST /{id}/decisions` (32 KiB body:
  `{ decision, trailer?, comment?, reviewer? }`), `POST /{id}/cancel` (also cancels the hosted copy
  of a sent request), `DELETE /{id}` (204; a sent request's hosted copy is cancelled first, or the
  delete is refused),
  `GET /{id}/disclosure` (what a send would transfer; no outbound call) and `POST /{id}/send`
  (1 KiB body: `{ confirmed }`, answers `{ request, message }`).
  Refusals are `{ error, code }` with 400 `invalid_request` / `confirmation_required`, 404
  `not_found` / `session_not_found`, 409 `nothing_staged` / `not_a_repository` / `tree_mismatch` /
  `signature_rejected` / `not_a_reviewer` / `not_open` / `no_api_key` / `remote_pending`, 503
  `verification_unavailable`, and 502 with the transport's code for a send that failed
  (`network_error`, `timeout`, `server_update_required`, `invalid_api_key`, `permission_denied`,
  `rate_limited`, `server_unavailable`, `invalid_response`, `invalid_request`, `request_conflict`,
  `too_large`, `unexpected_redirect`, `server_error`). The project is derived server-side; a
  request made for another project is not found.
- **Transport (slice C).** `ReviewRequestTransport` sends a stored request's bundle bytes as
  `{ requestId, bundle }` to fixed `https://viberails.ai/api/v1/review-requests` (header
  `X-Api-Key` only, no redirects or cookies, 90 s to send, 30 s to poll or cancel, 1 MiB answers),
  polls `GET /{reference}/decisions` and cancels with `DELETE /{reference}`. Every answer is
  validated before use: this request's id and tree, a `rvw_` reference, a known status, a
  plausible expiry, at most 50 well-formed decisions (distinct ids, listed kinds, trailer grammar,
  comment bound). Failures are fixed local wording with a stable code and never carry remote text.
  There is no capability probe: a host without slice B answers the create 404/405/501
  (`server_update_required`), and nothing is recorded or queued until the host has confirmed the
  request by reference, so the first POST is the probe and the client fails closed. The hosted
  contract is `VibeRails-Front/Services/ReviewRequests/README.md`.
- **Send is explicit.** `ReviewRequestSender.DisclosureAsync` lists what leaves the machine: the
  destination, the audience (`email`: always listed people, never a public link), every file with
  its change kind, line count and omission reason, the text bytes, tree and base ids, each session
  by name with the sharing disclosure and whether it is still live, the reviewers, card key, note
  and the expiry (one month after the send). `SendAsync` refuses without `{ "confirmed": true }`
  (`confirmation_required`), for a request that is not open (`not_open`) and without an account key
  (`no_api_key`), and transfers nothing in those cases. A confirmed send records `SentUTC`, the
  reference, the sending key's SHA-256 fingerprint and the hosted expiry on the ledger row
  (`review-requests/2`, additive nullable columns), brings the next decision check forward to one
  interval, queues each attached session through `ISessionArchiveReader.EnsureSessionShareUploadAsync`
  under that fingerprint (the existing `SessionShareUploads` queue and `SessionDataDrainJob`; a live
  session uploads after it ends; no new upload path), records `SessionsQueuedUTC`
  (`review-requests/3`), publishes `review_request_updated`, and answers the stored request with a
  fixed message. A second send transfers nothing; a send interrupted between the hosted confirmation
  and the queue is finished by the next send call or by the poller, under the key that sent it,
  without posting the bundle again. A local cancel also cancels the hosted copy when the current key
  is the one that sent it; otherwise the poller retries the cancellation. **Delete** cancels the
  hosted copy first: while it cannot (host unreachable, or sent with another account's key) the
  delete is refused with 409 `remote_pending` and the request stays, cancelled, because its row is
  the only thing that can still revoke the hosted copy; an expired or already-cancelled hosted copy
  needs no host. A delete that races a send makes the send cancel its own hosted copy.
- **Poll.** `ReviewRequestPoller.PollDueAsync` runs from `BoardSyncScheduler` after the card-share
  refresh (the lease-holding root only) and paces itself through `ReviewRequestPollState`
  (`.review-request-poll.json` beside state.db: account fingerprint, next due time and backoff,
  never a key or reference): every 15 minutes while a sent request is open, doubling to a two-hour
  ceiling while none is or the host is unreachable, never on the 60 s tick. A request that already
  has a decision is still polled until its expiry, at the backoff cadence, because the host keeps
  taking decisions until it is cancelled or expires: a later approval after a rejection, or another
  reviewer's approval for a second rule, still reaches the ledger. Only requests sent with
  the current key's fingerprint are polled; an expired request is polled for seven more days so a
  late approval is still collected; a sent request whose sessions were never queued has them
  queued first. Each new hosted decision (its hosted id is stored on the
  decision row, so each lands once) goes through `RecordDecisionAsync` with that id: an approval is
  re-verified with viberails.ai and matched to a listed reviewer, a rejection lands with its
  comment. A decision the ledger refuses (`tree_mismatch`, `signature_rejected`, `not_a_reviewer`)
  is remembered for the process and not re-verified every pass; an unavailable verifier leaves it
  pending. Landed decisions publish `review_request_updated` (id, status, card key, sent) so the
  card UI (slice E) can refresh.
- **Reviewer desktop (slice D).** "Reviews requested of you" (view `review-inbox`, reached from the
  PR Reviews header, and the VS Code view of the same name) lists the requests viberails.ai holds for
  the account's verified email. `ReviewInboxService` fetches them with the account key from
  `GET https://viberails.ai/api/v1/review-requests/inbox` on the Board sync cadence
  (`ReviewInboxState`, `.review-inbox.json` beside state.db: 15 minutes while a listed request is
  open, doubling to two hours otherwise, never the 60 s tick; "Check now" fetches at once, at most
  once per 10 s; a key change starts from an empty list) and serves the root-only routes
  `/api/v1/review-inbox` (`GET` list, `POST /refresh`, `POST /{reference}/reject` with `{ comment }`,
  `POST /{reference}/approve`). Reject posts `{ rejected, comment }` to the host's decision route
  under the reviewer's key. Approve only opens the popup:
  `vb --vca-hook sign-review --review <reference> --workdir <project> --console-window` through
  `VcaHookConsoleRespawn` (CREATE_NEW_CONSOLE; Windows, interactive desktop, not CI; refused with
  `popup_unavailable` otherwise), once per reference while it is open; its exit code is the only
  thing the service learns, and it publishes `review_inbox_updated` when anything changes.
- **The approval popup.** `ReviewSignaturePopup` (the `HumanSignaturePopup` family; both share
  `SigningKeyPrompt`) fetches the bundle itself with the account key, shows the repository and the
  local copy it will use, the requester, card and note (the requester's words, through
  `HumanSignaturePopup.Clean`), the sessions, every file with its change kind, counts and omission,
  the tree, the base and the hosted page (file counts are computed from the carried text, never
  taken from the requester's `changedLines`), then stops before any key prompt when the request is
  cancelled or expired, the account is not a listed reviewer, this account already approved it, or a
  file is listed without content ("cannot be approved"). Another reviewer's approval or rejection
  does not close it: viberails.ai keeps taking decisions (a rule may name several approvers, and an
  approval after someone else's rejection is still this reviewer's to give), so the popup, the page
  and the VS Code view only stop at cancelled or expired (Codex slice D review, F2). Otherwise
  `ReviewTreeRebuilder` proves the tree (next bullet) and
  the popup prints the verdict, lists the synced keys, reads the password with echo off, signs the
  tree with `SigningKeyService.SignCommitTreeAsync` (the staged-tree statement, so nothing new is
  signable), confirms the signature with viberails.ai, warns when the key's account is not the saved
  one, and posts `{ approved, trailer }` to `POST /api/v1/review-requests/{reference}/decisions`.
  Exit code 0 means the approval was recorded; the window pauses either way so the verdict can be read.
- **The tree is proven on the reviewer's machine (decision 7).** `ReviewTreeRebuilder.RebuildAsync`
  runs in the reviewer's repository: the base commit must exist locally and own the bundle's base
  tree; a temporary index (`GIT_INDEX_FILE`) is loaded with `read-tree <baseTree>`; each listed
  file's staged text is hashed here from its exact UTF-8 bytes (capture decoded strict UTF-8, so the
  round trip is byte-exact, byte-order mark and CRLF included) and placed with
  `update-index --index-info`, a deleted path and a rename's source removed, a changed file keeping
  its base mode and a new file assumed `100644`; `write-tree --missing-ok` must equal `treeId`. No
  blob is written and the real index is never touched. The base is proven too, because the reviewer
  read a diff, not a tree. First the shape: every transition must be canonical
  (`ReviewTreeRebuilder.ShapeProblem`, shared by `ParseBundle`, the popup and the hosted contract):
  an added file has no base text and no previous path, a deleted file no staged text and no previous
  path, a modified file no previous path, a renamed or copied file a distinct previous path, no path
  twice and no rename source also listed (`inconsistent_change`). Then the base: a changed, renamed,
  copied or deleted file's source must exist in the base and its shown base text must hash to the
  base commit's blob there, and a file listed as added or the destination of a rename or copy must
  be absent from the base, or its overwritten content would never be shown (`base_mismatch`; a
  changed file shown without base text and not marked omitted is `base_text_missing`). Last the
  whole transition: `diff-tree --name-status --no-renames` between the base tree and the rebuilt
  tree must touch only listed paths and rename sources (`hidden_change`), because an added path
  that collides with a base directory or file makes `update-index` drop the conflicting entries
  without a word. Without these, `Before = After` over a real change, an added file carrying base
  text, a deleted file carrying staged text, a previous path pointing at another base file, a
  rename onto an existing file, or a path collision deleting unlisted files all read as "no text
  changes" on viberails.ai while the signature still covered the true tree (Codex slice D reviews,
  F1). Refusals name the reason: a file without content, an unknown change kind, an inconsistent
  transition, a missing base commit ("git fetch"), a base tree that is not the commit's, a base
  disagreement, a hidden change, or a tree mismatch, which a new executable also produces: the
  bundle carries no modes, and the reviewer cannot tell a mode bit from a hidden change, so neither
  is signed. `GitCli.RunAsync` gained `environment` and `standardInput` for this and is still the
  one git launch.

- **Request review from a card (slice E).** The saved card editor has a **Request review** section
  beside Share card (`board-card-request-review.js`, the `board-card-sharing.js` pattern). Opening
  it reads `GET /api/v1/review-requests/staged` (`ReviewRequestService.PreviewAsync`): what a
  request made now would capture, from the same staged capture as create but storing nothing: each
  file with its change kind, `+added −removed` (`ReviewLineDiff`, Myers edit distance over lines,
  null beyond 20,000 edits so the snapshot's single count is shown instead) and omission reason, the
  tree id, the text bytes, the `Signed Approval` rules of the staged `vc.rules.md` files whose path a
  staged file touches (resolved and matched as the hook does; their approvers prefill the reviewers
  unless the person already typed some), the fixed destination, audience and session disclosure.
  Sessions come from the card's Sessions rail (never Automation or review recordings; checked by
  default up to ten), the note is optional, and Create posts to the existing create route with the
  card key. Below, the project's requests (this card's first) show their status (`Open, not sent
  yet` / `Sent, awaiting decisions` / `Approved by x` / `Rejected by x: comment` / `Cancelled`), the
  approval trailer with Copy, Send… (reads the disclosure and shows it in `confirmDialog`; only a
  confirmed answer posts `{ confirmed: true }`; the backend's refusal wording, including
  `server_update_required`, is shown as is), Cancel and Delete (both through `confirmDialog`; a
  409 `remote_pending` refusal is shown). The list refreshes on `review_request_updated`. The card
  header carries this card's newest request as a badge (`Review approved by x` and so on) and the
  Commits rail marks a linked commit whose tree a reviewer approved (`GET
  /api/v1/review-requests/commit-approvals?sha=…`, at most 50 shas of 7 to 64 hex characters:
  one `git log --no-walk=unsorted --ignore-missing --format=%H %T` through `GitCli`, then the
  hook's `FindVerifiedApprovalsAsync` per distinct tree). The Rules page shows `APPROVED` with
  "Approved by x through a review request over the staged tree" on a deferred `Signed Approval`
  or `Human verified` finding when `IStoredCommitApprovalSource` (the root registers the hook's
  `ReviewRequestApprovalSource`) finds a verified approval over the staged tree (`git write-tree`)
  from one of the rule's approvers: `HookRoutes.WithStoredApprovalsAsync`, read-only, quiet when
  the ledger, the tree or a hit is missing.
- **MCP `request_review` (slice E).** `ReviewRequestTool` on both transports (not in the Board
  auto-grants): `reviewers` (1 to 10 emails), optional `note` and `cardKey`. It calls the root's
  create route with the inherited tool credentials (loopback only, no redirects, like
  `create_session_share_link`), attaches only the calling session, then reads the disclosure and
  answers with the request id, tree, files, sessions and exactly what a send would transfer. It
  never sends, decides or signs: the tool description and the answer both say that a person sends
  from the card after reading the disclosure, and that an older viberails.ai answers
  `server_update_required` with nothing transferred. An older root (404/405 on create) is reported
  as needing an update; the root's own refusals keep their code and wording; unparsed errors report
  only the status. The inherited credentials are a scoped tool contact, so
  `AgentCredentialService.Allows` admits it to exactly `POST /api/v1/review-requests` and
  `GET /{id}/disclosure`, and the routes refuse (403 `forbidden`) a contact that names any session
  but its own or reads a request that does not attach it (Codex slice E review, F1).
- **A send that races a delete (Codex slice E review, F2, slice C code).** When the host confirms a
  request whose row a concurrent Delete already removed, `SendAsync` first puts the row back with
  its own token (open, then marked sent with the reference, the sending key's fingerprint and the
  expiry, then cancelled; each step is recoverable if the process dies between them), and only then
  asks the host to cancel the copy, on the transport's own 30-second deadline, never the caller's
  token. A confirmed cancellation deletes the row again and answers 404 `not_found`; a failed or
  timed-out one leaves the row, prompts the poller and answers 409 `remote_pending`, so the poller's
  cancellation pass retries it and Delete waits for the host. A caller that disconnects mid-way
  cannot take the only revocation record with it, and is never told the copy was cancelled when it
  was not.

The decision route's only outbound call is the existing public signature verification, with a tree
id and no repository content. The send route and the poller are the only code that transfers a
bundle or reads hosted decisions; the security contract is
[API_SEC.md](../../../API_SEC.md#review-requests-sent-to-viberailsai-vb-50-slice-c).

## Design decisions (settled 2026-10-23)

1. **What is signed: the staged tree id**, with the existing statement
   `VibeRails human-verified commit v1\ntree <id>\n` (`CommitSignatureFormat`). A reviewer's
   approval is therefore byte-for-byte the trailer the local Git Guard popup writes, and the existing
   `Signed Approval` rule, verifier, popup rounds and committed-policy logic accept it unchanged. A
   local commit before push is the same case through its tree: `git commit --amend` keeps the tree,
   so the signature stays valid. Any staged change after the review invalidates it, which is right.
2. **VCA trigger: no new rule.** `Signed Approval('path', 'lead@example.com')` at WARN, COMMIT or
   STOP is the gate, bypassable like every hook. The ledger only places an approval that already
   exists; "show whether it was reviewed" is the request status on the card and the Rules page.
3. **Where the reviewer works: reads on viberails.ai, signs on their own VibeRails.** Private keys
   never leave the desktop, the dashboard Sign route and MCP refuse the commit statement by design,
   so the reviewer signs in a console popup of the same family as the commit popup. The reviewer
   needs a viberails.ai account with a verified email (the approver match) and a synced key.
4. **Reject** is a decision plus a comment. Line comments are a later card.
5. **Privacy.** Before anything is sent, the developer sees exactly what will leave the machine:
   each file with its counts (and which are listed without text), the tree id, each session by name
   with the session-sharing disclosure (terminal output, prompts, saved code changes), the reviewer
   emails, and the one-month expiry. Review requests are always for listed people, never public.
6. The security review against API_SEC.md landed with the transport slice (2026-10-24):
   [Review requests sent to viberails.ai](../../../API_SEC.md#review-requests-sent-to-viberailsai-vb-50-slice-c).
7. **The bundle is evidence, not proof of the tree (2026-10-24, Codex review of slice B, High).**
   The requester supplies both the tree id and the file text, so viberails.ai cannot prove that
   one makes up the other; a crafted request can show harmless text and collect an approval over
   a different tree. Rob's decision: the reviewer's VibeRails proves it at signing time (slice D).
   `vb --sign-review` rebuilds the tree from the reviewer's own copy of the base commit and the
   bundle (temporary index: read the base tree, write each file's staged content as a blob, apply
   deletions and renames, `write-tree`) and refuses to sign unless it equals `treeId`. So capture
   must carry exact bytes for every changed path (binary and oversized files included) for a
   request to be approvable, and a base commit the reviewer lacks is refused. The ledger and the
   commit hook are unchanged: they check the signature over the tree, which is still the right
   statement. Implemented in slice D (`ReviewTreeRebuilder`, above); carrying bytes for files
   listed without content is still open.

## Slices

| Slice | Scope | State |
| --- | --- | --- |
| A | Desktop core: capture, ledger, decision verification, commit-hook application, routes | Done (this document, VB-50) |
| B | viberails.ai: request storage and endpoints, reviewer page over the existing session replay viewer and email-audience access | Implemented in VibeRails-Front (VB-OGK99-278, 2026-10-24); contract in its `Services/ReviewRequests/README.md`; cross-repo test `Tests/headless/BoardSync.Integration/ReviewRequestRoundTripTests.cs` |
| C | Desktop transport: send a request and its sessions (reuse `SessionShareUploads`), poll decisions on the Board sync cadence, land approvals in the ledger | Done (VB-DPGU2-279 / VIBE-207, 2026-10-24; this document) |
| D | Reviewer desktop: "Reviews requested of you" (page, VS Code view) and the `vb --vca-hook sign-review` popup that rebuilds the tree in the reviewer's clone and signs it | Done (VB-PR2U7-280 / VIBE-208, 2026-10-24; this document). Open: bytes for binary, oversized and bundle-limit files, so such requests can be approved |
| E | UI: Request review from a card (Sessions rail picker, approvers from the rule), status on the card, commit and Rules page; MCP `request_review` that never signs | Done (VB-4XXCY-281 / VIBE-209, 2026-10-24; this document) |
| F | Line comments, head-commit scope, VS Code surfacing, reviewer notification, retention | Later, VB-XBVE0-282 |

## Tests

```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ReviewRequest|FullyQualifiedName~StoredReviewApproval"
```

`Tests/Services/ReviewRequests/ReviewRequestServiceTests.cs` runs the service against a real Git
repository, the real snapshot reader and a temporary state.db. `Tests/Routes/ReviewRequestRoutesTests.cs`
runs the routes through the real middleware (it joins the `ProcessEnvIsolation` collection because
the project comes from `ParserConfigs`), with viberails.ai intercepted for disclosure, send and
cancel. `Tests/VcaRegression/StoredReviewApprovalTests.cs` runs the real commit-msg hook host with
a seeded ledger, a fake verifier and a popup that never signs, including an approval that arrived
through the poller. `Tests/DB/SchemaSnapshotTests.cs` holds the two tables in `docs/schema/state.sql`.

Slice C, all against an intercepted `HttpMessageHandler` (`ReviewRequestTestSupport`):
`ReviewRequestTransportTests.cs` (what is sent, what is accepted, fixed wording with no remote text,
reference and decision validation, timeouts), `ReviewRequestSenderTests.cs` (the disclosure,
confirmation and key gates, the recorded hosted copy, sessions queued under the pinned fingerprint,
the event, cancel reaching the host) and `ReviewRequestPollerTests.cs` (the 15-minute cadence and
two-hour backoff with a fake clock, decisions landing once with re-verification, refused and
unavailable decisions, the key pin, expiry grace, the persisted next due time). The cross-repository
round trip (`Tests/headless/BoardSync.Integration/ReviewRequestRoundTripTests.cs`) parses the hosted
service's create and poll JSON with `ReviewRequestTransport.ParseStatus`.

```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ReviewRequestTransportTests|FullyQualifiedName~ReviewRequestSenderTests|FullyQualifiedName~ReviewRequestPollerTests"
dotnet test Tests/headless/BoardSync.Integration/BoardSync.Integration.csproj   # needs the sibling VibeRails-Front checkout
```

Slice D: `ReviewTreeRebuilderTests.cs` (a real repository plays the requester through the real bundle
capture and a real clone plays the reviewer: CRLF, a byte-order mark, a deletion, a rename and a
modified executable rebuild; tampered text, a file without content, a missing base commit, a plain
directory and a new executable are refused; blob ids equal `git hash-object`), `ReviewInboxServiceTests.cs`
(the 15-minute cadence and two-hour backoff with a fake clock, the persisted list beside state.db, a key
change, manual-refresh spacing, reject's body and the host's refusals, approve opening the popup once
and recording its exit), `ReviewInboxTransportTests.cs` (the inbox, bundle and decision calls and their
validation), `Tests/Services/VCA/ReviewSignaturePopupTests.cs` (the popup with a fake console, key store,
verifier and host: the order summary → verdict → keys, every refusal before a key prompt, the posted
trailer), `GitCliTests` (environment and stdin reach git), the node test
`Tests/wwwroot/js/review-inbox-view.test.mjs`, and the extension's `review-inbox-view.test.ts`. The
cross-repository round trip now also reads the inbox and bundle projections through the desktop's
validation and rebuilds the tree in a clone.

```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ReviewTreeRebuilder|FullyQualifiedName~ReviewInbox|FullyQualifiedName~ReviewSignaturePopup"
node --test Tests/wwwroot/js/review-inbox-view.test.mjs
```

Slice E: `ReviewLineDiffTests.cs` (numstat-like counts, added and deleted files, the edit bound),
`ReviewRequestServiceTests` (`PreviewListsStagedFiles…`: a real repository with a staged
`vc.rules.md`, the counts, the matched rule and suggested reviewers, nothing stored, `nothing_staged`;
`CommitApprovalsMatchTheCommittedTree…`: a committed approved tree found by full sha and prefix, an
unknown sha, the sha and count bounds), `ReviewRequestRoutesTests` (both routes behind both
credentials, the preview, approvals read from the ledger with a fake tree resolver),
`HookRoutesVcaOverviewTests.WithStoredApprovals…` (only deferred signature findings, with the rule's
approvers, quiet without a ledger or tree), `Tests/Services/Mcp/ReviewRequestToolTests.cs` (no call
without the inherited credentials or with invalid input, the create body with the calling session,
the disclosure in the answer, an older root, the root's refusals, unparsed errors, cancellation) and
the HTTP and stdio registration tests; the node tests `board-card-request-review.test.mjs` and the
Rules page case in `vca-git-guard.test.mjs`; the Playwright case "Request review …" in
`UITests/tests/board-ux.spec.js` (`npx playwright test --config playwright.board.config.js`).

```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ReviewLineDiff|FullyQualifiedName~ReviewRequestTool|FullyQualifiedName~HookRoutesVcaOverview|FullyQualifiedName~ReviewRequestRoutes|FullyQualifiedName~ReviewRequestServiceTests"
node --test Tests/wwwroot/js/board-card-request-review.test.mjs Tests/wwwroot/js/vca-git-guard.test.mjs
```
