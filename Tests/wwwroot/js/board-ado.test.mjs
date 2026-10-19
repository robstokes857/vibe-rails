import test from 'node:test';
import assert from 'node:assert/strict';
import { BoardApi } from '../../../VibeRails/wwwroot/js/modules/board-api.js';
import { BoardAdoPanel, adoColumnSummary, adoLaneRows, adoOrganization, adoTokenUrl, boardAdoSection } from '../../../VibeRails/wwwroot/js/modules/board-ado.js';

const BOARD_LINK = 'https://dev.azure.com/contoso/Fabrikam%20Web/_boards/board/t/Fabrikam%20Team/Stories';
const QUERY_LINK = 'https://dev.azure.com/contoso/Fabrikam%20Web/_queries/query/8a8c8212-15ca-41ed-97aa-1d6fbfbcd581/';

function field(value = '') {
    return { value, checked: false, placeholder: '', textContent: '', className: '', innerHTML: '', hidden: false, href: '', addEventListener() {} };
}

const LANES = [{ id: 'col_backlog', name: 'Backlog' }, { id: 'col_progress', name: 'In Progress' }, { id: 'col_done', name: 'Done' }];
const COLUMNS = [
    { name: 'New', laneId: 'col_backlog', laneName: 'Backlog', automatic: true },
    { name: 'Active', laneId: 'col_progress', laneName: 'In Progress', automatic: true },
    { name: 'Closed', laneId: 'col_done', laneName: 'Done', automatic: true }
];

function savedConnection(overrides = {}) {
    return {
        boardId: 'brd_sprint', organization: 'contoso', project: 'Fabrikam Web', team: 'Fabrikam Team', backlogLevel: 'Stories',
        queryId: null, boardLink: BOARD_LINK, adoBoardName: 'Stories', hasToken: true, authStatus: 'saved', pointsField: null,
        narrowWiql: null, skipOldDone: true, enabled: true, disabledReason: null, dedicatedBoard: true, lastReport: 'ok: 1 created',
        lastWriteWins: true, columns: COLUMNS, lanes: LANES, ...overrides
    };
}

const TEST_RESULT = {
    ok: true, project: 'Fabrikam Web', error: null, boardId: 'brd_sprint',
    board: { name: 'Stories', team: 'Fabrikam Team', backlogLevel: 'Stories', queryId: null, queryType: null, workItemCount: 12,
        countCapped: false, pointsField: 'Microsoft.VSTS.Scheduling.StoryPoints', columns: COLUMNS, warnings: [] }
};

function createHarness({ connection = savedConnection() } = {}) {
    const fields = new Map([
        '[data-ado-fields]', '[data-ado-action="connect"]', '[data-ado-action="pull"]', '[data-ado-action="unlink"]',
        '[data-ado-link]', '[data-ado-token]', '[data-ado-token-link]', '[data-ado-token-hint]', '[data-ado-narrow]',
        '[data-ado-points]', '[data-ado-enabled]', '[data-ado-skip-done]', '[data-ado-status]', '[data-ado-summary]',
        '[data-ado-lanes]', '[data-ado-report]', '[data-ado-board]'
    ].map(selector => [selector, field()]));
    const laneSelects = [];
    const calls = [];
    let current = connection;
    const app = {
        apiCall: async (...args) => {
            calls.push(args);
            const [path, method] = args;
            if (path.includes('/ado') && !path.includes('/ado/') && method === 'GET') return current;
            if (path.endsWith('/ado') && method === 'PUT') {
                const body = args[2];
                current = { ...current, ...body, boardLink: body.boardLink || current.boardLink, organization: 'contoso',
                    personalAccessToken: undefined, hasToken: true, authStatus: 'saved' };
                return { ...current, columns: null, lanes: null };
            }
            if (path.endsWith('/ado') && method === 'DELETE') return { message: 'Azure DevOps unlinked. Board and cards kept.' };
            if (path.endsWith('/ado/test')) return { ...TEST_RESULT, boardId: current.boardId || 'brd_sprint' };
            if (path.includes('/ado/pull')) return { outcome: 'ok', message: '0 created, 0 updated, 1 unchanged, 0 skipped.' };
            throw new Error('unexpected ' + method + ' ' + path);
        },
        showToast() {}
    };
    const root = {
        querySelector: selector => fields.get(selector) || null,
        querySelectorAll: selector => (selector === '[data-ado-lane]' ? laneSelects : [])
    };
    return { panel: new BoardAdoPanel(app, root, { id: 'brd_sprint', name: 'Sprint 4' }), fields, calls, laneSelects };
}

