// The AI board manager switch (VIBE-204): one machine-wide setting, off by default. It saves at
// once and never joins the general settings form, like the PR Reviewer switch. While it is off the
// board shows no manager chip, Board settings has no Manager tab, and the card editor offers no
// "Use the board manager"; the backend refuses to save or start a board manager.
export const AI_BOARD_MANAGER_PATH = '/api/v1/board/ai-board-manager';

/** The status sentence under the switch for a loaded or saved value. */
export const aiBoardManagerStatus = (enabled, saved = false) =>
    `${saved ? 'Saved: ' : ''}${enabled
        ? 'The AI board manager is on. Set up a board\'s manager in its Board settings > Manager.'
        : 'The AI board manager is off.'}`;

export class AiBoardManagerSettingsPanel {
    constructor(app, root) {
        this.app = app;
        this.root = root;
        this.abort = new AbortController();
        this.busy = false;
        this.toggle = root.querySelector('[data-ai-board-manager-enabled]');
        this.status = root.querySelector('[data-ai-board-manager-status]');
        this.toggle?.addEventListener('change', () => void this.save(), { signal: this.abort.signal });
        root.querySelector('[data-ai-board-manager-reload]')?.addEventListener('click', () => void this.load(), { signal: this.abort.signal });
    }

    request(method, body = null) {
        return this.app.apiCall(AI_BOARD_MANAGER_PATH, method, body, {
            showLoading: false, preferErrorResponseMessage: true, signal: this.abort.signal
        });
    }

    async load() {
        if (this.busy || this.abort.signal.aborted || !this.toggle) return;
        this.busy = true;
        this.toggle.disabled = true;
        this.status.textContent = 'Loading the AI board manager setting…';
        try {
            const result = await this.request('GET');
            if (this.abort.signal.aborted) return;
            this.toggle.checked = result?.enabled === true;
            this.toggle.disabled = false;
            this.status.textContent = aiBoardManagerStatus(this.toggle.checked);
        } catch (error) {
            if (!this.abort.signal.aborted) this.status.textContent = error?.message || 'Could not load the AI board manager setting. Reload to try again.';
        } finally { this.busy = false; }
    }

    async save() {
        if (this.busy || this.abort.signal.aborted || !this.toggle) return;
        this.busy = true;
        const wanted = this.toggle.checked;
        this.toggle.disabled = true;
        this.status.textContent = 'Saving…';
        try {
            const result = await this.request('PUT', { enabled: wanted });
            if (this.abort.signal.aborted) return;
            this.toggle.checked = result?.enabled === true;
            this.toggle.disabled = false;
            this.status.textContent = aiBoardManagerStatus(this.toggle.checked, true);
        } catch (error) {
            // A lost PUT response is uncertain: reload the saved value before another change.
            if (!this.abort.signal.aborted) this.status.textContent = `${error?.message || 'Save was not confirmed.'} Reload to check the saved setting.`;
        } finally { this.busy = false; }
    }

    unload() { this.abort.abort(); }
}
