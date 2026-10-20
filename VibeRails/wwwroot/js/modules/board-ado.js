// Azure DevOps Services settings belong to the board being edited (VIBE-165), mirroring Jira
// (board-jira.js). One dev.azure.com team board, backlog or saved query link plus a personal access
// token connects it: the board supplies its work items, columns and points field, so there is no
// organization, WIQL or field to type. Tokens are write-only and saved separately.
import { BoardApi } from './board-api.js';
import { escapeHtml, confirmDialog } from './utils.js';

// Organization names as the server accepts them (AdoBoardLink.IsOrganization).
const ORGANIZATION = /^[A-Za-z0-9](?:[A-Za-z0-9-]{0,48}[A-Za-z0-9])?$/;

/** The organization in a dev.azure.com link, or '' when the link is anything else. */
export function adoOrganization(link) {
    try {
        const url = new URL(String(link || '').trim());
        if (url.protocol !== 'https:' || url.hostname.toLowerCase() !== 'dev.azure.com' || url.username || url.password) return '';
        const organization = url.pathname.split('/')[1] || '';
        return ORGANIZATION.test(organization) ? organization : '';
    } catch {
        return '';
    }
}

/** Personal access tokens are created per organization. */
export const adoTokenUrl = organization => `https://dev.azure.com/${encodeURIComponent(organization)}/_usersSettings/tokens`;

const QUERY_ID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const LEGACY_HOST = '.visualstudio.com';

/**
 * What a typed link points at, close enough to AdoBoardLink.Parse to say so while typing; Connect
 * still decides. kind is 'empty', 'board' (a team board or backlog), 'query', 'organization' (a
 * dev.azure.com page that is neither), 'legacy' ({org}.visualstudio.com) or 'other'. The
 * organization is filled in whenever it is known, because it is all the token page needs.
 */
export function readAdoLink(link) {
    const text = String(link || '').trim();
    if (!text) return { kind: 'empty', organization: '' };
    let url;
    try {
        url = new URL(text);
    } catch {
        return { kind: 'other', organization: '' };
    }
    const host = url.hostname.toLowerCase();
    if (url.protocol === 'https:' && host.endsWith(LEGACY_HOST) && !url.username && !url.password) {
        const organization = host.slice(0, -LEGACY_HOST.length);
        return { kind: 'legacy', organization: ORGANIZATION.test(organization) ? organization : '' };
    }
    const organization = adoOrganization(text);
    if (!organization) return { kind: 'other', organization: '' };
    let segments;
    try {
        segments = url.pathname.split('/').filter(Boolean).map(decodeURIComponent);
    } catch {
        return { kind: 'organization', organization };
    }
    const [, project = '', area, page, ...rest] = segments;
    const is = (segment, expected) => String(segment || '').toLowerCase() === expected;
    if (segments.length === 7 && is(area, '_boards') && is(page, 'board') && is(rest[0], 't'))
        return { kind: 'board', organization, project, team: rest[1], level: rest[2] };
    if (segments.length === 6 && is(area, '_backlogs') && is(page, 'backlog'))
        return { kind: 'board', organization, project, team: rest[0], level: rest[1] };
    if (segments.length === 5 && is(area, '_queries') && (is(page, 'query') || is(page, 'query-edit')) && QUERY_ID.test(rest[0]))
        return { kind: 'query', organization, project };
    return { kind: 'organization', organization };
}

