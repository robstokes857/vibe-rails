import { durationLabel, prepareFrames, upperBound, advancePosition, eventsFor } from './timeline.mjs';
import { ScreenPreview } from './screen-preview.mjs';

let disposed = false, host = null;
const lifetime = new AbortController();
const embedded = window.frameElement?.dataset.sessionReplay === 'true';
const $ = id => document.getElementById(id);
const text = (id, value) => { $(id).textContent = value; };
const basename = path => path.split(/[\\/]/).filter(Boolean).at(-1) || path;
const icons = { tool: '↗', proxy: '⇄', change: '±', prompt: '›' };
let manifest = null, frames = [], exchanges = [], events = [], position = 0, frameIndex = 0;
let playing = false, busy = false, ready = false, lastTick = 0, version = 0;
let loadController = null, term = null, editor = null, editorModel = null, editorPromise = null, patchDecorations = null;
let selectedChange = null, changeRequest = 0, eventSignature = '', lastFollowId = null;
let libraryOffset = 0, searchRequest = 0, searchTimer = null, libraryItems = [];
let toastTimers = [], tickHandle = null;
let viewMode = 'simple';
let selectedEvent = null, eventRequest = 0;
let filePage = 0, eventPage = null;
const filePageSize = 100, eventPageSize = 60;
const screenPreview = new ScreenPreview({ content: $('html-screen'), status: $('html-status'), time: $('html-time'),
    geometry: $('html-geometry'), refresh: $('html-refresh'), modeButtons: [...document.querySelectorAll('[data-screen-mode]')],
    phone: $('html-phone'), shell: $('html-shell') });
screenPreview.reset('Choose a recording to preview its screen.');
function sampleScreen(force = false) {
    if (!ready || !term) return;
    screenPreview.sample(term, { force, label: durationLabel(position - manifest.session.started), playing, hasFrames: frames.length > 0 });
}
$('html-refresh').addEventListener('click', () => { if (!busy) sampleScreen(true); });

// Keep the native selects as the source of values; Tom Select owns their UI.
const selects = {};
for (const id of ['speed', 'activity-filter']) {
    selects[id] = new TomSelect($(id), {
        create: false, maxItems: 1, maxOptions: 200, allowEmptyOption: false, closeAfterSelect: true,
        dropdownParent: 'body', controlInput: null,
        onInitialize() { this.control_input.setAttribute('aria-label', $(id).getAttribute('aria-label')); }
    });
}

function setView(mode) {
    viewMode = mode === 'advanced' ? 'advanced' : 'simple';
    document.body.dataset.view = viewMode;
    $('view-simple').setAttribute('aria-pressed', String(viewMode === 'simple'));
    $('view-advanced').setAttribute('aria-pressed', String(viewMode === 'advanced'));
    text('view-description', viewMode === 'simple' ? 'Just the session. At your pace.' : 'Terminal, code, and tools in sync.');
    try { localStorage.setItem('replay-view', viewMode); } catch { /* Storage may be disabled. */ }
    Object.values(selects).forEach(select => select.close());
    clearToasts();
    if (viewMode === 'simple') expandInspector(false);
    else if (ready) {
        renderPosition(true);
    }
    requestAnimationFrame(() => { fitTerminal(); editor?.layout(); });
    emitState();
}
$('view-simple').addEventListener('click', () => setView('simple'));
$('view-advanced').addEventListener('click', () => setView('advanced'));
try { viewMode = localStorage.getItem('replay-view') || 'simple'; } catch { /* Use Simple by default. */ }
setView(viewMode);

function setInspectorTab(tab, focus = false) {
    for (const name of ['code', 'events']) {
        $(name + '-tab').setAttribute('aria-selected', String(name === tab));
        $(name + '-tab').tabIndex = name === tab ? 0 : -1;
        $(name + '-panel').hidden = name !== tab;
    }
    selects['activity-filter'].close();
    if (focus) $(tab + '-tab').focus();
    if (ready) {
        if (tab === 'code') renderFiles(true);
        else renderActivity(true);
    }
    if (tab === 'code') requestAnimationFrame(() => editor?.layout());
}
for (const tab of ['code', 'events']) {
    $(tab + '-tab').addEventListener('click', () => setInspectorTab(tab));
    $(tab + '-tab').addEventListener('keydown', event => {
        if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
        event.preventDefault();
        setInspectorTab(event.key === 'Home' ? 'code' : event.key === 'End' ? 'events' : tab === 'code' ? 'events' : 'code', true);
    });
}

