# Session viewer

The shared read-only session viewer, promoted from the `vibe-books/session_replay_2`
lab on 2026-09-30 (VB-6TYZ2-4). It powers desktop history, Board and Automation replay,
and the uploaded-session page in VibeRails-Front. It does not control a live terminal.

## Embedding

```js
import { mountSessionViewer } from './session-replay/viewer.mjs';

const viewer = mountSessionViewer(container, {
    sessionId,
    request: (path, { signal }) => recordingApi(path, signal),
    library: true,
    autoplay: false,
    onEvent: ({ type, state, error }) => { /* host UI */ }
});
await viewer.ready;
await viewer.seek(viewer.getState().started + 10000);
// Before removing the host or closing its modal:
viewer.dispose();
```

Give the host an explicit height (at least 540px). The component appends its own
iframe and loading status; it leaves other host children alone. Browser embeds use
a trusted static `srcdoc` template and inherit the host origin. Each document owns
all DOM IDs, styles, library globals, keyboard handlers, terminal and editor state.
Multiple instances can coexist. The data adapter runs in the host, so existing
cookie/header authentication stays there. No recording text is inserted into the template.
Serve the bundle and `../assets/{xterm,monaco,tom-select}` locally. No npm package,
framework, remote CDN, new server or production build-time sibling dependency is required.

VS Code desktop webviews first load their own same-origin `fake.html?id=…` shell,
then write the trusted template into it. VS Code's resource service worker requires
a controlled client carrying the webview ID; `srcdoc` cannot load those resources.
The extension permits `frame-src 'self'`, and the viewer copies the host's meta CSP
into the replacement document before loading assets. Scripts retain the existing
nonce/resource-origin restrictions. Closing during either load aborts initialization;
each frame load has a 15-second timeout. Keep the real-webview regression when changing
this integration, since ordinary browser CSP tests do not exercise VS Code resource routing.

### Controls and state

`ready` resolves after initialization and optional initial load. Asset errors reject
it. Recording errors appear inside the viewer and emit `error`; inspect `state.ready`.
Methods: `load(id)`, `play()`, `pause()`, `seek(utcMilliseconds)`, `setSpeed(1|2|5|10|25|'max')`,
`setSkipIdle(boolean)`, `reload()`, and idempotent `dispose()`. Max has no clock: each animation
frame writes the next ~256 KB of output until the recording end, so Pause still stops it part way.
The batch boundary is a frame index, not a timestamp, so frames that share an instant can span
ticks; playback ends (and Play restarts) only when `position` is at the end **and** every frame up
to it has been written (`playbackComplete` in `timeline.mjs`), not merely when the end is reached.
There is one layout (terminal, code and events together); the Simple view, `setView`, the `view`
option and `getState().view` were removed in VIBE-65.
Async controls wait for initialization. Pause also cancels pending initial autoplay.
`seekToUtc` accepts an epoch timestamp or ISO string and applies a 1.5s lead-in, used
for Board comments. A seek past the recording end stays finished rather than restarting.

`getState()` returns sessionId, ready, busy, playing, position, started, end,
frameIndex, frameCount, cols and rows. Times are UTC epoch milliseconds.
`subscribe(listener)` returns an unsubscribe function. Events are `state`, `loaded`,
`error` (with error text), and `close-request` (unhandled Escape inside an embed).
Hosts decide whether to close on that request. Inspector/dialog Escape takes priority.
Controls are ignored while a terminal rebuild is in progress; loads supersede older
loads and abort their requests. Disposal aborts requests, invalidates pending results,
stops playback/timers, disconnects the resize observer, disposes terminal/editor/models
and selects, then removes the owned window and its remaining listeners.

### Data adapter

`request(path, {signal})` returns parsed objects for the lab contract:

| Path | Result |
| --- | --- |
| `/api/status` | `{sources:[{name,available}]}` |
| `/api/sessions?q=&offset=` | `{items:SessionInfo[],nextOffset}`; -1 ends paging |
| `/api/sessions/{id}` | Manifest with session, prompts, changes, geometry, cards, frameSource, frameMaxId, proxyMaxId, frameCount, frameBytes, end, notes |
| `…/frames?after=&max=&source=` | `{items:[{id,at,data,cols,rows}],next,done}` |
| `…/exchanges?after=&max=` | `{items:Exchange[],next,done}` |
| `…/changes/{id}` | `{id,path,diff}` |
| `…/exchanges/{id}` | `{id,before,after,response,displayTruncated,captureTruncated}` |

Full record fields are in `VibeRails.Data.Abstractions/Replay/ReplayRecords.cs`.
Frame data accepts base64 or Uint8Array. Raw and enriched streams are alternatives:
geometry byte counts align resizes within raw chunks, including final zero-byte resizes.
Snapshot maximum IDs bound pages; exact session IDs scope every detail lookup.
Patches describe prompt windows, not complete files. Proxy timestamps mark completed
captures, not tool execution. A reload takes a new snapshot for an open session.

