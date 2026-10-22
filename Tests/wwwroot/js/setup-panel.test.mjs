import test from 'node:test';
import assert from 'node:assert/strict';
import { buildSetupModel } from '../../../VibeRails/wwwroot/js/modules/setup-panel.js';

const method = (id, extra = {}) => ({ id, label: id, command: `install ${id}`, requires: null, note: null, recommended: false, ...extra });
const tool = (id, group, state, extra = {}) => ({
    id, name: extra.name || id, group, state, why: `${id} why`, docsUrl: 'https://example.test', brand: null,
    path: state === 'missing' ? null : `C:\\bin\\${id}.exe`, version: null, methods: [], ...extra
});

const status = tools => ({ platform: 'windows', checkedUtc: '2026-10-14T12:00:00Z', tools });
const section = (model, key) => model.sections.find(item => item.key === key);
const rows = (model, sectionKey, groupKey) => section(model, sectionKey)?.groups.find(group => group.key === groupKey)?.tools || [];

test('required comes first, with the AI CLIs as an "at least one" group, and optional last', () => {
    const model = buildSetupModel(status([
        tool('node', 'optional', 'missing'),
        tool('git', 'required', 'ready'),
        tool('claude', 'agent', 'missing'),
        tool('pwsh', 'required', 'missing'),
        tool('python', 'optional', 'ready')
    ]));
    assert.deepEqual(model.sections.map(item => item.key), ['required', 'optional']);
    assert.deepEqual(section(model, 'required').groups.map(group => group.key), ['required', 'agent']);
    assert.deepEqual(rows(model, 'required', 'required').map(item => item.id), ['git', 'pwsh']);
    assert.equal(section(model, 'required').groups[1].hint, 'Install at least one.');
    assert.deepEqual(rows(model, 'optional', 'optional').map(item => item.id), ['node', 'python']);
});

test('a machine with every required tool and one agent is complete and raises no alert', () => {
    const model = buildSetupModel(status([
        tool('git', 'required', 'ready', { name: 'Git' }),
        tool('pwsh', 'required', 'ready', { name: 'PowerShell 7' }),
        tool('claude', 'agent', 'ready', { name: 'Claude Code' }),
        tool('codex', 'agent', 'missing', { name: 'Codex' }),
        tool('node', 'optional', 'missing')
    ]));
    assert.equal(model.complete, true);
    assert.equal(model.alert, null);
    // Once one agent is ready the others stay collapsed behind "Install…".
    assert.equal(rows(model, 'required', 'agent').find(item => item.id === 'codex').expandedByDefault, false);
});

test('missing required tools and no agent produce the app-load alert text', () => {
    const model = buildSetupModel(status([
        tool('git', 'required', 'missing', { name: 'Git' }),
        tool('pwsh', 'required', 'missing', { name: 'PowerShell 7' }),
        tool('claude', 'agent', 'missing', { name: 'Claude Code' }),
        tool('node', 'optional', 'missing', { name: 'Node.js' })
    ]));
    assert.equal(model.complete, false);
    assert.equal(model.alert, 'Install Git, PowerShell 7 and an AI coding CLI to start using VibeRails.');
    assert.equal(rows(model, 'required', 'required')[1].expandedByDefault, true);
    // With no agent installed, every agent row opens so the user can pick one; optional rows stay shut.
    assert.equal(rows(model, 'required', 'agent')[0].expandedByDefault, true);
    assert.equal(rows(model, 'optional', 'optional')[0].expandedByDefault, false);
});

test('restart-needed counts as installed', () => {
    const model = buildSetupModel(status([
        tool('git', 'required', 'ready'),
        tool('claude', 'agent', 'restart', { name: 'Claude Code' })
    ]));
    assert.equal(model.complete, true);
    assert.deepEqual(model.restartNeeded.map(item => item.id), ['claude']);
});

test('a method whose prerequisite is missing is blocked by that tool', () => {
    const model = buildSetupModel(status([
        tool('codex', 'agent', 'missing', { methods: [method('native', { recommended: true }), method('npm', { requires: 'node' })] }),
        tool('node', 'optional', 'missing', { name: 'Node.js' })
    ]));
    const codex = rows(model, 'required', 'agent')[0];
    assert.equal(codex.methods[0].blockedBy, null);
    assert.equal(codex.methods[1].blockedBy.name, 'Node.js');
});

test('a missing package manager comes first, only while missing, and never raises the alert', () => {
    const present = buildSetupModel(status([tool('brew', 'package-manager', 'ready')]));
    assert.equal(present.sections.length, 0);
    const absent = buildSetupModel(status([
        tool('git', 'required', 'ready'),
        tool('claude', 'agent', 'ready'),
        tool('brew', 'package-manager', 'missing'),
        tool('node', 'optional', 'missing')
    ]));
    assert.deepEqual(absent.sections.map(item => item.key), ['package-manager', 'required', 'optional']);
    assert.deepEqual(rows(absent, 'package-manager', 'package-manager').map(item => item.id), ['brew']);
    assert.equal(rows(absent, 'package-manager', 'package-manager')[0].expandedByDefault, true);
    assert.equal(absent.alert, null);
});

