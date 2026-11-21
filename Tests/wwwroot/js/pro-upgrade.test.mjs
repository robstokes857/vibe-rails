import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import {
    billingUrl, isProFeatureAvailable, normalizeEntitlements, openProUpgrade, proBadge, proUpgradeView,
    readEntitlements, requirePro, showProRequired, unknownEntitlements, updateProBadges
} from '../../../VibeRails/wwwroot/js/modules/pro-upgrade.js';
import { openRemoteBoards } from '../../../VibeRails/wwwroot/js/modules/board-remote.js';
import { openBoardSharing } from '../../../VibeRails/wwwroot/js/modules/board-sharing.js';
import { showSessionShareModal } from '../../../VibeRails/wwwroot/js/modules/terminal-session-share.js';

const FEATURES = ['remote_boards', 'remote_terminals', 'session_sharing', 'card_sharing'];
const LOCAL_NOTE = /Everything local stays free, and nothing was deleted\./;
const locked = state => ({ state, enforced: true, features: Object.fromEntries(FEATURES.map(feature => [feature, false])),
    billingUrl: 'https://viberails.ai/Billing', checkedUtc: '2026-11-20T10:00:00Z' });
const pro = () => ({ ...locked('pro'), features: Object.fromEntries(FEATURES.map(feature => [feature, true])) });

const realm = { document: globalThis.document, window: globalThis.window };

// Just enough page for the prompt: the mounted dialog body, its status line and its Check again button.
function harness(t, { entitlements = locked('free'), apiKey = '••••abcd', refresh = async () => entitlements, bridge = null } = {}) {
    t.after(() => { globalThis.document = realm.document; globalThis.window = realm.window; });
    const status = { textContent: '' };
    const check = { hidden: true, disabled: false };
    const dialogClasses = [];
    const listeners = {};
    const host = {
        isConnected: false,
        closest: selector => selector === '.modal-dialog' ? { classList: { add: name => dialogClasses.push(name) } } : null,
        querySelector: selector => selector === '[data-pro-upgrade-status]' ? status
            : selector === '[data-pro-upgrade-check]' ? check : null,
        addEventListener: (type, listener) => { listeners[type] = listener; }
    };
    const modals = [], toasts = [], calls = [];
    const app = {
        entitlements: normalizeEntitlements(entitlements),
        appSettings: { apiKey },
        showModal: (title, html) => { modals.push({ title, html }); host.isConnected = true; },
        closeModal: () => { host.isConnected = false; calls.push('close'); },
        showToast: (...args) => toasts.push(args),
        openRemoteAccount: () => calls.push('sign-in'),
        refreshEntitlements: async options => {
            calls.push(['refresh', options]);
            app.entitlements = normalizeEntitlements(await refresh());
            return app.entitlements;
        }
    };
    globalThis.document = { querySelector: selector => selector === '#modal-container [data-pro-upgrade]' && host.isConnected ? host : null };
    globalThis.window = bridge ? { __viberails_openExternal__: bridge } : {};
    const click = selector => {
        const event = { prevented: false, target: { closest: candidate => candidate === selector ? {} : null },
            preventDefault() { event.prevented = true; } };
        listeners.click(event);
        return event;
    };
    return { app, modals, toasts, calls, status, check, dialogClasses, click };
}

test('an unreadable answer is unknown and locks nothing', () => {
    for (const raw of [null, undefined, {}, [], 'offline', { state: 'trial', features: { remote_boards: false } },
        { state: 'unknown', features: { remote_boards: false } }]) {
        const entitlements = normalizeEntitlements(raw);
        assert.equal(entitlements.state, 'unknown');
        assert.deepEqual(entitlements.features, Object.fromEntries(FEATURES.map(feature => [feature, true])));
        assert.equal(entitlements.billingUrl, 'https://viberails.ai/Billing');
    }
    assert.deepEqual(unknownEntitlements(), normalizeEntitlements({}));
    // Enforcement is off on today's server: a Free account still reports every feature.
    const free = normalizeEntitlements({ state: 'free', enforced: false, features: { remote_boards: true, card_sharing: true } });
    assert.equal(free.enforced, false);
    assert.ok(FEATURES.every(feature => isProFeatureAvailable(free, feature)), 'an absent feature is available');
    assert.ok(FEATURES.every(feature => !isProFeatureAvailable(normalizeEntitlements(locked('signed_out')), feature)));
    assert.equal(isProFeatureAvailable(undefined, 'remote_boards'), true, 'an app without the state locks nothing');
});

