import * as assert from 'assert/strict';
import * as vscode from 'vscode';
import { BackendRequestError } from '../../backend-api';
import {
    LAUNCHER_ALL_HIDDEN_MESSAGE,
    LAUNCHER_EMPTY_MESSAGE,
    LAUNCHER_ITEM_CONTEXT,
    LAUNCHER_LOAD_FAILED_MESSAGE,
    LAUNCHER_NONE_RUNNABLE_MESSAGE,
    LauncherItem,
    LauncherTreeDataProvider,
    RunLauncherDependencies,
    isLauncherItemRunnable,
    launcherEmptyMessage,
    launcherItemDescription,
    normalizeLauncherItems,
    runLauncherItem,
    scriptNameFromItem,
    visibleLauncherItems
} from '../../launcher-view';

function automation(jobId: number, label: string, order: number, enabled = true): LauncherItem {
    return { key: `job:${jobId}`, kind: 'automation', label, jobId, enabled, order, status: null };
}

function script(id: string, status: string, order: number, enabled = true, extra: Partial<LauncherItem> = {}): LauncherItem {
    return { key: `script:${id}`, kind: 'script', label: id, jobId: Number.NaN, enabled, order, status, ...extra };
}

function tick(): Promise<void> {
    return new Promise((resolve) => setTimeout(resolve, 0));
}

suite('Launcher view catalog rules', () => {
    test('normalizes the server catalog like the nav flyout: sorted, malformed rows dropped', () => {
        const items = normalizeLauncherItems([
            { key: 'job:7', kind: 'automation', label: 'Nightly review', jobId: 7, enabled: true, order: 2 },
            { key: 'script:backup.ps1', kind: 'script', label: 'Backup', jobId: 0, enabled: true, order: 0, status: 'approved', scriptFileName: 'backup.ps1' },
            { key: '', kind: 'automation', label: 'No key', jobId: 1, enabled: true, order: 1 },
            { key: 'job:x', kind: 'automation', label: 'Bad id', jobId: 'nope', enabled: true, order: 1 },
            { key: 'script:new.py', kind: 'script', label: 'new.py', enabled: false, order: 1 },
            null,
            'junk'
        ]);

        assert.deepEqual(items.map((item) => item.key), ['script:backup.ps1', 'script:new.py', 'job:7']);
        assert.equal(items[0].scriptFileName, 'backup.ps1');
        assert.equal(items[1].status, 'unapproved');
        assert.equal(items[1].enabled, false);
        assert.equal(items[2].status, null);
        assert.equal(items[2].jobId, 7);
        assert.deepEqual(normalizeLauncherItems(undefined), []);
        assert.deepEqual(normalizeLauncherItems({ items: [] }), []);
    });

    test('automations are always runnable, scripts only while signed, hidden rows never show', () => {
        const rows = [
            automation(1, 'A', 0),
            script('s.py', 'approved', 1),
            script('m.py', 'modified', 2),
            script('u.py', 'unapproved', 3),
            automation(2, 'Hidden', 4, false),
            script('h.py', 'approved', 5, false)
        ];

        assert.deepEqual(rows.filter(isLauncherItemRunnable).map((row) => row.key), ['job:1', 'script:s.py', 'job:2', 'script:h.py']);
        assert.deepEqual(visibleLauncherItems(rows).map((row) => row.key), ['job:1', 'script:s.py']);
    });

    test('empty states use the flyout texts', () => {
        assert.equal(launcherEmptyMessage(null), LAUNCHER_LOAD_FAILED_MESSAGE);
        assert.equal(launcherEmptyMessage([]), LAUNCHER_EMPTY_MESSAGE);
        assert.equal(launcherEmptyMessage([script('u.py', 'unapproved', 0)]), LAUNCHER_NONE_RUNNABLE_MESSAGE);
        assert.equal(launcherEmptyMessage([automation(1, 'A', 0, false)]), LAUNCHER_ALL_HIDDEN_MESSAGE);
        assert.equal(launcherEmptyMessage([automation(1, 'A', 0, false), script('u.py', 'unapproved', 1)]), LAUNCHER_ALL_HIDDEN_MESSAGE);
        assert.equal(launcherEmptyMessage([automation(1, 'A', 0)]), undefined);
    });

    test('scripts run by registration id and describe their runtime', () => {
        assert.equal(scriptNameFromItem(script('abc123', 'approved', 0, true, { label: 'Backup' })), 'abc123');
        assert.equal(scriptNameFromItem(automation(1, 'Nightly', 0)), 'Nightly');
        assert.equal(launcherItemDescription(automation(1, 'A', 0)), undefined);
        assert.equal(launcherItemDescription(script('x', 'approved', 0, true, { label: 'Backup', scriptFileName: 'backup.PS1' })), 'PowerShell script');
        assert.equal(launcherItemDescription(script('tidy.py', 'approved', 0)), 'Python script');
        assert.equal(launcherItemDescription(script('run.sh', 'approved', 0)), 'Bash script');
        assert.equal(launcherItemDescription(script('odd.txt', 'approved', 0)), 'Script');
    });
});

