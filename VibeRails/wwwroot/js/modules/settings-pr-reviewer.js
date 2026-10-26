import { escapeHtml } from './utils.js';

// Shared machine setting; saves immediately and never joins the general settings form.
export class PrReviewerSettingsPanel {
    constructor(app, root) {
        this.app = app;
        this.root = root;
        this.abort = new AbortController();
        this.toggle = root.querySelector('[data-pr-reviewer-enabled]');
        this.status = root.querySelector('[data-pr-reviewer-status]');
        this.repositories = [];
        this.repositoryList = root.querySelector('[data-pr-reviewer-repositories]');
        this.repositoryInput = root.querySelector('[data-pr-reviewer-repository]');
        this.addButton = root.querySelector('[data-pr-reviewer-add-repository]');
        root.querySelector('[data-pr-reviewer-repository-form]').addEventListener('submit', event => {
            event.preventDefault();
            void this.addRepository();
        }, { signal: this.abort.signal });
        this.repositoryList.addEventListener('click', event => {
            const button = event.target.closest('[data-pr-reviewer-remove-repository]');
            if (button) void this.removeRepository(Number(button.dataset.prReviewerRemoveRepository));
        }, { signal: this.abort.signal });
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
            this.repositories = settings.repositories || [];
            this.renderRepositories();
            this.setDisabled(false);
            this.status.textContent = settings.enabled ? 'PR Reviewer and GitHub publishing are on.' : 'PR Reviewer and GitHub publishing are off.';
        } catch (error) {
            if (!this.abort.signal.aborted) this.status.textContent = error.message || 'Could not load PR Reviewer settings. Reload to try again.';
        } finally { this.busy = false; }
    }

    async addRepository() {
        if (this.busy || this.repositoryInput.disabled || this.abort.signal.aborted) return;
        const repository = this.repositoryInput.value.trim().toLowerCase();
        const [owner, name] = repository.split('/');
        if (!/^[a-z0-9](?:[a-z0-9]|-(?=[a-z0-9])){0,38}$/.test(owner || '') ||
            !/^[a-z0-9._-]{1,100}$/.test(name || '') || name === '.' || name === '..' || repository.split('/').length !== 2) {
            this.status.textContent = 'Enter a GitHub repository as owner/repository.';
            this.repositoryInput.focus();
            return;
        }
        if (this.repositories.some(saved => saved.toLowerCase() === repository)) {
            this.status.textContent = 'That repository is already in the list.';
            this.repositoryInput.focus();
            return;
        }
        if (this.repositories.length >= 50) {
            this.status.textContent = 'Watch at most 50 repositories. Remove one to add another.';
            return;
        }
        if (await this.save([...this.repositories, repository])) {
            this.repositoryInput.value = '';
            this.repositoryInput.focus();
        }
    }

    async removeRepository(index) {
        if (this.busy || this.repositoryInput.disabled || this.abort.signal.aborted ||
            !Number.isInteger(index) || index < 0 || index >= this.repositories.length) return;
        if (await this.save(this.repositories.filter((_, i) => i !== index))) {
            const buttons = this.repositoryList.querySelectorAll('button');
            (buttons[Math.min(index, buttons.length - 1)] || this.repositoryInput).focus();
        }
    }

    renderRepositories() {
        this.repositoryList.hidden = this.repositories.length === 0;
        this.repositoryList.innerHTML = this.repositories.map((repository, index) => `
            <li class="d-flex align-items-center justify-content-between gap-2 py-2 border-bottom">
                <span class="text-break">${escapeHtml(repository)}</span>
                <button type="button" class="btn btn-sm btn-outline-secondary flex-shrink-0"
                    data-pr-reviewer-remove-repository="${index}" aria-label="Remove ${escapeHtml(repository)}">Remove</button>
            </li>`).join('');
    }

    async save(repositories = null) {
        if (this.busy || this.abort.signal.aborted) return;
        this.busy = true;
        this.setDisabled(true);
        this.status.textContent = 'Saving…';
        try {
            const body = { enabled: this.toggle.checked };
            if (repositories !== null) body.repositories = repositories;
            const settings = await this.request('PUT', body);
            if (this.abort.signal.aborted) return;
            this.toggle.checked = settings.enabled === true;
            this.app.updatePrReviewerNav(settings);
            this.repositories = settings.repositories || [];
            this.renderRepositories();
            this.setDisabled(false);
            this.status.textContent = repositories !== null ? 'Saved repositories.' : settings.enabled ? 'Saved: PR Reviewer and GitHub publishing are on.' : 'Saved: PR Reviewer and GitHub publishing are off.';
            return true;
        } catch (error) {
            // A lost PUT response is uncertain. Reload the saved value before another edit.
            if (!this.abort.signal.aborted) this.status.textContent = `${error.message || 'Save was not confirmed.'} Reload to check the saved setting.`;
        } finally { this.busy = false; }
    }

    setDisabled(disabled) {
        this.toggle.disabled = disabled;
        this.repositoryInput.disabled = disabled;
        this.addButton.disabled = disabled;
        this.repositoryList.querySelectorAll('button').forEach(button => { button.disabled = disabled; });
    }

    unload() { this.abort.abort(); }
}
