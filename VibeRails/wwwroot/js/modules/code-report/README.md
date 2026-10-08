# Code report viewer

`RuleController.attachCodeQualityOverview()` mounts `CodeReportViewer` in the dedicated,
full-width Quality workspace (`code-quality`, `[data-code-analyzer-report]`). Rules and Git
Guard stay on their own page (`dashboard` / `agents`); opening Rules does not start analysis,
and opening Quality does not run rule validation. Settings is the cog beside Automation's play
button, leaving room for both navigation destinations. The controller supplies the latest real
MintLint response from its scan cache; the viewer requests a current repository graph through
`app.apiCall`. Scan scope, exclusions, Scan again, Fix and Copy scan summary stay in the Quality
header. There is no second report store, scan engine or theme preference.

## Composition and lifetime

- `viewer.js` owns the split map/health/file-list composition and the saved-details panel.
  File selection focuses Atlas and its inspector; **Open details** replaces the sidebar with an
  inline panel of saved measurements and captured excerpts (never a modal or window).
  **Show in code explorer** and Escape return to the map. Copy context is absent.
- The chrome is compact: the sidebar is the grade, the radar and the file list, with **Git
  changes** available beside the default **Report files** list and the changed files lit on the map on load.
  1.5 seconds after Atlas is ready, the first report file is selected using the normal 800 ms map focus
  animation. User input cancels this opening selection, as do reload and teardown; empty reports,
  failed maps and a first file outside the map skip it. Reduced motion follows Atlas's preference.
  The scan's count,
  duration and age sit in the card header; the metric averages live in a file's details; map
  coverage (`toggleDiagnostics()`) and the scan log open from the card's menu. The verdict, the
  grade, the radar and the map read the host's `--quality-*`, `--node-*` and `--graph-*` tokens.
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
(`node_modules`, `vendor`, `assets`) and C# `bin`/`obj` output are always excluded,
including priority report paths (segment names matched case-insensitively). The dependency
inclusion control and request property are removed; an older client's `includeDependencies`
property is ignored. `diagnostics.includesDependencies` remains false for response compatibility.
`assets` is filtered because vendored bundles dominate it and minified names create bogus references.
Saved analysis for excluded files remains available in the sidebar. No excluded or ignored file
is discovered outside Git's catalog. Non-C#
`bin`/`obj` sources remain eligible. Rust crate maps start at `main.rs`/`lib.rs` and at Cargo's
auto-discovered `src/bin`, `tests`, `examples` and `benches` files, and a bare `use child::…`
resolves against the current module first, as Rust 2018 does. A local JS/TS import that names a known file extension
(`.js`, `.ts`, `.json`, `.vue`, …) resolves only to that file; any other dotted tail
(`./user.service`, `./app.module`) is a module stem and still probes the source extensions and
`index` files. When the graph exceeds the
byte limit, references and declarations give way before file nodes; exceptionally long paths can
also reduce the file set. Truncation is disclosed in Map coverage. Search covers the supplied
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

## The field and the Git changes list (VIBE-46)

The searchable snapshot and the rendered view have separate limits. The server keeps its
2,000-file / 2,800-node budget; the embedded Atlas template draws the whole snapshot at once
on two canvases inside `#stage`, with no DOM element per entity:

