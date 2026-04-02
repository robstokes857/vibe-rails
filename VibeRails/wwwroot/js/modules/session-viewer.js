function createModal(title, onClose) {
    const overlay = document.createElement('div');
    overlay.style.cssText = 'position:fixed;inset:0;background:rgba(0,0,0,.7);display:flex;align-items:center;justify-content:center;z-index:9999;';

    const modal = document.createElement('div');
    modal.style.cssText = 'background:#1e1e1e;border:1px solid #444;border-radius:6px;display:flex;flex-direction:column;overflow:hidden;width:90vw;max-width:1100px;height:80vh;';

    const hdr = document.createElement('div');
    hdr.style.cssText = 'display:flex;align-items:center;padding:8px 12px;border-bottom:1px solid #444;flex-shrink:0;gap:8px;';
    hdr.innerHTML = `<span style="color:#ccc;font-size:13px;font-family:monospace;flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">${title}</span>`
        + `<button style="background:none;border:none;color:#aaa;cursor:pointer;font-size:20px;line-height:1;padding:0 4px;">&times;</button>`;

    const body = document.createElement('div');
    body.style.cssText = 'flex:1;overflow:hidden;position:relative;';

    modal.append(hdr, body);
    overlay.appendChild(modal);
    document.body.appendChild(overlay);

    const close = () => { onClose?.(); overlay.remove(); };
    overlay.addEventListener('click', e => { if (e.target === overlay) close(); });
    hdr.querySelector('button').addEventListener('click', close);

    return { body, close };
}

function getApiBaseUrl() {
    return window.__viberails_API_BASE__ || '';
}

function getApiHeaders() {
    try {
        const tabToken = window.sessionStorage.getItem('viberails_tab');
        return tabToken ? { viberails_tab: tabToken } : {};
    } catch (error) {
        return {};
    }
}

async function fetchJson(endpoint) {
    const baseUrl = getApiBaseUrl();
    const response = await fetch(baseUrl + endpoint, {
        method: 'GET',
        headers: getApiHeaders(),
        credentials: 'include',
        cache: 'no-store'
    });

    if (response.status === 401) {
        if (window.__viberails_VSCODE__) {
            throw new Error('Session expired. Close and reopen the VibeRails panel to re-authenticate.');
        }
        window.location.href = `${baseUrl}/auth/bootstrap`;
        throw new Error('Unauthorized');
    }

    if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
    }

    return response.json();
}

export async function showTranscriptModal(sessionId) {
    const { body } = createModal(`Transcript — ${sessionId}`);

    const toolbar = document.createElement('div');
    toolbar.style.cssText = 'display:flex;justify-content:flex-end;padding:4px 12px;border-bottom:1px solid #333;flex-shrink:0;';
    const exportBtn = document.createElement('button');
    exportBtn.textContent = 'Export .txt';
    exportBtn.style.cssText = 'background:#2d2d2d;border:1px solid #555;color:#ccc;padding:4px 12px;border-radius:4px;cursor:pointer;font-size:12px;font-family:monospace;';
    exportBtn.disabled = true;
    toolbar.appendChild(exportBtn);

    const pre = document.createElement('pre');
    pre.style.cssText = 'margin:0;padding:12px;color:#d4d4d4;font-size:12px;line-height:1.5;overflow:auto;flex:1;box-sizing:border-box;white-space:pre-wrap;word-break:break-word;';
    pre.textContent = 'Loading\u2026';

    body.style.cssText += 'display:flex;flex-direction:column;';
    body.append(toolbar, pre);

    let transcriptText = null;

    try {
        const json = await fetchJson(`/api/v1/chatHistory/${encodeURIComponent(sessionId)}/transcript`);
        transcriptText = json.text ?? '(no transcript)';
        pre.textContent = transcriptText;
        exportBtn.disabled = false;
    } catch (err) {
        pre.textContent = `Error: ${err.message}`;
    }

    exportBtn.addEventListener('click', () => {
        if (!transcriptText) return;
        const blob = new Blob([transcriptText], { type: 'text/plain' });
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        a.download = `${sessionId}.txt`;
        a.click();
        URL.revokeObjectURL(url);
    });
}

