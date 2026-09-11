import { escapeHtml } from './utils.js';

const KEYS_API = '/api/v1/settings/keys';
const VERIFY_URL = 'https://viberails.ai/public/api/v1/signatures/verify';
export const MAX_SIGNING_BYTES = 64 * 1024;
export const MIN_PASSWORD_LENGTH = 8;
export const MAX_PASSWORD_LENGTH = 128;

// The backend runs with invariant globalization, where .NET's string.Normalize is a no-op, so
// composing to NFC here is what keeps "the same passphrase typed on another keyboard" unlocking
// the same key. Every password leaves this module through normalizeKeyPassword.
export function normalizeKeyPassword(password) {
    return String(password ?? '').normalize('NFC');
}

export function validateKeyPassword(password, confirmation = password) {
    const normalized = normalizeKeyPassword(password);
    const length = [...normalized].length;
    if (!normalized.trim() || length < MIN_PASSWORD_LENGTH || length > MAX_PASSWORD_LENGTH) {
        return `Enter a password with ${MIN_PASSWORD_LENGTH}–${MAX_PASSWORD_LENGTH} characters. It cannot be blank.`;
    }
    if (/^[\s\p{Nd}]*$/u.test(normalized)) {
        return 'A password cannot be digits only. Use a passphrase of several words.';
    }
    if (normalized !== normalizeKeyPassword(confirmation)) return 'The password entries do not match.';
    return null;
}

export function signingPayloadBase64(bytes) {
    if (bytes.length > MAX_SIGNING_BYTES) throw new Error('Choose a message or file no larger than 64 KiB.');
    // Avoid a spread over the entire payload (argument limits differ between browsers).
    let binary = '';
    for (let offset = 0; offset < bytes.length; offset += 8192) {
        binary += String.fromCharCode(...bytes.subarray(offset, offset + 8192));
    }
    return btoa(binary);
}

export class SettingsKeysPanel {
    constructor(app, root) {
        this.app = app;
        this.root = root;
        this.keys = [];
        this.warnings = [];
        this.apiKeyConfigured = false;
        this.busy = false;
        this.mounted = false;
        this.disposed = false;
        this.actionKey = null;
        this.abortController = new AbortController();
        this.onPageHide = () => this.clearSecrets();
    }

    async activate() {
        if (this.disposed) return;
        if (!this.mounted) this.mount();
        await this.refresh();
    }

    mount() {
        this.mounted = true;
        this.root.innerHTML = `
            <div class="sandbox-item-card text-start mb-3">
                <div class="d-flex align-items-center justify-content-between gap-2 flex-wrap mb-2">
                    <h5 class="sandbox-item-name mb-0">RSA-4096 signing keys</h5>
                    <a class="btn btn-sm btn-outline-secondary" href="https://viberails.ai/Keys" target="_blank" rel="noopener noreferrer">Keys &amp; validations online</a>
                </div>
                <p class="text-muted small mb-2">Your private key stays encrypted on this computer. Only your public key is shared with viberails.ai when you have a saved API key.</p>
                <p class="text-muted small mb-2">Registering a public key lets anyone verify your signatures and see your account email when a signature is valid.</p>
                <p class="text-muted small mb-0" data-keys-api-status role="status"></p>
            </div>
            <div class="sandbox-item-card text-start mb-3">
                <h5 class="sandbox-item-name mb-3">Create a key pair</h5>
                <form data-key-create novalidate autocomplete="off">
                    <div class="mb-3">
                        <label class="form-label" for="key-create-name">Key name</label>
                        <input class="form-control" id="key-create-name" maxlength="80" placeholder="My signing key" required>
                    </div>
                    <div class="row g-3 mb-2">
                        <div class="col-sm-6">
                            <label class="form-label" for="key-create-password">Password</label>
                            <input type="password" class="form-control" id="key-create-password" autocomplete="new-password" aria-describedby="key-password-help" required>
                        </div>
                        <div class="col-sm-6">
                            <label class="form-label" for="key-create-confirm">Confirm password</label>
                            <input type="password" class="form-control" id="key-create-confirm" autocomplete="new-password" required>
                        </div>
                    </div>
                    <p class="form-text text-muted small" id="key-password-help">Use 8–128 characters; a passphrase of several words is strongest, and digits alone are not accepted. This password is the only protection for a copied key file or a backup. Keep it safe; it cannot be recovered.</p>
                    <button type="submit" class="btn btn-outline-primary">Create RSA-4096 key pair</button>
                </form>
            </div>
            <div class="sandbox-item-card text-start mb-3">
                <div class="d-flex justify-content-between align-items-center mb-3">
                    <h5 class="sandbox-item-name mb-0">Your keys on this computer</h5>
                    <button type="button" class="btn btn-sm btn-outline-secondary" data-key-action="refresh">Refresh</button>
                </div>
                <div data-keys-list></div>
                <p class="small text-warning mt-2 mb-0" data-keys-warnings role="status" hidden></p>
            </div>
            <div class="sandbox-item-card text-start mb-3" data-key-workbench hidden></div>
            <p class="small mb-3" data-keys-feedback role="status" aria-live="polite"></p>`;
        this.root.querySelector('[data-key-create]').addEventListener('submit', event => {
            event.preventDefault();
            void this.create();
        });
        this.root.addEventListener('click', event => {
            const button = event.target.closest('[data-key-action]');
            if (button && this.root.contains(button)) void this.handleAction(button);
        }, { signal: this.abortController.signal });
        window.addEventListener('pagehide', this.onPageHide);
    }