/** The line under the link field for readAdoLink's result. Escaped. */
export function adoLinkHelp(read) {
    const organization = escapeHtml(read?.organization || '');
    switch (read?.kind) {
        case 'board':
            return `<i class="fa-solid fa-circle-check text-success" aria-hidden="true"></i> Team <strong>${escapeHtml(read.team)}</strong>, `
                + `<strong>${escapeHtml(read.level)}</strong> in ${escapeHtml(read.project)} (${organization}).`;
        case 'query':
            return `<i class="fa-solid fa-circle-check text-success" aria-hidden="true"></i> Saved query in ${escapeHtml(read.project)} (${organization}).`;
        case 'organization':
            return `Organization <strong>${organization}</strong>. To connect, open its team board, backlog or saved query and paste that address instead.`;
        case 'legacy':
            return 'This is an older visualstudio.com address. Open the same board on dev.azure.com and paste that address instead'
                + (organization ? `: it starts with https://dev.azure.com/${organization}/.` : '.');
        case 'other':
            return 'Only https://dev.azure.com links work. Open the board in Azure DevOps and copy the address bar.';
        default:
            return 'Open the team board, backlog or saved query in Azure DevOps and copy the address bar. Only dev.azure.com links work.';
    }
}

/**
 * The connection form. lanesFrom names the local board that the New board dialog connects: Connect
 * copies its lanes into the new board, so the form says that instead of naming the board it edits.
 */
export const boardAdoSection = ({ lanesFrom = null } = {}) => `
    <section data-ado-panel>
        <h6 class="mb-0" data-ado-heading>Azure DevOps <span class="badge text-bg-secondary ms-2" data-ado-status>Not connected</span></h6>
        <fieldset class="mt-3" data-ado-fields disabled>
            ${lanesFrom === null ? '<p class="small mb-2">Board: <strong data-ado-board></strong></p>'
                : `<p class="small mb-2">Lanes copied from <strong>${escapeHtml(lanesFrom)}</strong></p>`}
            <p class="text-muted small">${lanesFrom === null
                ? 'An empty board becomes the Azure DevOps board itself. Your only local board, or one with cards, lane Automations or viberails.ai sync, stays as it is: Connect creates a separate Azure DevOps board and moves existing Azure DevOps cards there with their comments, files and sessions.'
                : 'Connect creates the board, named after the Azure DevOps team or query, and pulls its work items.'} Use the board picker to switch between Azure DevOps and your local work. Azure DevOps wins on title, description, type, priority, tags and points. A local edit to those fields is replaced on the next pull. A new card is placed by its board column; after that its lane and board are yours, and a state change is only noted in a comment. Assignee, flagged, blocked, comments, sessions and commits stay here.</p>
            <div class="mb-3">
                <label class="form-label" for="ado-link">Azure DevOps board or query link</label>
                <input class="form-control" id="ado-link" data-ado-link type="url" autocomplete="off" spellcheck="false"
                    placeholder="https://dev.azure.com/your-org/Project/_boards/board/t/Team/Stories">
                <small class="form-text text-muted d-block" data-ado-link-help role="status">${adoLinkHelp({ kind: 'empty' })}</small>
            </div>
            <div class="mb-3">
                <label class="form-label" for="ado-token">Personal access token</label>
                <p class="small mb-2">
                    <a class="btn btn-sm btn-outline-primary me-1" target="_blank" rel="noopener noreferrer" data-ado-token-link hidden>Create a token in <span data-ado-token-org></span> <i class="fa-solid fa-arrow-up-right-from-square" aria-hidden="true"></i></a>
                    <span class="text-muted" data-ado-token-hint>Paste the board link above to get a link to your organization's token page.</span>
                    <span class="text-muted">Give the token both scopes: <strong>Work Items: Read &amp; write</strong> and <strong>Project and Team: Read</strong>.</span>
                </p>
                <input class="form-control" id="ado-token" data-ado-token type="text" autocomplete="off"
                    spellcheck="false" autocapitalize="none" autocorrect="off" placeholder="Paste a personal access token">
                <small class="form-text text-muted">Connect reads the project to check access. Saved on this machine with your other API keys. It is never written to the board and never shown again.</small>
            </div>
            <div class="form-check form-switch mb-2">
                <input class="form-check-input" type="checkbox" id="ado-enabled" data-ado-enabled>
                <label class="form-check-label" for="ado-enabled">Pull about every 15 minutes while VibeRails is open</label>
            </div>
            <div class="form-check mb-3">
                <input class="form-check-input" type="checkbox" id="ado-skip-done" data-ado-skip-done>
                <label class="form-check-label" for="ado-skip-done">Skip Done work items not changed in the last 30 days</label>
                <small class="form-text text-muted d-block">Board links only. A saved query is pulled as written.</small>
            </div>
            <div class="d-flex flex-wrap gap-2">
                <button type="button" class="btn btn-outline-primary btn-sm" data-ado-action="connect">Connect</button>
                <button type="button" class="btn btn-outline-secondary btn-sm" data-ado-action="pull">Pull now</button>
                <button type="button" class="btn btn-outline-danger btn-sm ms-auto" data-ado-action="unlink" hidden>Unlink Azure DevOps</button>
            </div>
            <div class="small mt-3" data-ado-summary hidden></div>
            <details class="mt-3" data-ado-advanced>
                <summary class="small">Advanced: narrow with WIQL, points, lanes</summary>
                <p class="text-muted small mt-2 mb-2">Saved when you click Connect.</p>
                <div class="mb-3">
                    <label class="form-label" for="ado-narrow">Narrow with WIQL</label>
                    <textarea class="form-control" id="ado-narrow" data-ado-narrow rows="2" spellcheck="false"
                        placeholder="[System.Tags] CONTAINS 'frontend'"></textarea>
                    <small class="form-text text-muted">Optional, board links only. Added to the board's own query with AND. Leave out ORDER BY.</small>
                </div>
                <div class="mb-3">
                    <label class="form-label" for="ado-points-field">Points field</label>
                    <input class="form-control" id="ado-points-field" data-ado-points type="text" autocomplete="off" spellcheck="false"
                        placeholder="Detected from the process">
                    <small class="form-text text-muted">Leave blank to use Story Points, Effort or Size. A field reference name looks like Microsoft.VSTS.Scheduling.StoryPoints.</small>
                </div>
                <p class="form-label mb-1">Lane for new cards from each Azure DevOps column</p>
                <div data-ado-lanes></div>
            </details>
        </fieldset>
        <p class="text-muted small mt-3 mb-0" data-ado-report role="status"></p>
    </section>`;

