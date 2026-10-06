import test from 'node:test';
import assert from 'node:assert/strict';
import { BoardApi } from '../../../VibeRails/wwwroot/js/modules/board-api.js';
import { BoardJiraPanel, JIRA_TOKEN_URL, boardJiraSection, jiraColumnSummary, jiraLaneRows } from '../../../VibeRails/wwwroot/js/modules/board-jira.js';

const ROBS_LINK = 'https://robstokes857.atlassian.net/jira/software/projects/SCRUM/boards/1?filter=&groupBy=none&atlOrigin=eyJpIjoiOWI3NmJkMjE1Yjc1NDVhZjhlNDk5NDhkMmYxZjcyNDkiLCJwIjoiaiJ9';
const SAVED_LINK = 'https://robstokes857.atlassian.net/jira/software/projects/SCRUM/boards/1';

function field(value = '') {
    return { value, checked: false, placeholder: '', textContent: '', className: '', innerHTML: '', hidden: false, addEventListener() {} };
}

const LANES = [{ id: 'col_backlog', name: 'Backlog' }, { id: 'col_progress', name: 'In Progress' }, { id: 'col_done', name: 'Done' }];
const COLUMNS = [
    { name: 'To Do', laneId: 'col_backlog', laneName: 'Backlog', automatic: true },
    { name: 'In Progress', laneId: 'col_progress', laneName: 'In Progress', automatic: true },
    { name: 'Done', laneId: 'col_done', laneName: 'Done', automatic: true }
];

function savedConnection(overrides = {}) {
    return {
        siteUrl: 'https://robstokes857.atlassian.net', email: 'rob@example.com', hasToken: true, authStatus: 'saved',
        jql: 'project = SCRUM ORDER BY Rank ASC', enabled: true, lastReport: 'ok: 1 created',
        boardLink: SAVED_LINK, jiraBoardId: '1', jiraBoardName: 'SCRUM board', narrowJql: null, skipOldDone: true,
        columns: COLUMNS, lanes: LANES, suggestedEmail: null, ...overrides
    };
}

function createHarness({ connection = savedConnection(), appSettings = {} } = {}) {
    const fields = new Map([
        ['[data-jira-fields]', field()],
        ['[data-jira-action="connect"]', field()],
        ['[data-jira-action="pull"]', field()],
        ['[data-jira-link]', field()],
        ['[data-jira-link-help]', field()],
        ['[data-jira-email]', field()],
        ['[data-jira-email-help]', field()],
        ['[data-jira-token]', field('')],
        ['[data-jira-narrow]', field()],
        ['[data-jira-points]', field()],
        ['[data-jira-enabled]', field()],
        ['[data-jira-skip-done]', field()],
        ['[data-jira-status]', field()],
        ['[data-jira-summary]', field()],
        ['[data-jira-lanes]', field()],
        ['[data-jira-report]', field()]
    ]);
    const laneSelects = [];
    const calls = [];
    const toasts = [];
    let current = connection;
    const app = {
        appSettings,
        apiCall: async (...args) => {
            calls.push(args);
            const [path, method] = args;
            if (path === '/api/v1/board/boards' && method === 'GET') {
                return { boards: [{ id: 'brd_main', position: 0 }] };
            }
            if (path.includes('/jira') && !path.includes('/jira/') && method === 'GET') return current;
            if (path.endsWith('/jira') && method === 'PUT') {
                const body = args[2];
                current = { ...current, ...body, boardLink: body.boardLink ? SAVED_LINK : current.boardLink, apiToken: undefined, hasToken: true, authStatus: 'saved' };
                return { ...current, columns: null, lanes: null };
            }
            if (path.endsWith('/jira/test')) {
                return { ok: true, account: 'Rob Stokes', board: { id: '1', name: 'SCRUM board', type: 'scrum', issueCount: 12,
                    storyPointsFieldId: 'customfield_10016', storyPointsFieldName: 'Story point estimate', columns: COLUMNS, warnings: [] } };
            }
            if (path.includes('/jira/pull')) return { outcome: 'ok', message: '0 created, 0 updated, 1 unchanged, 0 skipped.' };
            throw new Error('unexpected ' + method + ' ' + path);
        },
        showToast: (...args) => toasts.push(args)
    };
    const root = {
        querySelector: selector => fields.get(selector) || null,
        querySelectorAll: selector => (selector === '[data-jira-lane]' ? laneSelects : [])
    };
    return { panel: new BoardJiraPanel(app, root, { id: 'brd_sprint', name: 'Sprint 4' }), fields, calls, toasts, laneSelects };
}