test('reads go to the entitlement route, a forced read to its refresh, and a failure is unknown', async () => {
    const calls = [];
    const app = { apiCall: async (...args) => { calls.push(args); return locked('free'); } };
    assert.equal((await readEntitlements(app)).state, 'free');
    assert.equal((await readEntitlements(app, true)).state, 'free');
    assert.deepEqual(calls, [
        ['/api/v1/account/entitlements', 'GET', null, { showLoading: false }],
        ['/api/v1/account/entitlements/refresh', 'POST', null, { showLoading: false }]]);
    const offline = await readEntitlements({ apiCall: async () => { throw new Error('offline'); } });
    assert.equal(offline.state, 'unknown');
    assert.equal(isProFeatureAvailable(offline, 'remote_boards'), true);
});

test('the Billing link carries the feature name and nothing else', () => {
    const slugs = { remote_boards: 'remote-boards', remote_terminals: 'remote-terminals', session_sharing: 'session-sharing', card_sharing: 'card-sharing' };
    // The VS Code bridge must accept every link this dialog builds.
    const bridge = new RegExp(readFileSync('vscode-viberails/src/external-sign-in.ts', 'utf8')
        .match(/\/(\^https:[^\n]*Billing[^\n]*\$)\/\.test/)[1]);
    for (const feature of FEATURES) {
        const url = billingUrl(normalizeEntitlements(locked('free')), feature);
        assert.equal(url, `https://viberails.ai/Billing?feature=${slugs[feature]}`);
        assert.match(url, bridge);
    }
    // A page address with a query, fragment, credentials or another scheme is never extended.
    for (const hostile of ['https://viberails.ai/Billing?board=brd_7k2qz', 'https://viberails.ai/Billing#key=abcdef',
        'https://user:secret@viberails.ai/Billing', 'javascript:alert(1)', 'not a url', '', null]) {
        assert.equal(billingUrl(normalizeEntitlements({ ...locked('free'), billingUrl: hostile }), 'card_sharing'),
            'https://viberails.ai/Billing?feature=card-sharing');
    }
});

test('signed out: the prompt offers sign-in, which also creates accounts', t => {
    const { app, modals, calls, click, dialogClasses } = harness(t, { entitlements: locked('signed_out'), apiKey: '' });
    openProUpgrade(app, 'remote_boards');
    assert.equal(modals[0].title, 'Sign in to use remote Boards');
    assert.match(modals[0].html, /Sign in to continue; the same page also creates an account\./);
    assert.match(modals[0].html, LOCAL_NOTE);
    assert.match(modals[0].html, /data-action="close-modal">Not now<[\s\S]*data-pro-upgrade-sign-in>Sign in</, 'the safe action comes first');
    assert.doesNotMatch(modals[0].html, /data-pro-upgrade-billing|data-pro-upgrade-check|href=/);
    assert.deepEqual(dialogClasses, ['pro-upgrade-dialog']);
    click('[data-pro-upgrade-sign-in]');
    assert.deepEqual(calls, ['sign-in']);
});

test('free: the prompt links to Billing for that feature and then offers Check again', t => {
    const { app, modals, check, click } = harness(t);
    openProUpgrade(app, 'session_sharing');
    assert.equal(modals[0].title, 'Session sharing needs Vibe Rails Pro');
    assert.match(modals[0].html, LOCAL_NOTE);
    assert.match(modals[0].html, /<a class="btn btn-primary" href="https:\/\/viberails\.ai\/Billing\?feature=session-sharing" target="_blank" rel="noopener noreferrer" data-pro-upgrade-billing>Upgrade to Pro<\/a>/);
    assert.match(modals[0].html, /Not now<[\s\S]*data-pro-upgrade-check hidden>Check again<[\s\S]*Upgrade to Pro</);
    assert.equal(check.hidden, true, 'Check again waits until Billing was opened');
    const event = click('[data-pro-upgrade-billing]');
    assert.equal(event.prevented, false, 'a browser follows the anchor');
    assert.equal(check.hidden, false);
});

test('an inactive subscription is sent to manage billing', t => {
    const { app, modals } = harness(t, { entitlements: locked('inactive') });
    openProUpgrade(app, 'remote_terminals');
    assert.equal(modals[0].title, 'Your Vibe Rails Pro subscription is inactive');
    assert.match(modals[0].html, /Remote terminals are paused until the subscription is active again\./);
    assert.match(modals[0].html, /href="https:\/\/viberails\.ai\/Billing\?feature=remote-terminals"[^>]*>Manage billing</);
    assert.match(modals[0].html, LOCAL_NOTE);
});

