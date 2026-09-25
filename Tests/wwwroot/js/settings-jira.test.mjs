import test from 'node:test';
import assert from 'node:assert/strict';
import { BoardApi } from '../../../VibeRails/wwwroot/js/modules/board-api.js';
import { SettingsJiraPanel } from '../../../VibeRails/wwwroot/js/modules/settings-jira.js';

function field(value = '') {
    return { value, checked: false, placeholder: '', textContent: '', className: '', addEventListener() {} };
}

function createHarness() {
    const fields = new Map([
        ['[data-jira-action="save"]', field()],
        ['[data-jira-action="test"]', field()],
        ['[data-jira-action="dry-run"]', field()],
        ['[data-jira-action="pull"]', field()],
        ['[data-jira-site]', field('https://acme.atlassian.net')],
        ['[data-jira-email]', field('ada@example.com')],
        ['[data-jira-token]', field('')],
        ['[data-jira-jql]', field('project = PROJ AND updated >= -30d')],
        ['[data-jira-points]', field('')],
        ['[data-jira-enabled]', field()],
        ['[data-jira-status]', field()],
        ['[data-jira-report]', field()]
    ]);
    const calls = [];
    const toasts = [];
    const app = {
        apiCall: async (...args) => {
            calls.push(args);
            const [path, method] = args;
            if (path === '/api/v1/board/boards' && method === 'GET') {
                return { boards: [{ id: 'brd_main', position: 0 }] };
            }
            if (path.endsWith('/jira') && method === 'GET') {
                return { siteUrl: 'https://acme.atlassian.net', email: 'ada@example.com', hasToken: true,
                    authStatus: 'saved', jql: 'project = PROJ', enabled: true, lastReport: 'ok: 1 created' };
            }
            if (path.endsWith('/jira') && method === 'PUT') return { ...args[2], hasToken: true, authStatus: 'saved', apiToken: undefined };
            if (path.endsWith('/jira/test')) return { ok: true, account: 'Ada Lovelace' };
            if (path.includes('/jira/pull')) return { outcome: 'ok', message: '0 created, 0 updated, 1 unchanged, 0 skipped.' };
            throw new Error('unexpected ' + method + ' ' + path);
        },
        showToast: (...args) => toasts.push(args)
    };
    const root = {
        querySelector: selector => fields.get(selector) || null,
        querySelectorAll: () => []
    };
    return { panel: new SettingsJiraPanel(app, root), fields, calls, toasts };
}

test('opening Integrations loads the connection and never displays the token', async () => {
    const { panel, fields, calls } = createHarness();
    await panel.activate();
    assert.equal(fields.get('[data-jira-site]').value, 'https://acme.atlassian.net');
    assert.equal(fields.get('[data-jira-token]').value, '');
    assert.match(fields.get('[data-jira-token]').placeholder, /Token saved/);
    assert.equal(fields.get('[data-jira-status]').textContent, 'Token saved');
    assert.equal(calls.filter(call => String(call[0]).includes('jira') && call[1] === 'GET').length, 1);
    assert.ok(calls.every(call => !JSON.stringify(call).includes('secret')));
});

test('save sends a blank token so the server keeps the saved one, and the field is cleared again', async () => {
    const { panel, fields, calls, toasts } = createHarness();
    await panel.activate();
    fields.get('[data-jira-token]').value = '';
    fields.get('[data-jira-enabled]').checked = true;
    await panel._save();
    const save = calls.find(call => call[1] === 'PUT');
    assert.equal(save[2].apiToken, '');
    assert.equal(save[2].siteUrl, 'https://acme.atlassian.net');
    assert.equal(save[2].enabled, true);
    assert.equal(fields.get('[data-jira-token]').value, '');
    assert.equal(toasts[0][0], 'Jira');
});

test('test and dry run report the server text and a rejection names the reason', async () => {
    const { panel, fields, calls } = createHarness();
    await panel.activate();
    await panel._test();
    assert.match(fields.get('[data-jira-report]').textContent, /Ada Lovelace/);
    await panel._pull(true);
    assert.ok(calls.some(call => String(call[0]).includes('dryRun=true')));
    assert.match(fields.get('[data-jira-report]').textContent, /unchanged/);

    BoardApi.attach({ apiCall: async () => { throw new Error('Site URL must be an https Jira Cloud address.'); }, showToast() {} });
    await panel._save();
    assert.match(fields.get('[data-jira-report]').textContent, /https Jira Cloud address/);
});

test('leaving the tab clears the token field', async () => {
    const { panel, fields } = createHarness();
    await panel.activate();
    fields.get('[data-jira-token]').value = 'typed-secret';
    panel.clearSecrets();
    assert.equal(fields.get('[data-jira-token]').value, '');
});

test('the panel targets the board the Board view last had open, not the first board', async () => {
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
        value: { getItem: key => (key === 'viberails.board.selected.v1' ? 'brd_sprint' : null) }
    });
    try {
        await panel.activate();
        await panel._save();
    } finally {
        if (previous) Object.defineProperty(globalThis, 'localStorage', previous);
        else delete globalThis.localStorage;
    }
    const jiraCalls = calls.filter(call => String(call[0]).includes('/jira'));
    assert.ok(jiraCalls.length >= 2);
    assert.ok(jiraCalls.every(call => String(call[0]).includes('/boards/brd_sprint/jira')));
    assert.equal(fields.get('[data-jira-board]').textContent, 'Sprint 4');
});
