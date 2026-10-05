import { mountSessionViewer } from '../../session-replay/viewer.mjs';

function createModal(title, onClose) {
    const overlay = document.createElement('div');
    overlay.style.cssText = 'position:fixed;inset:0;background:rgba(0,0,0,.7);display:flex;align-items:center;justify-content:center;z-index:9999;';

    const modal = document.createElement('div');
    modal.style.cssText = 'background:var(--color-bg-surface, #1e1e1e);border:1px solid var(--color-border, #334155);border-radius:8px;display:flex;flex-direction:column;overflow:hidden;width:90vw;max-width:1100px;height:80vh;';

    const hdr = document.createElement('div');
    hdr.style.cssText = 'display:flex;align-items:center;padding:8px 12px;border-bottom:1px solid var(--color-border, #334155);flex-shrink:0;gap:8px;';

    const titleEl = document.createElement('span');
    titleEl.style.cssText = 'color:var(--color-text-muted, #94a3b8);font-size:13px;font-family:monospace;flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;';
    titleEl.textContent = title ?? '';

    const closeBtn = document.createElement('button');
    closeBtn.type = 'button';
    closeBtn.style.cssText = 'background:none;border:none;color:var(--color-text-muted, #94a3b8);cursor:pointer;font-size:20px;line-height:1;padding:0 4px;';
    closeBtn.textContent = '\u00d7';
    closeBtn.setAttribute('aria-label', 'Close');

    hdr.append(titleEl, closeBtn);

    const body = document.createElement('div');
    body.style.cssText = 'flex:1;overflow:hidden;position:relative;';

    modal.append(hdr, body);
    overlay.appendChild(modal);
    document.body.appendChild(overlay);

    const close = () => { onClose?.(); overlay.remove(); };
    overlay.addEventListener('click', e => { if (e.target === overlay) close(); });
    closeBtn.addEventListener('click', close);

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

async function fetchJson(endpoint, signal) {
    const baseUrl = getApiBaseUrl();
    const response = await fetch(baseUrl + endpoint, {
        method: 'GET',
        signal,
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

async function downloadFile(endpoint, fallbackFileName) {
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
        const contentType = response.headers.get('content-type') || '';
        if (contentType.includes('application/json')) {
            const payload = await response.json();
            throw new Error(payload?.error || payload?.message || `HTTP ${response.status}`);
        }

        const message = await response.text();
        throw new Error(message || `HTTP ${response.status}`);
    }

    const blob = await response.blob();
    const disposition = response.headers.get('content-disposition') || '';
    const fileNameMatch = disposition.match(/filename\*=UTF-8''([^;]+)|filename="?([^"]+)"?/i);
    let fileName = fallbackFileName;
    try {
        fileName = decodeURIComponent(fileNameMatch?.[1] || fileNameMatch?.[2] || fallbackFileName);
    } catch { /* malformed percent-encoding — use fallback */ }
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    link.remove();
    URL.revokeObjectURL(url);
}

export async function downloadRawSession(sessionId) {
    await downloadFile(`/api/v1/chatHistory/${encodeURIComponent(sessionId)}/raw-session`, `${sessionId}-raw-session.zip`);
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

/**
 * @param {string} sessionId
 * @param {{ seekToUtc?: string|number|Date }} [options] — a wall-clock instant to jump to (a board
 *   comment's createdAt). Playback fast-forwards to a moment before it, marks it in the toolbar,
 *   and then plays at the selected speed so the reader sees the context leading up to it.
 */
export async function showReplayModal(sessionId, { seekToUtc = null } = {}) {
    let viewer;
    const { body, close } = createModal(`Session Replay — ${sessionId}`, () => viewer?.dispose());
    body.parentElement.style.cssText = 'background:var(--color-bg-base, #121212);border:1px solid var(--color-border, #334155);border-radius:8px;display:flex;flex-direction:column;overflow:hidden;width:96vw;max-width:1800px;height:94dvh;';
    body.style.cssText = 'flex:1;min-height:0;overflow:hidden;position:relative;';
    viewer = mountSessionViewer(body, {
        sessionId, seekToUtc, autoplay: true,
        onEvent(event) { if (event.type === 'close-request') close(); },
        request(path, { signal }) {
            return fetchJson(path.replace(/^\/api(?=\/)/, '/api/v1/session-replay'), signal);
        }
    });
    // The embedded viewer handles inspector/dialog Escape before asking to close.
    await viewer.ready.catch(() => {});
    return { viewer, close };
}
