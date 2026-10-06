const LINK_API = '/api/v1/settings/remote-link';
export const SIGN_IN_URL = 'https://viberails.ai/link';
const TERMINAL_STATUSES = new Set(['idle', 'linked', 'denied', 'expired', 'unavailable', 'error', 'cancelled']);
const LOOPBACK_HOSTS = new Set(['localhost', '127.0.0.1', '[::1]']);

// Local Front mode (VB-8NI09-170): the backend reports the loopback https origin this process
// talks to, and its sign-in page is that origin plus /link. Anything else means the production
// page, so a malformed value can never widen what the panel accepts.
export function signInPageFor(localFrontOrigin) {
    if (typeof localFrontOrigin !== 'string' || !localFrontOrigin) return SIGN_IN_URL;
    let url;
    try {
        url = new URL(localFrontOrigin);
    } catch {
        return SIGN_IN_URL;
    }
    return url.protocol === 'https:' && LOOPBACK_HOSTS.has(url.hostname) && !url.username && !url.password
        && url.pathname === '/' && !url.search && !url.hash
        ? `${url.origin}/link` : SIGN_IN_URL;
}

// The server must supply the fixed verification page. Only the validated public user code
// may be added locally as a fragment; the website submits it in an antiforgery-protected POST.
export function isSignInUrl(value, page = SIGN_IN_URL) {
    return value === page;
}

export function signInUrlForCode(userCode, page = SIGN_IN_URL) {
    return typeof userCode === 'string' && userCode.length === 9 && /^[A-Z0-9]{4}-[A-Z0-9]{4}$/.test(userCode)
        ? `${page}#code=${userCode}` : page;
}

export class RemoteAccountLinkPanel {
    constructor(app, root, { onLinked = () => {} } = {}) {
        this.app = app;
        this.root = root;
        this.onLinked = onLinked;
        this.state = { status: 'idle' };
        this.busy = false;
        this.disposed = false;
        this.suspended = false;
        this.generation = 0;
        this.abortController = null;
        this.pollTimer = null;
        this.countdownTimer = null;
        this.onPageHide = () => {
            this.suspended = true;
            this._invalidate();
        };
        this.onPageShow = () => {
            if (!this.suspended || this.disposed) return;
            this.suspended = false;
            void this.refresh();
        };
    }

    // The sign-in page this backend accepts: production, or the local Front in local mode.
    get signInPage() {
        return signInPageFor(this.app.appSettings?.localFrontOrigin);
    }

    get siteName() {
        return this.signInPage === SIGN_IN_URL ? 'viberails.ai' : 'the local Front';
    }

    mount() {
        this.root.querySelector('[data-remote-link-start]').addEventListener('click', () => void this.start());
        this.root.querySelector('[data-remote-link-cancel]').addEventListener('click', () => void this.cancel());
        this.root.querySelector('[data-remote-link-copy]').addEventListener('click', () => void this.copyCode());
        this.root.querySelector('[data-remote-link-open]').addEventListener('click', event => this.openSignInPage(event));
        this.unsubscribe = this.app.appEventClient?.on('remote-account-linked', () => {
            // Events can arrive after a later manual key save. Ask the backend to check
            // the current credential instead of treating an event's hint as newest state.
            if (!this.disposed && !this.suspended) void this.refresh();
        });
        window.addEventListener('pagehide', this.onPageHide);
        window.addEventListener('pageshow', this.onPageShow);
        return this.refresh();
    }

    async refresh() {
        if (this.disposed || this.suspended) return;
        return this._request('GET');
    }

    async start() {
        if (this.disposed || this.suspended || this.busy || this.state.status === 'pending') return;
        this.state = { status: 'idle' };
        return this._request('POST');
    }

    async cancel() {
        if (this.disposed || this.suspended || this.state.status === 'cancelling') return;
        // Supersede even an unfinished start or poll before asking the backend to cancel.
        this.state = { status: 'cancelling' };
        return this._request('DELETE');
    }

