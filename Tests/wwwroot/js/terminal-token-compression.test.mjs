import assert from 'node:assert/strict';
import test from 'node:test';
import { TerminalTabTokenCompression } from '../../../VibeRails/wwwroot/js/modules/terminal-token-compression.js';

function makeManager({ enabled = true, apiCall } = {}) {
    const state = {
        id: 'tab one',
        tokenSaverEnabled: enabled,
        tokenSaverPending: false,
        tokenSaverRevision: 0,
        ui: {}
    };
    const saved = [];
    const toasts = [];
    const manager = {
        tabs: new Map([[state.id, { state }]]),
        app: {
            apiCall: apiCall ?? (async (_url, _method, body) => ({ enabled: body.enabled })),
            showToast: (...args) => toasts.push(args)
        },
        saveTabMeta: (_tabId, payload) => saved.push(payload)
    };
    const compression = new TerminalTabTokenCompression(manager);
    manager._tabMetaPayload = tabState => compression.writeMetadata(tabState);
    return { compression, manager, state, saved, toasts };
}

test('compression toggle updates only the addressed tab through its token-saver endpoint', async () => {
    const calls = [];
    const { compression, state, saved } = makeManager({
        enabled: true,
        apiCall: async (...args) => {
            calls.push(args);
            return { enabled: false };
        }
    });

    await compression.toggle(state.id);

    assert.deepEqual(calls, [[
        '/api/v1/terminal/tabs/tab%20one/token-saver',
        'PUT',
        { enabled: false }
    ]]);
    assert.equal(state.tokenSaverEnabled, false);
    assert.equal(state.tokenSaverPending, false);
    assert.equal(saved.at(-1).tokenSaverEnabled, false);
});

test('failed compression toggle preserves state and surfaces the backend error', async () => {
    const { compression, state, saved, toasts } = makeManager({
        enabled: true,
        apiCall: async () => { throw new Error('child unavailable'); }
    });

    await compression.toggle(state.id);

    assert.equal(state.tokenSaverEnabled, true);
    assert.equal(state.tokenSaverPending, false);
    assert.equal(saved.length, 0);
    assert.equal(toasts.length, 1);
    assert.match(toasts[0][1], /child unavailable/);
});

test('a stale initial refresh cannot overwrite a user toggle', async () => {
    let resolveGet;
    const getResponse = new Promise(resolve => { resolveGet = resolve; });
    const { compression, state } = makeManager({
        enabled: true,
        apiCall: async (_url, method, body) => method === 'GET'
            ? getResponse
            : { enabled: body.enabled }
    });

    const refresh = compression.refresh(state.id);
    await compression.toggle(state.id);
    resolveGet({ enabled: true });
    await refresh;

    assert.equal(state.tokenSaverEnabled, false);
});

test('pending compression update stays visible and disables repeat clicks', async () => {
    let resolvePut;
    const putResponse = new Promise(resolve => { resolvePut = resolve; });
    const { compression, state } = makeManager({
        apiCall: async () => putResponse
    });
    state.ui = {
        item: {
            classList: { toggle: () => {} },
            dataset: {}
        },
        tokenCompression: {
            classList: { toggle: () => {} },
            dataset: {},
            setAttribute: () => {}
        }
    };

    const update = compression.toggle(state.id);

    assert.equal(state.ui.tokenCompression.hidden, false);
    assert.equal(state.ui.tokenCompression.disabled, true);
    assert.equal(state.ui.tokenCompression.dataset.state, 'pending');
    assert.match(state.ui.tokenCompression.innerHTML, /fa-spinner/);

    resolvePut({ enabled: false });
    await update;
    assert.equal(state.ui.tokenCompression.disabled, false);
});

test('compression control exposes polished state and an explicit per-tab label', () => {
    const classes = new Map();
    const attributes = new Map();
    const item = {
        classList: { toggle: (name, on) => classes.set(name, on) },
        dataset: {}
    };
    const button = {
        innerHTML: '',
        title: '',
        classList: { toggle: (name, on) => classes.set(`button:${name}`, on) },
        dataset: {},
        setAttribute: (name, value) => attributes.set(name, value)
    };
    const tab = {
        state: {
            tokenSaverEnabled: true,
            tokenSaverPending: false,
            ui: { item, tokenCompression: button }
        }
    };

    const compression = new TerminalTabTokenCompression({});
    compression.render(tab);

    assert.equal(classes.get('is-token-compression-on'), true);
    assert.equal(classes.get('button:is-on'), true);
    assert.equal(item.dataset.tokenCompression, 'on');
    assert.equal(button.dataset.state, 'on');
    assert.match(button.innerHTML, /fa-compress/);
    assert.equal(attributes.get('aria-pressed'), 'true');
    assert.match(attributes.get('aria-label'), /this tab/);
});