const OVERFLOW_LABEL = 'Azure DevOps lane (unmatched)';

/** "New → Backlog · Active → Azure DevOps lane (unmatched)". Escaped. */
export function adoColumnSummary(columns) {
    return (columns || [])
        .map(column => `${escapeHtml(column.name)} → ${escapeHtml(column.laneName || OVERFLOW_LABEL)}`)
        .join(' · ');
}

/** The lane picker rows. A blank value is automatic, labelled with where automatic puts the column. */
export function adoLaneRows(columns, lanes, { query = false } = {}) {
    if (!columns?.length) {
        return query
            ? '<p class="text-muted small mb-0" data-ado-lanes-empty>A saved query has no board columns, so lanes match state names.</p>'
            : '<p class="text-muted small mb-0" data-ado-lanes-empty>Connect to read the board\'s columns. Until then, lanes match state names.</p>';
    }
    return columns.map((column, index) => {
        const automatic = column.automatic ? `Automatic (${escapeHtml(column.laneName || OVERFLOW_LABEL)})` : 'Automatic';
        const options = (lanes || []).map(lane =>
            `<option value="${escapeHtml(lane.id)}"${!column.automatic && lane.id === column.laneId ? ' selected' : ''}>${escapeHtml(lane.name)}</option>`).join('');
        return `
            <div class="d-flex flex-wrap align-items-center gap-2 mb-2">
                <label class="small text-break flex-grow-1" for="ado-lane-${index}">${escapeHtml(column.name)}</label>
                <select class="form-select form-select-sm w-auto mw-100" id="ado-lane-${index}" data-ado-lane="${index}">
                    <option value="">${automatic}</option>${options}
                </select>
            </div>`;
    }).join('');
}