function notice(message = '', error = false) {
    text('notice', message); $('notice').hidden = !message; $('notice').classList.toggle('error', error);
}
async function api(path, signal) {
    if (disposed) throw new DOMException('Viewer disposed', 'AbortError');
    signal = signal ? AbortSignal.any([signal, lifetime.signal]) : lifetime.signal;
    if (host?.request) return host.request(path, { signal });
    const response = await fetch(path, { signal, cache: 'no-store' });
    if (!response.ok) {
        let message = `Request failed (${response.status})`;
        try { message = (await response.json()).error || message; } catch { /* Not a JSON error response. */ }
        throw new Error(message);
    }
    return response.json();
}
function node(tag, className, value) {
    const element = document.createElement(tag);
    if (className) element.className = className;
    if (value !== undefined) element.textContent = value;
    return element;
}
function enableControls() {
    for (const id of ['play', 'restart', 'rewind', 'forward', 'scrubber']) $(id).disabled = !ready || busy;
    $('file-search').disabled = !ready || !manifest?.changes.length;
    $('file-list').setAttribute('aria-busy', String(busy));
    $('jump-change').disabled = !ready || busy || !selectedChange;
    $('jump-event').disabled = !ready || busy || !selectedEvent;
    $('open-event-code').disabled = !ready || busy;
    $('follow-events').disabled = !ready;
    $('reload').disabled = !manifest || busy;
    $('html-refresh').disabled = !ready || busy;
    $('html-shell').setAttribute('aria-busy', String(busy));
    emitState();
}
function pause() {
    playing = false; cancelAnimationFrame(tickHandle);
    $('play').replaceChildren(document.createTextNode('▶ '), node('span', '', 'Play'));
    updatePlaybackStatus();
    emitState();
    if (!busy) sampleScreen(true);
}
function updatePlaybackStatus() {
    if (ready && manifest) text('playback-status', position >= manifest.end ? 'End of recording' : playing ? 'Playing' : 'Paused · ready to play');
}
function clearToasts() {
    toastTimers.forEach(clearTimeout); toastTimers = []; $('toast-stack').replaceChildren();
}
function fitTerminal() {
    if (!term) return;
    const width = $('terminal-viewport').clientWidth - 28;
    term.options.fontSize = Math.max(8, Math.min(14, Math.floor(width / (term.cols * .61))));
    text('geometry', `${term.cols} × ${term.rows}`);
}
function createTerminal() {
    term?.dispose(); $('terminal').replaceChildren();
    if (!window.Terminal) throw new Error('The xterm assets are missing. Rebuild the project with VibeRailsRoot pointing at the main checkout.');
    const first = frames[0] || manifest?.geometry[0];
    term = new window.Terminal({ cols: first?.cols || 120, rows: first?.rows || 30, cursorBlink: false, disableStdin: true, allowProposedApi: true,
        scrollback: 8000, fontSize: 12, fontFamily: 'Cascadia Code, Consolas, monospace', lineHeight: 1.14,
        theme: { background: '#101319', foreground: '#cbd3e2', cursor: '#b4a3ff', selectionBackground: '#55457988',
            black: '#202632', red: '#f392a4', green: '#8bd2ab', yellow: '#e8cd91', blue: '#94bfff', magenta: '#c7adff', cyan: '#88cfd5', white: '#d6dbea' } });
    term.open($('terminal')); fitTerminal();
}
const terminalObserver = new ResizeObserver(fitTerminal);
terminalObserver.observe($('terminal-viewport'));