test('the recommended method stays selected while its package manager is missing', async () => {
    const { SetupPanel } = await import('../../../VibeRails/wwwroot/js/modules/setup-panel.js');
    const container = { innerHTML: '', addEventListener() {} };
    const panel = new SetupPanel({}, container, {});
    panel.status = status([
        tool('codex', 'agent', 'missing', { methods: [method('brew', { requires: 'brew', recommended: true }), method('native')] }),
        tool('brew', 'package-manager', 'missing', { name: 'Homebrew' })
    ]);
    panel.render();
    assert.match(container.innerHTML, /aria-checked="true"\s+data-setup-action="method"\s+data-tool="codex" data-method="brew"/);
    assert.match(container.innerHTML, /Needs Homebrew first\./);
});

// ---- Installer ownership (PR #84 review) ------------------------------------------------

const { SetupInstallWindow } = await import('../../../VibeRails/wwwroot/js/modules/setup-install-window.js');
const { SettingsController } = await import('../../../VibeRails/wwwroot/js/modules/settings-controller.js');

/** Just enough browser for SetupInstallWindow.open() without xterm or a real socket. */
function fakeBrowser() {
    globalThis.window = { location: { origin: 'http://127.0.0.1:5000' }, addEventListener() {}, removeEventListener() {} };
    globalThis.document = { activeElement: null, addEventListener() {}, removeEventListener() {} };
    globalThis.sessionStorage = { getItem: () => null };
    globalThis.WebSocket = class { static OPEN = 1; close() {} send() {} };
}

/** Replaces the xterm window with a status line the test can read. */
function headlessWindow(installWindow) {
    installWindow._mount = function () {
        const status = { textContent: '', dataset: {} };
        this.layer = { status, remove() {}, querySelector: selector => selector === '[data-run-status]' ? status : null };
    };
    return installWindow;
}

test('a finished install re-checks without repainting a newer install opened meanwhile', async () => {
    fakeBrowser();
    let resolveRefresh;
    const installWindow = headlessWindow(new SetupInstallWindow({ showError() {} }, {
        onFinished: () => new Promise(resolve => { resolveRefresh = resolve; })
    }));
    const claude = { id: 'claude', name: 'Claude Code' };
    const codex = { id: 'codex', name: 'Codex' };

    installWindow.open(claude, { id: 'native', label: 'Official installer', command: 'install claude' });
    const finishing = installWindow._finish(installWindow._token, 'Finished · checking…', 'success', 0);
    assert.equal(installWindow.running, false);
    // Codex starts while Claude Code's re-check is still in flight.
    installWindow.open(codex, { id: 'native', label: 'Official installer', command: 'install codex' });
    const codexStatus = installWindow.layer.status;
    installWindow._paintState('Installing…', 'waiting');

    resolveRefresh({ tools: [{ id: 'claude', state: 'ready' }, { id: 'codex', state: 'missing' }] });
    await finishing;

    assert.equal(codexStatus.textContent, 'Installing…');
    assert.equal(installWindow.tool, codex);
    assert.equal(installWindow.running, true);
});

test('an install keeps its owner when Settings is left and reopened', async () => {
    fakeBrowser();
    const toasts = [];
    const alerts = [];
    const refreshed = { platform: 'windows', checkedUtc: '2026-10-14T12:00:00Z', tools: [] };
    const app = {
        showToast: (...args) => toasts.push(args),
        showError() {},
        apiCall: async () => refreshed,
        setupAlert: { update: status => alerts.push(status) }
    };
    const controller = new SettingsController(app);
    const container = () => ({ innerHTML: '', addEventListener() {}, contains: () => true, querySelector: () => null });

    const first = controller._setupPanel = controller._mountSetupPanel(container());
    first.installWindow.running = true;
    first.installWindow.show = () => {};
    controller.unload();
    const second = controller._setupPanel = controller._mountSetupPanel(container());

    // The panel built on return sees the running install and refuses a second one.
    assert.equal(second.installWindow, first.installWindow);
    let opened = 0;
    second.installWindow.open = () => { opened++; };
    second.status = { tools: [tool('claude', 'agent', 'missing', { methods: [method('native')] })] };
    const install = { dataset: { setupAction: 'install', tool: 'claude', method: 'native' } };
    second._onClick({ target: { closest: () => install } });
    assert.equal(opened, 0);
    assert.equal(toasts.length, 1);

    // Its re-check lands on the mounted panel, not the one left behind.
    await second.installWindow.onFinished();
    assert.equal(second.status, refreshed);
    assert.equal(first.status, null);

    // With Settings closed, only the warning bar is left to update.
    controller.unload();
    alerts.length = 0;
    await first.installWindow.onFinished();
    assert.deepEqual(alerts, [refreshed]);
});