    async request(path, method = 'GET', body = null) {
        return this.app.apiCall(path, method, body, {
            showLoading: false, preferErrorResponseMessage: true, signal: this.abortController.signal
        });
    }

    async run(operation, busyMessage) {
        if (this.busy || this.disposed) return;
        this.setBusy(true);
        this.feedback(busyMessage);
        try {
            await operation();
        } catch (error) {
            if (!this.disposed) this.feedback(error?.message || 'The key operation failed. Please try again.', true);
        } finally {
            this.clearSecrets();
            if (!this.disposed) this.setBusy(false);
        }
    }

    async refresh() {
        await this.run(async () => {
            const response = await this.request(KEYS_API);
            if (this.disposed) return;
            this.keys = response.keys || [];
            this.warnings = Array.isArray(response.warnings) ? response.warnings : [];
            this.apiKeyConfigured = response.apiKeyConfigured === true;
            this.renderKeys();
            this.feedback('');
        }, 'Loading your keys…');
    }

    async create() {
        if (this.busy || this.disposed) return;
        const nameInput = this.root.querySelector('#key-create-name');
        const name = nameInput.value.trim();
        const password = normalizeKeyPassword(this.root.querySelector('#key-create-password').value);
        const confirmation = this.root.querySelector('#key-create-confirm').value;
        const error = validateKeyPassword(password, confirmation);
        this.clearSecrets();
        if (!name || [...name].length > 80) return this.feedback('Enter a key name with 1–80 characters.', true);
        if (error) return this.feedback(error, true);
        await this.run(async () => {
            const response = await this.request(KEYS_API, 'POST', { name, password });
            if (this.disposed) return;
            this.upsertKey(response.key);
            nameInput.value = '';
            this.showSyncResult(response, 'Key pair created.');
        }, 'Creating and protecting your RSA-4096 key pair…');
    }

    upsertKey(key) {
        this.keys = [key, ...this.keys.filter(item => item.id !== key.id)];
        if (this.actionKey?.id === key.id) this.actionKey = key;
        this.renderKeys();
    }

    showSyncResult(response, prefix = '') {
        if (response.syncStatus === 'synced') {
            this.apiKeyConfigured = true;
            this.feedback(`${prefix} Public key saved to your viberails.ai account.`.trim());
        } else if (response.syncStatus === 'not_configured') {
            this.apiKeyConfigured = false;
            this.feedback(`${prefix} Save an API key in General, then use Sync public key.`.trim());
        } else {
            this.feedback(`${prefix} The key is saved locally, but public key sync failed. ${response.syncError || 'Please try Sync public key again.'}`.trim(), true);
        }
        this.renderKeys();
    }