async function loadSession(id) {
    if (disposed) return;
    const run = ++version;
    loadController?.abort(); loadController = new AbortController(); const signal = loadController.signal;
    pause(); clearToasts(); ready = false; busy = true; enableControls(); notice();
    screenPreview.reset('Loading the recording’s screen…');
    $('sessions-dialog').close(); $('terminal-empty').hidden = true;
    term?.dispose(); term = null; $('terminal').replaceChildren();
    editor?.setModel(null); editorModel?.dispose(); editorModel = null;
    frames = []; exchanges = []; events = []; frameIndex = 0; selectedChange = null; lastFollowId = null;
    changeRequest++; eventRequest++; $('detail-dialog').close();
    selectedEvent = null; filePage = 0; eventPage = null; eventSignature = '';
    $('file-search').value = ''; $('file-list').replaceChildren(node('div', 'small-empty', 'Reading captured files…'));
    $('event-content').hidden = true; $('event-empty').hidden = false; $('open-event-code').hidden = true;
    text('event-body', ''); $('event-tabs').replaceChildren();
    $('activity-list').replaceChildren(node('div', 'small-empty', 'Reading the recording…'));
    showCodeEmpty('Loading code captures…');
    text('playback-status', 'Reading session metadata…');
    try {
        const data = await api(`/api/sessions/${encodeURIComponent(id)}`, signal);
        if (run !== version) return;
        manifest = data; position = data.session.started;
        renderMetadata();
        text('playback-status', `Loading ${data.frameCount.toLocaleString()} terminal frames…`);
        const raw = []; let loaded = 0;
        await Promise.all([
            (async () => {
                let cursor = 0;
                while (cursor < data.frameMaxId) {
                    const page = await api(`/api/sessions/${id}/frames?after=${cursor}&max=${data.frameMaxId}&source=${data.frameSource}`, signal);
                    if (run !== version) return;
                    for (const frame of page.items) {
                        frame.data = typeof frame.data === 'string' ? Uint8Array.from(atob(frame.data), character => character.charCodeAt(0)) : new Uint8Array(frame.data);
                        raw.push(frame);
                    }
                    loaded += page.items.length;
                    text('playback-status', `Loading terminal ${loaded.toLocaleString()} / ${data.frameCount.toLocaleString()} · ${exchanges.length} proxy exchanges`);
                    if (page.done) break;
                    if (page.next <= cursor) throw new Error('The terminal page did not advance. Reload the recording.');
                    cursor = page.next;
                }
            })(),
            (async () => {
                let cursor = 0;
                while (cursor < data.proxyMaxId) {
                    const page = await api(`/api/sessions/${id}/exchanges?after=${cursor}&max=${data.proxyMaxId}`, signal);
                    if (run !== version) return;
                    exchanges.push(...page.items);
                    text('playback-status', `Loading terminal ${loaded.toLocaleString()} / ${data.frameCount.toLocaleString()} · ${exchanges.length} proxy exchanges`);
                    if (page.done) break;
                    if (page.next <= cursor) throw new Error('The proxy page did not advance. Reload the recording.');
                    cursor = page.next;
                }
            })()
        ]);
        if (run !== version) return;
        frames = prepareFrames(raw, data.geometry, data.frameSource);
        // Preserve captured byte order even if the machine clock moved backwards.
        let previous = data.session.started;
        for (const frame of frames) { frame.at = Math.max(previous, frame.at); previous = frame.at; }
        exchanges.sort((a, b) => a.at - b.at || a.cursor - b.cursor);
        events = eventsFor(data, exchanges);
        manifest.end = Math.max(manifest.end, frames.at(-1)?.at || 0, events.at(-1)?.at || 0);
        createTerminal(); renderMetadata(); renderFiles(); renderActivity(true); renderMarkers();
        if (!embedded) { const params = new URLSearchParams({ session: id }); history.replaceState(null, '', `?${params}`); }
        ready = true; busy = false;
        if (!frames.length) {
            $('terminal-empty').hidden = false;
            $('terminal-empty').replaceChildren(node('div','empty-symbol','›_'), node('h3','','No terminal output in this recording'), node('p','','You can still explore any captured code and proxy activity.'));
        }
        const issues = [];
        if (loaded !== data.frameCount) issues.push('Some terminal rows changed while loading. Reload the snapshot.');
        if (exchanges.some(exchange => exchange.truncated)) issues.push('Some proxy responses were truncated during capture.');
        if (data.notes.some(note => note.includes('byte totals differ'))) issues.push('Some terminal resize metadata is incomplete.');
        if (data.session.ended === null) issues.push('Open session · viewing a snapshot. Reload for newer activity.');
        notice(issues.join(' '));
        await seek(data.session.started);
        emitState('loaded');
        return getState();
    } catch (error) {
        if (run !== version || error.name === 'AbortError') return;
        loadController.abort(); ready = false; notice(error.message, true); text('playback-status', 'Could not load this recording');
        screenPreview.reset('Could not load the screen. Reload the recording to try again.');
        emitState('error', error.message);
    } finally { if (run === version) { busy = false; enableControls(); } }
}

function renderMetadata() {
    const { session, cards } = manifest;
    text('project', (session.project || basename(session.directory) || 'SESSION RECORDING').toUpperCase());
    text('session-title', cards[0]?.title || session.title || `${session.cli} session`);
    text('session-id', session.id); $('copy-id').hidden = false;
    text('session-state', session.ended ? 'Completed' : 'Open · snapshot'); $('session-state').hidden = false;
    text('started', new Date(session.started).toLocaleString(undefined, { month:'short',day:'numeric',hour:'numeric',minute:'2-digit' }));
    text('duration', durationLabel(manifest.end - session.started)); text('total', durationLabel(manifest.end - session.started));
    text('environment', session.environment || session.cli); $('environment').title = session.environment;
    text('board', cards.map(card => card.key).join(', ') || 'No linked card'); $('board').title = cards.map(card => `${card.key}: ${card.title}`).join('\n');
    text('working-directory', session.directory); $('working-directory').title = session.directory;
    text('change-count', manifest.changes.length); text('activity-count', events.length);
    $('scrubber').max = Math.max(1, manifest.end - session.started);
    updateModel();
}
function updateModel() {
    const available = exchanges.filter(exchange => exchange.model);
    const last = available[upperBound(available, position) - 1] || available[0];
    text('model', last?.model || manifest?.session.cli || '—');
    text('effort', last?.effort || 'Not captured');
    $('model').title = last ? `From ${last.provider} request ${last.id}` : 'Model was not captured; showing the session CLI';
}
function filteredFiles() {
    const query = $('file-search').value.trim().toLowerCase();
    return (manifest?.changes || []).filter(change => change.path.toLowerCase().includes(query));
}
function renderFiles(follow = false) {
    const files = filteredFiles();
    const selectedIndex = files.findIndex(change => change.id === selectedChange?.id);
    if (follow && selectedIndex >= 0) filePage = Math.floor(selectedIndex / filePageSize);
    filePage = Math.max(0, Math.min(filePage, Math.ceil(files.length / filePageSize) - 1));
    const start = filePage * filePageSize;
    const fragment = document.createDocumentFragment();
    for (const change of files.slice(start, start + filePageSize)) {
        const row = node('button', 'file-row'); row.dataset.changeId = change.id;
        row.title = `${change.path} · ${change.timing}`;
        row.setAttribute('aria-pressed', String(change.id === selectedChange?.id));
        const label = node('span', 'file-row-label');
        label.append(node('strong', '', basename(change.path)), node('small', '', change.path));
        const stats = node('span', 'file-row-stats');
        stats.append(node('span', 'event-time', durationLabel(change.at - manifest.session.started)),
            node('span', change.hasDiff ? 'added' : 'muted', change.hasDiff ? `+${change.added ?? '—'} / −${change.deleted ?? '—'}` : 'No patch'));
        row.append(node('span', 'event-icon change', '±'), label, stats);
        row.addEventListener('click', () => chooseChange(change)); fragment.append(row);
    }
    if (!files.length) fragment.append(node('div', 'small-empty', manifest?.changes.length ? 'No files match this filter.' : 'No code captures in this recording.'));
    $('file-list').replaceChildren(fragment);
    text('file-list-status', files.length ? `${start + 1}–${Math.min(start + filePageSize, files.length)} of ${files.length} captures` : '0 captures');
    $('files-previous').disabled = filePage === 0;
    $('files-next').disabled = start + filePageSize >= files.length;
    if (follow) {
        const row = $('file-list').querySelector('[aria-pressed="true"]');
        if (row) $('file-list').scrollTop = row.offsetTop - $('file-list').offsetTop;
    }
}
function renderMarkers() {
    const fragment = document.createDocumentFragment(), seen = new Set();
    for (const event of events) {
        if (event.kind === 'proxy') continue;
        const percent = Math.max(0, Math.min(100, (event.at - manifest.session.started) / Math.max(1, manifest.end - manifest.session.started) * 100));
        const key = event.kind + Math.round(percent * 4); if (seen.has(key)) continue; seen.add(key);
        const mark = node('i', `timeline-mark ${event.kind}`); mark.style.left = `${percent}%`; fragment.append(mark);
    }
    $('timeline-markers').replaceChildren(fragment);
}