    async _request(method) {
        this._invalidate();
        const generation = this.generation;
        this.abortController = new AbortController();
        this.busy = true;
        this._render();
        if (this.state.status === 'pending' && this.state.expiresAt != null) this._startCountdown();
        try {
            const state = await this.app.apiCall(LINK_API, method, null, {
                showLoading: false, signal: this.abortController.signal
            });
            if (!this._isCurrent(generation)) return;
            this.busy = false;
            this._apply(state);
        } catch {
            if (!this._isCurrent(generation)) return;
            this.busy = false;
            this._apply({ status: 'error', error: method === 'DELETE' ? 'cancel_failed' : 'request_failed' });
        }
    }

    _apply(response) {
        this._clearTimers();
        this.busy = false;
        if (response?.status === 'pending') {
            const expiresAt = Date.parse(response.expiresAt);
            const interval = Math.max(3, Math.min(60, Number(response.interval) || 3));
            if (response.error === 'save_failed' && response.expiresAt == null) {
                // Approval has already finished. The backend retains the key in memory
                // while retrying the local save; the original device-code TTL no longer applies.
                this.state = { status: 'pending', awaitingSave: true, interval, error: 'save_failed' };
            } else if (response.userCode == null && response.verificationUri == null && response.expiresAt == null) {
                // Another dashboard can observe the shared attempt before its start completes.
                this.state = { status: 'pending', preparing: true, interval };
            } else if (!isSignInUrl(response.verificationUri, this.signInPage)
                || typeof response.userCode !== 'string' || response.userCode.length !== 9 || !/^[A-Z0-9]{4}-[A-Z0-9]{4}$/.test(response.userCode)
                || !Number.isFinite(expiresAt)) {
                this.state = { status: 'error' };
            } else {
                this.state = {
                    status: 'pending', userCode: response.userCode, verificationUri: this.signInPage,
                    expiresAt, interval, error: response.error
                };
                this._startCountdown();
            }
            if (this.state.status === 'pending') {
                const generation = this.generation;
                this.pollTimer = setTimeout(() => {
                    if (this._isCurrent(generation)) void this.refresh();
                }, this.state.interval * 1000);
            }
        } else {
            this.state = TERMINAL_STATUSES.has(response?.status) ? {
                status: response.status, keyHint: response.keyHint,
                account: response.account, error: response.error
            } : { status: 'error' };
            if (this.state.status === 'linked' && typeof this.state.keyHint === 'string') {
                this.onLinked(this.state);
            }
        }
        this._render();
    }

    _isCurrent(generation) {
        return !this.disposed && !this.suspended && generation === this.generation;
    }

    _invalidate() {
        this.generation++;
        this.abortController?.abort();
        this.abortController = null;
        this._clearTimers();
    }

    _clearTimers() {
        clearTimeout(this.pollTimer);
        clearInterval(this.countdownTimer);
        this.pollTimer = null;
        this.countdownTimer = null;
    }

    _startCountdown() {
        // The server owns expiry. A response already carrying an approved key may arrive
        // after the displayed countdown reaches zero; do not abort it or stop polling.
        this.countdownTimer = setInterval(() => this._renderCountdown(), 1000);
    }