test('a Board whose owner pays is informational: no upgrade, sign-in or Billing action', t => {
    // Even a signed-out or Free member is never asked to pay for someone else's Board.
    for (const entitlements of [locked('free'), locked('signed_out'), pro()]) {
        const { app, modals } = harness(t, { entitlements });
        openProUpgrade(app, 'remote_boards', { payer: 'board_owner' });
        assert.equal(modals[0].title, "This Board's owner needs Vibe Rails Pro");
        assert.match(modals[0].html, /You do not need to upgrade\./);
        assert.match(modals[0].html, LOCAL_NOTE);
        assert.doesNotMatch(modals[0].html, /Upgrade to Pro|Manage billing|Sign in|Check again|href=|Billing/);
        assert.deepEqual([...modals[0].html.matchAll(/<(?:button|a)\b[^>]*>([^<]*)</g)].map(match => match[1]), ['Close']);
    }
});

test('the VS Code webview opens Billing through its bridge with the exact link', t => {
    const opened = [];
    const { app, check, click } = harness(t, { bridge: url => opened.push(url) });
    openProUpgrade(app, 'card_sharing');
    const event = click('[data-pro-upgrade-billing]');
    assert.deepEqual(opened, ['https://viberails.ai/Billing?feature=card-sharing']);
    assert.equal(event.prevented, true);
    assert.equal(check.hidden, false);
});

test('Check again closes the prompt and confirms once the backend reports the feature', async t => {
    let answer = locked('free');
    const { app, toasts, calls, status, check, click } = harness(t, { refresh: async () => answer });
    openProUpgrade(app, 'remote_boards');
    click('[data-pro-upgrade-billing]');

    click('[data-pro-upgrade-check]');
    assert.equal(check.disabled, true);
    assert.equal(status.textContent, 'Checking your plan…');
    click('[data-pro-upgrade-check]');
    await new Promise(resolve => setImmediate(resolve));
    assert.deepEqual(calls, [['refresh', { force: true }]], 'one forced read at a time');
    assert.match(status.textContent, /not active on this account yet/);
    assert.equal(check.disabled, false);
    assert.deepEqual(toasts, []);

    // A failed or offline check proves nothing: the prompt stays.
    answer = null;
    click('[data-pro-upgrade-check]');
    await new Promise(resolve => setImmediate(resolve));
    assert.match(status.textContent, /Could not check your plan/);
    assert.ok(!calls.includes('close'));

    answer = pro();
    click('[data-pro-upgrade-check]');
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(calls.at(-1), 'close');
    assert.deepEqual(toasts, [['Vibe Rails Pro', 'Remote Boards are ready to use.', 'success']]);
});

test('a prompt closed during the check stays closed and silent', async t => {
    const { app, toasts, calls, click } = harness(t, { refresh: async () => pro() });
    openProUpgrade(app, 'remote_boards');
    click('[data-pro-upgrade-check]');
    app.closeModal();
    await new Promise(resolve => setImmediate(resolve));
    assert.deepEqual(calls.filter(call => call === 'close'), ['close']);
    assert.deepEqual(toasts, []);
});

test('requirePro lets unknown and Pro run, and prompts for a locked feature instead', t => {
    for (const entitlements of [null, pro(), { ...locked('free'), enforced: false, features: {} }]) {
        const { app, modals } = harness(t, { entitlements });
        assert.ok(FEATURES.every(feature => requirePro(app, feature)));
        assert.deepEqual(modals, []);
    }
    const free = harness(t);
    assert.equal(requirePro(free.app, 'card_sharing'), false);
    assert.equal(free.modals[0].title, 'Card sharing needs Vibe Rails Pro');
    const signedOut = harness(t, { entitlements: locked('signed_out'), apiKey: '' });
    assert.equal(requirePro(signedOut.app, 'remote_terminals'), false);
    assert.equal(signedOut.modals[0].title, 'Sign in to use remote terminals');
    // One feature can be locked while the rest stay available.
    const partial = harness(t, { entitlements: { ...pro(), features: { session_sharing: false } } });
    assert.equal(requirePro(partial.app, 'remote_boards'), true);
    assert.equal(requirePro(partial.app, 'session_sharing'), false);
});