// confirmDialog renders into document.body; this stands in for it and answers when told to.
function fakeConfirm(t) {
    const saved = { document: globalThis.document, window: globalThis.window };
    const dialogs = [];
    const element = () => {
        const node = { children: [], listeners: {}, textContent: '', className: '',
            setAttribute() {}, append(...items) { node.children.push(...items); }, remove() {}, focus() {},
            addEventListener(type, listener) { node.listeners[type] = listener; } };
        return node;
    };
    const walk = node => [node, ...node.children.flatMap(walk)];
    globalThis.window = { addEventListener() {}, removeEventListener() {} };
    globalThis.document = {
        activeElement: null, createElement: element,
        body: { appendChild(overlay) {
            const nodes = walk(overlay);
            const button = label => nodes.find(node => node.textContent === label);
            dialogs.push({ text: nodes.map(node => node.textContent).join('\n'),
                answer: confirmed => (confirmed ? button(dialogs.confirmLabel) : button('Cancel')).listeners.click() });
            if (dialogs.autoAnswer !== undefined) dialogs.at(-1).answer(dialogs.autoAnswer);
        } }
    };
    dialogs.confirmLabel = 'Unlink Azure DevOps';
    t.after(() => {
        if (saved.document) globalThis.document = saved.document; else delete globalThis.document;
        if (saved.window) globalThis.window = saved.window; else delete globalThis.window;
    });
    return dialogs;
}

test('the form asks for a dev.azure.com link and a token, with the scope note and no email field', () => {
    const html = boardAdoSection();
    assert.match(html, /Azure DevOps board or query link/);
    assert.match(html, /data-ado-link type="url"/);
    assert.match(html, /Personal access token/);
    assert.match(html, /Work Items: Read &amp; write/);
    assert.match(html, /data-ado-token-link hidden/);
    assert.match(html, /rel="noopener noreferrer"/);
    assert.match(html, /data-ado-report role="status"/);
    for (const action of ['connect', 'pull', 'unlink']) assert.match(html, new RegExp(`data-ado-action="${action}"`));
    for (const absent of ['email', 'Email', 'data-ado-org', 'dry-run', 'window.confirm']) assert.ok(!html.includes(absent), `${absent} should not appear`);
    // WIQL, points and lanes are Advanced options.
    const advanced = html.indexOf('data-ado-advanced');
    for (const option of ['data-ado-narrow', 'data-ado-points', 'data-ado-lanes']) assert.ok(advanced < html.indexOf(option), option);
});

test('the organization comes only from an https dev.azure.com link and builds the token page', () => {
    assert.equal(adoOrganization(BOARD_LINK), 'contoso');
    assert.equal(adoOrganization(`  ${QUERY_LINK}  `), 'contoso');
    for (const link of ['', 'not a url', 'http://dev.azure.com/contoso/P', 'https://contoso.visualstudio.com/P',
        'https://user:pat@dev.azure.com/contoso/P', 'https://dev.azure.com.evil.example/contoso', 'https://dev.azure.com/',
        'https://dev.azure.com/-bad/P', 'https://dev.azure.com/con%2Fsoso/P', 'javascript:alert(1)']) {
        assert.equal(adoOrganization(link), '', link);
    }
    assert.equal(adoTokenUrl('contoso'), 'https://dev.azure.com/contoso/_usersSettings/tokens');
});

