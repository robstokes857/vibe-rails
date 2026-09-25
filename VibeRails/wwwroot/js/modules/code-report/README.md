# Code report viewer

`RuleController.attachRulesOverview()` mounts `CodeReportViewer` inline in Project health's
Code quality card (`[data-code-analyzer-report]`), so the map, health summary and file list are
the first thing QUALITY shows. There is no second screen: the old `code-quality` route is an
alias that loads Project health. The controller supplies the latest real MintLint response from
its scan cache; the viewer requests a current repository graph through `app.apiCall`. Scan
scope, exclusions, Scan again, Fix and Copy scan summary stay in the card header; rule
enforcement and Git Guard stay in the Rules card. There is no second report store, scan engine
or theme preference.

## Composition and lifetime

- `viewer.js` owns the split map/health/file-list composition and the saved-details panel.
  File selection focuses Atlas and its inspector; **Open details** replaces the sidebar with an
  inline panel of saved measurements and captured excerpts (never a modal or window).
  **Show in code explorer** and Escape return to the map. Copy context is absent.
- `radar-interactions.js` adds category explanations, hover sectors, roving category keys,
  detail activation and Escape dismissal. Capture-phase Escape handling precedes the app's
  document-level Back shortcut. Interactions wait for the quality reveal to finish.
- `theme-sync.js` reads inherited `--color-*` tokens after the existing theme bridge and
  updates Atlas in place. Custom themes and the VS Code opt-in remain host-owned.
- `styles.css` scopes composition styles to `.code-report`; load host CSS, quality CSS,
  then this file. Container queries handle narrow webviews; the file list scrolls independently.
- `setLoading`, `setResponse`, `setError` and `destroy` own graph requests, generation guards,
  component state and teardown. Navigation destroys Atlas, radar interactions, theme observers,
  animation frames and the details panel, and aborts pending graph requests. Late responses
  are ignored.
  A graph failure preserves saved metrics; failed/missing analysis is never given a score.

Excerpts and report timestamps describe the supplied scan; the graph describes the current
working tree. An absent map file can still open its saved details. Report text is escaped;
no source is executed, copied to the clipboard or sent to an external service by this viewer.

## Graph contract

`POST /api/v1/code-analyzer/graph` accepts `{ files?: string[] }` with at most 1,000 safe
repository-relative paths to prioritize. It is read-only, mapped on active root backends,
and requires both existing session and tab credentials. The repository root is server-derived.
The response follows Code Atlas schema `1.0` plus `capturedUtc`, `truncated`, `fileCount`
and an evidence description. Absent optional node fields must be omitted, not serialized as null.

[`RepositoryCodeGraph`](../../../../Services/CodeReports/RepositoryCodeGraph.cs) reads Git's
tracked/untracked, non-ignored file catalog and uses the existing working-tree path guard to
refuse links, junctions and paths outside the repository. No database reads/writes are added.
The snapshot contains real directory ancestry (up to 32 levels), file nodes, parser declarations,
local JS/TS imports and unambiguous type-name references. Cross-directory references also have
domain edges. These are lexical source evidence, not resolved call graphs or runtime dependencies.
No coverage or quality metric is inferred from the graph.

Limits: 1,000 files, 2,800 nodes, 10,000 edges, 512 characters per reference evidence,
8 MiB serialized graph, 12 declarations per file, 128 KiB per source file,
16 MiB source budget and a 4 Mi-character Git catalog with a two-minute catalog timeout.
Files beyond source-read limits retain structure without declarations. An entry the path guard
refuses is omitted and marks the map truncated; a catalog read that exceeds its character bound
or its timeout is reported as that bound, not as a server fault. Standard generated/vendor
directories are excluded unless a file is explicitly in the report. When the graph exceeds the
byte limit, references and declarations give way before file nodes; exceptionally long paths can
also reduce the file set. Truncation is disclosed in the map note. Search covers the supplied
snapshot, not omitted repository files.

## Provenance and vendor updates

The approved composition comes from `vibe-quality-workbench/dist` (September 2026).
Only reusable components and scoped helpers were ported. Its preview entry point, theme
storage, preview CSS, fixture reports/graph, static server and demo shell are not included.

`vendor/quality` contains the four unchanged Quality Lab shipping files.
`vendor/atlas` is Code Atlas 0.14.0 with the workbench's four approved patches: visible domain
connections, combined toolbar/canvas controls, inspector scroll reset and host nonce support.
The source patch scripts and integration references live in the sibling workbench's `docs/`.
Refresh from that approved bundle, retaining the relative module imports and TypeScript declarations.

This integration adds one bounded-scale patch to the embedded Atlas layout library:

1. In `scopeNodes`, when more than 200 entities are eligible and the scope has directory
   children, show those direct children (up to the existing 700 visible-node limit). Preserve
   the eligible total. Search and `focusNode` still reveal any supplied file, even when it
   is outside the overview. Smaller snapshots keep the original constellation.
2. In `layout`, use `groups.length` and `slot = i` instead of wrapping group centers every
   12 domains. Independent directories must not occupy identical centers.

Atlas keeps its opaque-origin sandbox and MessageChannel lifecycle. Pass the host's
`window.__viberails_NONCE__`; do not add `allow-same-origin`, eval, or CSP exceptions.

## Verification

Backend graph and authenticated route regressions live under `Tests/Services/CodeReports`
and `Tests/Routes/CodeGraphRoutesTests.cs`. `UITests/tests/code-quality-ux.spec.js` covers the
real frontend with test-only supplied data: selection, visible connections, large overviews,
saved metrics, scrolling, radar keyboard interaction, themes, reduced motion, narrow sizes,
independent errors, escaped excerpts and navigation races. Run it with
`npx playwright test --config playwright.quality.config.js` from `UITests`.

The report case in `vscode-viberails/src/test/suite/smoke.test.ts` uses a real backend and
installed VS Code webview with the production HTML/CSP and a test-only nonce-bearing probe.
Also review the real repository in the browser after graph supplier or Atlas layout changes.
