# Review requests before a commit (VB-50)

A review step before the commit: the developer asks a named person to look at the staged changes
and the AI sessions that produced them, and to sign off. Unlike a pull request, the reviewer sees
the whole conversation, not just the diff. This document is the component contract for the part
that exists (slice A, 2026-10-23: the desktop core) and records the design decisions for the rest.

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
  `{ decision, trailer?, comment?, reviewer? }`), `POST /{id}/cancel`, `DELETE /{id}` (204).
  Refusals are `{ error, code }` with 400 `invalid_request`, 404 `not_found` / `session_not_found`,
  409 `nothing_staged` / `not_a_repository` / `tree_mismatch` / `signature_rejected` /
  `not_a_reviewer` / `not_open`, and 503 `verification_unavailable`. The project is derived
  server-side; a request made for another project is not found.

Nothing in this slice sends a request anywhere. The decision route's only outbound call is the
existing public signature verification, with a tree id and no repository content.

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
6. The security review against API_SEC.md lands with the transport slice.
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
   statement.

## Slices

| Slice | Scope | State |
| --- | --- | --- |
| A | Desktop core: capture, ledger, decision verification, commit-hook application, routes | Done (this document, VB-50) |
| B | viberails.ai: request storage and endpoints, reviewer page over the existing session replay viewer and email-audience access | Implemented in VibeRails-Front (VB-OGK99-278, 2026-10-24); contract in its `Services/ReviewRequests/README.md`; cross-repo test `Tests/headless/BoardSync.Integration/ReviewRequestRoundTripTests.cs` |
| C | Desktop transport: send a request and its sessions (reuse `SessionShareUploads`), poll decisions on the Board sync cadence, land approvals in the ledger | Backlog, VB-DPGU2-279 |
| D | Reviewer desktop: "Reviews requested of you" and a `vb --sign-review` console popup that shows the bundle summary and signs the tree | Backlog, VB-PR2U7-280 |
| E | UI: Request review from a card (Sessions rail picker, approvers from the rule), status on the card, commit and Rules page; MCP `request_review` that never signs | Backlog, VB-4XXCY-281 |
| F | Line comments, head-commit scope, VS Code surfacing, reviewer notification, retention | Later, VB-XBVE0-282 |

## Tests

```bash
dotnet test Tests/Tests.csproj --filter "FullyQualifiedName~ReviewRequest|FullyQualifiedName~StoredReviewApproval"
```

`Tests/Services/ReviewRequests/ReviewRequestServiceTests.cs` runs the service against a real Git
repository, the real snapshot reader and a temporary state.db. `Tests/Routes/ReviewRequestRoutesTests.cs`
runs the routes through the real middleware (it joins the `ProcessEnvIsolation` collection because
the project comes from `ParserConfigs`). `Tests/VcaRegression/StoredReviewApprovalTests.cs` runs the
real commit-msg hook host with a seeded ledger, a fake verifier and a popup that never signs.
`Tests/DB/SchemaSnapshotTests.cs` holds the two tables in `docs/schema/state.sql`.