test('opening Board Settings loads the settings view, shows the board and never displays the token', async () => {
    const { panel, fields, calls } = createHarness();
    await panel.activate();
    const reads = calls.filter(call => String(call[0]).includes('/ado') && call[1] === 'GET');
    assert.equal(reads.length, 1);
    assert.match(reads[0][0], /\/boards\/brd_sprint\/ado\?settings=true$/);
    assert.equal(fields.get('[data-ado-board]').textContent, 'Sprint 4');
    assert.equal(fields.get('[data-ado-link]').value, BOARD_LINK);
    assert.equal(fields.get('[data-ado-token]').value, '');
    assert.match(fields.get('[data-ado-token]').placeholder, /^•{32}$/);
    assert.equal(panel._body().personalAccessToken, '');
    assert.equal(fields.get('[data-ado-status]').textContent, 'Connected');
    assert.equal(fields.get('[data-ado-status]').className, 'badge text-bg-success');
    assert.equal(fields.get('[data-ado-token-link]').hidden, false);
    assert.equal(fields.get('[data-ado-token-link]').href, 'https://dev.azure.com/contoso/_usersSettings/tokens');
    assert.equal(fields.get('[data-ado-token-hint]').hidden, true);
    assert.equal(fields.get('[data-ado-skip-done]').checked, true);
    assert.equal(fields.get('[data-ado-action="unlink"]').hidden, false);
    assert.match(fields.get('[data-ado-summary]').innerHTML, /Azure DevOps board <strong>Stories<\/strong>/);
    assert.match(fields.get('[data-ado-summary]').innerHTML, /New → Backlog/);
    assert.match(fields.get('[data-ado-lanes]').innerHTML, /Automatic \(In Progress\)/);
    assert.equal(fields.get('[data-ado-report]').textContent, 'ok: 1 created');
    assert.equal(fields.get('[data-ado-fields]').disabled, false);
});

test('each auth status has its badge', async () => {
    for (const [overrides, text, tone] of [
        [{}, 'Connected', 'success'],
        [{ adoBoardName: null }, 'Token saved', 'success'],
        [{ authStatus: 'expired' }, 'Expired', 'danger'],
        [{ authStatus: 'none', hasToken: false }, 'Not connected', 'secondary']
    ]) {
        const { panel, fields } = createHarness({ connection: savedConnection(overrides) });
        await panel.activate();
        assert.equal(fields.get('[data-ado-status]').textContent, text);
        assert.equal(fields.get('[data-ado-status]').className, `badge text-bg-${tone}`);
    }
});

