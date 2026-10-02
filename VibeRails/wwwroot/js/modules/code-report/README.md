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

`POST /api/v1/code-analyzer/graph` accepts `{ files?: string[], includeDependencies?: boolean }` with at most 1,000 safe
repository-relative paths to prioritize. It is read-only, mapped on active root backends,
and requires both existing session and tab credentials. The repository root is server-derived.
The response follows Code Atlas schema `1.0` plus `capturedUtc`, `truncated`, `fileCount`
and an evidence description. `diagnostics` separates supported-file and intentional filter counts
from omission codes/counts with explanations. Absent optional node fields must be omitted, not serialized as null.

[`RepositoryCodeGraph`](../../../../Services/CodeReports/RepositoryCodeGraph.cs) reads Git's
tracked/untracked, non-ignored file catalog and uses the existing working-tree path guard to
refuse links, junctions and paths outside the repository. No database reads/writes are added.
The snapshot contains real directory ancestry (up to 32 levels), file nodes, parser declarations,
local JS/TS imports/re-exports (including TypeScript emitted-extension substitution), Python
package imports, Rust declared modules and scoped `use` paths, and C#/PHP type-name mentions
qualified by namespaces and import aliases. Bare identifiers are never matched across languages.
TypeScript interfaces and type aliases have `interface` and `type` kinds, with source lines;
type signatures are not mapped as runtime functions. This does not change saved analyzer metrics.
Cross-directory references also have
domain edges. These are lexical source evidence, not resolved call graphs or runtime dependencies.
No coverage or quality metric is inferred from the graph.

Limits: 2,000 files, 2,800 nodes, 10,000 edges, 512 characters per reference evidence,
8 MiB serialized graph, 12 declarations per file, 128 KiB per source file,
16 MiB source budget and a 4 Mi-character Git catalog with a two-minute catalog timeout.
The request still accepts at most 1,000 priority paths. Remaining files are selected in directory
rounds so an alphabetically early test tree cannot take the entire file budget. The selection
charges every directory it introduces against the node budget (a file is one node, each new
directory in its ancestry one more), so a very wide tree keeps fewer files than the file limit
and a file whose ancestry no longer fits is skipped rather than dropped later; the map is marked
truncated whenever an eligible file was left out. Files take precedence over optional
declarations within the node budget.
Files beyond source-read limits retain structure without declarations. An entry the path guard
refuses is omitted and marks the map truncated; a catalog read that exceeds its character bound
or its timeout is reported as that bound, not as a server fault. Dependency directories
(`node_modules`, `vendor`, `assets`) and C# `bin`/`obj` output are excluded (segment names matched
case-insensitively) unless explicitly in the report. `assets` is on that list because it holds
vendored bundles far more often than first-party code: this repository's `wwwroot/assets` Bootstrap
bundle alone marked every map partial and its minified names created bogus references. The map's
**Include vendor, node_modules and assets sources** checkbox sends `includeDependencies: true`,
making those cataloged files eligible under the same containment and read bounds; C# build output
remains filtered. No excluded or ignored file is discovered outside Git's catalog. Non-C#
`bin`/`obj` sources remain eligible. Rust crate maps start at `main.rs`/`lib.rs` and at Cargo's
auto-discovered `src/bin`, `tests`, `examples` and `benches` files, and a bare `use child::…`
resolves against the current module first, as Rust 2018 does. A local JS/TS import that names a known file extension
(`.js`, `.ts`, `.json`, `.vue`, …) resolves only to that file; any other dotted tail
(`./user.service`, `./app.module`) is a module stem and still probes the source extensions and
`index` files. When the graph exceeds the
byte limit, references and declarations give way before file nodes; exceptionally long paths can
also reduce the file set. Truncation is disclosed in the map note. Search covers the supplied
snapshot, not omitted repository files. **Map coverage and filters** explains file/node limits,
per-file declaration caps, oversized or binary sources, total read budget, unreadable paths,
depth limits, shortened evidence, edge limits and serialized trimming with separate counts.

Python resolution uses package ancestry and conventional `src` roots, including namespace
packages; ambiguous full paths stay omitted. Rust follows explicit `mod` trees under conventional
`main.rs`/`lib.rs` roots and uses cataloged Cargo manifests as workspace boundaries. External
crates, `#[path]`, generated modules and macro expansion are not inferred. C#/PHP references
remain lexical evidence; compiler/project binding and C# cross-file global usings are not modeled.
JS/TS package aliases and bundler configuration are also outside this snapshot.

C#/PHP scope evidence is bounded before graph construction: at most 8,192 references,
64 Ki characters of shared scope text (also at most twice source length), and 256 Ki
characters of declaration/reference text (also at most four times source length) per file.
Candidates are lazy and unrelated identifier names do not expand imports. Resolution examines
at most 65,536 candidates / 4 Mi characters per file and 1,048,576 candidates / 32 Mi characters
per graph. Exhausted scopes or resolution work report `reference-scope-limit` or
`reference-work-limit` in coverage diagnostics. An unfinished lookup never emits a partial
match, because a later candidate could make that match ambiguous.