    renderKeys() {
        const status = this.root.querySelector('[data-keys-api-status]');
        status.textContent = this.apiKeyConfigured
            ? 'New public keys sync automatically using your saved API key.'
            : 'Save your viberails.ai API key in General to sync public keys to your account.';
        this.root.querySelector('[data-keys-list]').innerHTML = this.keys.length === 0
            ? '<p class="text-muted small mb-0">No keys yet. Create your first key pair above.</p>'
            : this.keys.map(key => `
                <article class="settings-key-item">
                    <div class="d-flex justify-content-between gap-2 flex-wrap">
                        <strong class="text-break">${escapeHtml(key.name)}</strong>
                        <span class="small ${key.cloudKeyId ? 'text-success' : 'text-muted'}">${key.cloudKeyId ? 'Public key synced' : 'Stored locally'}</span>
                    </div>
                    <div class="text-muted small my-1">Created ${escapeHtml(this.formatDate(key.createdUtc))}</div>
                    <div class="small mb-2">SHA-256 fingerprint<br><code class="settings-key-fingerprint">${escapeHtml(key.fingerprint)}</code></div>
                    <div class="d-flex flex-wrap gap-2">
                        <button type="button" class="btn btn-sm btn-outline-secondary" data-key-action="public" data-key-id="${escapeHtml(key.id)}">Download public key</button>
                        <button type="button" class="btn btn-sm btn-outline-secondary" data-key-action="backup" data-key-id="${escapeHtml(key.id)}">Back up private key</button>
                        <button type="button" class="btn btn-sm btn-outline-primary" data-key-action="sign" data-key-id="${escapeHtml(key.id)}">Sign</button>
                        <button type="button" class="btn btn-sm btn-outline-secondary" data-key-action="sync" data-key-id="${escapeHtml(key.id)}">Sync public key</button>
                    </div>
                </article>`).join('');
        const warnings = this.root.querySelector('[data-keys-warnings]');
        if (warnings) {
            warnings.textContent = this.warnings.join(' ');
            warnings.hidden = this.warnings.length === 0;
        }
        this.setBusy(this.busy);
    }

    async handleAction(button) {
        if (this.busy || this.disposed) return;
        const action = button.dataset.keyAction;
        if (action === 'refresh') return this.refresh();
        if (action === 'close') return this.closeWorkbench();
        const key = this.keys.find(item => item.id === button.dataset.keyId);
        if (!key) return;
        if (action === 'public') return this.download(`${this.safeName(key)}-public.pem`, key.publicKeyPem, 'application/x-pem-file');
        if (action === 'sign' || action === 'backup' || action === 'sync') this.openWorkbench(key, action);
    }

    openWorkbench(key, action) {
        this.clearSecrets();
        this.actionKey = key;
        const signing = action === 'sign';
        const syncing = action === 'sync';
        const workbench = this.root.querySelector('[data-key-workbench]');
        workbench.hidden = false;
        workbench.innerHTML = `
            <div class="d-flex justify-content-between gap-2 mb-2">
                <h5 class="sandbox-item-name mb-0">${signing ? 'Sign with' : syncing ? 'Sync' : 'Back up'} ${escapeHtml(key.name)}</h5>
                <button type="button" class="btn btn-sm btn-outline-secondary" data-key-action="close" aria-label="Close key action">Close</button>
            </div>
            <p class="text-muted small">${signing ? 'Sign the exact text or file bytes with RSA-PSS-SHA256. The download includes the content and signature for verification.' : syncing ? 'Unlock your key to prove it belongs to you, then register its public key with your account. Valid signatures will identify your account email publicly.' : 'Download a password-encrypted private key backup. You will need this same password to use it, and the backup is only as strong as that password.'}</p>
            <form data-key-use novalidate autocomplete="off">
                ${signing ? `
                    <div class="mb-3">
                        <label class="form-label" for="key-sign-type">What to sign</label>
                        <select class="form-select" id="key-sign-type"><option value="message">Message</option><option value="file">File</option></select>
                    </div>
                    <div class="mb-3" data-key-message>
                        <label class="form-label" for="key-sign-message">Message</label>
                        <textarea class="form-control" id="key-sign-message" rows="3" maxlength="65536" spellcheck="false"></textarea>
                    </div>
                    <div class="mb-3" data-key-file hidden>
                        <label class="form-label" for="key-sign-file">File (up to 64 KiB)</label>
                        <input type="file" class="form-control" id="key-sign-file">
                    </div>` : ''}
                <div class="mb-3">
                    <label class="form-label" for="key-use-password">Password for this key</label>
                    <input type="password" class="form-control" id="key-use-password" autocomplete="off" required>
                </div>
                <button type="submit" class="btn btn-outline-primary">${signing ? 'Sign & download verification JSON' : syncing ? 'Unlock & sync public key' : 'Download encrypted private key'}</button>
            </form>
            ${signing ? `<p class="text-muted small mt-3 mb-0">Verify by sending the downloaded JSON to <code class="settings-key-fingerprint">POST ${VERIFY_URL}</code>. ${key.cloudKeyId ? '' : 'Sync this public key to your account before generating a request for online verification.'}</p>` : ''}`;
        workbench.querySelector('[data-key-use]').addEventListener('submit', event => {
            event.preventDefault();
            void this.useKey(action);
        });
        workbench.querySelector('#key-sign-type')?.addEventListener('change', event => {
            const isFile = event.target.value === 'file';
            workbench.querySelector('[data-key-message]').hidden = isFile;
            workbench.querySelector('[data-key-file]').hidden = !isFile;
        });
        workbench.scrollIntoView?.({ block: 'nearest', behavior: 'smooth' });
        workbench.querySelector(signing ? '#key-sign-message' : '#key-use-password').focus();
    }

