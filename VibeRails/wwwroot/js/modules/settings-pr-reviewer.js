// Shared machine setting; saves immediately and never joins the general settings form.
export class PrReviewerSettingsPanel {
    constructor(app, root) {
        this.app = app;
        this.root = root;
        this.abort = new AbortController();
        this.toggle = root.querySelector('[data-pr-reviewer-enabled]');
        this.status = root.querySelector('[data-pr-reviewer-status]');
        this.repositories = root.querySelector('[data-pr-reviewer-repositories]');
        this.saveRepositories = root.querySelector('[data-pr-reviewer-save-repositories]');
        this.saveRepositories.addEventListener('click', () => void this.save(true), { signal: this.abort.signal });
        this.toggle.addEventListener('change', () => void this.save(), { signal: this.abort.signal });
        root.querySelector('[data-pr-reviewer-reload]').addEventListener('click', () => void this.load(), { signal: this.abort.signal });
    }

    async request(method, body = null) {
        return this.app.apiCall('/api/v1/pr-reviews/settings', method, body, {
            showLoading: false, preferErrorResponseMessage: true, signal: this.abort.signal
        });
    }

    async load() {
        if (this.busy || this.abort.signal.aborted) return;
        this.busy = true;
        this.setDisabled(true);
        this.status.textContent = 'Loading PR Reviewer settings…';
        try {
            const settings = await this.request('GET');
            if (this.abort.signal.aborted) return;
            this.toggle.checked = settings.enabled === true;
            this.app.updatePrReviewerNav(settings);
            this.repositories.value = (settings.repositories || []).join('\n');
            this.setDisabled(false);
            this.status.textContent = settings.enabled ? 'PR Reviewer and GitHub publishing are on.' : 'PR Reviewer and GitHub publishing are off.';
        } catch (error) {
            if (!this.abort.signal.aborted) this.status.textContent = error.message || 'Could not load PR Reviewer settings. Reload to try again.';
        } finally { this.busy = false; }
    }

    async save(includeRepositories = false) {
        if (this.busy || this.abort.signal.aborted) return;
        this.busy = true;
        this.setDisabled(true);
        this.status.textContent = 'Saving…';
        try {
            const body = { enabled: this.toggle.checked };
            if (includeRepositories) body.repositories = this.repositories.value.split(/\r?\n/).map(s => s.trim()).filter(Boolean);
            const settings = await this.request('PUT', body);
            if (this.abort.signal.aborted) return;
            this.toggle.checked = settings.enabled === true;
            this.app.updatePrReviewerNav(settings);
            this.setDisabled(false);
            if (includeRepositories) this.repositories.value = (settings.repositories || []).join('\n');
            this.status.textContent = includeRepositories ? 'Saved repositories. Open Reviews to load their queues.' : settings.enabled ? 'Saved: PR Reviewer and GitHub publishing are on.' : 'Saved: PR Reviewer and GitHub publishing are off.';
        } catch (error) {
            // A lost PUT response is uncertain. Reload the saved value before another edit.
            if (!this.abort.signal.aborted) this.status.textContent = `${error.message || 'Save was not confirmed.'} Reload to check the saved setting.`;
        } finally { this.busy = false; }
    }

    setDisabled(disabled) {
        this.toggle.disabled = disabled;
        this.repositories.disabled = disabled;
        this.saveRepositories.disabled = disabled;
    }

    unload() { this.abort.abort(); }
}