Python and Rust module import evidence is read in linear time and bounded to 256 Ki
characters of emitted path text per file (a Rust use-group repeats its prefix for every
leaf). A file that exhausts it keeps the imports already read and reports
`import-evidence-limit`; each import is independent evidence, so a partial list is never
ambiguous. TypeScript type-argument delimiters are paired in one pass per file, so an
unmatched `<` in an in-progress file costs nothing further.

## Provenance and vendor updates

The approved composition comes from `vibe-quality-workbench/dist` (September 2026).
Only reusable components and scoped helpers were ported. Its preview entry point, theme
storage, preview CSS, fixture reports/graph, static server and demo shell are not included.

`vendor/quality` contains the four unchanged Quality Lab shipping files.
`vendor/atlas` is Code Atlas 0.14.0 with the workbench's four approved patches: visible domain
connections, combined toolbar/canvas controls, inspector scroll reset and host nonce support.
The source patch scripts and integration references live in the sibling workbench's `docs/`.
Refresh from that approved bundle, retaining the relative module imports and TypeScript declarations.

This integration also patches the embedded Atlas layout and connectors:

1. `scopeNodes` shows the whole snapshot: every directory, file, type and function the
   supplier kept is drawn at once, in the root view and inside any scope (VIBE-33). The
   previous "direct children above 200 entities" overview and 700-node cap are gone; the
   3,000-node contract limit is the only bound, and a `Showing N of M` notice appears only
   beyond it. Search and `focusNode` still reveal any supplied file.
2. In `layout`, use `groups.length` and `slot = i` instead of wrapping group centers every
   12 domains. Independent directories must not occupy identical centers.
3. Nodes use curved, directed connectors and moving signals like Cards. Animation respects
   reduced motion and hidden pages; dense overviews retain a bounded animation budget.
4. Above 600 visible nodes the view is **dense** (`DENSE_VIEW_NODES`, exported by the layout
   module; the renderer adds `.dense` to the stage). Repulsion switches from the exact O(n²)
   pass to a Barnes-Hut quadtree with the same force law (theta 1; extent and spacing stay
   within a few percent of the exact result, 2,800 nodes lay out in ~0.4 s instead of ~2 s).
   Smaller views keep the exact pass and their original constellation.
5. A dense field holds still: no ambient sway or pointer parallax, and the animation loop
   only draws during orbit transitions and inertia. While it turns, each frame writes node
   positions and the visible signals only; depth cues, stacking order, labels, hover targets
   and the static threads are written on the frame the motion settles (`.turning` fades the
   threads meanwhile). The entrance is one compositor transform instead of an orbit easing.
6. Signals (`.edge-flow`) live in their own promoted SVG (`#signals`) rather than inside each
   `.edge-group`, so their CSS animation repaints 180 paths instead of every link; `highlight`
   mirrors `highlight`/`dim`/`change-muted` onto the flow. Edge labels take their position
   when a link is hovered or highlighted. The Nodes view draws up to 10,000 links; a dense
   unselected view spreads its 180 signals across the field with a stride.
7. Dense hover dims through one veil (`#veil`) instead of a class on every node and link:
   connected nodes get `.lit` and rise above it, connected links are redrawn in `#lit-layer`.
   Dense glyphs cap at 1.5× and threads use `--stroke-scale` so thousands of entities read as
   points of light with hairlines at any zoom; module labels avoid each other.

Known limits of the dense view, measured on this repository's 2,800-node / 7,400-link map in
headless Chromium: the first draw takes ~1 s after the graph arrives, a hover highlight
~0.1–0.2 s, and rotation runs at roughly 10–15 frames per second because every node is a DOM
element with its own compositor layer. Pan, zoom and the idle signals stay smooth.

Atlas keeps its opaque-origin sandbox and MessageChannel lifecycle. Pass the host's
`window.__viberails_NONCE__`; do not add `allow-same-origin`, eval, or CSP exceptions.

## Verification

Backend graph and authenticated route regressions live under `Tests/Services/CodeReports`
and `Tests/Routes/CodeGraphRoutesTests.cs`. `UITests/tests/code-quality-ux.spec.js` covers the
real frontend with test-only supplied data: selection, visible connections, whole-snapshot
and dense overviews (bounded signals, hover veil, still field, rotation settle), saved metrics,
scrolling, radar keyboard interaction, themes, reduced motion, narrow sizes, independent
errors, escaped excerpts and navigation races. Run it with
`npx playwright test --config playwright.quality.config.js` from `UITests`.

The report case in `vscode-viberails/src/test/suite/smoke.test.ts` uses a real backend and
installed VS Code webview with the production HTML/CSP and a test-only nonce-bearing probe.
Also review the real repository in the browser after graph supplier or Atlas layout changes.

## Saved Board reports

`board-card-checks.js` mounts this same viewer for saved Code quality evidence. The card shows
scope, coverage, limitations and qualified freshness ahead of the report; technical capture
metadata stays in a disclosure. Its saved measurements precede the current repository map on
small layouts. Closing or replacing the card aborts fetches and destroys the viewer; a late
response cannot mount into another card. VCA evidence uses escaped text in the same report rail.
The Checks summary has no aggregate score. See `Services/Board/CHECKS.md` for the persistence contract.
