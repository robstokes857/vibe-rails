// Jira Cloud settings belong to the board being edited (VIBE-80). One Jira board link plus an API
// token connects it (VIBE-102): the board supplies its issues, its columns and its story points
// field, so there is no site, JQL or field id to type. Tokens are write-only and saved separately.
import { BoardApi } from './board-api.js';
import { escapeHtml } from './utils.js';

export const JIRA_TOKEN_URL = 'https://id.atlassian.com/manage-profile/security/api-tokens';

export const boardJiraSection = () => `
    <section data-jira-panel>
        <h6 class="mb-0" data-jira-heading>Jira Cloud <span class="badge text-bg-secondary ms-2" data-jira-status>Not connected</span></h6>
        <fieldset class="mt-3" data-jira-fields disabled>
            <p class="small mb-2">Board: <strong data-jira-board></strong></p>
            <p class="text-muted small">Connect creates a separate Jira board for imported issues. Existing Jira cards move there with their comments, files and sessions. Use the board picker to switch between Jira and your local work. Jira wins on title, description, lane, type, priority, tags and story points. A local edit to those fields is replaced on the next pull. Assignee, flagged, blocked, comments, sessions and commits stay here.</p>
            <div class="mb-3">
                <label class="form-label" for="jira-link">Jira board link</label>
                <input class="form-control" id="jira-link" data-jira-link type="url" autocomplete="off" spellcheck="false"
                    placeholder="https://your-site.atlassian.net/jira/software/projects/KEY/boards/1">
                <small class="form-text text-muted" data-jira-link-help>Open the board in Jira and copy the address bar.</small>
            </div>
            <div class="mb-3">
                <label class="form-label" for="jira-token">API token</label>
                <input class="form-control" id="jira-token" data-jira-token type="text" autocomplete="off"
                    spellcheck="false" autocapitalize="none" autocorrect="off" placeholder="Paste an API token">
                <small class="form-text text-muted"><a href="${JIRA_TOKEN_URL}" target="_blank" rel="noopener noreferrer" data-jira-token-link>Create a token <i class="fa-solid fa-arrow-up-right-from-square" aria-hidden="true"></i></a>. Saved on this machine with your other API keys. It is never written to the board and never shown again.</small>
            </div>
            <div class="mb-3">
                <label class="form-label" for="jira-email">Atlassian account email</label>
                <input class="form-control" id="jira-email" data-jira-email type="email" autocomplete="off" spellcheck="false"
                    placeholder="you@example.com">
                <small class="form-text text-muted" data-jira-email-help>The account the API token belongs to.</small>
            </div>
            <div class="form-check form-switch mb-2">
                <input class="form-check-input" type="checkbox" id="jira-enabled" data-jira-enabled>
                <label class="form-check-label" for="jira-enabled">Pull about every 15 minutes while VibeRails is open</label>
            </div>
            <div class="form-check mb-3">
                <input class="form-check-input" type="checkbox" id="jira-skip-done" data-jira-skip-done>
                <label class="form-check-label" for="jira-skip-done">Skip Done issues not updated in the last 30 days</label>
            </div>
            <div class="d-flex flex-wrap gap-2">
                <button type="button" class="btn btn-outline-primary btn-sm" data-jira-action="connect">Connect</button>
                <button type="button" class="btn btn-outline-secondary btn-sm" data-jira-action="pull">Pull now</button>
            </div>
            <div class="small mt-3" data-jira-summary hidden></div>
            <details class="mt-3" data-jira-advanced>
                <summary class="small">Advanced: narrow with JQL, story points, lanes</summary>
                <p class="text-muted small mt-2 mb-2">Saved when you click Connect.</p>
                <div class="mb-3">
                    <label class="form-label" for="jira-narrow">Narrow with JQL</label>
                    <textarea class="form-control" id="jira-narrow" data-jira-narrow rows="2" spellcheck="false"
                        placeholder="labels = frontend"></textarea>
                    <small class="form-text text-muted">Optional. Added to the board's own filter with AND. Leave out ORDER BY.</small>
                </div>
                <div class="mb-3">
                    <label class="form-label" for="jira-points-field">Story points field</label>
                    <input class="form-control" id="jira-points-field" data-jira-points type="text" autocomplete="off" spellcheck="false"
                        placeholder="From the board">
                    <small class="form-text text-muted">Leave blank to use the board's estimation field. A field id looks like customfield_10016.</small>
                </div>
                <p class="form-label mb-1">Lane for each Jira column</p>
                <div data-jira-lanes></div>
            </details>
        </fieldset>
        <p class="text-muted small mt-3 mb-0" data-jira-report role="status"></p>
    </section>`;