test('a server denial opens the same prompt; other failures are left to the caller', t => {
    const stale = harness(t, { entitlements: null });
    const error = Object.assign(new Error('Vibe Rails Pro is required.'), { code: 'pro_required', feature: 'remote_boards', payer: 'account' });
    assert.equal(showProRequired(stale.app, 'remote_boards', error), true);
    assert.equal(stale.modals[0].title, 'Remote Boards need Vibe Rails Pro');
    assert.deepEqual(stale.calls, [['refresh', undefined]], 'a stale local copy is read again, without a forced server check');

    const body = harness(t);
    assert.equal(showProRequired(body.app, 'session_sharing', { success: false, status: 'pro_required', message: 'Pro required' }), true);
    assert.equal(body.modals[0].title, 'Session sharing needs Vibe Rails Pro');
    assert.deepEqual(body.calls, [], 'the local copy already agrees');

    const member = harness(t, { entitlements: pro() });
    assert.equal(showProRequired(member.app, 'remote_boards', { ...error, payer: 'board_owner' }), true);
    assert.equal(member.modals[0].title, "This Board's owner needs Vibe Rails Pro");
    assert.deepEqual(member.calls, [], "the member's own plan is not in question");

    const other = harness(t);
    for (const denial of [new Error('Bad API key'), { success: false, message: 'Try again' }, { code: 'not_found' }, null, undefined]) {
        assert.equal(showProRequired(other.app, 'remote_boards', denial), false);
    }
    assert.deepEqual(other.modals, []);
});

test('a dialog that refuses to close keeps its work and the reason is still shown', t => {
    const { app, toasts } = harness(t);
    app.showModal = () => false;
    openProUpgrade(app, 'card_sharing');
    assert.equal(toasts.length, 1);
    assert.equal(toasts[0][0], 'Card sharing needs Vibe Rails Pro');
    assert.match(toasts[0][1], LOCAL_NOTE);
    assert.equal(toasts[0][2], 'warning');
});

test('the Pro badge shows only beside a locked feature and follows later changes', () => {
    const app = { entitlements: normalizeEntitlements(locked('free')) };
    assert.match(proBadge(app, 'card_sharing'), /^<span class="badge pro-badge" data-pro-badge="card_sharing" title="Part of Vibe Rails Pro">Pro<\/span>$/);
    assert.match(proBadge({ entitlements: unknownEntitlements() }, 'card_sharing'), / hidden>Pro</);
    assert.match(proBadge(null, 'card_sharing'), / hidden>Pro</);

    const badges = FEATURES.map(feature => ({ hidden: true, dataset: { proBadge: feature } }));
    const root = { querySelectorAll: selector => selector === '[data-pro-badge]' ? badges : [] };
    updateProBadges({ entitlements: normalizeEntitlements({ ...pro(), features: { remote_terminals: false } }) }, root);
    assert.deepEqual(badges.map(badge => badge.hidden), [true, false, true, true]);
    updateProBadges({ entitlements: unknownEntitlements() }, root);
    assert.ok(badges.every(badge => badge.hidden));
    assert.doesNotThrow(() => updateProBadges(app, null));
});

test('every prompt names the product as two words and uses no markup from its inputs', () => {
    for (const feature of FEATURES) {
        for (const [entitlements, apiKey, payer] of [[locked('signed_out'), '', undefined], [locked('free'), 'key', undefined],
            [locked('inactive'), 'key', undefined], [pro(), 'key', 'board_owner']]) {
            const view = proUpgradeView({ entitlements: normalizeEntitlements(entitlements), appSettings: { apiKey } }, feature, { payer });
            assert.doesNotMatch(`${view.title} ${view.body}`, /VibeRails|[<>&]/);
            assert.ok(view.body.split('. ').length <= 2, 'the body is one or two sentences');
        }
    }
});

// ---- Entry points without a test file of their own: Remote boards, collaborators, session sharing.

// An app whose showModal behaves like the shared container: a new dialog closes the one before it.
function modalApp(t, { entitlements, apiCall = async () => assert.fail('no request expected') }) {
    t.after(() => { globalThis.document = realm.document; globalThis.window = realm.window; });
    const modals = [];
    let onClose = null;
    const app = {
        entitlements: normalizeEntitlements(entitlements), appSettings: { apiKey: '••••abcd' }, modals,
        apiCall, showToast() {}, refreshEntitlements: async () => app.entitlements,
        showModal: (title, html, options = {}) => { onClose?.(); onClose = options.onClose || null; modals.push({ title, html }); }
    };
    return app;
}

