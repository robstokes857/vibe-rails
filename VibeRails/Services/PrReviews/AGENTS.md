# PR Reviews

The **Reviews** page and six MCP tools track lead reviews of configured GitHub repositories
(or the current project's origin when no watches are configured): which PRs were reviewed, what
each pass found, and which PR to review next. Frontend:
`wwwroot/js/modules/pr-reviews-controller.js` (list and detail pages, polling) and
`pr-reviews-view.js` (wording, list arithmetic and markup; node-tested). Routes:
`Routes/PrReviewRoutes.cs`, root-only. Storage: `IPrReviewStore` / `PrReviewStore` in
`~/.vibe_rails/pr_reviews.db` in Debug, Release, root backend and stdio MCP alike, component
`pr-reviews/1` (additive). Completed passes can publish GitHub reviews when enabled below.

- **Settings > PR Reviewer** is a dedicated settings navigation page with an immediate-save
  **Enable PR Reviewer and GitHub publishing** switch, off by default. This machine-wide setting
  lives behind `IPrReviewStore` in `pr_reviews.db` (`pr-review-delivery/1`, additive), shared by
  root backends and stdio MCP. The same page edits an optional machine-wide `repositories` list:
  one GitHub `owner/name` per row with immediate-save Add/Remove actions, up to 50 entries,
  validated, lowercased and deduplicated ignoring case before any write.
  `PrReviewerRepositories` (`pr-review-repositories/1`, additive) stores it
  in `pr_reviews.db`. Settings saves are atomic; omitting/null `repositories` preserves the list
  for older clients, while `[]` restores origin fallback. Removing a watch never deletes history;
  re-add it to access that history. GET/PUT `/api/v1/pr-reviews/settings` require both dashboard
  credentials and no-store responses. Off refuses queue/start operations before GitHub access;
  history, drafts and already running passes remain available. A running pass may finish locally.
  Disabling prevents new POST reservations atomically in SQLite; a POST already reserved may
  finish. Enabling never backfills old results or starts a scheduler. Existing root/Automation
  lifecycles apply: no background host, daemon or OS registration is introduced.
- **Publishing is separate from local completion.** Completion and an immutable delivery snapshot
  commit together. `blocking=true` or `verdict=request_changes` produces a real GitHub
  `REQUEST_CHANGES` review; other feedback produces `COMMENT` (including recommend_approve,
  which is a recommendation, not an approval). GitHub branch protection decides whether the
  request changes prevents merging. The user's authenticated `gh` identity authors the review;
  no VibeRails GitHub token is stored. The repository comes from the selected watch (or project origin), the PR from the
  saved pass, and `commit_id` from its full 40-character reviewed SHA. Publishing rechecks the
  PR head and refuses stale/missing SHAs instead of substituting the latest commit. Local
  feedback remains up to 100,000 characters; outbound snapshots cap at 60,000, including marker.
- **Receipts and safe retries.** Detail responses expose `deliveries`; completion exposes
  `delivery`. States are `pending`, `disabled`, `failed`, `unknown`, `published`. Authentication,
  permissions, stale heads and publishing errors remain visible beside the local completed
  result. Only a matching positive GitHub id, identity, body, commit and review event confirms
  publication. A disable/project change after the local commit returns the saved completion
  with a separate disabled/failed delivery, so the page can reload the completed result.
  A unique marker and a cross-process SQLite reservation prevent duplicate POSTs.
  Before sending, the reservation durably becomes `unknown`; a crash/timeout/unexpected response
  cannot authorize another POST. Retry checks the original account's GitHub reviews for its
  exact snapshot (100/page, at most 20 pages); absence is still unknown, never proof to resend.
  Definite 4xx refusals can retry, as can failures before dispatch. Use
  `POST /{number}/reviews/{id}/publish`, the detail page's retry/check action or
  `retry_pr_review_publish`. Receipts/snapshots survive local deletion. Local edits do not edit,
  dismiss or republish GitHub reviews; start a new pass for changed findings. Existing history
  without a delivery is never automatically published, including adopted legacy passes.

- **Automatic legacy adoption.** Before each repository operation, the store reads a consistent
  snapshot of its PRs and passes from legacy `state.db` using a separate read-only connection.
  The new feature never changes the legacy schema or rows and never locks its writer. Adoption
  data, `pr-review-adoption/1` fingerprints and review-id mappings commit together under the
  dedicated file's writer lock; competing starts cannot duplicate or reorder adoption. Existing
  ids are retained when available; collisions with new passes receive stable mapped ids.
  All stored fields, including draft feedback, verdicts, blocking flags, lifecycle and raw
  timestamps, carry over. Reviewer settings and deliveries are new-file-only and never adopted.
- **Older versions can keep running.** Later legacy changes (including deletions) adopt on the
  next operation if the destination PR has not changed since its last adoption. Unchanged legacy
  rows never overwrite local edits or resurrect local deletions. GitHub lifecycle fields and
  last-write bookkeeping are excluded from authored-content fingerprints; lifecycle refreshes
  merge by latest check time, so automatic polling cannot produce edit conflicts. A PR changed
  in both files keeps serving its current history and adds an inspectable conflict to the Reviews
  list, even for a locally deleted PR. Other reads/writes and imports continue. Both exact
  snapshots are archived in `PrReviewLegacyConflicts` (`pr-review-adoption/2`). The user can
  inspect drafts/passes and choose **Keep current history** or **Use legacy history** through
  `POST /{number}/legacy-conflict/resolve`; matching content-version tokens prevent stale choices
  from overwriting later edits (409 asks for a fresh inspection). Archives remain after resolution.
  This is conservative at PR granularity for authored edits; new writes are never mirrored back
  to older versions. List polling pauses while an inspection is open. Missing legacy files/tables are rechecked on
  later calls; failures roll back and retry automatically. Schema generation is independently
  guarded at 1 in the dedicated file. No upgrade command or manual backup is needed.
- **Repository selection is explicit.** With watches configured, list, queue and lifecycle sync
  cover those repositories without resolving or switching the checkout's origin. Each queue
  entry, tracked PR, pass and delivery identifies `repository` as `owner/name`; list/queue expose
  `repositories`, and their legacy singular `repository` is null for multiple targets. Queue ranks
  apply across all targets, with repository/name and number breaking equal-age ties. A GitHub
  failure identifies the repository and fails the queue with 502 rather than returning partial
  success. The existing open-list limit applies per repository.
  REST single-PR/pass operations accept `?repository=owner%2Fname`; the five corresponding MCP
  tools accept optional `repository`. It must select a configured repository, or match the origin
  fallback. Omission works with one target; multiple targets require scope (400) rather than
  guessing by PR number. Reads, draft/edit/delete, conflict resolution, completion and publish/retry
  retain repository+PR/pass isolation. Publishing re-resolves the same explicit target, so settings
  changes cannot retarget a completed result to another watch.
  Without explicit watches, `PrReviewRepositoryResolver` parses the dashboard project's `origin`
  (`GitHubRepository.TryParseRemote`): github.com only, GitHub owner/name character rules. Records
  are keyed by case-insensitive `owner/name` and PR number, so checkouts of the same repository
  share history. Without a GitHub origin, the list says why and other operations answer 409.
  Remote URLs are never echoed because they can embed credentials.
- **The state is derived, never stored** (`PrTrackerStatus.Derive`): an unfinished pass is
  `in_review`; otherwise the newest completed review decides between `blocker` and `complete`;
  no completed review is `not_started`. In review outranks Blocker so an interrupted run stays in
  the recovery queue. `blocking` defaults to `verdict == request_changes` on completion and can
  be flipped afterwards on any review.
- **Timestamps are the server's.** A request carrying `createdAt`, `updatedAt`, `startedAt` or
  `reviewedAt` at all (even null) is refused by name. `openedAt` is the exception: it is GitHub's
  own `createdAt` for the PR.
- **Start is idempotent; one pending pass per PR.** Start upserts the PR and returns the open pass
  if there is one (a partial unique index enforces it). Draft checkpoints only the supplied
  fields. Untracked (404) and tracked-without-a-pending-pass (409) are different errors.
  The detail page sends `useCurrentHead: true` on start: `gh pr view` reads that number's metadata
  and current SHA before any write, including PRs outside the open-list limit. A failed lookup
  is a 502 and starts nothing. Explicit SHA starts, including MCP, record the reviewed SHA;
  completion requires a full current-head SHA to publish safely.
- **GitHub's lifecycle is separate and never rewrites a review.** `POST github/sync` asks GitHub
  for every tracked PR's open/merged/closed state by number (aliased GraphQL, 50 per call; a
  number that is not a PR here still lets the rest of the batch through). It never moves
  `UpdatedUTC`, which means "last review write"; a poll that bumped it would reorder the history.
  `unknown` (never synced) is treated as live. A PR that merged with a blocker standing keeps
  that blocker: the page says so instead of hiding it.
