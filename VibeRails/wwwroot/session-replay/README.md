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
    view: 'simple',
    onEvent: ({ type, state, error }) => { /* host UI */ }
});
await viewer.ready;
await viewer.seek(viewer.getState().started + 10000);
// Before removing the host or closing its modal:
viewer.dispose();
```

Give the host an explicit height (at least 540px). The component appends its own
iframe and loading status; it leaves other host children alone. Each iframe uses
a trusted static `srcdoc` template and inherits the host origin. Its document owns
all DOM IDs, styles, library globals, keyboard handlers, terminal and editor state.
Multiple instances can coexist. The data adapter runs in the host, so existing
cookie/header authentication stays there. No recording text is inserted into srcdoc.
Serve the bundle and `../assets/{xterm,monaco,tom-select}` locally. No npm package,
framework, remote CDN, new server or production build-time sibling dependency is required.

### Controls and state

`ready` resolves after initialization and optional initial load. Asset errors reject
it. Recording errors appear inside the viewer and emit `error`; inspect `state.ready`.
Methods: `load(id)`, `play()`, `pause()`, `seek(utcMilliseconds)`, `setSpeed(1|2|5|10|25|100)`,
`setSkipIdle(boolean)`, `setView('simple'|'advanced')`, `reload()`, and idempotent `dispose()`.
Async controls wait for initialization. Pause also cancels pending initial autoplay.
`seekToUtc` accepts an epoch timestamp or ISO string and applies a 1.5s lead-in, used
for Board comments. A seek past the recording end stays finished rather than restarting.

`getState()` returns sessionId, ready, busy, playing, position, started, end,
frameIndex, frameCount, cols, rows and view. Times are UTC epoch milliseconds.
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
session page. It shows prompts and saved patches; Board and proxy/tool details are unavailable.
Missing metadata/patches/output have explicit empty states.

## Mobile

At widths up to 720px, the player displays selectable HTML read from xterm's settled
screen instead of shrinking terminal text. It samples every three seconds while playing,
and immediately after pause, seek, restart or end. Reading reflows soft-wrapped lines;
Screen lines preserves spacing and scrolls horizontally. ANSI parsing still happens in
the replay-only xterm instance. This is a best-effort current screen, not a transcript.
There is no separate Session v2 panel or live-terminal replacement.

## Sharing and checks

This directory is canonical. From the desktop repo root, update the two copies explicitly:

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