// Any element a dialog asks for: remembers its listeners and takes whatever is written to it.
function anyNode(overrides = {}) {
    const node = { listeners: {}, textContent: '', innerHTML: '', value: '', hidden: false, disabled: false, dataset: {}, isConnected: true,
        addEventListener(type, listener) { node.listeners[type] = listener; }, focus() {}, select() {},
        closest: () => null, querySelectorAll: () => [], ...overrides };
    return node;
}

test('Remote boards still opens for a locked plan: its list and Delete are how a downgraded owner cleans up', t => {
    const app = modalApp(t, { entitlements: locked('free') });
    globalThis.document = { querySelector: () => null };
    const dispose = openRemoteBoards(app);
    assert.deepEqual(app.modals.map(modal => modal.title), ['Remote boards']);
    assert.doesNotThrow(dispose);
});

test('a locked plan can list and edit collaborators but is prompted before inviting', async t => {
    const calls = [];
    const state = { configured: true, isOwner: true, sharing: { limit: 3, invitations: [{ id: 'invite_1', email: 'member@example.test', status: 'accepted' }] } };
    const app = modalApp(t, { entitlements: locked('free'), apiCall: async (_url, method) => { calls.push(method); return method === 'GET' ? state : { saved: true }; } });
    const form = sharingEdit => anyNode({ dataset: sharingEdit ? { sharingEdit } : {}, reportValidity: () => true,
        querySelector: selector => selector === '[data-sharing-remove]' ? null : anyNode({ value: 'next@example.test' }) });
    const forms = [form('invite_1'), form(null)];
    const content = anyNode({ querySelectorAll: () => forms });
    const host = anyNode({ querySelector: selector => selector === '[data-sharing-content]' ? content : anyNode() });
    globalThis.document = { querySelector: () => host };

    openBoardSharing(app, 'brd_main');
    await new Promise(resolve => setImmediate(resolve));
    assert.deepEqual(calls, ['GET'], 'the list still loads');
    assert.match(content.innerHTML, /type="submit">Invite<\/button><span class="badge pro-badge" data-pro-badge="remote_boards" title="Part of Vibe Rails Pro">Pro</);

    await forms[0].listeners.submit({ preventDefault() {} });
    assert.deepEqual(calls, ['GET', 'PUT', 'GET'], 'an existing collaborator is still the server\'s decision');
    assert.deepEqual(app.modals.map(modal => modal.title), ['Share board']);

    await forms[1].listeners.submit({ preventDefault() {} });
    assert.deepEqual(calls, ['GET', 'PUT', 'GET'], 'no invitation was sent');
    assert.deepEqual(app.modals.map(modal => modal.title), ['Share board', 'Remote Boards need Vibe Rails Pro']);
});

test('session sharing prompts before its dialog when locked, and on the server\'s denial', async t => {
    const lockedApp = modalApp(t, { entitlements: locked('signed_out') });
    lockedApp.appSettings.apiKey = '';
    assert.equal(await showSessionShareModal(lockedApp, 'session-1', 'Fix the build'), null);
    assert.deepEqual(lockedApp.modals.map(modal => modal.title), ['Sign in to use session sharing']);

    const calls = [];
    const denial = { success: false, status: 'pro_required', message: 'Session sharing needs Vibe Rails Pro.', feature: 'session_sharing' };
    const app = modalApp(t, { entitlements: pro(), apiCall: async (...args) => { calls.push(args.slice(0, 2)); return denial; } });
    const nodes = new Map();
    const root = anyNode({ querySelector: selector => nodes.get(selector) || nodes.set(selector, anyNode()).get(selector) });
    globalThis.document = { getElementById: () => root, querySelector: () => null };
    const shared = showSessionShareModal(app, 'session-1', 'Fix the build');
    nodes.get('[data-share-create]').listeners.click();
    assert.equal(await shared, null, 'the replaced dialog settles without a link');
    assert.deepEqual(calls, [['/api/v1/sessions/session-1/sharing-links', 'POST']]);
    assert.deepEqual(app.modals.map(modal => modal.title), ['Share session', 'Session sharing needs Vibe Rails Pro']);
    assert.notEqual(nodes.get('[data-share-retry]').hidden, false, 'no retry is offered for a plan denial');
});
