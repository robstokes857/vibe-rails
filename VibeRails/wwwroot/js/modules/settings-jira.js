// Jira Cloud connection for the current board. The token input is write-only and this panel
// saves itself; it is not part of the shared Settings form, so a token never rides that payload.

import { BoardApi } from './board-api.js';
import { pickSelectedBoard } from './board-selection.js';

export class SettingsJiraPanel {
    constructor(app, root) {
        this.app = app;
        this.root = root;
        this._loaded = false;
    }

    async activate() {
        if (this._loaded) return;
        this._loaded = true;
        this.root.querySelector('[data-jira-action="save"]')?.addEventListener('click', () => this._save());
        this.root.querySelector('[data-jira-action="test"]')?.addEventListener('click', () => this._test());
        this.root.querySelector('[data-jira-action="dry-run"]')?.addEventListener('click', () => this._pull(true));
        this.root.querySelector('[data-jira-action="pull"]')?.addEventListener('click', () => this._pull(false));
        await this._load();
    }

    clearSecrets() {
        const token = this.root.querySelector('[data-jira-token]');
        if (token) token.value = '';
    }

    // The board the Board view last had open, so Save/Test/Pull never act on a different board
    // than the one the user is looking at. The panel names it.
    async _selectedBoard() {
        BoardApi.attach(this.app);
        return pickSelectedBoard(await BoardApi.getBoardsAsync());
    }

    async _load() {
        const board = await this._selectedBoard();
        const label = this.root.querySelector('[data-jira-board]');
        if (label) label.textContent = board?.name || '';
        if (!board) {
            this._report('Open a project with a board before connecting Jira.');
            return;
        }
        const boardId = board.id;
        this._board = boardId;
        const connection = await BoardApi.getJiraConnectionAsync(boardId);
        this._fill(connection);
    }

    _fill(connection) {
        this._set('[data-jira-site]', connection.siteUrl || '');
        this._set('[data-jira-email]', connection.email || '');
        this._set('[data-jira-jql]', connection.jql || '');
        this._set('[data-jira-points]', connection.storyPointsFieldId || '');
        const enabled = this.root.querySelector('[data-jira-enabled]');
        if (enabled) enabled.checked = !!connection.enabled;
        const token = this.root.querySelector('[data-jira-token]');
        if (token) {
            token.value = '';
            token.placeholder = connection.hasToken ? 'Token saved — leave blank to keep it' : 'API token';
        }
        const badge = this.root.querySelector('[data-jira-status]');
        if (badge) {
            const status = connection.authStatus || 'none';
            badge.textContent = status === 'saved' ? 'Token saved' : status === 'expired' ? 'Expired' : 'Not connected';
            badge.className = 'badge ' + (status === 'saved' ? 'text-bg-success' : status === 'expired' ? 'text-bg-danger' : 'text-bg-secondary');
        }
        if (connection.disabledReason) this._report(connection.disabledReason);
        else if (connection.lastReport) this._report(connection.lastReport);
    }

    _body() {
        return {
            siteUrl: this.root.querySelector('[data-jira-site]')?.value ?? '',
            email: this.root.querySelector('[data-jira-email]')?.value ?? '',
            apiToken: this.root.querySelector('[data-jira-token]')?.value ?? '',
            jql: this.root.querySelector('[data-jira-jql]')?.value ?? '',
            storyPointsFieldId: this.root.querySelector('[data-jira-points]')?.value ?? '',
            enabled: !!this.root.querySelector('[data-jira-enabled]')?.checked
        };
    }

    async _save() {
        if (!this._board) return;
        try {
            const saved = await BoardApi.saveJiraConnectionAsync(this._board, this._body());
            this._fill(saved);
            this.app.showToast('Jira', 'Connection saved. A pull replaces mapped fields with Jira\'s values.', 'success');
        } catch (error) {
            this._report(error?.message || 'Could not save the Jira connection.');
        }
    }

    async _test() {
        if (!this._board) return;
        this._report('Testing…');
        try {
            const result = await BoardApi.testJiraConnectionAsync(this._board);
            const message = result.ok ? `Connected as ${result.account}.` : (result.error || 'Jira refused the connection.');
            await this._load();
            this._report(message);
        } catch (error) {
            this._report(error?.message || 'Test failed.');
        }
    }

    async _pull(dryRun) {
        if (!this._board) return;
        this._report(dryRun ? 'Dry run…' : 'Pulling…');
        try {
            const report = await BoardApi.pullJiraAsync(this._board, dryRun);
            this._report(report.message || report.outcome);
            if (!dryRun) await this._load();
        } catch (error) {
            this._report(error?.message || 'Pull failed.');
        }
    }

    _set(selector, value) {
        const element = this.root.querySelector(selector);
        if (element) element.value = value;
    }

    _report(text) {
        const element = this.root.querySelector('[data-jira-report]');
        if (element) element.textContent = text || '';
    }
}