Desktop maps these paths to `/api/v1/session-replay` using its existing session/tab
credentials. IReplayStore reads SQLite in ReadOnly/query_only mode. Board metadata comes
from IBoardStore. The API adds no migrations or diagnostic writes. Hosted replay uses
`createEnvelopeSource(envelope)` and the envelope already fetched by its owner-authenticated
session page. It shows prompts and saved patches, plus proxy/model/tool details when the upload
includes them. Older uploads have no proxy captures. Board context is not included in envelopes.
Missing metadata/patches/output have explicit empty states.

The manifest's nullable `tokensSaved` is the estimated net request reduction for this
recording: sum the attributed proxy captures' `charsBefore - charsAfter`, clamp the
total at zero, then divide by four (rounding down once). The header labels it as an
estimate; these stored UTF-16 character sizes are not provider token usage or the
live meter's wire-byte tally. Only exact session IDs contribute. A snapshot's total
and proxy maximum ID are read together; reload an open recording to update it.
No attributed measurements means “Unavailable”, while measured passthroughs show zero.
This uses existing captures without schema changes or historical backfills.
Full uploads use `proxyExchanges`; compact website playback retains only
`proxySavings: [{sessionId, charsBefore, charsAfter}]`, so savings remain visible
without transferring proxy bodies. Older uploads without either show “Unavailable”.

### Exchange summary limits

Summary reads select at most 2,000,000 response characters in SQLite before materializing or
parsing them. Request model/effort extraction uses the same prefix bound; an incomplete request
prefix has no extracted metadata. Captured bodies stay unchanged and the existing detail view
retains its separate display bound.

Desktop and uploaded-envelope tool summaries retain at most 64 calls, 256 characters per id/name,
8,192 argument characters per call and 65,536 argument characters per exchange. The limits also apply while accumulating SSE
fragments. `ParseNote` identifies truncated summaries and malformed JSON/event shapes; valid
events and subsequent exchanges still load. Invalid indexes, arrays, objects and escaped Unicode
cannot abort the recording. The uploaded parser applies the same two-million-character input
bound itself, retains bounded generated IDs for calls without IDs, and reports both malformed
data and display truncation when they occur together. Raw capture details remain available.

A desktop exchange page contains at most 30 entries and a conservative 1 MiB JSON budget, including
worst-case string escaping. The cursor advances only over returned entries, so a page stopped
by the byte budget leaves its next exchange for the following request. The limits bound display
and request memory; they are not capture quotas or retention rules.

## Theme

`style.css` declares the app's Focus Dark tokens (`--color-bg-base`, `--color-primary`, …) with
the same names and values as `wwwroot/style.css`, and derives every color from them; buttons use
the app's outline register. `session-viewer-timeline.test.mjs` fails if a token drifts from the
app. The xterm palette is the live terminal's: VibeTerminal's default, or the Terminal settings
theme saved in `localStorage.viberails_terminal_theme` and listed by
`../assets/xterm/terminal-themes.js` (optional; without it the default applies). The editor uses
the app's `viberails-dark` colors. The iframe does not follow the VS Code theme bridge.

## Mobile

At widths up to 720px, the player displays selectable HTML read from xterm's settled
screen instead of shrinking terminal text. It samples every three seconds while playing,
and immediately after pause, seek, restart or end. Reading reflows soft-wrapped lines;
Screen lines preserves spacing and scrolls horizontally. ANSI parsing still happens in
the replay-only xterm instance. This is a best-effort current screen, not a transcript.
There is no separate Session v2 panel or live-terminal replacement.

## Sharing and checks

This directory is canonical. Check recipient diffs before syncing: the website currently
has its own simple view and envelope caching/timestamp fixes. Preserve those changes when
porting viewer features until the copies are reconciled. From the desktop repo root, the
explicit full-copy commands are:

```powershell
node Scripts/sync-session-viewer.mjs ../vibe-books/session_replay_2/wwwroot ../VibeRails-Front/VibeRails-Front/wwwroot/session-replay
node Scripts/sync-session-viewer.mjs --check ../vibe-books/session_replay_2/wwwroot ../VibeRails-Front/VibeRails-Front/wwwroot/session-replay
node --test Tests/wwwroot/js/session-viewer*.test.mjs
dotnet test Tests/Tests.csproj --filter 'FullyQualifiedName~ReplayStoreTests|FullyQualifiedName~SessionReplayRoutesTests'
npm --prefix UITests exec -- playwright test --config playwright.replay.config.js
```

Run Playwright from `UITests` if npm does not forward its working directory. The lab's
`/` is the standalone example; `/embed.html?session=<id>` demonstrates the same mount API.
The website's `TestingUIs/sessions-harness/serve.py` and `replay-smoke.mjs` check the real
session page without starting its production services. Bundled assets retain their licenses.

The extension's `session-replay-webview.test.ts` uses the production webview CSP with
fixture recordings and no backend. It covers assets, styles, playback, independent
instances, Monaco patches, script restrictions, close during initialization and reopening.
Set `VIBERAILS_TEST_GREP=Session replay in a VS Code webview` to run it alone. In an
Electron-hosted shell, unset `ELECTRON_RUN_AS_NODE` and set `VIBERAILS_VSCODE_CLI` to
the full `Code.exe` path before running `npm test` from `vscode-viberails`.