export class BoardAdoPanel {
    /**
     * newBoard: the New board dialog. Connect asks for a new board made from this board's lanes,
     * even when this board could become the Azure DevOps board, and pulls once it succeeds rather
     * than leave the new board empty until the interval.
     */
    constructor(app, root, board, onChanged, { newBoard = false } = {}) {
        this.app = app;
        this.root = root;
        // The settings dialog moves the heading into its header after mounting the panel.
        this._status = root.querySelector('[data-ado-status]');
        this._board = board.id;
        this._source = board.id;
        this._boardName = board.name;
        this._onChanged = onChanged;
        this._newBoard = newBoard;
        this._events = new AbortController();
        this._loaded = false;
        this._ready = false;
        this._busy = false;
        this._disposed = false;
        this._columns = [];
        this._lanes = [];
        this._savedLink = '';
        this._savedOrganization = '';
        this._hasConnection = false;
    }

    _alive() {
        return !this._disposed && this.root.isConnected !== false;
    }

    async activate() {
        if (this._loaded || !this._alive()) return;
        this._loaded = true;
        BoardApi.attach(this.app);
        const label = this.root.querySelector('[data-ado-board]');
        if (label) label.textContent = this._boardName;
        const actions = { connect: () => this._connect(), pull: () => this._pull(false), unlink: () => this._unlink() };
        for (const [action, handler] of Object.entries(actions)) {
            this.root.querySelector(`[data-ado-action="${action}"]`)?.addEventListener('click', handler,
                { signal: this._events.signal });
        }
        // The token page is per organization, so a typed link points the token link at its organization.
        this.root.querySelector('[data-ado-link]')?.addEventListener('input', () => this._tokenLink(),
            { signal: this._events.signal });
        await this._run(async () => {
            await this._load();
            this._ready = true;
        });
    }

    clearSecrets() {
        const token = this.root.querySelector('[data-ado-token]');
        if (token) token.value = '';
    }

    dispose() {
        this._disposed = true;
        this._events.abort();
        this.clearSecrets();
    }

    async _load() {
        const connection = await BoardApi.getAdoConnectionAsync(this._board, { settings: true });
        if (this._alive()) this._fill(connection);
    }

    async _run(action) {
        if (this._busy || !this._alive()) return;
        this._busy = true;
        const fields = this.root.querySelector('[data-ado-fields]');
        if (fields) fields.disabled = true;
        try {
            await action();
        } catch (error) {
            if (this._alive()) this._report(error?.message || 'Could not load the Azure DevOps connection. Reopen Board Settings to retry.');
        } finally {
            this._busy = false;
            if (this._alive() && fields) fields.disabled = !this._ready;
        }
    }

    _fill(connection) {
        const saved = !!(connection.boardLink || connection.organization);
        this._hasConnection = saved;
        const unlink = this.root.querySelector('[data-ado-action="unlink"]');
        if (unlink) unlink.hidden = !saved;
        const pull = this.root.querySelector('[data-ado-action="pull"]');
        if (pull) pull.disabled = !saved;
        this._savedLink = connection.boardLink || '';
        this._savedOrganization = connection.organization || '';
        this._set('[data-ado-link]', this._savedLink);
        this._set('[data-ado-narrow]', connection.narrowWiql || '');
        this._set('[data-ado-points]', connection.pointsField || '');
        // A new connection pulls on the interval, with old done work items left out.
        this._check('[data-ado-enabled]', saved ? !!connection.enabled : true);
        this._check('[data-ado-skip-done]', connection.skipOldDone ?? true);
        const token = this.root.querySelector('[data-ado-token]');
        if (token) {
            // The dots only indicate a saved token; never submit a masking value as a replacement.
            token.value = '';
            token.placeholder = connection.hasToken ? '•'.repeat(32) : 'Paste a personal access token';
        }
        this._tokenLink();
        const badge = this._status;
        if (badge) {
            const status = connection.authStatus || 'none';
            badge.textContent = status === 'saved' ? (connection.adoBoardName ? 'Connected' : 'Token saved')
                : status === 'expired' ? 'Expired' : 'Not connected';
            badge.className = 'badge ' + (status === 'saved' ? 'text-bg-success' : status === 'expired' ? 'text-bg-danger' : 'text-bg-secondary');
        }
        if (Array.isArray(connection.columns)) {
            this._columns = connection.columns;
            this._lanes = connection.lanes || [];
            const lanes = this.root.querySelector('[data-ado-lanes]');
            if (lanes) lanes.innerHTML = adoLaneRows(this._columns, this._lanes, { query: !!connection.queryId });
        }
        if (connection.adoBoardName) {
            this._summary(`${connection.queryId ? 'Saved query' : 'Azure DevOps board'} <strong>${escapeHtml(connection.adoBoardName)}</strong>`
                + (this._columns.length ? `<br>${adoColumnSummary(this._columns)}` : ''));
        } else this._summary('');
        this._report(connection.disabledReason || connection.lastReport || '');
    }