const OVERFLOW_LABEL = 'Jira lane (unmatched)';

/** "To Do → Backlog · In Progress → Jira lane (unmatched)". Escaped. */
export function jiraColumnSummary(columns) {
    return (columns || [])
        .map(column => `${escapeHtml(column.name)} → ${escapeHtml(column.laneName || OVERFLOW_LABEL)}`)
        .join(' · ');
}

/** The lane picker rows. A blank value is automatic, labelled with where automatic puts the column. */
export function jiraLaneRows(columns, lanes) {
    if (!columns?.length) {
        return '<p class="text-muted small mb-0" data-jira-lanes-empty>Connect to read the board\'s columns. Until then, lanes match Jira status names.</p>';
    }
    return columns.map((column, index) => {
        const automatic = column.automatic ? `Automatic (${escapeHtml(column.laneName || OVERFLOW_LABEL)})` : 'Automatic';
        const options = (lanes || []).map(lane =>
            `<option value="${escapeHtml(lane.id)}"${!column.automatic && lane.id === column.laneId ? ' selected' : ''}>${escapeHtml(lane.name)}</option>`).join('');
        return `
            <div class="d-flex flex-wrap align-items-center gap-2 mb-2">
                <label class="small text-break flex-grow-1" for="jira-lane-${index}">${escapeHtml(column.name)}</label>
                <select class="form-select form-select-sm w-auto mw-100" id="jira-lane-${index}" data-jira-lane="${index}">
                    <option value="">${automatic}</option>${options}
                </select>
            </div>`;
    }).join('');
}

export class BoardJiraPanel {
    constructor(app, root, board, onChanged) {
        this.app = app;
        this.root = root;
        // The settings dialog moves the heading into its header after mounting the panel.
        this._status = root.querySelector('[data-jira-status]');
        this._board = board.id;
        this._boardName = board.name;
        this._onChanged = onChanged;
        this._events = new AbortController();
        this._loaded = false;
        this._ready = false;
        this._busy = false;
        this._disposed = false;
        this._columns = [];
        this._lanes = [];
        this._savedLink = '';
    }

    _alive() {
        return !this._disposed && this.root.isConnected !== false;
    }

    async activate() {
        if (this._loaded || !this._alive()) return;
        this._loaded = true;
        BoardApi.attach(this.app);
        const label = this.root.querySelector('[data-jira-board]');
        if (label) label.textContent = this._boardName;
        const actions = { connect: () => this._connect(), pull: () => this._pull(false) };
        for (const [action, handler] of Object.entries(actions)) {
            this.root.querySelector(`[data-jira-action="${action}"]`)?.addEventListener('click', handler,
                { signal: this._events.signal });
        }
        await this._run(async () => {
            await this._load();
            this._ready = true;
        });
    }

    clearSecrets() {
        const token = this.root.querySelector('[data-jira-token]');
        if (token) token.value = '';
    }

    dispose() {
        this._disposed = true;
        this._events.abort();
        this.clearSecrets();
    }

    async _load() {
        const connection = await BoardApi.getJiraConnectionAsync(this._board, { settings: true });
        if (this._alive()) this._fill(connection);
    }

