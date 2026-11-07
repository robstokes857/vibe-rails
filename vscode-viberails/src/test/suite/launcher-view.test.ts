import * as assert from 'assert/strict';
import { BackendRequestError } from '../../backend-api';
import { LauncherItem, LauncherTreeDataProvider, RunLauncherDependencies, normalizeLauncherItems,
    runLauncherItem, isLauncherItemRunnable, LAUNCHER_LOAD_FAILED_MESSAGE } from '../../launcher-view';

const item = (id = 'preset_1', extra: Partial<LauncherItem> = {}): LauncherItem => ({
    id, key: `preset:${id}`, definitionType: 'preset', kind: 'agent', name: 'Reviewer', label: 'Reviewer',
    enabled: true, retired: false, canLaunch: true, ...extra
});
const tick = () => new Promise(resolve => setTimeout(resolve, 5));
function dependencies(overrides: Partial<RunLauncherDependencies> = {}) {
    const calls: unknown[][] = [], errors: string[] = [], messages: object[] = [];
    let refreshes = 0;
    const deps: RunLauncherDependencies = {
        request: async (...args) => { calls.push(args); return { id: 'run_1' }; },
        openDashboard: async () => {}, reveal: () => {}, post: async message => { messages.push(message); return true; },
        notifyQueued: () => {}, notifyError: message => errors.push(message), refresh: () => { refreshes++; },
        running: new Set(), clickCooldownMs: 0, ...overrides
    };
    return { deps, calls, errors, messages, refreshes: () => refreshes };
}
suite('Global launch catalog', () => {
    test('retains stable identity and unavailable entries without accepting legacy jobs', () => {
        const entries = normalizeLauncherItems([item(), item('retired', { retired: true }),
            { key: 'job:1', jobId: 1, name: 'Legacy' }, null]);
        assert.equal(entries.length, 2);
        assert.equal(entries[0].id, 'preset_1');
        assert.equal(isLauncherItemRunnable(entries[1]), false);
        const provider = new LauncherTreeDataProvider({ isBackendReady: () => true, fetchItems: async () => ({ items: entries }) });
        assert.equal(provider.getTreeItem(entries[1]).command, undefined);
        assert.equal(provider.getTreeItem(entries[0]).command?.command, 'viberails.launcher.run');
        provider.dispose();
    });
    test('groups presets and actions and discards responses after backend shutdown', async () => {
        let ready = true;
        let response: Promise<unknown> = Promise.resolve({ items: [item(), item('action_1', { definitionType: 'action', kind: 'vca' })] });
        const provider = new LauncherTreeDataProvider({ isBackendReady: () => ready, fetchItems: () => response });
        await provider.refresh();
        assert.deepEqual(provider.getChildren().map(row => row.label), ['Agents and Quick Launches', 'Actions']);
        let resolve!: (value: unknown) => void;
        response = new Promise(done => { resolve = done; });
        const pending = provider.refresh(); ready = false; resolve({ items: [item()] }); await pending;
        assert.deepEqual(provider.getItems(), []);
        ready = true; response = Promise.reject(new Error('offline')); await provider.refresh();
        assert.equal(provider.getMessage(), LAUNCHER_LOAD_FAILED_MESSAGE);
        provider.dispose();
    });
    test('coalesces overlapping refreshes into one subsequent request', async () => {
        let calls = 0, resolve!: (value: unknown) => void;
        const provider = new LauncherTreeDataProvider({ isBackendReady: () => true,
            fetchItems: () => { calls++; return new Promise(done => { resolve = done; }); } });
        const first = provider.refresh(), next = provider.refresh(), same = provider.refresh();
        assert.equal(next, same); resolve({ items: [] }); await first; await tick();
        assert.equal(calls, 2); resolve({ items: [item()] }); await next;
        assert.equal(provider.getItems().length, 1); provider.dispose();
    });
});
suite('Typed launch dispatch', () => {
    test('presets use the shared template and terminal launch flow in the dashboard', async () => {
        const state = dependencies();
        await runLauncherItem(item(), state.deps);
        assert.equal(state.calls.length, 0);
        assert.deepEqual(state.messages, [{ command: 'launchDefinition', definitionId: 'preset_1', definitionType: 'preset' }]);
        const failed = dependencies({ post: async () => false });
        await runLauncherItem(item(), failed.deps);
        assert.match(failed.errors[0], /dashboard could not receive/);
        await tick();
    });
    test('posts one stable definition and occurrence, never an environment or job', async () => {
        const state = dependencies();
        await runLauncherItem(item('action_1', { definitionType: 'action' }), state.deps);
        assert.equal(state.calls.length, 1);
        assert.deepEqual(state.calls[0].slice(0, 2), ['POST', '/api/v1/runs']);
        assert.equal((state.calls[0][2] as any).definitionId, 'action_1');
        assert.match((state.calls[0][2] as any).occurrenceId, /^[a-f0-9-]{36}$/);
        assert.deepEqual(state.messages, [{ command: 'launchQueued', definitionId: 'action_1', definitionType: 'action' }]);
        await tick();
    });
    test('guards duplicate delivery, retired definitions and server ineligibility', async () => {
        let resolve!: (value: unknown) => void, count = 0;
        const state = dependencies({ request: () => { count++; return new Promise(done => { resolve = done; }); } });
        const action = item('action_1', { definitionType: 'action' });
        const first = runLauncherItem(action, state.deps);
        await runLauncherItem(action, state.deps); assert.equal(count, 1);
        resolve({}); await first; await tick();
        await runLauncherItem(item('retired', { retired: true }), state.deps);
        await runLauncherItem(item('ineligible', { canLaunch: false }), state.deps);
        assert.equal(count, 1);
        const rejected = dependencies({ request: async () => { throw new BackendRequestError(409, 'Definition changed'); } });
        await runLauncherItem(action, rejected.deps);
        assert.equal(rejected.refreshes(), 1); assert.match(rejected.errors[0], /Definition changed/);
        await tick();
    });
});