- **The queue is arithmetic** (`PrReviewQueue.Build`): rank `in_review`, `never_reviewed`, then
  `new_commits`; oldest first within a rank. Drafts and PRs reviewed at their current head are
  skipped; a review with no recorded SHA counts as up to date. A recorded SHA of 7+ characters
  matches the full head it abbreviates, so a short SHA does not re-queue a PR forever. The queue
  answers 502 when `gh` fails, so "nothing to review" and "cannot see GitHub" never look alike.
- **`gh` is the user's.** `GitHubPullRequestClient` runs it through `ICliWrapper` (argv, stdin
  closed for reads, `GH_HOST=github.com`, `GH_PROMPT_DISABLED`, 20 s per call) with an explicit
  `-R owner/name`, GraphQL variables or fixed-host REST paths. Publishing uses JSON on stdin,
  never feedback in argv/logged command text. VibeRails stores no GitHub credential. Failure
  messages use fixed wording; stderr/remote error prose is never reflected. See API_SEC.md,
  "GitHub pull requests and review publishing".
- **The page** polls queue, sync and list once a minute on the list page only, never while the
  UI is hidden, and never with a second round behind a slow first. The detail page does not poll,
  so a half-written review is not re-rendered away. Each form keeps unsaved values across local
  detail actions; saving clears only the submitted form's draft. Inputs stay disabled through
  the save and re-read. Deletes go through `confirmDialog` and discard superseded confirmations.

MCP tools (both transports, outside the Board auto-grants): `get_pr_review_queue`,
`get_pr_review`, `start_pr_review`, `save_pr_review_draft`, `complete_pr_review`,
`retry_pr_review_publish`. Completion/retry are outbound writes when enabled. They resolve
the fallback project through `IBoardProjectResolver`; repository arguments select only saved
watches or that origin. No tool accepts a host, path or credential. Configure watches through
Settings > PR Reviewer (or the authenticated settings API).

Tests: `Tests/DB/PrReviewStoreTests.cs`, `Tests/DB/PrReviewDatabaseIsolationTests.cs`, `Tests/Services/PrReviews/*`,
`Tests/Routes/PrReviewRoutesTests.cs`, `Tests/Services/Mcp/PrReviewToolTests.cs`,
`Tests/wwwroot/js/pr-reviews-*.test.mjs`, `UITests/tests/pr-reviews.spec.js` (static frontend with
API fixtures; `playwright.pr-reviews.config.js`).