test('the form asks for a board link and a token, links to token creation, and has no site, JQL-filter or dry-run inputs', () => {
    const html = boardJiraSection();
    assert.match(html, /data-jira-link/);
    assert.match(html, /Jira board link/);
    assert.ok(html.includes(`href="${JIRA_TOKEN_URL}"`));
    assert.match(html, /rel="noopener noreferrer"/);
    assert.match(html, /data-jira-action="connect"/);
    assert.match(html, /data-jira-action="pull"/);
    for (const removed of ['data-jira-site', 'data-jira-jql', 'Site URL', 'JQL filter', 'dry-run', 'Save connection']) {
        assert.ok(!html.includes(removed), `${removed} should be gone`);
    }
    // JQL survives only as an Advanced narrowing option.
    assert.ok(html.indexOf('data-jira-advanced') < html.indexOf('data-jira-narrow'));
});

test('opening Board Settings loads the settings view, shows the board and never displays the token', async () => {
    const { panel, fields, calls } = createHarness();
    await panel.activate();
    const reads = calls.filter(call => String(call[0]).includes('/jira') && call[1] === 'GET');
    assert.equal(reads.length, 1);
    assert.match(reads[0][0], /\/boards\/brd_sprint\/jira\?settings=true$/);
    assert.equal(fields.get('[data-jira-link]').value, SAVED_LINK);
    assert.equal(fields.get('[data-jira-email]').value, 'rob@example.com');
    assert.equal(fields.get('[data-jira-token]').value, '');
    assert.match(fields.get('[data-jira-token]').placeholder, /Token saved/);
    assert.equal(fields.get('[data-jira-status]').textContent, 'Connected');
    assert.equal(fields.get('[data-jira-skip-done]').checked, true);
    assert.match(fields.get('[data-jira-summary]').innerHTML, /SCRUM board/);
    assert.match(fields.get('[data-jira-summary]').innerHTML, /To Do → Backlog/);
    assert.match(fields.get('[data-jira-lanes]').innerHTML, /Automatic \(Backlog\)/);
    assert.equal(fields.get('[data-jira-fields]').disabled, false);
});