    async useKey(action) {
        if (this.busy || this.disposed || !this.actionKey) return;
        const key = this.actionKey;
        const password = normalizeKeyPassword(this.root.querySelector('#key-use-password').value);
        this.clearSecrets();
        const error = validateKeyPassword(password);
        if (error) return this.feedback(error, true);
        await this.run(async () => {
            const path = `${KEYS_API}/${encodeURIComponent(key.id)}`;
            if (action === 'sync') {
                const response = await this.request(`${path}/sync`, 'POST', { password });
                if (this.disposed) return;
                this.upsertKey(response.key);
                this.showSyncResult(response);
                return;
            }
            if (action === 'backup') {
                const response = await this.request(`${path}/export`, 'POST', { password });
                if (this.disposed) return;
                this.download(response.fileName, response.encryptedPrivateKeyPem, 'application/x-pem-file');
                this.feedback('Encrypted private key backup downloaded. Keep it and your password safe.');
                return;
            }
            const bytes = await this.readSigningBytes();
            if (this.disposed) return;
            const payloadBase64 = signingPayloadBase64(bytes);
            const response = await this.request(`${path}/sign`, 'POST', { password, payloadBase64 });
            if (this.disposed) return;
            // The verify endpoint accepts this file as-is; fingerprint and public key also make
            // offline verification possible without the cloud.
            const verification = {
                keyId: response.keyId, fingerprint: response.fingerprint, publicKeyPem: response.publicKeyPem,
                algorithm: response.algorithm, payloadBase64: response.payloadBase64, signatureBase64: response.signatureBase64
            };
            this.download(`${this.safeName(key)}-signature.json`, JSON.stringify(verification, null, 2), 'application/json');
            this.feedback(response.keyId ? 'Signed verification JSON downloaded.' : 'Signature downloaded. Sync your public key, then sign again to include its online key ID.');
        }, action === 'backup' ? 'Unlocking encrypted backup…' : action === 'sync' ? 'Proving key ownership and syncing public key…' : 'Signing…');
    }

    async readSigningBytes() {
        if (this.root.querySelector('#key-sign-type').value === 'file') {
            const file = this.root.querySelector('#key-sign-file').files?.[0];
            if (!file) throw new Error('Choose a file to sign.');
            if (file.size > MAX_SIGNING_BYTES) throw new Error('Choose a file no larger than 64 KiB.');
            return new Uint8Array(await file.arrayBuffer());
        }
        return new TextEncoder().encode(this.root.querySelector('#key-sign-message').value);
    }

    closeWorkbench() {
        this.clearSecrets();
        const workbench = this.root.querySelector('[data-key-workbench]');
        workbench.hidden = true;
        workbench.innerHTML = '';
        this.actionKey = null;
    }

    feedback(message, error = false) {
        const target = this.root.querySelector('[data-keys-feedback]');
        if (!target || this.disposed) return;
        target.textContent = message;
        target.classList.toggle('text-danger', error);
        target.classList.toggle('text-muted', !error);
    }

    setBusy(busy) {
        this.busy = busy;
        this.root.setAttribute('aria-busy', String(busy));
        this.root.querySelectorAll('button, input, select, textarea').forEach(input => { input.disabled = busy; });
    }

    clearSecrets() {
        this.root.querySelectorAll('input[type="password"]').forEach(input => { input.value = ''; });
    }

    formatDate(value) {
        const date = new Date(value);
        return Number.isNaN(date.getTime()) ? '' : date.toLocaleDateString();
    }

    safeName(key) {
        return key.name.replace(/[^a-zA-Z0-9_-]+/g, '-').slice(0, 80) || 'signing-key';
    }

    download(fileName, content, type) {
        const url = URL.createObjectURL(new Blob([content], { type }));
        const link = document.createElement('a');
        link.href = url;
        link.download = fileName;
        document.body.append(link);
        link.click();
        link.remove();
        setTimeout(() => URL.revokeObjectURL(url), 1000);
    }

    unload() {
        this.clearSecrets();
        this.disposed = true;
        this.abortController.abort();
        window.removeEventListener('pagehide', this.onPageHide);
        this.actionKey = null;
        this.keys = [];
    }
}