suite('Launcher view provider', () => {
    test('rows run on click and carry the flyout icon, title and inline-action context', () => {
        const provider = new LauncherTreeDataProvider({ isDashboardOpen: () => true, fetchItems: async () => ({ items: [] }) });
        try {
            const item = automation(7, 'Nightly review', 0);
            const node = provider.getTreeItem(item);
            assert.equal(node.label, 'Nightly review');
            assert.equal(node.description, undefined);
            assert.equal(node.contextValue, LAUNCHER_ITEM_CONTEXT);
            assert.equal(node.tooltip, 'Run Nightly review now');
            assert.equal(node.command?.command, 'viberails.launcher.run');
            assert.equal(node.command?.arguments?.[0], item);
            assert.equal((node.iconPath as vscode.ThemeIcon).id, 'play');
            assert.equal(node.collapsibleState, vscode.TreeItemCollapsibleState.None);

            const scriptNode = provider.getTreeItem(script('b.ps1', 'approved', 1));
            assert.equal((scriptNode.iconPath as vscode.ThemeIcon).id, 'terminal');
            assert.equal(scriptNode.description, 'PowerShell script');
        } finally {
            provider.dispose();
        }
    });

    test('shows nothing while the dashboard is closed and the flyout texts otherwise', async () => {
        let open = false;
        let fail = false;
        let calls = 0;
        let response: unknown = { items: [] };
        const provider = new LauncherTreeDataProvider({
            isDashboardOpen: () => open,
            fetchItems: async () => {
                calls += 1;
                if (fail) { throw new Error('boom'); }
                return response;
            }
        });
        let changes = 0;
        const subscription = provider.onDidChangeTreeData(() => { changes += 1; });
        try {
            await provider.refresh();
            assert.deepEqual(provider.getChildren(), []);
            assert.equal(provider.getMessage(), undefined);
            assert.equal(calls, 0);

            open = true;
            await provider.refresh();
            assert.equal(calls, 1);
            assert.deepEqual(provider.getChildren(), []);
            assert.equal(provider.getMessage(), LAUNCHER_EMPTY_MESSAGE);

            response = {
                items: [
                    { key: 'job:2', kind: 'automation', label: 'B', jobId: 2, enabled: true, order: 1 },
                    { key: 'job:1', kind: 'automation', label: 'A', jobId: 1, enabled: true, order: 0 },
                    { key: 'script:u.py', kind: 'script', label: 'u.py', enabled: true, order: 2, status: 'unapproved' }
                ]
            };
            await provider.refresh();
            assert.deepEqual(provider.getChildren().map((item) => item.label), ['A', 'B']);
            assert.deepEqual(provider.getChildren(provider.getChildren()[0]), []);
            assert.equal(provider.getMessage(), undefined);

            fail = true;
            await provider.refresh();
            assert.deepEqual(provider.getChildren(), []);
            assert.equal(provider.getMessage(), LAUNCHER_LOAD_FAILED_MESSAGE);

            // A dashboard that closed while the request was in flight leaves no stale rows.
            fail = false;
            let release: () => void = () => undefined;
            response = new Promise<unknown>((resolve) => { release = () => resolve({ items: [{ key: 'job:1', kind: 'automation', label: 'A', jobId: 1, enabled: true, order: 0 }] }); });
            const pending = provider.refresh();
            open = false;
            release();
            await pending;
            assert.deepEqual(provider.getChildren(), []);
            assert.equal(provider.getMessage(), undefined);
            assert.equal(changes, 5);
        } finally {
            subscription.dispose();
            provider.dispose();
        }
    });

    test('overlapping refreshes coalesce into one follow-up load', async () => {
        let release: () => void = () => undefined;
        let calls = 0;
        const provider = new LauncherTreeDataProvider({
            isDashboardOpen: () => true,
            fetchItems: () => {
                calls += 1;
                return new Promise<unknown>((resolve) => { release = () => resolve({ items: [] }); });
            }
        });
        try {
            const first = provider.refresh();
            const second = provider.refresh();
            const third = provider.refresh();
            assert.equal(calls, 1);
            assert.equal(second, third);

            release();
            await first;
            await tick();
            assert.equal(calls, 2);

            release();
            await Promise.all([second, third]);
            await tick();
            assert.equal(calls, 2);
        } finally {
            provider.dispose();
        }
    });
});

