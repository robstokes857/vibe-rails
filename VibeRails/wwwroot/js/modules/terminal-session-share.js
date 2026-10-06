const openShares = new WeakMap();
const SHARE_URL = /^https:\/\/viberails\.ai\/shared\/session\?key=[a-f0-9]{64}$/;

// The session id and label are captured when Share is clicked. A tab can start a new
// session while this POST is pending; its replacement must never receive this link.
export function showSessionShareModal(app, sessionId, label) {
    if (!sessionId) return Promise.resolve(null);
    const previous = openShares.get(app);
    if (previous?.root.isConnected) return previous.promise;
    let closed = false;
    app.showModal('Share session', `
        <div id="terminal-session-share">
            <p>Anyone with this link can view the terminal recording, prompts and saved code changes.</p>
            <p class="text-muted">Links expire after one month. You can revoke them sooner in
                <a href="https://viberails.ai/SessionSharingLinks" target="_blank" rel="noopener noreferrer">Sharing links</a>.</p>
            <p data-share-name class="fw-semibold text-break"></p>
            <p data-share-status role="status" aria-live="polite">Creating your sharing link…</p>
            <div data-share-result hidden>
                <label for="session-share-url" class="form-label">Sharing link</label>
                <input id="session-share-url" class="form-control" readonly spellcheck="false">
                <p data-share-expiry class="text-muted small mt-2"></p>
                <button type="button" class="btn btn-primary" data-share-copy>Copy link</button>
            </div>
            <button type="button" class="btn btn-outline-primary" data-share-retry hidden>Try again</button>
        </div>`, { onClose: () => { closed = true; } });
    const root = document.getElementById('terminal-session-share');
    const status = root.querySelector('[data-share-status]');
    const url = root.querySelector('#session-share-url');
    const retry = root.querySelector('[data-share-retry]');
    const copy = root.querySelector('[data-share-copy]');
    const displayName = String(label || 'Session replay').trim().slice(0, 160) || 'Session replay';
    root.querySelector('[data-share-name]').textContent = displayName;
    copy.addEventListener('click', async () => {
        try {
            await navigator.clipboard.writeText(url.value);
            if (!closed) copy.textContent = 'Copied';
        } catch {
            url.focus(); url.select();
            status.textContent = 'Select and copy the link above.';
        }
    });
    retry.addEventListener('click', () => { void create(); });
    const promise = create();
    openShares.set(app, { root, promise });
    return promise;

    async function create() {
        retry.hidden = true;
        status.textContent = 'Creating your sharing link…';
        try {
            // Closing the modal hides its result but does not cancel durable upload scheduling.
            const result = await app.apiCall(`/api/v1/sessions/${encodeURIComponent(sessionId)}/sharing-links`,
                'POST', { displayName }, { showLoading: false });
            if (closed || !root.isConnected) return result;
            if (!result?.success || !SHARE_URL.test(result.url || '')) {
                status.textContent = result?.message || 'Could not create the link. Try again shortly.';
                retry.hidden = false;
                return result;
            }
            url.value = result.url;
            status.textContent = result.message;
            const expiry = new Date(result.expiresUtc);
            root.querySelector('[data-share-expiry]').textContent = Number.isFinite(expiry.getTime())
                ? `Expires ${expiry.toLocaleString()}` : '';
            root.querySelector('[data-share-result]').hidden = false;
            url.focus(); url.select();
            return result;
        } catch {
            if (!closed && root.isConnected) {
                status.textContent = 'Could not create the link. Check your connection and try again.';
                retry.hidden = false;
            }
            return null;
        }
    }
}