- `#field` holds the links, the points and the labels. It is repainted on interaction, on
  ambient sway and on camera motion, as a handful of batched strokes and fills (links grouped by
  family and crossing, points by family and depth brightness). `#effects` holds the flowing
  signals (at most 180, spread across the references, or the lit entity's own links) and the
  selection ripple; it is repainted every frame they move. Both clear under reduced motion.
- Ambient links are budgeted: `CodeAtlasLayout.ambientLinks` keeps every tree link first and a
  hash-sampled set of references up to `AMBIENT_LINK_LIMIT` (4,500). Hovering or selecting an
  entity lights every link of that entity from the complete set, so the budget hides nothing
  from inspection; the budget is not announced, `fieldStats()` reports it.
- The stage hit-tests projected points: hovering within a few pixels shows the tooltip and lights
  the neighbourhood (a dense field dims through one veil), a press right on a point in Pan mode
  drags it, a press near one selects it on release unless the pointer moves, and Rotate always
  turns the field. Search, the inspector's relationship rows and the breadcrumbs remain the
  keyboard paths to an entity; Enter or Space on the focused stage selects the hovered one.
- Motion: sway and pointer parallax, the entrance sweep, per-entity fade-in and the signals run
  at one frame per 30 ms while idle and every frame during a gesture. A camera gesture whose
  frame costs more than 24 ms thins the ambient links (`stride`) before it drops frames; a field
  whose ambient sway costs more than 18 ms for twelve frames holds still
  (`#stage[data-field="still"]`), keeping only the signals. The layout cache is keyed by scope,
  filter and reveal. Reduced motion stops all of it; the Cards view keeps its SVG curves.
- Semantic zoom (VIBE-48, retuned for readability): a dense field (more than `DENSE_VIEW_NODES`
  entities) is its directories while zoomed out. `CodeAtlasLayout.detail` settles files to dust
  (`FILE_FLOOR`, 35% alpha and smaller points) and brightens them between 45% and 80% zoom, then
  fades classes and types in between 70% and 100% and functions between 90% and 130%; a link
  fades with the fainter of its endpoints, and while the files are dust the field draws only the
  tree spokes and the directory-to-directory references (the server's domain edges, stroked
  wider), so a file's own references arrive with the file. A directory's point grows with the
  files beneath it (`item.weight`) and keeps at least 88% brightness at any depth; labels go to
  changed files first, then to directories by weight; the signals travel the directory references
  first and never ride dust. Labels and hit-testing follow the drawn set. It is a filter inside
  `paintField`, never a relayout, so clusters keep their places. The hovered, selected and
  changed entities and every entity in the lit set always draw (tracing a file reveals its
  declarations at any zoom), as does everything under a search or an explicit entity filter.
  Zoomed out, a repeated directory name (`src`, `tests`) is labelled once. A notice overlaid in
  the stage's corner (`#detail-notice`) says what zooming in reveals and the legend dims those
  rows; it is an overlay because a notice in the summary row would change the stage height, and
  the ResizeObserver refits the camera on every stage resize. `fieldStats()` reports `zoom`,
  `fileDetail`, `shown` and the `hidden` counts, `locate(id)` whether a point is drawn.
- Highlight changes is on by default and is emphasis, not a blackout: unchanged points keep
  about half their brightness, links half their alpha and directory labels stay up, so the
  changed files (ringed and labelled in `--graph-changed`) read against the structure they sit
  in. Hovering while it is on keeps the hovered entity's neighbourhood lit. The summary row says
  how many changed files are on the map; the link budget is no longer announced there.
- Palette: the host owns it. `style.css` declares `--node-module/-file/-class/-function/-data`
  and `--graph-edge/-cross-edge/-changed` on `:root` (the app theme: lavender-white directories,
  violet files, sand declarations, teal data, amber changes) and maps them from the editor's
  symbol, chart and git colors in the VS Code theme bridge; `theme-sync.js` passes them through
  `themeFromCss`, and the legend dots use the same tokens the canvas paints with. The bundle's
  own palettes (host-side and in the template; change both) are the fallback for a host that
  declares none and match the app defaults.
- Globe, tones and the wheel (VIBE-71). The layout is still the flat map in the front view, but
  `CodeAtlasLayout.layout` now returns its shell on `positions.space` (`radius`, `focal`,
  `pivot`): every top-level cluster sits on one sphere, near and far by turns, and is itself a
  ball of its files (a declaration stays within 15 units of its file). `CodeAtlasSpace.turn`
  eases from 0 at the front to 1 at `TURN_FULL` (.6 rad) on either axis; the perspective divide
  (`.6` to `1.6`) and the depth fog (`.62` floor at the front, `.45` turned) grow with it, so the
  approved front view is untouched and depth appears as the field turns. Points are drawn back to
  front every paint. Each top-level directory has a tone from a nine-hue wheel (`TONE_HUES`,
  stepping four at a time so neighbours differ); its directory points, file points and links take
  the tone, with lightness and saturation from the theme's `--node-module`/`--node-file` tokens;
  classes, functions and data keep their family colours. Points larger than dust (`DUST_RADIUS`)
  are shaded sphere sprites cached per colour and size; dust stays batched flat fills, and points
  outside the viewport are skipped. Past 100% zoom labels grow as `zoom^.35` and points as
  `zoom^.75`, ambient links cap at 1.2px and thin again, and a link with both ends off-screen is
  not drawn. The canvas runs under the floating camera rail (no gutter): the fit and the labels
  keep clear of `field.controls`, and one document-level wheel listener zooms the map from
  anywhere in the frame (a scroller that can still move keeps its wheel; the inspector and search
  results swallow theirs at their ends), so the frame never hands a wheel to the host page. On the
  host, `viewer.js` keeps the wheel inside the sidebar lists (`SIDEBAR_SCROLLERS`) when they cannot
  scroll further, and sizes the layout to the viewport (`--code-report-height`, 600 to 1400px,
  measured from the chrome above and below it, counting only visible elements after the
  container) so the page ends under the card. `fieldStats()` adds `focal`, `radius`, `turn`,
  `tones` and `controls`.
- `CodeAtlas.locate(id)` and `CodeAtlas.fieldStats()` exist on the frame's own global for
  browser tests and tracing; the host bridge does not expose them.

Why it is built this way: the previous DOM renderer promoted 2,800 buttons and rewrote 7,400
SVG paths per frame, which cost roughly 200 ms per rotating frame and 0.1–0.2 s per hover on
the real repository; a paged 120-entity view was tried and rejected as showing too little.
Do not reintroduce per-entity DOM, SVG links or per-node CSS animations into the Code graph view.

Beside the map, the sidebar's file list switches between **Report files** (the scanned sources)
and **Git changes**: `GET /api/v1/code-analyzer/changes` lists the working tree's changes against
HEAD (status, staged/unstaged, line counts, binary; at most 2,000 entries) and
`GET /api/v1/code-analyzer/changes/diff?path=` serves one file's HEAD and working-tree text
(1,000,000 characters per side, a 5 MiB read bound, binary reported rather than returned); a
renamed entry also passes `original=` so the HEAD side is read at its old path, validated like
the path itself. Both
are read-only, mapped on the active root backend only, require both credentials and apply the
same safe-path rules as graph priorities plus the working-tree path guard. A row opens the shared
Monaco diff viewer (`diff-modal.js`) with every change in its rail, loading each diff on first
view; rows for files on the map can also focus them. The same git list feeds Atlas's **Highlight
changes**. The last graph is cached per report (`startedUtc`) and request, so returning to the
page replays it without capturing the repository again; a new scan refreshes it.

These integration patches live in the shipped `vendor/atlas/code-atlas.mjs` HTML template (an
escaped string literal on line 2; decode it to review). Retain them when refreshing the approved
vendor bundle; `code-map-field.test.mjs` executes its embedded layout module directly, and browser
tests exercise the real sandbox.

Atlas keeps its opaque-origin sandbox and MessageChannel lifecycle. Pass the host's
`window.__viberails_NONCE__`; do not add `allow-same-origin`, eval, or CSP exceptions.

## Verification

Backend graph and authenticated route regressions live under `Tests/Services/CodeReports`
and `Tests/Routes/CodeGraphRoutesTests.cs`. `UITests/tests/code-quality-ux.spec.js` covers the
real frontend with test-only supplied data: selection, the canvas field at 224 and 2,700
entities (the latter under 4x CPU throttling, with hover veil, keyboard rotation and search),
signals and reduced motion, Cards curves, mouse rotation inertia, the ambient link budget, the
Git changes list and diff viewer, permanent dependency exclusion, saved metrics, scrolling,
radar keyboard interaction, themes, narrow sizes, independent errors, escaped excerpts and
navigation races. Run it with
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