suite('Launcher view run dispatch', () => {
    function dependencies(overrides: Partial<RunLauncherDependencies> = {}) {
        const log: string[] = [];
        const posts: object[] = [];
        const errors: string[] = [];
        const queued: string[] = [];
        const deps: RunLauncherDependencies = {
            request: async (method, route) => {
                log.push(`${method} ${route}`);
                return { success: true, message: 'Automation queued.', runId: 'run-1' };
            },
            openDashboard: async () => { log.push('open'); },
            reveal: () => { log.push('reveal'); },
            post: async (message) => { posts.push(message); return true; },
            notifyQueued: (label, message) => { queued.push(`${label}: ${message}`); },
            notifyError: (message) => { errors.push(message); },
            refresh: () => { log.push('refresh'); },
            running: new Set<string>(),
            // A zero cooldown still releases the row on the next macrotask: `await tick()` first.
            clickCooldownMs: 0,
            ...overrides
        };
        return { deps, log, posts, errors, queued };
    }

    function elapsed(ms: number): Promise<void> {
        return new Promise((resolve) => setTimeout(resolve, ms));
    }

    test('an automation row is queued from the host and the dashboard is told', async () => {
        const f = dependencies();
        await runLauncherItem(automation(7, 'Nightly review', 0), f.deps);
        assert.deepEqual(f.log, ['POST /api/v1/jobs/7/run']);
        assert.deepEqual(f.queued, ['Nightly review: Automation queued.']);
        assert.deepEqual(f.posts, [{ command: 'automationQueued', jobId: 7, message: 'Automation queued.' }]);
        assert.deepEqual(f.errors, []);
        await tick();
        assert.equal(f.deps.running.size, 0);
    });

    test('a script row opens the dashboard and hands over the registration id', async () => {
        const f = dependencies();
        await runLauncherItem(script('abc123', 'approved', 0, true, { label: 'Backup' }), f.deps);
        assert.deepEqual(f.log, ['open', 'reveal']);
        assert.deepEqual(f.posts, [{ command: 'runScript', name: 'abc123' }]);
        assert.deepEqual(f.errors, []);
        assert.deepEqual(f.queued, []);
    });

    test('server errors surface verbatim and a vanished row refreshes the list', async () => {
        const rejected = dependencies({ request: async () => { throw new BackendRequestError(400, 'The codex CLI is not on PATH.'); } });
        await runLauncherItem(automation(7, 'Nightly review', 0), rejected.deps);
        assert.deepEqual(rejected.errors, ['Nightly review: The codex CLI is not on PATH.']);
        assert.deepEqual(rejected.log, []);
        assert.deepEqual(rejected.posts, []);

        const gone = dependencies({ request: async () => { throw new BackendRequestError(404, 'Automation not found.'); } });
        await runLauncherItem(automation(9, 'Old', 0), gone.deps);
        assert.deepEqual(gone.log, ['refresh']);
        assert.deepEqual(gone.errors, ['Old: Automation not found.']);

        const silent = dependencies({ post: async () => false });
        await runLauncherItem(script('s.py', 'approved', 0), silent.deps);
        assert.deepEqual(silent.errors, ['s.py: The dashboard did not respond. Try again.']);

        const noFolder = dependencies({ openDashboard: async () => { throw new Error('No workspace folder is open.'); } });
        await runLauncherItem(script('s.py', 'approved', 0), noFolder.deps);
        assert.deepEqual(noFolder.errors, ['s.py: No workspace folder is open.']);
        await tick();
        assert.equal(noFolder.deps.running.size, 0);
    });

    test('a second click while a run is in flight is ignored', async () => {
        let release: (value: unknown) => void = () => undefined;
        const f = dependencies({ request: () => new Promise<unknown>((resolve) => { release = resolve; }) });
        const item = automation(7, 'Nightly review', 0);

        const first = runLauncherItem(item, f.deps);
        await runLauncherItem(item, f.deps);
        assert.equal(f.deps.running.has('job:7'), true);

        release({ message: 'Automation queued.' });
        await first;
        assert.deepEqual(f.queued, ['Nightly review: Automation queued.']);
        await tick();
        assert.equal(f.deps.running.size, 0);

        await runLauncherItem(undefined, f.deps);
        await runLauncherItem({ ...item, key: '' }, f.deps);
        assert.equal(f.queued.length, 1);
    });

    test('the double-click delivery of a quick run is ignored until the cooldown passes', async () => {
        const f = dependencies({ clickCooldownMs: 40 });
        const item = automation(7, 'Nightly review', 0);

        await runLauncherItem(item, f.deps); // the click; the request settles at once
        await runLauncherItem(item, f.deps); // the double-click's second delivery, a little later
        assert.deepEqual(f.log, ['POST /api/v1/jobs/7/run']);
        assert.deepEqual(f.queued, ['Nightly review: Automation queued.']);
        assert.equal(f.deps.running.has('job:7'), true);

        await elapsed(80);
        assert.equal(f.deps.running.has('job:7'), false);
        await runLauncherItem(item, f.deps); // a deliberate later click runs again
        assert.deepEqual(f.log, ['POST /api/v1/jobs/7/run', 'POST /api/v1/jobs/7/run']);

        // A failed first delivery is guarded the same way: one error dialog, not two.
        const rejected = dependencies({
            clickCooldownMs: 40,
            request: async () => { throw new BackendRequestError(400, 'The codex CLI is not on PATH.'); }
        });
        await runLauncherItem(item, rejected.deps);
        await runLauncherItem(item, rejected.deps);
        assert.deepEqual(rejected.errors, ['Nightly review: The codex CLI is not on PATH.']);
        await elapsed(80);
        assert.equal(rejected.deps.running.size, 0);
    });
});