async function writeUntil(target, run) {
    const endIndex = upperBound(frames, target);
    while (frameIndex < endIndex && run === version) {
        const first = frames[frameIndex], currentTerm = term;
        if (currentTerm.cols !== first.cols || currentTerm.rows !== first.rows) { currentTerm.resize(first.cols, first.rows); fitTerminal(); }
        const batch = []; let length = 0;
        while (frameIndex < endIndex && frames[frameIndex].cols === first.cols && frames[frameIndex].rows === first.rows && length < 256 * 1024) {
            const bytes = frames[frameIndex++].data; batch.push(bytes); length += bytes.length;
        }
        if (length) {
            const bytes = new Uint8Array(length); let offset = 0;
            for (const part of batch) { bytes.set(part, offset); offset += part.length; }
            await new Promise(resolve => currentTerm.write(bytes, resolve));
        }
    }
}
async function seek(target) {
    if (!ready || busy || !Number.isFinite(target)) return;
    const run = version; pause(); clearToasts(); busy = true; enableControls();
    target = Math.max(manifest.session.started, Math.min(manifest.end, target));
    text('playback-status', 'Rebuilding terminal…');
    try {
        if (target < position) { createTerminal(); frameIndex = 0; }
        await writeUntil(target, run);
        if (run !== version) return;
        position = target; renderPosition(true);
    } finally { if (run === version) { busy = false; enableControls(); } }
}
async function tick(now) {
    if (!playing || busy) return;
    const run = version;
    const elapsed = Math.max(0, Math.min(now - lastTick, 250)); lastTick = now;
    const nextActivity = Math.min(frames[frameIndex]?.at ?? Infinity, events[upperBound(events, position)]?.at ?? Infinity, manifest.end);
    const target = advancePosition(position, elapsed, Number($('speed').value), manifest.end, nextActivity, $('skip-idle').checked);
    busy = true;
    try {
        await writeUntil(target, run);
        if (run !== version) return;
        const old = position; position = target;
        if (playing) {
            const calls = events.slice(upperBound(events, old), upperBound(events, position)).filter(event => event.kind === 'tool');
            for (const event of calls.slice(-3)) showToast(event);
        }
        renderPosition(false);
        if (position >= manifest.end) { pause(); sampleScreen(true); }
    } catch (error) { pause(); notice(error.message, true); }
    finally {
        if (run === version) { busy = false; enableControls(); if (playing) tickHandle = requestAnimationFrame(tick); }
    }
}
async function togglePlay() {
    if (!ready) return;
    if (playing) { pause(); return; }
    if (busy) return;
    if (position >= manifest.end) await seek(manifest.session.started);
    if (disposed || !ready) return;
    playing = true; emitState(); lastTick = performance.now();
    sampleScreen(true);
    updatePlaybackStatus();
    const pauseIcon = node('span', 'pause-icon'); pauseIcon.setAttribute('aria-hidden', 'true');
    $('play').replaceChildren(pauseIcon, node('span', '', 'Pause'));
    tickHandle = requestAnimationFrame(tick);
}
function renderPosition(force) {
    emitState();
    text('position', durationLabel(position - manifest.session.started)); $('scrubber').value = position - manifest.session.started;
    text('frame-count', `${frameIndex.toLocaleString()} / ${frames.length.toLocaleString()} frames`);
    updatePlaybackStatus();
    sampleScreen(force || !playing);
    if (viewMode === 'simple') return;
    updateModel(); renderActivity(force);
    const reached = manifest.changes.filter(change => change.at <= position).at(-1);
    if (reached?.id !== lastFollowId) {
        lastFollowId = reached?.id;
        if (reached) void showChange(reached);
        else { selectedChange = null; changeRequest++; renderFiles(); showCodeEmpty('No code changes yet', 'Choose a file below or play to its capture time.'); }
    }
}
function renderActivity(force = false) {
    const filter = $('activity-filter').value;
    const filtered = filter === 'all' ? events : events.filter(event => event.kind === filter);
    const reached = upperBound(filtered, position);
    const page = eventPage ?? Math.floor(Math.max(0, reached - 1) / eventPageSize);
    const start = Math.max(0, Math.min(page, Math.ceil(filtered.length / eventPageSize) - 1)) * eventPageSize;
    const signature = `${filter}:${reached}:${events.length}:${start}:${events.indexOf(selectedEvent)}`;
    if (!force && signature === eventSignature) return; eventSignature = signature;
    const shown = filtered.slice(start, start + eventPageSize);
    const fragment = document.createDocumentFragment();
    for (const event of shown) {
        const row = node('button', `activity-row ${event.at > position ? 'future' : ''}`);
        row.title = `Inspect ${event.kind === 'proxy' ? 'request' : event.kind}: ${event.title}`;
        row.dataset.eventIndex = events.indexOf(event);
        row.setAttribute('aria-pressed', String(event === selectedEvent));
        row.append(node('span', `event-icon ${event.kind}`, icons[event.kind]), node('span', 'event-text', event.title.replace(/\s+/g, ' ').slice(0, 200)), node('span', 'event-time', durationLabel(event.at - manifest.session.started)));
        if (event === filtered[reached - 1]) row.classList.add('at-playhead');
        row.addEventListener('click', () => openEvent(event)); fragment.append(row);
    }
    if (!shown.length) fragment.append(node('div','small-empty',filter === 'all' ? 'No prompt, code, or proxy activity was captured.' : `No ${filter} events were captured.`));
    $('activity-list').replaceChildren(fragment);
    text('event-list-status', filtered.length ? `${start + 1}–${Math.min(start + eventPageSize, filtered.length)} of ${filtered.length} events` : '0 events');
    $('events-previous').disabled = start === 0;
    $('events-next').disabled = start + eventPageSize >= filtered.length;
    $('follow-events').setAttribute('aria-pressed', String(eventPage === null));
    const active = $('activity-list').querySelector(eventPage === null ? '.at-playhead' : '[aria-pressed="true"]');
    if (active) $('activity-list').scrollTop = active.offsetTop - $('activity-list').offsetTop - 40;
}
function showToast(event) {
    if (viewMode !== 'advanced') return;
    const toast = node('button', 'tool-toast');
    const content = node('span', 'event-text'); content.append(node('span','toast-label','TOOL CALL'), document.createTextNode(event.tool.name));
    toast.append(node('span', 'event-icon', '↗'), content, node('span', 'event-time', durationLabel(event.at - manifest.session.started)));
    toast.addEventListener('click', () => openEvent(event)); $('toast-stack').append(toast);
    while ($('toast-stack').children.length > 3) $('toast-stack').firstElementChild.remove();
    toastTimers.push(setTimeout(() => toast.remove(), 4000));
}