test('a new connection starts with both switches on, no token link until an organization is typed', async () => {
    const empty = { boardId: 'brd_sprint', hasToken: false, authStatus: 'none', enabled: false, columns: [], lanes: LANES };
    const { panel, fields } = createHarness({ connection: empty });
    await panel.activate();
    assert.equal(fields.get('[data-ado-enabled]').checked, true);
    assert.equal(fields.get('[data-ado-skip-done]').checked, true);
    assert.equal(fields.get('[data-ado-status]').textContent, 'Not connected');
    assert.equal(fields.get('[data-ado-token]').placeholder, 'Paste a personal access token');
    assert.equal(fields.get('[data-ado-action="unlink"]').hidden, true);
    assert.equal(fields.get('[data-ado-action="pull"]').disabled, true);
    assert.match(fields.get('[data-ado-lanes]').innerHTML, /Connect to read the board's columns/);
    assert.equal(fields.get('[data-ado-token-link]').hidden, true);
    assert.equal(fields.get('[data-ado-token-hint]').hidden, false);

    fields.get('[data-ado-link]').value = BOARD_LINK.replace('contoso', 'fabrikam-labs');
    panel._tokenLink();
    assert.equal(fields.get('[data-ado-token-link]').hidden, false);
    assert.equal(fields.get('[data-ado-token-link]').href, 'https://dev.azure.com/fabrikam-labs/_usersSettings/tokens');
    fields.get('[data-ado-link]').value = 'https://contoso.visualstudio.com/Fabrikam';
    panel._tokenLink();
    assert.equal(fields.get('[data-ado-token-link]').hidden, true);
});

test('a typed link listener is registered and removed with the panel', async () => {
    const { panel, fields } = createHarness();
    const listeners = [];
    fields.get('[data-ado-link]').addEventListener = (type, listener, options) => listeners.push({ type, options });
    await panel.activate();
    assert.equal(listeners[0].type, 'input');
    assert.equal(listeners[0].options.signal.aborted, false);
    panel.dispose();
    assert.equal(listeners[0].options.signal.aborted, true);
});

test('Connect saves the pasted link and token, reads the board, and reports what it found', async () => {
    const empty = { boardId: 'brd_sprint', hasToken: false, authStatus: 'none', enabled: false, columns: [], lanes: LANES };
    const { panel, fields, calls } = createHarness({ connection: empty });
    const changes = [];
    panel._onChanged = (action, board) => changes.push([action, board]);
    await panel.activate();
    fields.get('[data-ado-link]').value = `  ${BOARD_LINK}  `;
    fields.get('[data-ado-token]').value = 'typed-pat';
    await panel._connect();

    const save = calls.find(call => call[1] === 'PUT');
    assert.deepEqual(save[2], { boardLink: BOARD_LINK, personalAccessToken: 'typed-pat', narrowWiql: '', pointsField: '',
        enabled: true, skipOldDone: true });
    const order = calls.filter(call => call[1] !== 'GET' || String(call[0]).includes('settings')).map(call => call[1] + ' ' + call[0]);
    assert.deepEqual(order.slice(1), [
        'PUT /api/v1/board/boards/brd_sprint/ado',
        'POST /api/v1/board/boards/brd_sprint/ado/test',
        'GET /api/v1/board/boards/brd_sprint/ado?settings=true'
    ]);
    assert.equal(fields.get('[data-ado-token]').value, '');
    const summary = fields.get('[data-ado-summary]').innerHTML;
    assert.match(summary, /<strong>Stories<\/strong> \(Fabrikam Team\) · about 12 work items/);
    assert.match(summary, /Active → In Progress/);
    assert.match(summary, /Points: Microsoft\.VSTS\.Scheduling\.StoryPoints/);
    assert.equal(fields.get('[data-ado-summary]').hidden, false);
    assert.equal(fields.get('[data-ado-report]').textContent, 'Connected to Fabrikam Web.');
    assert.deepEqual(changes, [['test', 'brd_sprint']]);
});

test('Connect follows the dedicated board from the save, then from the test, for every later call', async () => {
    const { panel, calls } = createHarness({ connection: savedConnection({ boardId: 'brd_ado' }) });
    const changed = [];
    panel._onChanged = (action, id) => changed.push([action, id]);
    await panel.activate();
    await panel._connect();
    assert.ok(calls.some(([url, method]) => method === 'POST' && url === '/api/v1/board/boards/brd_ado/ado/test'));
    assert.ok(calls.some(([url, method]) => method === 'GET' && url === '/api/v1/board/boards/brd_ado/ado?settings=true'));
    await panel._pull(false);
    assert.ok(calls.some(([url, method]) => method === 'POST' && url === '/api/v1/board/boards/brd_ado/ado/pull'));
    assert.deepEqual(changed, [['test', 'brd_ado'], ['pull', 'brd_ado']]);

    const moved = createHarness();
    await moved.panel.activate();
    const fallback = moved.panel.app.apiCall;
    moved.panel.app.apiCall = async (...args) => (String(args[0]).endsWith('/ado/test')
        ? (moved.calls.push(args), { ...TEST_RESULT, boardId: 'brd_elsewhere' })
        : fallback(...args));
    await moved.panel._connect();
    assert.equal(moved.panel._board, 'brd_elsewhere');
    assert.deepEqual(moved.calls.at(-1).slice(0, 2), ['/api/v1/board/boards/brd_elsewhere/ado?settings=true', 'GET']);
});

test('a lane picked under Advanced is sent by column name, and blank means automatic', async () => {
    const { panel, calls, laneSelects } = createHarness();
    await panel.activate();
    laneSelects.push({ dataset: { adoLane: '0' }, value: '' }, { dataset: { adoLane: '1' }, value: 'col_backlog' }, { dataset: { adoLane: '9' }, value: 'x' });
    await panel._connect();
    const save = calls.find(call => call[1] === 'PUT');
    assert.deepEqual(save[2].columnMap, { New: '', Active: 'col_backlog' });
    assert.equal(save[2].boardLink, BOARD_LINK);
    assert.equal(save[2].personalAccessToken, '');
});

test('a different link sends no lane picks, because they belong to the previous board', async () => {
    const { panel, fields, calls, laneSelects } = createHarness();
    await panel.activate();
    laneSelects.push({ dataset: { adoLane: '0' }, value: 'col_progress' });
    fields.get('[data-ado-link]').value = QUERY_LINK;
    await panel._connect();
    const save = calls.find(call => call[1] === 'PUT');
    assert.equal(save[2].boardLink, QUERY_LINK);
    assert.equal('columnMap' in save[2], false);
});

test('Advanced narrowing and points are sent as typed and filled back from the saved connection', async () => {
    const { panel, fields, calls } = createHarness({ connection: savedConnection({ narrowWiql: "[System.Tags] CONTAINS 'web'", pointsField: 'Custom.Size' }) });
    await panel.activate();
    assert.equal(fields.get('[data-ado-narrow]').value, "[System.Tags] CONTAINS 'web'");
    assert.equal(fields.get('[data-ado-points]').value, 'Custom.Size');
    fields.get('[data-ado-narrow]').value = "[System.AreaPath] UNDER 'Fabrikam Web\\UI'";
    fields.get('[data-ado-enabled]').checked = false;
    fields.get('[data-ado-skip-done]').checked = false;
    await panel._connect();
    const save = calls.find(call => call[1] === 'PUT')[2];
    assert.equal(save.narrowWiql, "[System.AreaPath] UNDER 'Fabrikam Web\\UI'");
    assert.equal(save.pointsField, 'Custom.Size');
    assert.equal(save.enabled, false);
    assert.equal(save.skipOldDone, false);
});

test('a saved query says it has no columns and its summary names the query type', async () => {
    const query = savedConnection({ boardLink: QUERY_LINK, queryId: '8a8c8212-15ca-41ed-97aa-1d6fbfbcd581', team: null,
        backlogLevel: null, adoBoardName: 'Active bugs', columns: [] });
    const { panel, fields } = createHarness({ connection: query });
    await panel.activate();
    assert.match(fields.get('[data-ado-lanes]').innerHTML, /A saved query has no board columns/);
    assert.match(fields.get('[data-ado-summary]').innerHTML, /Saved query <strong>Active bugs<\/strong>/);
    panel._showResult({ ok: true, project: 'Fabrikam Web', board: { name: 'Active bugs', queryId: 'q', queryType: 'flat',
        workItemCount: 2000, countCapped: true, pointsField: null, columns: [], warnings: [] } });
    const summary = fields.get('[data-ado-summary]').innerHTML;
    assert.match(summary, /<strong>Active bugs<\/strong> \(flat query\) · 2000\+ work items; a pull reads the 2000 most recently changed/);
    assert.match(summary, /Points: off/);
});

test('a failed Connect shows the server reason and a refusal from Azure DevOps shows its error', async () => {
    const { panel, fields } = createHarness();
    await panel.activate();
    BoardApi.attach({ apiCall: async () => { throw new Error('Use a https://dev.azure.com link.'); }, showToast() {} });
    await panel._connect();
    assert.match(fields.get('[data-ado-report]').textContent, /dev\.azure\.com link/);
    assert.equal(fields.get('[data-ado-fields]').disabled, false, 'a failed action leaves the form usable');

    panel._showResult({ ok: false, error: 'Azure DevOps rejected the personal access token.' });
    assert.match(fields.get('[data-ado-report]').textContent, /rejected the personal access token/);
    panel._showResult(null);
    assert.equal(fields.get('[data-ado-report]').textContent, 'Azure DevOps refused the connection.');
});

test('board names, columns and warnings are escaped, and a result without a board reports the project only', () => {
    const { panel, fields } = createHarness();
    panel._showResult({ ok: true, project: 'P', board: { name: '<b>x</b>', team: '<i>t</i>', workItemCount: 1, columns: [{ name: '<s>', laneName: null }],
        warnings: ['<img src=x onerror=alert(1)>'] } });
    const summary = fields.get('[data-ado-summary]').innerHTML;
    for (const raw of ['<img', '<b>x</b>', '<i>t</i>', '<s>']) assert.ok(!summary.includes(raw), raw);
    assert.match(summary, /about 1 work item</);
    assert.match(summary, /&lt;s&gt; → Azure DevOps lane \(unmatched\)/);
    panel._showResult({ ok: true, project: 'Fabrikam Web' });
    assert.equal(fields.get('[data-ado-report]').textContent, 'Connected to Fabrikam Web.');
});

test('a disabled connection shows why', async () => {
    const { panel, fields } = createHarness({ connection: savedConnection({ disabledReason: 'Azure DevOps rejected the narrowing WIQL.', enabled: false }) });
    await panel.activate();
    assert.equal(fields.get('[data-ado-report]').textContent, 'Azure DevOps rejected the narrowing WIQL.');
    assert.equal(fields.get('[data-ado-enabled]').checked, false);
});

test('pull now reports the server text and reloads the connection', async () => {
    const { panel, fields, calls } = createHarness();
    await panel.activate();
    await panel._pull(false);
    assert.ok(calls.some(call => String(call[0]).endsWith('/ado/pull')));
    assert.match(fields.get('[data-ado-report]').textContent, /unchanged/);
    assert.equal(calls.filter(call => String(call[0]).endsWith('?settings=true')).length, 2);
});

test('column names and lane names are escaped in the lane map', () => {
    const html = adoLaneRows([{ name: '<script>', laneId: null, laneName: null, automatic: true }], [{ id: '"x', name: '<i>' }]);
    assert.ok(!html.includes('<script>'));
    assert.ok(!html.includes('<i>'));
    assert.match(html, /Automatic \(Azure DevOps lane \(unmatched\)\)/);
    assert.match(html, /value="&quot;x"/);
    assert.match(html, /data-ado-lane="0"/);
    assert.equal(adoColumnSummary([{ name: 'A&B', laneName: null }]), 'A&amp;B → Azure DevOps lane (unmatched)');
});

test('unlink asks first, keeps the board, and clears the connection', async t => {
    const dialogs = fakeConfirm(t);
    const { panel, fields, calls } = createHarness();
    const changes = [];
    panel._onChanged = (action, board) => changes.push([action, board]);
    await panel.activate();

    dialogs.autoAnswer = false;
    await panel._unlink();
    assert.match(dialogs[0].text, /Unlink Azure DevOps board/);
    assert.match(dialogs[0].text, /imported cards, comments, files and sessions stay in VibeRails/);
    assert.equal(calls.filter(call => call[1] === 'DELETE').length, 0);
    assert.deepEqual(changes, []);

    dialogs.autoAnswer = true;
    await panel._unlink();
    assert.deepEqual(calls.filter(call => call[1] === 'DELETE').map(call => call[0]), ['/api/v1/board/boards/brd_sprint/ado']);
    assert.equal(fields.get('[data-ado-report]').textContent, 'Azure DevOps unlinked. The board and its cards have been kept.');
    assert.equal(fields.get('[data-ado-link]').value, '');
    assert.equal(fields.get('[data-ado-status]').textContent, 'Not connected');
    assert.equal(fields.get('[data-ado-action="unlink"]').hidden, true);
    assert.equal(fields.get('[data-ado-action="pull"]').disabled, true);
    assert.deepEqual(changes, [['unlink', 'brd_sprint']]);

    await panel._unlink();
    assert.equal(dialogs.length, 2, 'nothing left to unlink');
});

test('closing while the unlink question is open never deletes', async t => {
    const dialogs = fakeConfirm(t);
    const { panel, calls } = createHarness();
    let changed = 0;
    panel._onChanged = () => changed++;
    await panel.activate();
    const unlinking = panel._unlink();
    panel.dispose();
    dialogs[0].answer(true);
    await unlinking;
    assert.equal(calls.filter(call => call[1] === 'DELETE').length, 0);
    assert.equal(changed, 0);
});

test('closing during the unlink request ignores its completion', async t => {
    const dialogs = fakeConfirm(t);
    dialogs.autoAnswer = true;
    const { panel, fields } = createHarness();
    let changed = 0;
    panel._onChanged = () => changed++;
    await panel.activate();
    let finish;
    panel.app.apiCall = async () => new Promise(resolve => { finish = resolve; });
    const unlinking = panel._unlink();
    await new Promise(resolve => setImmediate(resolve));
    panel.dispose();
    finish({ message: 'ok' });
    await unlinking;
    assert.equal(changed, 0);
    assert.equal(fields.get('[data-ado-link]').value, BOARD_LINK);
});

test('closing Board Settings clears the token field', async () => {
    const { panel, fields } = createHarness();
    await panel.activate();
    fields.get('[data-ado-token]').value = 'typed-pat';
    panel.dispose();
    assert.equal(fields.get('[data-ado-token]').value, '');
});

test('closing during a load ignores the late response and cannot save', async () => {
    const { panel, fields } = createHarness();
    let resolve;
    let writes = 0;
    panel.app.apiCall = async (_path, method) => {
        if (method !== 'GET') writes++;
        return new Promise(done => { resolve = done; });
    };
    fields.get('[data-ado-link]').value = BOARD_LINK;
    const loading = panel.activate();
    await panel._connect();
    assert.equal(writes, 0);
    assert.equal(fields.get('[data-ado-fields]').disabled, true);
    panel.dispose();
    resolve(savedConnection({ boardLink: 'https://dev.azure.com/late/L/_boards/board/t/T/Stories' }));
    await loading;
    await panel._connect();
    assert.equal(writes, 0);
    assert.equal(fields.get('[data-ado-link]').value, BOARD_LINK);
    assert.equal(fields.get('[data-ado-token]').value, '');
});

test('a failed initial load is reported and leaves writes disabled', async () => {
    const { panel, fields } = createHarness();
    let calls = 0;
    panel.app.apiCall = async () => { calls++; throw new Error('Connection unavailable'); };
    await panel.activate();
    await panel._connect();
    await panel._pull(false);
    await panel._unlink();
    assert.equal(calls, 1);
    assert.match(fields.get('[data-ado-report]').textContent, /Connection unavailable/);
    assert.equal(fields.get('[data-ado-fields]').disabled, true);
});

test('connect prevents overlapping actions and disposal suppresses late completion', async () => {
    const { panel, fields } = createHarness();
    await panel.activate();
    let resolve;
    let calls = 0;
    let changed = 0;
    panel._onChanged = () => changed++;
    panel.app.apiCall = async () => { calls++; return new Promise(done => { resolve = done; }); };
    fields.get('[data-ado-token]').value = 'typed-pat';
    const connecting = panel._connect();
    await panel._connect();
    await panel._pull(false);
    assert.equal(calls, 1);
    panel.dispose();
    resolve(savedConnection({ boardId: 'brd_late' }));
    await connecting;
    assert.equal(changed, 0);
    assert.equal(panel._board, 'brd_sprint');
    assert.equal(fields.get('[data-ado-token]').value, '');
});

for (const failure of ['test', 'settings-before-test', 'settings-after-test']) {
    test(`separation followed by a failed ${failure} never retries with source lane IDs`, async () => {
        const source = savedConnection({ boardId: 'brd_sprint',
            columns: [{ name: 'Resolved', laneId: 'source_done', laneName: 'Done', automatic: false }],
            lanes: [{ id: 'source_done', name: 'Done' }] });
        const destination = savedConnection({ boardId: 'brd_ado',
            columns: [{ name: 'Resolved', laneId: 'destination_done', laneName: 'Done', automatic: false }],
            lanes: [{ id: 'destination_done', name: 'Done' }] });
        const { panel, fields, laneSelects } = createHarness({ connection: source });
        // Reflect the selected option when the production panel replaces the lane-picker DOM.
        Object.defineProperty(fields.get('[data-ado-lanes]'), 'innerHTML', { set(html) {
            laneSelects.length = 0;
            const selected = html.match(/<option value="([^"]+)" selected>/);
            if (selected) laneSelects.push({ dataset: { adoLane: '0' }, value: selected[1] });
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
            if (path.endsWith('/ado/test')) {
                if (failure === 'test' && failOnce) { failOnce = false; throw new Error('Test unavailable'); }
                return { ok: true, project: 'Fabrikam Web', boardId: 'brd_ado' };
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
        assert.equal(panel._board, 'brd_ado');
        await panel._connect();
        assert.deepEqual(saves[0].columnMap, { Resolved: 'source_done' });
        if (failure === 'settings-before-test') assert.equal('columnMap' in saves[1], false);
        else assert.deepEqual(saves[1].columnMap, { Resolved: 'destination_done' });
    });
}