    // Says what the typed link points at, and points the token link at the typed link's
    // organization first, then the saved one; no token link until either is known.
    _tokenLink() {
        const read = readAdoLink(this.root.querySelector('[data-ado-link]')?.value);
        const help = this.root.querySelector('[data-ado-link-help]');
        if (help) {
            const html = adoLinkHelp(read);
            // Rewriting identical text would make a screen reader repeat it on every keystroke.
            if (help.innerHTML !== html) help.innerHTML = html;
            const warn = read.kind === 'organization' || read.kind === 'legacy' || read.kind === 'other';
            help.className = `form-text d-block ${warn ? 'text-warning-emphasis' : 'text-muted'}`;
        }
        const link = this.root.querySelector('[data-ado-token-link]');
        if (!link) return;
        const organization = read.organization || this._savedOrganization;
        const known = ORGANIZATION.test(organization);
        if (known) link.href = adoTokenUrl(organization);
        link.hidden = !known;
        const name = this.root.querySelector('[data-ado-token-org]');
        if (name) name.textContent = known ? organization : '';
        const hint = this.root.querySelector('[data-ado-token-hint]');
        if (hint) hint.hidden = known;
    }

    _body() {
        const body = {
            boardLink: (this.root.querySelector('[data-ado-link]')?.value ?? '').trim(),
            personalAccessToken: this.root.querySelector('[data-ado-token]')?.value ?? '',
            narrowWiql: this.root.querySelector('[data-ado-narrow]')?.value ?? '',
            pointsField: this.root.querySelector('[data-ado-points]')?.value ?? '',
            enabled: !!this.root.querySelector('[data-ado-enabled]')?.checked,
            skipOldDone: !!this.root.querySelector('[data-ado-skip-done]')?.checked
        };
        // Until the connection has its board; after that the save edits it.
        if (this._newBoard && this._board === this._source) body.newBoard = true;
        // Lane picks belong to the board they were read from; a different link starts automatic.
        if (this._columns.length && body.boardLink === (this._savedLink || '')) {
            const columnMap = {};
            for (const select of this.root.querySelectorAll('[data-ado-lane]')) {
                const column = this._columns[Number(select.dataset?.adoLane)];
                if (column) columnMap[column.name] = select.value || '';
            }
            body.columnMap = columnMap;
        }
        return body;
    }

    // A connection that moved boards keeps none of the source board's lane picks: the store
    // remapped them while cloning the lanes, and source IDs must never be sent on a retry.
    _follow(boardId) {
        if (!boardId || boardId === this._board) return false;
        this._board = boardId;
        this._columns = [];
        this._lanes = [];
        const lanes = this.root.querySelector('[data-ado-lanes]');
        if (lanes) lanes.innerHTML = adoLaneRows([], []);
        return true;
    }