function ensureEditor() {
    if (editorPromise) return editorPromise;
    editorPromise = new Promise((resolve, reject) => {
        if (!window.require?.config) { reject(new Error('Monaco assets could not load. Rebuild the project.')); return; }
        window.require.config({ paths: { vs: new URL('../assets/monaco/vs', import.meta.url).href } });
        const timeout = setTimeout(() => reject(new Error('Monaco took too long to load.')), 20000);
        window.require(['vs/editor/editor.main'], () => {
            clearTimeout(timeout);
            if (disposed) { reject(new DOMException('Viewer disposed', 'AbortError')); return; }
            monaco.editor.defineTheme('replay', { base: 'vs-dark', inherit: true, rules: [], colors: {
                'editor.background': '#151922', 'editor.foreground': '#c6cedf', 'editorLineNumber.foreground': '#4f5a70',
                'editor.lineHighlightBackground': '#1a202c', 'scrollbarSlider.background': '#49516b55' } });
            monaco.languages.register({ id:'captured-patch' });
            monaco.languages.setMonarchTokensProvider('captured-patch', { tokenizer:{ root:[
                [/^diff .*$/, 'comment'], [/^@@.*$/, 'type'], [/^(---|\+\+\+).*$/, 'type'],
                [/^\+.*$/, 'string'], [/^-.*$/, 'invalid'], [/^.*$/, '']
            ] } });
            editor = monaco.editor.create($('editor'), { value: '', language:'captured-patch', theme:'replay', readOnly:true, domReadOnly:true,
                minimap:{enabled:false}, automaticLayout:true, fontFamily:'Cascadia Code, Consolas, monospace', fontSize:11, lineHeight:19,
                scrollBeyondLastLine:false, wordWrap:'off', renderLineHighlight:'none', folding:true, lineNumbersMinChars:3,
                padding:{top:12,bottom:12}, overviewRulerLanes:0, hideCursorInOverviewRuler:true });
            editorModel = editor.getModel();
            resolve(editor);
        }, error => { clearTimeout(timeout); reject(error); });
    }).catch(error => { editorPromise = null; throw error; });
    return editorPromise;
}
function showCodeEmpty(title, description = 'Select another captured change to continue.') {
    text('file-name', title); text('file-stats',''); text('change-timing','Saved patches · prompt window timing');
    $('code-empty').replaceChildren(node('div','empty-symbol','{ }'),node('h3','',title),node('p','',description));
    $('code-empty').hidden = false; $('editor').hidden = true;
}
async function showChange(change) {
    selectedChange = change; renderFiles(true); enableControls();
    const request = ++changeRequest, run = version;
    text('file-name', basename(change.path)); $('file-name').title = change.path;
    $('file-stats').replaceChildren(node('span','added',`+${change.added ?? '—'}`),node('span','deleted',`−${change.deleted ?? '—'}`));
    text('change-timing', `${durationLabel(change.at - manifest.session.started)} · ${change.timing.toLowerCase()}`);
    if (!change.hasDiff) {
        $('code-empty').replaceChildren(node('div','empty-symbol','±'),node('h3','','Change recorded; patch unavailable'),node('p','','The capture contains file statistics only. Full file contents were not saved.'));
        $('code-empty').hidden = false; $('editor').hidden = true; return;
    }
    try {
        const [detail] = await Promise.all([api(`/api/sessions/${manifest.session.id}/changes/${change.id}`,loadController?.signal),ensureEditor()]);
        if (run !== version || request !== changeRequest) return;
        editorModel?.dispose(); editorModel = monaco.editor.createModel(detail.diff || '', 'captured-patch'); editor.setModel(editorModel);
        patchDecorations ??= editor.createDecorationsCollection();
        patchDecorations.set((detail.diff || '').split('\n').flatMap((line,index) => {
            const added=line.startsWith('+')&&!line.startsWith('+++'), removed=line.startsWith('-')&&!line.startsWith('---');
            return added||removed ? [{ range:new monaco.Range(index+1,1,index+1,1), options:{ isWholeLine:true,className:added?'patch-added':'patch-removed' } }] : [];
        }));
        $('code-empty').hidden = true; $('editor').hidden = false; editor.layout();
        text('code-description', detail.diff?.includes('... [truncated]') ? 'Saved patch · capture truncated' : 'Saved patch · read only');
    } catch (error) {
        if (run !== version || request !== changeRequest || error.name === 'AbortError') return;
        showCodeEmpty('Could not open patch', error.message);
    }
}
async function chooseChange(change) {
    if (!change || busy) return;
    await seek(change.at); await showChange(change);
}
function showDetail(kind, title, meta, body) {
    text('detail-kind',kind.toUpperCase()); text('detail-title',title); text('detail-meta',meta); text('detail-body',body);
    $('detail-tabs').replaceChildren(); if (!$('detail-dialog').open) $('detail-dialog').showModal();
}
function pretty(value) { try { return JSON.stringify(JSON.parse(value),null,2); } catch { return value || '(No body captured)'; } }
function showEventDetail(kind, title, meta, body) {
    $('event-empty').hidden = true; $('event-content').hidden = false;
    text('event-kind', kind.toUpperCase()); text('event-title', title); text('event-meta', meta); text('event-body', body);
    const exchange = selectedEvent.exchange;
    text('event-summary', durationLabel(selectedEvent.at - manifest.session.started) + (exchange ? ` · HTTP ${exchange.status} · ${(exchange.elapsedMs / 1000).toFixed(2)}s` : ' · Capture details'));
    $('event-tabs').replaceChildren(); $('event-details').scrollTop = 0;
}
async function openEvent(event) {
    pause(); selectedEvent = event;
    setInspectorTab('events');
    const filter = $('activity-filter').value;
    if (filter !== 'all' && filter !== event.kind) selects['activity-filter'].setValue('all', true);
    const filtered = events.filter(item => $('activity-filter').value === 'all' || item.kind === $('activity-filter').value);
    eventPage = Math.floor(filtered.indexOf(event) / eventPageSize);
    renderActivity(true); enableControls();
    $('open-event-code').hidden = event.kind !== 'change';
    const request = ++eventRequest, run = version;
    const timing = `${new Date(event.at).toLocaleString()} · ${durationLabel(event.at - manifest.session.started)} into recording`;
    if (event.kind === 'prompt') {
        showEventDetail('User prompt', `Prompt ${event.prompt.sequence}`, timing, event.prompt.text);
        return;
    }
    if (event.kind === 'change') {
        const change = event.change;
        showEventDetail('Code capture', basename(change.path), timing,
            `${change.path}\n\n${change.type} · +${change.added ?? '—'} / −${change.deleted ?? '—'}\n${change.timing}\n\n${change.hasDiff ? 'Open the captured file to view its saved patch.' : 'File statistics were captured, but no patch was saved.'}`);
        return;
    }
    const exchange = event.exchange;
    const metadata = `${exchange.provider} · HTTP ${exchange.status} · ${(exchange.elapsedMs/1000).toFixed(2)}s · ${exchange.model || 'Model not captured'}\n${timing}\n${exchange.method} ${exchange.path}\nRecorded at response completion.${exchange.truncated ? '\nResponse was truncated during capture.' : ''}${exchange.parseNote ? '\n' + exchange.parseNote : ''}`;
    showEventDetail(event.kind === 'tool' ? 'Emitted tool call' : 'Proxy exchange', event.title, metadata,
        event.tool ? pretty(event.tool.arguments) : 'Loading captured request and response…');
    try {
        const detail = await api(`/api/sessions/${manifest.session.id}/exchanges/${exchange.id}`, loadController?.signal);
        if (request !== eventRequest || run !== version) return;
        const tabs = [];
        if (event.tool) tabs.push(['Tool arguments', event.tool.arguments]);
        tabs.push(['Request', detail.before], ['Sent upstream', detail.after], ['Response', detail.response]);
        const buttons = tabs.map(([label, body], index) => {
            const button = node('button', '', label); button.setAttribute('aria-pressed', String(index === 0));
            button.addEventListener('click', () => {
                for (const item of buttons) item.setAttribute('aria-pressed', String(item === button));
                text('event-body', pretty(body));
            });
            return button;
        });
        $('event-tabs').replaceChildren(...buttons); text('event-body', pretty(tabs[0][1]));
        if (detail.displayTruncated) text('event-meta', metadata + '\nDisplay is limited to the first 2,000,000 characters of each body.');
    } catch (error) {
        if (request === eventRequest && run === version && error.name !== 'AbortError') text('event-body', error.message);
    }
}

