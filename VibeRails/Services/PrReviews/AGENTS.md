# PR Reviews

The **Reviews** page and five MCP tools track lead reviews of the current project's GitHub pull
requests: which PRs were reviewed, what each pass found, and which PR to review next. Frontend:
`wwwroot/js/modules/pr-reviews-controller.js` (list and detail pages, polling) and
`pr-reviews-view.js` (wording, list arithmetic and markup; node-tested). Routes:
`Routes/PrReviewRoutes.cs`, root-only. Storage: `PrReviewStore` in `state.db`, component
`pr-reviews/1` (additive). Nothing in this feature writes to GitHub.

- **The repository comes from the project.** `PrReviewRepositoryResolver` parses the dashboard
  project's `origin` remote (`GitHubRepository.TryParseRemote`): github.com only, owner/name
  checked against GitHub's character rules. Records are keyed by `owner/name` and PR number, so
  two checkouts of one repository share their history. A project without a GitHub origin lists
  nothing and says why; every other call answers 409. The remote URL is never echoed: it can
  embed a credential.
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
  is a 502 and starts nothing. Explicit SHA starts, including MCP, remain local writes.
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
  closed, `GH_HOST=github.com`, `GH_PROMPT_DISABLED`, 20 s timeout) with an explicit `-R owner/name` or GraphQL
  variables. VibeRails stores no GitHub credential. Failure messages are fixed wording keyed on how
  `gh` ended; its stderr is never reflected. See API_SEC.md, "GitHub pull request reads".
- **The page** polls queue, sync and list once a minute on the list page only, never while the
  UI is hidden, and never with a second round behind a slow first. The detail page does not poll,
  so a half-written review is not re-rendered away. Each form keeps unsaved values across local
  detail actions; saving clears only the submitted form's draft. Inputs stay disabled through
  the save and re-read. Deletes go through `confirmDialog` and discard superseded confirmations.

MCP tools (both transports, outside the Board auto-grants): `get_pr_review_queue`,
`get_pr_review`, `start_pr_review`, `save_pr_review_draft`, `complete_pr_review`. They resolve
the project through `IBoardProjectResolver` and take no repository or path argument.

Tests: `Tests/DB/PrReviewStoreTests.cs`, `Tests/Services/PrReviews/*`,
`Tests/Routes/PrReviewRoutesTests.cs`, `Tests/Services/Mcp/PrReviewToolTests.cs`,
`Tests/wwwroot/js/pr-reviews-*.test.mjs`, `UITests/tests/pr-reviews.spec.js` (static frontend with
API fixtures; `playwright.pr-reviews.config.js`).
