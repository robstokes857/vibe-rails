const openShares = new WeakMap();
const SHARE_URL = /^https:\/\/viberails\.ai\/shared\/session\?key=[a-f0-9]{64}$/;
const RECIPIENT_LIMIT = 10;

export function parseShareEmails(text) {
    return [...new Set(String(text || '').split(/[\s,;]+/).map(part => part.trim()).filter(Boolean))];
}

// The session id and label are captured when Share is clicked. A tab can start a new
// session while this POST is pending; its replacement must never receive this link.
export function showSessionShareModal(app, sessionId, label) {
    if (!sessionId) return Promise.resolve(null);
    const previous = openShares.get(app);
    if (previous?.root.isConnected) return previous.promise;
    let closed = false;
    let settle;
    const promise = new Promise(resolve => { settle = resolve; });
    app.showModal('Share session', `
        <div id="terminal-session-share">
            <p>A link shares the terminal recording, prompts and saved code changes.</p>
            <p class="text-muted">Links expire after one month. You can revoke them sooner in
                <a href="https://viberails.ai/SessionSharingLinks" target="_blank" rel="noopener noreferrer">Sharing links</a>.</p>
            <p data-share-name class="fw-semibold text-break"></p>
            <fieldset data-share-setup class="mb-2">
                <legend class="form-label">Who can view</legend>
                <div class="form-check">
                    <input class="form-check-input" type="radio" name="session-share-access" id="session-share-access-public" value="public" checked>
                    <label class="form-check-label" for="session-share-access-public">Anyone with the link</label>
                </div>
                <div class="form-check">
                    <input class="form-check-input" type="radio" name="session-share-access" id="session-share-access-email" value="email">
                    <label class="form-check-label" for="session-share-access-email">Only people I list</label>
                </div>
                <div data-share-emails hidden class="mt-2">
                    <label for="session-share-emails" class="form-label">Email addresses</label>
                    <textarea id="session-share-emails" class="form-control" rows="3" spellcheck="false" placeholder="name@example.com, one per line or comma-separated"></textarea>
                    <p class="text-muted small mt-1 mb-0">Up to ${RECIPIENT_LIMIT} people per session, counting all of its links. Each person signs in to viberails.ai with that verified email address. Nobody is notified or looked up.</p>
                </div>
                <button type="button" class="btn btn-primary mt-2" data-share-create>Create link</button>
            </fieldset>
            <p data-share-status role="status" aria-live="polite"></p>
            <div data-share-result hidden>
                <label for="session-share-url" class="form-label">Sharing link</label>
                <input id="session-share-url" class="form-control" readonly spellcheck="false">
                <p data-share-audience class="text-muted small mt-2 mb-0"></p>
                <p data-share-expiry class="text-muted small mt-2"></p>
                <button type="button" class="btn btn-primary" data-share-copy>Copy link</button>
            </div>
            <button type="button" class="btn btn-outline-primary" data-share-retry hidden>Try again</button>
        </div>`, { onClose: () => { closed = true; settle(null); } });
    const root = document.getElementById('terminal-session-share');
    const status = root.querySelector('[data-share-status]');
    const setup = root.querySelector('[data-share-setup]');
    const emailsBox = root.querySelector('[data-share-emails]');
    const url = root.querySelector('#session-share-url');
    const retry = root.querySelector('[data-share-retry]');
    const copy = root.querySelector('[data-share-copy]');
    const displayName = String(label || 'Session replay').trim().slice(0, 160) || 'Session replay';
    root.querySelector('[data-share-name]').textContent = displayName;
    let body = null;
    const chosen = () => root.querySelector('input[name="session-share-access"]:checked')?.value === 'email' ? 'email' : 'public';
    for (const radio of root.querySelectorAll('input[name="session-share-access"]'))
        radio.addEventListener('change', () => { emailsBox.hidden = chosen() !== 'email'; });
    copy.addEventListener('click', async () => {
        try {
            await navigator.clipboard.writeText(url.value);
            if (!closed) copy.textContent = 'Copied';
        } catch {
            url.focus(); url.select();
            status.textContent = 'Select and copy the link above.';
        }
    });
    root.querySelector('[data-share-create]').addEventListener('click', () => {
        const access = chosen();
        const emails = access === 'email' ? parseShareEmails(root.querySelector('#session-share-emails').value) : [];
        if (access === 'email' && !emails.length) { status.textContent = 'List at least one email address, or choose Anyone with the link.'; return; }
        if (emails.length > RECIPIENT_LIMIT) { status.textContent = `List up to ${RECIPIENT_LIMIT} people.`; return; }
        body = { displayName, access, emails };
        setup.hidden = true;
        void create();
    });
    retry.addEventListener('click', () => { void create(); });
    openShares.set(app, { root, promise });
    return promise;

    async function create() {
        retry.hidden = true;
        status.textContent = 'Creating your sharing link…';
        try {
            // Closing the modal hides its result but does not cancel durable upload scheduling.
            const result = await app.apiCall(`/api/v1/sessions/${encodeURIComponent(sessionId)}/sharing-links`,
                'POST', body, { showLoading: false });
            if (closed || !root.isConnected) { settle(result); return result; }
            if (!result?.success || !SHARE_URL.test(result.url || '')) {
                status.textContent = result?.message || 'Could not create the link. Try again shortly.';
                retry.hidden = false;
                return result;
            }
            url.value = result.url;
            status.textContent = result.message;
            root.querySelector('[data-share-audience]').textContent = result.access === 'email'
                ? 'Only these people can open it: ' + (result.recipients || body.emails).join(', ')
                : 'Anyone with this link can view the recording.';
            const expiry = new Date(result.expiresUtc);
            root.querySelector('[data-share-expiry]').textContent = Number.isFinite(expiry.getTime())
                ? `Expires ${expiry.toLocaleString()}` : '';
            root.querySelector('[data-share-result]').hidden = false;
            url.focus(); url.select();
            settle(result);
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