async function searchSessions(append = false) {
    const request = ++searchRequest;
    if (!append) { libraryOffset = 0; libraryItems = []; $('session-list').replaceChildren(node('div','small-empty','Loading recordings…')); }
    $('load-more').disabled = true;
    try {
        const data = await api(`/api/sessions?q=${encodeURIComponent($('session-search').value.trim())}&offset=${libraryOffset}`);
        if (request !== searchRequest) return;
        libraryItems.push(...data.items); libraryOffset = data.nextOffset;
        $('session-list').replaceChildren(...libraryItems.map(session => {
            const button = node('button','session-option'); const body = node('span','session-option-body');
            body.append(node('strong','',session.title || `${basename(session.directory)} · ${session.cli}`),node('small','',`${new Date(session.started).toLocaleString()} · ${session.id.slice(0,8)} · ${session.environment || session.cli}`));
            const badges = node('span','session-badges'); badges.append(node('span','',session.changes ? `${session.changes} code changes` : 'No code captures'),node('span','muted',session.hasTerminal ? 'Terminal captured' : 'No terminal output'));
            button.append(node('span','cli-icon',session.cli.slice(0,2)),body,badges);
            button.addEventListener('click',() => loadSession(session.id)); return button;
        }));
        if (!libraryItems.length) $('session-list').append(node('div','small-empty','No matching sessions. Try another project name or session ID.'));
        text('library-status',`${libraryItems.length} recordings · newest first`); $('load-more').hidden = libraryOffset<0;
    } catch(error) { if(request===searchRequest) $('session-list').replaceChildren(node('div','small-empty',error.message)); }
    finally { if(request===searchRequest) $('load-more').disabled = false; }
}
function browse() { $('sessions-dialog').showModal(); void searchSessions(); }
$('browse').addEventListener('click',browse); $('choose-session').addEventListener('click',browse);
$('session-search').addEventListener('input',() => { clearTimeout(searchTimer); searchTimer=setTimeout(() => searchSessions(),220); });
$('load-more').addEventListener('click',() => searchSessions(true));
document.querySelectorAll('[data-close]').forEach(button => button.addEventListener('click',() => $(button.dataset.close).close()));
$('play').addEventListener('click',togglePlay);
$('restart').addEventListener('click',() => seek(manifest.session.started));
$('rewind').addEventListener('click',() => seek(position-10000)); $('forward').addEventListener('click',() => seek(position+10000));
$('scrubber').addEventListener('input',() => { pause(); text('position',durationLabel(Number($('scrubber').value))); });
$('scrubber').addEventListener('change',() => seek(manifest.session.started+Number($('scrubber').value)));
$('reload').addEventListener('click',() => loadSession(manifest.session.id));
$('activity-filter').addEventListener('change', () => { eventPage = null; renderActivity(true); });
$('file-search').addEventListener('focus', pause);
$('file-search').addEventListener('input', () => { filePage = 0; renderFiles(); });
$('files-previous').addEventListener('click', () => { pause(); filePage--; renderFiles(); $('file-list').scrollTop = 0; });
$('files-next').addEventListener('click', () => { pause(); filePage++; renderFiles(); $('file-list').scrollTop = 0; });
function pageEvents(direction) {
    pause();
    const filtered = events.filter(event => $('activity-filter').value === 'all' || event.kind === $('activity-filter').value);
    eventPage = Math.max(0, (eventPage ?? Math.floor(Math.max(0, upperBound(filtered, position) - 1) / eventPageSize)) + direction);
    renderActivity(true); $('activity-list').scrollTop = 0;
}
$('events-previous').addEventListener('click', () => pageEvents(-1));
$('events-next').addEventListener('click', () => pageEvents(1));
$('follow-events').addEventListener('click', () => { eventPage = null; renderActivity(true); });
$('jump-change').addEventListener('click', () => seek(selectedChange.at));
$('jump-event').addEventListener('click', () => seek(selectedEvent.at));
$('open-event-code').addEventListener('click', () => { setInspectorTab('code'); void chooseChange(selectedEvent.change); });
function expandInspector(expanded) {
    $('inspector-panel').classList.toggle('expanded', expanded);
    text('expand-panel', expanded ? '×  Close' : '⤢  Expand');
    $('expand-panel').setAttribute('aria-expanded', String(expanded));
    editor?.layout();
}
$('expand-panel').addEventListener('click', () => expandInspector(!$('inspector-panel').classList.contains('expanded')));
$('copy-id').addEventListener('click',async () => { try { await navigator.clipboard.writeText(manifest.session.id); text('copy-id','Copied'); setTimeout(() => text('copy-id','Copy ID'),1500); } catch { notice('Copy the session ID from the header. Clipboard access is unavailable.'); } });
$('capture-notes').addEventListener('click',() => {
    pause(); showDetail('Recording details','About this recording','Local capture sources',manifest ? [
        `Session: ${manifest.session.id}`,`Directory: ${manifest.session.directory}`,`CLI: ${manifest.session.cli}`,`Exit code: ${manifest.session.exitCode ?? 'Not captured'}`,
        `${manifest.frameCount.toLocaleString()} stored terminal frames · ${(manifest.frameBytes/1024/1024).toFixed(1)} MB`,
        `${manifest.prompts.length} prompts · ${manifest.changes.length} code captures · ${exchanges.length} proxy exchanges`,
        '',...manifest.notes,'','Proxy events are linked by exact session ID. Older unlinked exchanges cannot be assigned to this recording.',
        'Tool calls are extracted from captured responses. Their timestamp is response completion, not tool execution. Request bodies include any retained tool results.',
        'Rewind resets xterm and replays the captured byte stream. Skip idle time changes playback pacing while keeping the original timestamps.'
    ].join('\n') : 'Select a recording to view its capture details.');
});
document.addEventListener('keydown',event => {
    if (event.defaultPrevented) return;
    if (event.key==='Escape' && $('inspector-panel').classList.contains('expanded')) { event.preventDefault(); expandInspector(false); return; }
    if (event.key==='Escape' && embedded && !document.querySelector('dialog[open]')) { emitState('close-request'); return; }
    if (document.querySelector('dialog[open]') || event.target.closest('input,select,textarea,.ts-wrapper,.ts-dropdown,.monaco-editor,.event-details,.html-screen,button') || !ready) return;
    if (event.code==='Space') { event.preventDefault(); void togglePlay(); }
    if (event.key==='ArrowLeft'||event.key==='ArrowRight') { event.preventDefault(); void seek(position+(event.key==='ArrowLeft'?-10000:10000)); }
});
async function startStandalone() { try {
    const status=await api('/api/status'); text('source-status',status.sources.filter(source=>source.available).map(source=>source.name).join(' · '));
    const id=new URLSearchParams(location.search).get('session'); if(id) await loadSession(id); else browse();
} catch(error) { notice(error.message,true); } }