    async _run(action) {
        if (this._busy || !this._alive()) return;
        this._busy = true;
        const fields = this.root.querySelector('[data-jira-fields]');
        if (fields) fields.disabled = true;
        try {
            await action();
        } catch (error) {
            if (this._alive()) this._report(error?.message || 'Could not load the Jira connection. Reopen Board Settings to retry.');
        } finally {
            this._busy = false;
            if (this._alive() && fields) fields.disabled = !this._ready;
        }
    }

    _fill(connection) {
        const saved = !!(connection.boardLink || connection.jql);
        this._savedLink = connection.boardLink || '';
        this._set('[data-jira-link]', this._savedLink);
        this._fillEmail(connection);
        this._set('[data-jira-narrow]', connection.narrowJql || '');
        this._set('[data-jira-points]', connection.storyPointsFieldId || '');
        // A new connection pulls on the interval. Old Done issues are left out unless that was
        // saved off, including for a VB-40 JQL connection being moved to a board link.
        this._check('[data-jira-enabled]', saved ? !!connection.enabled : true);
        this._check('[data-jira-skip-done]', connection.skipOldDone ?? true);
        const token = this.root.querySelector('[data-jira-token]');
        if (token) {
            // The dots only indicate a saved token; never submit a masking value as a replacement.
            token.value = '';
            token.placeholder = connection.hasToken ? '••••••••••••' : 'Paste an API token';
        }
        const help = this.root.querySelector('[data-jira-link-help]');
        if (help) {
            help.textContent = !connection.boardLink && connection.jql
                ? `This board pulls a saved JQL filter on ${connection.siteUrl || 'Jira'}: ${connection.jql}. Paste a board link to use the board's own issues and columns instead.`
                : 'Open the board in Jira and copy the address bar.';
        }
        const badge = this._status;
        if (badge) {
            const status = connection.authStatus || 'none';
            badge.textContent = status === 'saved' ? (connection.jiraBoardName ? 'Connected' : 'Token saved')
                : status === 'expired' ? 'Expired' : 'Not connected';
            badge.className = 'badge ' + (status === 'saved' ? 'text-bg-success' : status === 'expired' ? 'text-bg-danger' : 'text-bg-secondary');
        }
        if (Array.isArray(connection.columns)) {
            this._columns = connection.columns;
            this._lanes = connection.lanes || [];
            const lanes = this.root.querySelector('[data-jira-lanes]');
            if (lanes) lanes.innerHTML = jiraLaneRows(this._columns, this._lanes);
        }
        if (connection.jiraBoardName) {
            this._summary(`Jira board <strong>${escapeHtml(connection.jiraBoardName)}</strong>`
                + (this._columns.length ? `<br>${jiraColumnSummary(this._columns)}` : ''));
        }
        this._report(connection.disabledReason || connection.lastReport || '');
    }

    // Saved email first, then the repository's git user.email, then the VibeRails account email.
    _fillEmail(connection) {
        const account = this.app?.appSettings?.remoteAccountEmail || '';
        const email = connection.email || connection.suggestedEmail || account;
        this._set('[data-jira-email]', email);
        const help = this.root.querySelector('[data-jira-email-help]');
        if (!help) return;
        help.textContent = connection.email ? 'The account the API token belongs to.'
            : connection.suggestedEmail ? 'Filled in from git user.email. Change it if your Atlassian account uses another.'
                : account ? 'Filled in from your VibeRails account. Change it if your Atlassian account uses another.'
                    : 'The account the API token belongs to.';
    }

    _body() {
        const body = {
            boardLink: (this.root.querySelector('[data-jira-link]')?.value ?? '').trim(),
            email: this.root.querySelector('[data-jira-email]')?.value ?? '',
            apiToken: this.root.querySelector('[data-jira-token]')?.value ?? '',
            narrowJql: this.root.querySelector('[data-jira-narrow]')?.value ?? '',
            storyPointsFieldId: this.root.querySelector('[data-jira-points]')?.value ?? '',
            enabled: !!this.root.querySelector('[data-jira-enabled]')?.checked,
            skipOldDone: !!this.root.querySelector('[data-jira-skip-done]')?.checked
        };
        // Lane picks belong to the board they were read from; a different link starts automatic.
        if (this._columns.length && body.boardLink === (this._savedLink || '')) {
            const columnMap = {};
            for (const select of this.root.querySelectorAll('[data-jira-lane]')) {
                const column = this._columns[Number(select.dataset?.jiraLane)];
                if (column) columnMap[column.name] = select.value || '';
            }
            body.columnMap = columnMap;
        }
        return body;
    }

