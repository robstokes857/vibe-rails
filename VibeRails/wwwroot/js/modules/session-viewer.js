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

    const pre = document.createElement('pre');
    pre.style.cssText = 'margin:0;padding:12px;color:#d4d4d4;font-size:12px;line-height:1.5;overflow:auto;height:100%;box-sizing:border-box;white-space:pre-wrap;word-break:break-word;';
    pre.textContent = 'Loading\u2026';
    body.appendChild(pre);

    try {
        const json = await fetchJson(`/api/v1/chatHistory/${encodeURIComponent(sessionId)}/transcript`);
        pre.textContent = json.text ?? '(no transcript)';
    } catch (err) {
        pre.textContent = `Error: ${err.message}`;
    }
}

export async function showReplayModal(sessionId) {
    let term = null;

    const { body } = createModal(`Session Replay — ${sessionId}`, () => term?.dispose());

    const termEl = document.createElement('div');
    termEl.style.cssText = 'width:100%;height:100%;';
    body.appendChild(termEl);

    if (typeof window.Terminal !== 'function') {
        termEl.style.cssText += 'color:#aaa;padding:12px;font-family:monospace;';
        termEl.textContent = 'xterm.js not loaded';
        return;
    }

    // Use the same dimensions the PTY was recorded at (Terminal.DefaultCols/DefaultRows)
    // to avoid cursor-positioning misalignment that causes double-printing.
    const replayCols = 120;
    const replayRows = 30;

    term = new window.Terminal({
        cols: replayCols,
        rows: replayRows,
        fontFamily: '"Fira Code","JetBrains Mono",Consolas,monospace',
        fontSize: 13,
        disableStdin: true,
        convertEol: false,
        allowProposedApi: true,
        scrollback: 20000,
        theme: { background: '#1e1e1e', foreground: '#d4d4d4' }
    });

    term.open(termEl);

    term.write('Loading session data\u2026\r\n');

    try {
        const json = await fetchJson(`/api/v1/chatHistory/${encodeURIComponent(sessionId)}/replay`);
        term.reset();
        const bytes = Uint8Array.from(atob(json.content), c => c.charCodeAt(0));
        term.write(bytes);
    } catch (err) {
        term.write(`\r\nError: ${err.message}\r\n`);
    }
}