function getState() {
    return { sessionId: manifest?.session.id ?? null, ready, busy, playing, position,
        started: manifest?.session.started ?? 0, end: manifest?.end ?? 0,
        frameIndex, frameCount: frames.length, cols: term?.cols ?? 0, rows: term?.rows ?? 0, view: viewMode };
}
function emitState(type = 'state', error) { if (!disposed) host?.onEvent?.({ type, state: getState(), error }); }
function dispose() {
    if (disposed) return;
    disposed = true; version++; searchRequest++; changeRequest++; eventRequest++;
    playing = false; ready = false;
    lifetime.abort(); loadController?.abort(); cancelAnimationFrame(tickHandle);
    clearTimeout(searchTimer); clearToasts(); terminalObserver.disconnect();
    term?.dispose(); term = null; editor?.dispose(); editorModel?.dispose();
    Object.values(selects).forEach(select => select.destroy());
    host = null; frames = []; events = []; exchanges = []; manifest = null;
}
// This document is owned by one mount. Its window isolates IDs, CSS, AMD loaders,
// keyboard listeners and library globals; removing it releases document listeners.
window.sessionReplay = {
    configure(options) {
        host = options; document.body.dataset.embedded = 'true';
        document.body.dataset.library = String(options.library !== false);
        if (options.view) setView(options.view);
    },
    load: loadSession, pause, seek, setView, getState, dispose,
    play: () => playing ? Promise.resolve() : togglePlay(),
    setSpeed(value) { if (![1,2,5,10,25,100].includes(value)) throw new RangeError('Unsupported replay speed'); selects.speed.setValue(String(value)); },
    setSkipIdle(value) { $('skip-idle').checked = Boolean(value); },
    browse,
    reload: () => manifest ? loadSession(manifest.session.id) : Promise.resolve()
};
window.addEventListener('pagehide', dispose, { once: true });
if (!embedded) await startStandalone();