    // Connect = save, then check the token and read the board (its columns and an issue count).
    async _connect() {
        if (!this._ready) return;
        let savedBoard = false;
        await this._run(async () => {
            this._report('Connecting…');
            const saved = await BoardApi.saveJiraConnectionAsync(this._board, this._body());
            if (!this._alive()) return;
            const destination = saved.boardId || this._board;
            const moved = destination !== this._board;
            this._board = destination;
            savedBoard = true;
            if (moved) {
                // The store remapped these choices while cloning the lanes. Source IDs must
                // never be sent on a retry, including when the destination reload fails.
                this._columns = [];
                this._lanes = [];
                const lanes = this.root.querySelector('[data-jira-lanes]');
                if (lanes) lanes.innerHTML = jiraLaneRows([], []);
            }
            this._fill(saved);
            if (moved) {
                await this._load();
                if (!this._alive()) return;
            }
            this._report('Connecting…');
            const result = await BoardApi.testJiraConnectionAsync(this._board);
            if (!this._alive()) return;
            await this._load();
            if (!this._alive()) return;
            this._showResult(result);
        });
        // Saving may have moved the connection even if the provider test failed afterward.
        if (savedBoard && this._alive()) this._onChanged?.('test', this._board);
    }

    _showResult(result) {
        if (!result?.ok) {
            this._report(result?.error || 'Jira refused the connection.');
            return;
        }
        const board = result.board;
        if (!board) {
            this._report(`Connected as ${result.account}.`);
            return;
        }
        const count = Number.isInteger(board.issueCount)
            ? ` · about ${board.issueCount} issue${board.issueCount === 1 ? '' : 's'}`
            : '';
        const points = board.storyPointsFieldId
            ? `Story points: ${escapeHtml(board.storyPointsFieldName || board.storyPointsFieldId)}`
            : 'Story points: off';
        const warnings = (board.warnings || []).map(warning =>
            `<div class="text-warning-emphasis mt-1">${escapeHtml(warning)}</div>`).join('');
        this._summary(`<i class="fa-solid fa-circle-check text-success" aria-hidden="true"></i> `
            + `<strong>${escapeHtml(board.name || 'Board ' + board.id)}</strong>${board.type ? ` (${escapeHtml(board.type)})` : ''}${count}`
            + (board.columns?.length ? `<br>${jiraColumnSummary(board.columns)}` : '')
            + `<br>${points}${warnings}`);
        this._report(`Connected as ${result.account}.`);
    }

    async _pull(dryRun) {
        if (!this._ready) return;
        let pulled = false;
        await this._run(async () => {
            this._report(dryRun ? 'Dry run…' : 'Pulling…');
            const report = await BoardApi.pullJiraAsync(this._board, dryRun);
            if (!this._alive()) return;
            if (report.boardId) this._board = report.boardId;
            pulled = !dryRun;
            if (!dryRun) await this._load();
            if (!this._alive()) return;
            this._report(report.message || report.outcome);
        });
        if (pulled && this._alive()) this._onChanged?.('pull', this._board);
    }

    _set(selector, value) {
        const element = this.root.querySelector(selector);
        if (element) element.value = value;
    }

    _check(selector, checked) {
        const element = this.root.querySelector(selector);
        if (element) element.checked = !!checked;
    }

    _summary(html) {
        const element = this.root.querySelector('[data-jira-summary]');
        if (!element) return;
        element.innerHTML = html || '';
        element.hidden = !html;
    }

    _report(text) {
        const element = this.root.querySelector('[data-jira-report]');
        if (element) element.textContent = text || '';
    }
}