export async function showReplayModal(sessionId) {
    let term = null;
    let playbackTimer = null;

    const { body } = createModal(`Session Replay — ${sessionId}`, () => {
        if (playbackTimer) clearTimeout(playbackTimer);
        term?.dispose();
    });

    // Toolbar with playback controls
    const toolbar = document.createElement('div');
    toolbar.style.cssText = 'display:flex;align-items:center;gap:8px;padding:4px 12px;border-bottom:1px solid #333;flex-shrink:0;';

    const playBtn = document.createElement('button');
    playBtn.textContent = '\u25B6';
    playBtn.title = 'Play / Pause';
    playBtn.style.cssText = 'background:#2d2d2d;border:1px solid #555;color:#ccc;padding:4px 10px;border-radius:4px;cursor:pointer;font-size:14px;font-family:monospace;min-width:36px;';

    const speedSelect = document.createElement('select');
    speedSelect.style.cssText = 'background:#2d2d2d;border:1px solid #555;color:#ccc;padding:3px 6px;border-radius:4px;font-size:12px;font-family:monospace;';
    for (const [label, val] of [['1x', 1], ['2x', 2], ['5x', 5], ['10x', 10], ['Max', 0]]) {
        const opt = document.createElement('option');
        opt.value = val;
        opt.textContent = label;
        if (val === 5) opt.selected = true;
        speedSelect.appendChild(opt);
    }

    const progress = document.createElement('span');
    progress.style.cssText = 'color:#888;font-size:11px;font-family:monospace;margin-left:auto;';
    progress.textContent = '';

    toolbar.append(playBtn, speedSelect, progress);

    // Terminal container
    const termEl = document.createElement('div');
    termEl.style.cssText = 'width:100%;flex:1;overflow:hidden;';

    body.style.cssText += 'display:flex;flex-direction:column;';
    body.append(toolbar, termEl);

    if (typeof window.Terminal !== 'function') {
        termEl.style.cssText += 'color:#aaa;padding:12px;font-family:monospace;';
        termEl.textContent = 'xterm.js not loaded';
        return;
    }

    term = new window.Terminal({
        fontFamily: '"Fira Code","JetBrains Mono",Consolas,monospace',
        fontSize: 13,
        disableStdin: true,
        convertEol: false,
        allowProposedApi: true,
        scrollback: 20000,
        theme: { background: '#1e1e1e', foreground: '#d4d4d4' }
    });

    let fitAddon = null;
    if (window.FitAddon?.FitAddon) {
        fitAddon = new window.FitAddon.FitAddon();
        term.loadAddon(fitAddon);
    }

    term.open(termEl);
    fitAddon?.fit();
    term.write('Loading session data\u2026\r\n');

    let frames = [];
    let resizeEvents = []; // [{afterFrameIndex, cols, rows}]
    let initialCols = 120;
    let initialRows = 40;
    try {
        const json = await fetchJson(`/api/v1/chatHistory/${encodeURIComponent(sessionId)}/terminal-replay`);
        initialCols = json.initialCols || 120;
        initialRows = json.initialRows || 40;
        frames = (json.frames || []).map(f => ({
            data: Uint8Array.from(atob(f.data), c => c.charCodeAt(0)),
            delayMs: f.delayMs
        }));

        // Build resize timeline from enriched chunks — when cols/rows change between
        // consecutive non-alt chunks, we need to resize the replay terminal.
        // Map each enriched chunk to a byte offset in the combined legacy output so we
        // can figure out which legacy frame index corresponds to each resize.
        const chunks = json.chunks || [];
        let prevCols = initialCols, prevRows = initialRows;
        let chunkByteOffset = 0;
        for (const chunk of chunks) {
            const chunkBytes = atob(chunk.data).length;
            if (chunk.cols !== prevCols || chunk.rows !== prevRows) {
                // Find the legacy frame closest to this byte offset
                let accumulated = 0;
                let frameIdx = 0;
                for (let i = 0; i < frames.length; i++) {
                    accumulated += frames[i].data.length;
                    if (accumulated >= chunkByteOffset) { frameIdx = i; break; }
                }
                resizeEvents.push({ afterFrameIndex: frameIdx, cols: chunk.cols, rows: chunk.rows });
                prevCols = chunk.cols;
                prevRows = chunk.rows;
            }
            chunkByteOffset += chunkBytes;
        }
    } catch (err) {
        term.write(`\r\nError: ${err.message}\r\n`);
        return;
    }

    if (frames.length === 0) {
        term.write('\r\nNo replay data.\r\n');
        return;
    }

    // Resize xterm to match recording dimensions so alt-screen TUI content renders correctly
    term.resize(initialCols, initialRows);

    // Playback state
    let frameIndex = 0;
    let playing = false;
    let nextResizeIdx = 0; // index into resizeEvents
    const maxIdleMs = 500;

    function getSpeed() {
        return Number.parseInt(speedSelect.value, 10);
    }

    function updateProgress() {
        progress.textContent = `${frameIndex} / ${frames.length}`;
    }

    // Apply any pending resize events up to the given frame index
    function applyPendingResizes(idx) {
        while (nextResizeIdx < resizeEvents.length && resizeEvents[nextResizeIdx].afterFrameIndex <= idx) {
            const ev = resizeEvents[nextResizeIdx];
            term.resize(ev.cols, ev.rows);
            nextResizeIdx++;
        }
    }

    function scheduleNext() {
        if (!playing || frameIndex >= frames.length) {
            if (frameIndex >= frames.length) {
                playing = false;
                playBtn.textContent = '\u21BB';
                playBtn.title = 'Restart';
            }
            return;
        }

        const frame = frames[frameIndex];
        const speed = getSpeed();

        // Max speed — dump remaining frames immediately
        if (speed === 0) {
            while (frameIndex < frames.length) {
                applyPendingResizes(frameIndex);
                term.write(frames[frameIndex].data);
                frameIndex++;
            }
            updateProgress();
            playing = false;
            playBtn.textContent = '\u21BB';
            playBtn.title = 'Restart';
            return;
        }

        applyPendingResizes(frameIndex);
        term.write(frame.data);
        frameIndex++;
        updateProgress();

        if (frameIndex >= frames.length) {
            playing = false;
            playBtn.textContent = '\u21BB';
            playBtn.title = 'Restart';
            return;
        }

        // Delay to next frame — cap idle gaps, apply speed multiplier
        const rawDelay = frames[frameIndex].delayMs - frame.delayMs;
        const cappedDelay = Math.min(Math.max(rawDelay, 0), maxIdleMs);
        const scaledDelay = Math.max(Math.round(cappedDelay / speed), 1);

        playbackTimer = setTimeout(scheduleNext, scaledDelay);
    }

    function play() {
        if (frameIndex >= frames.length) {
            // Restart
            frameIndex = 0;
            nextResizeIdx = 0;
            term.reset();
            term.resize(initialCols, initialRows);
        }
        playing = true;
        playBtn.textContent = '\u23F8';
        playBtn.title = 'Pause';
        scheduleNext();
    }

    function pause() {
        playing = false;
        if (playbackTimer) { clearTimeout(playbackTimer); playbackTimer = null; }
        playBtn.textContent = '\u25B6';
        playBtn.title = 'Play';
    }

    playBtn.addEventListener('click', () => {
        if (playing) pause(); else play();
    });

    // Auto-start — reset and size to recording dimensions
    term.reset();
    term.resize(initialCols, initialRows);
    updateProgress();
    play();
}