    // Connect = save (which gives the connection its dedicated board), then check the token and
    // read the board or saved query (its columns and a work item count).
    async _connect() {
        if (!this._ready) return;
        let savedBoard = false;
        let connected = false;
        await this._run(async () => {
            this._report('Connecting…');
            const saved = await BoardApi.saveAdoConnectionAsync(this._board, this._body());
            if (!this._alive()) return;
            const moved = this._follow(saved.boardId);
            savedBoard = true;
            this._fill(saved);
            if (moved) {
                await this._load();
                if (!this._alive()) return;
            }
            this._report('Connecting…');
            const result = await BoardApi.testAdoConnectionAsync(this._board);
            if (!this._alive()) return;
            this._follow(result?.boardId);
            await this._load();
            if (!this._alive()) return;
            this._showResult(result);
            connected = !!result?.ok;
        });
        // Saving may have moved the connection even if the provider test failed afterward.
        if (savedBoard && this._alive()) this._onChanged?.('test', this._board);
        if (connected && this._newBoard && this._alive()) await this._pull(false);
    }

    async _unlink() {
        if (!this._ready || !this._hasConnection) return;
        let unlinked = false;
        await this._run(async () => {
            const confirmed = await confirmDialog({
                title: 'Unlink Azure DevOps board',
                message: 'Stop syncing this board with Azure DevOps and forget its saved personal access token? The board, imported cards, comments, files and sessions stay in VibeRails. Nothing is changed in Azure DevOps.',
                confirmLabel: 'Unlink Azure DevOps',
                danger: true
            });
            if (!confirmed || !this._alive()) return;
            this._report('Unlinking Azure DevOps…');
            await BoardApi.unlinkAdoConnectionAsync(this._board);
            if (!this._alive()) return;
            unlinked = true;
            this._fill({ columns: [], lanes: this._lanes });
            this._report('Azure DevOps unlinked. The board and its cards have been kept.');
        });
        if (unlinked && this._alive()) this._onChanged?.('unlink', this._board);
    }

    _showResult(result) {
        if (!result?.ok) {
            this._report(result?.error || 'Azure DevOps refused the connection.');
            return;
        }
        const connected = `Connected to ${result.project || 'Azure DevOps'}.`;
        const board = result.board;
        if (!board) {
            this._report(connected);
            return;
        }
        const plural = count => `${count} work item${count === 1 ? '' : 's'}`;
        const count = !Number.isInteger(board.workItemCount) ? ''
            : board.countCapped ? ` · ${board.workItemCount}+ work items; a pull reads the ${board.workItemCount} most recently changed`
                : ` · about ${plural(board.workItemCount)}`;
        const scope = board.queryId ? ` (${board.queryType ? `${escapeHtml(board.queryType)} query` : 'saved query'})`
            : board.team ? ` (${escapeHtml(board.team)})` : '';
        const points = board.pointsField ? `Points: ${escapeHtml(board.pointsField)}` : 'Points: off';
        const warnings = (board.warnings || []).map(warning =>
            `<div class="text-warning-emphasis mt-1">${escapeHtml(warning)}</div>`).join('');
        this._summary(`<i class="fa-solid fa-circle-check text-success" aria-hidden="true"></i> `
            + `<strong>${escapeHtml(board.name || board.backlogLevel || 'Azure DevOps board')}</strong>${scope}${count}`
            + (board.columns?.length ? `<br>${adoColumnSummary(board.columns)}` : '')
            + `<br>${points}${warnings}`);
        this._report(connected);
    }

    async _pull(dryRun) {
        if (!this._ready) return;
        let pulled = false;
        await this._run(async () => {
            this._report(dryRun ? 'Dry run…' : 'Pulling…');
            const report = await BoardApi.pullAdoAsync(this._board, dryRun);
            if (!this._alive()) return;
            this._follow(report.boardId);
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
        const element = this.root.querySelector('[data-ado-summary]');
        if (!element) return;
        element.innerHTML = html || '';
        element.hidden = !html;
    }

    _report(text) {
        const element = this.root.querySelector('[data-ado-report]');
        if (element) element.textContent = text || '';
    }
}