    _renderCountdown() {
        const seconds = Math.max(0, Math.ceil((this.state.expiresAt - Date.now()) / 1000));
        this.root.querySelector('[data-remote-link-countdown]').textContent =
            `Code expires in ${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;
    }

    _render() {
        // A saved account survives the temporary device-link attempt and backend restarts.
        const savedAccount = this.state.status === 'idle' && this.app.appSettings?.apiKey;
        const status = savedAccount ? 'linked' : this.state.status;
        const email = savedAccount ? this.app.appSettings.remoteAccountEmail : this.state.account?.email;
        const pending = status === 'pending';
        const needsApproval = pending && !this.state.awaitingSave && !this.state.preparing;
        const start = this.root.querySelector('[data-remote-link-start]');
        start.disabled = this.busy || pending;
        start.hidden = pending;
        const site = this.siteName;
        start.textContent = this.busy && !pending ? 'Connecting…'
            : status === 'idle' ? (site === 'viberails.ai' ? 'Sign in to viberails.ai' : 'Sign in to local Front')
                : status === 'linked' ? 'Switch account' : 'Try sign-in again';
        const cancel = this.root.querySelector('[data-remote-link-cancel]');
        cancel.hidden = !pending && !this.busy;
        cancel.disabled = status === 'cancelling';
        cancel.textContent = status === 'cancelling' ? 'Cancelling…' : 'Cancel';
        this.root.querySelector('[data-remote-link-pending]').hidden = !needsApproval;
        this.root.querySelector('[data-remote-link-code]').textContent = needsApproval ? this.state.userCode : '';
        const open = this.root.querySelector('[data-remote-link-open]');
        const page = this.root.querySelector('[data-remote-link-page]');
        if (page) page.textContent = this.signInPage;
        if (needsApproval) {
            open.href = signInUrlForCode(this.state.userCode, this.signInPage);
            this._renderCountdown();
        } else {
            open.removeAttribute('href');
            this.root.querySelector('[data-remote-link-countdown]').textContent = '';
        }
        this.root.querySelector('[data-remote-link-copy-status]').textContent = '';
        const messages = {
            idle: 'Sign in to connect this VibeRails instance to your account, or add an API key in Settings.',
            pending: this.state.error === 'save_failed'
                ? 'Sign-in approved. VibeRails could not save the key yet and will retry automatically.'
                : this.state.preparing ? 'Preparing sign-in…'
                : this.state.error === 'remote_error'
                    ? `Waiting for ${site} to respond. VibeRails will retry automatically while this code is valid.`
                    : 'Open the sign-in page to continue. Your code is filled in automatically; check the account and computer before approving.',
            linked: email ? `Logged in ${email}` : 'API key configured',
            denied: this.state.error === 'key_limit' ? `Your account has reached its API key limit. Manage your keys on ${site}, then try again.` : `The sign-in request was denied on ${site}.`,
            expired: 'This sign-in code expired. Start again to get a new code.',
            unavailable: `Sign-in is not available on ${site} yet. You can add an API key in Settings.`,
            error: this.state.error === 'cancel_failed'
                ? 'Could not confirm cancellation. Check your connection before trying again.'
                : this.state.error === 'key_changed'
                    ? 'Your saved API key changed during sign-in. Start again if you want to connect another account.'
                    : 'Could not complete sign-in. Check your connection and try again, or add an API key in Settings.',
            cancelled: 'Sign-in cancelled. Your saved API key has not changed.',
            cancelling: 'Cancelling sign-in…'
        };
        this.root.querySelector('[data-remote-link-status]').textContent = messages[status] || messages.error;
    }

    openSignInPage(event) {
        if (this.disposed || this.suspended || this.state.status !== 'pending'
            || this.state.awaitingSave || this.state.preparing || this.state.expiresAt <= Date.now()) {
            event.preventDefault();
            return;
        }
        // A real anchor is the browser/older-extension fallback. It is deliberately opened
        // by this second click, never after awaiting the start request (popup blockers).
        if (typeof window.__viberails_openExternal__ === 'function') {
            try {
                window.__viberails_openExternal__(signInUrlForCode(this.state.userCode, this.signInPage));
                event.preventDefault();
            } catch {
                // Let the same user click follow the anchor if the bridge is unavailable.
            }
        }
    }

    async copyCode() {
        if (this.disposed || this.suspended || this.state.status !== 'pending' || this.state.awaitingSave || this.state.preparing) return;
        const generation = this.generation;
        const output = this.root.querySelector('[data-remote-link-copy-status]');
        try {
            await navigator.clipboard.writeText(this.state.userCode);
            if (this._isCurrent(generation)) output.textContent = 'Code copied.';
        } catch {
            if (this._isCurrent(generation)) output.textContent = 'Select the code above and copy it to continue.';
        }
    }

    unload() {
        this.disposed = true;
        this._invalidate();
        this.unsubscribe?.();
        window.removeEventListener('pagehide', this.onPageHide);
        window.removeEventListener('pageshow', this.onPageShow);
        // Leave the shared backend attempt available to other tabs and a later account visit.
    }
}