test('a new connection fills the email from git, then the VibeRails account, and starts with both switches on', async () => {
    const empty = { boardId: 'brd_sprint', hasToken: false, authStatus: 'none', enabled: false, columns: [], lanes: LANES };
    const fromGit = createHarness({ connection: { ...empty, suggestedEmail: 'robstokes857@gmail.com' }, appSettings: { remoteAccountEmail: 'account@example.com' } });
    await fromGit.panel.activate();
    assert.equal(fromGit.fields.get('[data-jira-email]').value, 'robstokes857@gmail.com');
    assert.match(fromGit.fields.get('[data-jira-email-help]').textContent, /git user\.email/);
    assert.equal(fromGit.fields.get('[data-jira-enabled]').checked, true);
    assert.equal(fromGit.fields.get('[data-jira-skip-done]').checked, true);
    assert.equal(fromGit.fields.get('[data-jira-status]').textContent, 'Not connected');
    assert.match(fromGit.fields.get('[data-jira-lanes]').innerHTML, /Connect to read the board's columns/);

    const fromAccount = createHarness({ connection: { ...empty, suggestedEmail: null }, appSettings: { remoteAccountEmail: 'account@example.com' } });
    await fromAccount.panel.activate();
    assert.equal(fromAccount.fields.get('[data-jira-email]').value, 'account@example.com');
    assert.match(fromAccount.fields.get('[data-jira-email-help]').textContent, /VibeRails account/);
});

test('Connect saves the pasted link and token, reads the board, and reports what it found', async () => {
    const empty = { boardId: 'brd_sprint', hasToken: false, authStatus: 'none', enabled: false, columns: [], lanes: LANES, suggestedEmail: 'rob@example.com' };
    const { panel, fields, calls } = createHarness({ connection: empty });
    const changes = [];
    panel._onChanged = action => changes.push(action);
    await panel.activate();
    fields.get('[data-jira-link]').value = `  ${ROBS_LINK}  `;
    fields.get('[data-jira-token]').value = 'typed-secret';
    await panel._connect();

    const save = calls.find(call => call[1] === 'PUT');
    assert.equal(save[2].boardLink, ROBS_LINK);
    assert.equal(save[2].apiToken, 'typed-secret');
    assert.equal(save[2].email, 'rob@example.com');
    assert.equal(save[2].enabled, true);
    assert.equal(save[2].skipOldDone, true);
    assert.equal(save[2].narrowJql, '');
    assert.equal('siteUrl' in save[2], false);
    assert.equal('jql' in save[2], false);
    assert.equal('columnMap' in save[2], false);
    const order = calls.filter(call => call[1] !== 'GET' || String(call[0]).includes('settings')).map(call => call[1] + ' ' + call[0]);
    assert.deepEqual(order.slice(1), [
        'PUT /api/v1/board/boards/brd_sprint/jira',
        'POST /api/v1/board/boards/brd_sprint/jira/test',
        'GET /api/v1/board/boards/brd_sprint/jira?settings=true'
    ]);
    assert.equal(fields.get('[data-jira-token]').value, '');
    const summary = fields.get('[data-jira-summary]').innerHTML;
    assert.match(summary, /SCRUM board/);
    assert.match(summary, /about 12 issues/);
    assert.match(summary, /In Progress → In Progress/);
    assert.match(summary, /Story point estimate/);
    assert.equal(fields.get('[data-jira-summary]').hidden, false);
    assert.equal(fields.get('[data-jira-report]').textContent, 'Connected as Rob Stokes.');
    assert.deepEqual(changes, ['test']);
});

test('a lane picked under Advanced is sent by column name, and blank means automatic', async () => {
    const { panel, calls, laneSelects } = createHarness();
    await panel.activate();
    laneSelects.push({ dataset: { jiraLane: '0' }, value: '' }, { dataset: { jiraLane: '1' }, value: 'col_backlog' }, { dataset: { jiraLane: '9' }, value: 'x' });
    await panel._connect();
    const save = calls.find(call => call[1] === 'PUT');
    assert.deepEqual(save[2].columnMap, { 'To Do': '', 'In Progress': 'col_backlog' });
    assert.equal(save[2].boardLink, SAVED_LINK);
    assert.equal(save[2].apiToken, '');
});

test('a different board link sends no lane picks, because they belong to the previous board', async () => {
    const { panel, fields, calls, laneSelects } = createHarness();
    await panel.activate();
    laneSelects.push({ dataset: { jiraLane: '0' }, value: 'col_progress' });
    fields.get('[data-jira-link]').value = 'https://robstokes857.atlassian.net/jira/software/c/projects/OPS/boards/7';
    await panel._connect();
    const save = calls.find(call => call[1] === 'PUT');
    assert.equal(save[2].boardLink, 'https://robstokes857.atlassian.net/jira/software/c/projects/OPS/boards/7');
    assert.equal('columnMap' in save[2], false);
});

test('a failed Connect shows the server reason and a refusal from Jira shows its error', async () => {
    const { panel, fields } = createHarness();
    await panel.activate();
    BoardApi.attach({ apiCall: async () => { throw new Error('Paste a board link, not just the site.'); }, showToast() {} });
    await panel._connect();
    assert.match(fields.get('[data-jira-report]').textContent, /not just the site/);

    panel._showResult({ ok: false, error: 'Jira has no board 1 that this account can see.' });
    assert.match(fields.get('[data-jira-report]').textContent, /no board 1/);
});

test('board warnings are escaped and a JQL connection without a board reports the account only', () => {
    const { panel, fields } = createHarness();
    panel._showResult({ ok: true, account: 'Rob', board: { id: '1', name: '<b>x</b>', issueCount: 1, columns: [], warnings: ['<img src=x onerror=alert(1)>'] } });
    const summary = fields.get('[data-jira-summary]').innerHTML;
    assert.ok(!summary.includes('<img'));
    assert.ok(!summary.includes('<b>x</b>'));
    assert.match(summary, /about 1 issue</);
    assert.match(summary, /Story points: off/);
    panel._showResult({ ok: true, account: 'Rob' });
    assert.equal(fields.get('[data-jira-report]').textContent, 'Connected as Rob.');
});

test('a VB-40 JQL connection says what it pulls, keeps its interval switch and offers to skip old Done issues', async () => {
    const legacy = { siteUrl: 'https://acme.atlassian.net', email: 'ada@example.com', hasToken: true, authStatus: 'saved',
        jql: 'project = PROJ', enabled: false, skipOldDone: null, boardLink: null, columns: [], lanes: LANES };
    const { panel, fields, calls } = createHarness({ connection: legacy });
    await panel.activate();
    assert.equal(fields.get('[data-jira-link]').value, '');
    assert.match(fields.get('[data-jira-link-help]').textContent, /saved JQL filter on https:\/\/acme\.atlassian\.net: project = PROJ/);
    assert.equal(fields.get('[data-jira-enabled]').checked, false);
    assert.equal(fields.get('[data-jira-skip-done]').checked, true);
    assert.equal(fields.get('[data-jira-status]').textContent, 'Token saved');
    await panel._connect();
    const save = calls.find(call => call[1] === 'PUT');
    assert.equal(save[2].boardLink, '');
});

test('pull now reports the server text and reloads the connection', async () => {
    const { panel, fields, calls } = createHarness();
    await panel.activate();
    await panel._pull(false);
    assert.ok(calls.some(call => String(call[0]).endsWith('/jira/pull')));
    assert.match(fields.get('[data-jira-report]').textContent, /unchanged/);
});

test('column names and lane names are escaped in the lane map', () => {
    const html = jiraLaneRows([{ name: '<script>', laneId: null, laneName: null, automatic: true }], [{ id: '"x', name: '<i>' }]);
    assert.ok(!html.includes('<script>'));
    assert.ok(!html.includes('<i>'));
    assert.match(html, /Automatic \(Jira lane \(unmatched\)\)/);
    assert.match(html, /value="&quot;x"/);
    assert.equal(jiraColumnSummary([{ name: 'A&B', laneName: null }]), 'A&amp;B → Jira lane (unmatched)');
});

test('closing Board Settings clears the token field', async () => {
    const { panel, fields } = createHarness();
    await panel.activate();
    fields.get('[data-jira-token]').value = 'typed-secret';
    panel.dispose();
    assert.equal(fields.get('[data-jira-token]').value, '');
});

test('the panel targets the edited board regardless of the remembered board', async () => {
    const { panel, fields, calls } = createHarness();
    const app = panel.app;
    const fallback = app.apiCall;
    app.apiCall = async (...args) => {
        const [path, method] = args;
        if (path === '/api/v1/board/boards' && method === 'GET') {
            calls.push(args);
            return { boards: [{ id: 'brd_main', name: 'Main', position: 0 }, { id: 'brd_sprint', name: 'Sprint 4', position: 1 }] };
        }
        return fallback(...args);
    };
    fields.set('[data-jira-board]', field());
    const previous = Object.getOwnPropertyDescriptor(globalThis, 'localStorage');
    Object.defineProperty(globalThis, 'localStorage', {
        configurable: true,
        value: { getItem: key => (key === 'viberails.board.selected.v1' ? 'brd_main' : null) }
    });
    try {
        await panel.activate();
        await panel._connect();
    } finally {
        if (previous) Object.defineProperty(globalThis, 'localStorage', previous);
        else delete globalThis.localStorage;
    }
    const jiraCalls = calls.filter(call => String(call[0]).includes('/jira'));
    assert.ok(jiraCalls.length >= 3);
    assert.ok(jiraCalls.every(call => String(call[0]).includes('/boards/brd_sprint/jira')));
    assert.equal(fields.get('[data-jira-board]').textContent, 'Sprint 4');
    assert.ok(calls.every(call => call[0] !== '/api/v1/board/boards'));
});

test('closing during a load ignores the late response and cannot save', async () => {
    const { panel, fields } = createHarness();
    let resolve;
    let writes = 0;
    panel.app.apiCall = async (_path, method) => {
        if (method !== 'GET') writes++;
        return new Promise(done => { resolve = done; });
    };
    fields.get('[data-jira-link]').value = 'https://acme.atlassian.net/jira/software/projects/A/boards/2';
    const loading = panel.activate();
    await panel._connect();
    assert.equal(writes, 0);
    assert.equal(fields.get('[data-jira-fields]').disabled, true);
    panel.dispose();
    resolve(savedConnection({ boardLink: 'https://late.atlassian.net/jira/software/projects/L/boards/9' }));
    await loading;
    await panel._connect();
    assert.equal(writes, 0);
    assert.equal(fields.get('[data-jira-link]').value, 'https://acme.atlassian.net/jira/software/projects/A/boards/2');
    assert.equal(fields.get('[data-jira-token]').value, '');
});

test('a failed initial load is reported and leaves writes disabled', async () => {
    const { panel, fields } = createHarness();
    let calls = 0;
    panel.app.apiCall = async () => { calls++; throw new Error('Connection unavailable'); };
    await panel.activate();
    await panel._connect();
    await panel._pull(false);
    assert.equal(calls, 1);
    assert.match(fields.get('[data-jira-report]').textContent, /Connection unavailable/);
    assert.equal(fields.get('[data-jira-fields]').disabled, true);
});

test('connect prevents overlapping actions and disposal suppresses late completion', async () => {
    const { panel, fields, toasts } = createHarness();
    await panel.activate();
    let resolve;
    let calls = 0;
    let changed = 0;
    panel._onChanged = () => changed++;
    panel.app.apiCall = async () => { calls++; return new Promise(done => { resolve = done; }); };
    fields.get('[data-jira-token]').value = 'typed-secret';
    const connecting = panel._connect();
    await panel._connect();
    await panel._pull(false);
    assert.equal(calls, 1);
    panel.dispose();
    resolve(savedConnection({ boardLink: 'https://late.atlassian.net/jira/software/projects/L/boards/9' }));
    await connecting;
    assert.equal(changed, 0);
    assert.equal(toasts.length, 0);
    assert.equal(fields.get('[data-jira-token]').value, '');
});


test('Connect follows the returned dedicated board for provider checks and future pulls', async () => {
    const { panel, calls } = createHarness({ connection: savedConnection({ boardId: 'brd_jira' }) });
    const changed = [];
    panel._onChanged = (action, id) => changed.push([action, id]);
    await panel.activate();
    await panel._connect();
    assert.ok(calls.some(([url, method]) => method === 'POST' && url === '/api/v1/board/boards/brd_jira/jira/test'));
    await panel._pull(false);
    assert.ok(calls.some(([url, method]) => method === 'POST' && url.startsWith('/api/v1/board/boards/brd_jira/jira/pull')));
    assert.deepEqual(changed, [['test', 'brd_jira'], ['pull', 'brd_jira']]);
});

for (const failure of ['test', 'settings-before-test', 'settings-after-test']) {
    test(`separation followed by a failed ${failure} never retries with source lane IDs`, async () => {
        const source = savedConnection({ boardId: 'brd_sprint',
            columns: [{ name: 'QA', laneId: 'source_done', laneName: 'Done', automatic: false }],
            lanes: [{ id: 'source_done', name: 'Done' }] });
        const destination = savedConnection({ boardId: 'brd_jira',
            columns: [{ name: 'QA', laneId: 'destination_done', laneName: 'Done', automatic: false }],
            lanes: [{ id: 'destination_done', name: 'Done' }] });
        const { panel, fields, laneSelects } = createHarness({ connection: source });
        // Reflect the selected option when the production panel replaces the lane-picker DOM.
        Object.defineProperty(fields.get('[data-jira-lanes]'), 'innerHTML', { set(html) {
            laneSelects.length = 0;
            const selected = html.match(/<option value="([^"]+)" selected>/);
            if (selected) laneSelects.push({ dataset: { jiraLane: '0' }, value: selected[1] });
        } });
        let moved = false;
        let failOnce = true;
        let destinationReads = 0;
        const saves = [];
        panel.app.apiCall = async (path, method, body) => {
            if (method === 'PUT') {
                saves.push(body);
                moved = true;
                return { ...destination, columns: null, lanes: null };
            }
            if (path.endsWith('/jira/test')) {
                if (failure === 'test' && failOnce) { failOnce = false; throw new Error('Test unavailable'); }
                return { ok: true, account: 'Ada' };
            }
            if (!moved) return source;
            destinationReads++;
            if (failOnce && ((failure === 'settings-before-test' && destinationReads === 1)
                || (failure === 'settings-after-test' && destinationReads === 2))) {
                failOnce = false;
                throw new Error('Settings unavailable');
            }
            return destination;
        };
        await panel.activate();
        await panel._connect();
        assert.equal(panel._board, 'brd_jira');
        await panel._connect();
        assert.deepEqual(saves[0].columnMap, { QA: 'source_done' });
        if (failure === 'settings-before-test') assert.equal('columnMap' in saves[1], false);
        else assert.deepEqual(saves[1].columnMap, { QA: 'destination_done' });
    });
}
