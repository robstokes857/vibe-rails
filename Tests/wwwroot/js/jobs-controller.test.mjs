import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const modulePath = path.resolve('VibeRails/wwwroot/js/modules/jobs-controller.js');
const environmentModulePath = path.resolve('VibeRails/wwwroot/js/modules/environment-controller.js');
const {
    JobController,
    getJobCliForLlm,
    getJobLlmForCli
} = await import(pathToFileURL(modulePath).href);
const { EnvironmentController } = await import(pathToFileURL(environmentModulePath).href);

function createApp() {
    const calls = [];
    return {
        calls,
        data: { configs: {} },
        escapeHtml(value) {
            return String(value)
                .replaceAll('&', '&amp;')
                .replaceAll('<', '&lt;')
                .replaceAll('>', '&gt;')
                .replaceAll('"', '&quot;');
        },
        formatRelativeTime() { return 'just now'; },
        async apiCall(url, method = 'GET', body = null) {
            calls.push({ url, method, body });
            return { success: true, message: 'Queued.' };
        },
        showToast() {},
        showError(error) { throw new Error(error); }
    };
}

test('Jobs page separates shared Environment / Workers from Automation rules', () => {
    const controller = new JobController(createApp());
    const html = controller.renderPage();

    assert.match(html, /text-gradient">Jobs/);
    assert.doesNotMatch(html, /Durable local automation/);
    assert.match(html, /Environment \/ Workers/);
    assert.match(html, /same workers shown on the Workers screen/);
    assert.match(html, /Automation rules/);
    assert.match(html, /When workers run/);
    assert.match(html, /Recent runs/);
});

test('Jobs inline automation editor has no duplicate repository or prompt controls', () => {
    const source = readFileSync(modulePath, 'utf8');

    assert.doesNotMatch(source, /id=["']job-project["']/);
    assert.doesNotMatch(source, /id=["']job-prompt["']/);
    assert.match(source, /Runs in the current VibeRails repository/);
    assert.match(source, /includeBase:\s*false/);
});

test('Jobs page warns that execution modes are not security boundaries', () => {
    const controller = new JobController(createApp());
    const html = controller.renderPage();

    assert.match(html, /Jobs are not sandboxed/);
    assert.match(html, /operating-system account's permissions/);
    assert.match(html, /not security boundaries/);
    assert.match(html, /outside the selected repository or clone/);
    assert.match(html, /disposable account or machine/);
});

test('Jobs maps every non-shell picker CLI to the shared LLM enum values', () => {
    const expected = [
        ['codex', 1],
        ['claude', 2],
        ['antigravity', 3],
        ['copilot', 4],
        ['opencode', 6],
        ['glm-5.2', 7],
        ['kimi-k3', 8]
    ];

    for (const [cli, llm] of expected) {
        assert.equal(getJobLlmForCli(cli), llm);
        assert.equal(getJobCliForLlm(llm), cli);
    }
    assert.equal(getJobLlmForCli('shell'), null);
    assert.equal(getJobCliForLlm(5), null);
});

test('Jobs editor does not promise read-only or clone isolation', () => {
    const source = readFileSync(modulePath, 'utf8');

    assert.doesNotMatch(source, /cannot edit files/);
    assert.doesNotMatch(source, /edits a private clone/);
    assert.match(source, /Review only — requests read-only behavior; not a sandbox/);
    assert.match(source, /Isolated write — throwaway clone only; not a sandbox/);
    assert.match(source, /not an operating-system or process sandbox/);
    assert.match(source, /outside the clone/);
});

test('Jobs worker status stays hidden until at least one job exists', () => {
    const controller = new JobController(createApp());
    const worker = { hidden: false, dataset: {}, innerHTML: '' };
    controller.root = {
        querySelector(selector) {
            return selector === '[data-jobs-worker]' ? worker : null;
        }
    };
    controller.jobs = [];
    controller.renderWorker();

    assert.equal(worker.hidden, true);
    assert.equal(worker.innerHTML, '');

    controller.jobs = [{ id: 1 }];
    controller.workerStatus = { running: false, installed: false };
    controller.renderWorker();

    assert.equal(worker.hidden, false);
    assert.match(worker.innerHTML, /Jobs worker is off/);
});

test('Jobs renderer escapes job data and shows configured triggers', () => {
    const app = createApp();
    const controller = new JobController(app);
    const list = { innerHTML: '' };
    const count = { textContent: '' };
    controller.root = {
        querySelector(selector) {
            if (selector === '[data-jobs-list]') return list;
            if (selector === '[data-jobs-count]') return count;
            return null;
        }
    };
    controller.jobs = [{
        id: 7,
        name: '<Security review>',
        projectPath: 'C:\\repo&one',
        llm: 1,
        prompt: '<script>alert(1)</script>',
        executionMode: 0,
        timeoutMinutes: 30,
        enabled: true,
        triggers: [
            { kind: 0, scheduleKind: 0, intervalMinutes: 15 },
            { kind: 1 },
            { kind: 2 }
        ]
    }];

    controller.renderJobs();

    assert.equal(count.textContent, '1 rule');
    assert.match(list.innerHTML, /&lt;Security review&gt;/);
    assert.match(list.innerHTML, /&lt;script&gt;alert\(1\)&lt;\/script&gt;/);
    assert.doesNotMatch(list.innerHTML, /<script>/);
    assert.match(list.innerHTML, /Every 15 min/);
    assert.match(list.innerHTML, /Pre-commit VCA · non-blocking Job/);
    assert.match(list.innerHTML, /After successful commit/);
    assert.match(list.innerHTML, /Run now/);
});

test('Jobs renderer escapes job ids used in data attributes', () => {
    const controller = new JobController(createApp());
    const list = { innerHTML: '' };
    controller.root = {
        querySelector(selector) {
            if (selector === '[data-jobs-list]') return list;
            return null;
        }
    };
    controller.jobs = [{
        id: '7" data-owned="yes',
        name: 'Review',
        projectPath: '/repo',
        llm: 1,
        prompt: 'Review.',
        executionMode: 0,
        timeoutMinutes: 30,
        enabled: true,
        triggers: []
    }];

    controller.renderJobs();

    assert.doesNotMatch(list.innerHTML, /data-job-id="7" data-owned=/);
    assert.match(list.innerHTML, /data-job-id="7&quot; data-owned=&quot;yes"/);
});

test('Jobs cards display every supported provider instead of falling back to Claude', () => {
    const controller = new JobController(createApp());
    const list = { innerHTML: '' };
    controller.root = {
        querySelector(selector) {
            if (selector === '[data-jobs-list]') return list;
            return null;
        }
    };
    controller.jobs = [
        [1, 'Codex'], [2, 'Claude'], [3, 'Antigravity'], [4, 'Copilot'],
        [6, 'OpenCode'], [7, 'GLM 5.2'], [8, 'Kimi K3']
    ].map(([llm, name], index) => ({
        id: index + 1,
        name: `${name} review`,
        projectPath: '/repo',
        llm,
        prompt: 'Review.',
        executionMode: 0,
        timeoutMinutes: 30,
        enabled: true,
        triggers: []
    }));

    controller.renderJobs();

    for (const name of ['Codex', 'Claude', 'Antigravity', 'Copilot', 'OpenCode', 'GLM 5.2', 'Kimi K3']) {
        assert.match(list.innerHTML, new RegExp(`>${name}<`));
    }
});

test('Rules automation escapes job ids used in data attributes', async () => {
    const app = createApp();
    app.data = { configs: { rootPath: '/repo' }, isInGit: true };
    app.apiCall = async () => ({
        jobs: [{
            id: '9" data-owned="yes',
            name: 'Review',
            llm: 1,
            enabled: true,
            triggers: [{ kind: 1 }]
        }]
    });
    const host = { innerHTML: '', querySelectorAll: () => [] };
    const root = {
        querySelector(selector) {
            return selector === '[data-jobs-automation-host]' ? host : null;
        }
    };
    const controller = new JobController(app);

    await controller.attachRulesAutomation(root);

    assert.doesNotMatch(host.innerHTML, /data-rules-job-run="9" data-owned=/);
    assert.match(host.innerHTML, /data-rules-job-run="9&quot; data-owned=&quot;yes"/);
});

test('New Jobs derive repository, LLM, and prompt from the current worker context', async () => {
    const app = createApp();
    app.data = { configs: { rootPath: '/derived/current-repo' }, isInGit: true };
    app.closeModal = () => {};
    const controller = new JobController(app);
    controller.environments = [{
        id: 42,
        name: 'OpenCode review',
        cli: 'opencode',
        customPrompt: 'Perform the worker-owned security review.'
    }];
    controller.refreshAll = async () => {};

    const submit = { disabled: false, textContent: '' };
    const controls = new Map([
        ['[type="submit"]', submit],
        ['#job-llm-selection', { value: 'env:42:opencode' }],
        ['#job-trigger-schedule', { checked: false }],
        ['#job-trigger-vca', { checked: true }],
        ['#job-trigger-commit', { checked: false }],
        ['#job-name', { value: 'OpenCode review' }],
        ['#job-mode', { value: '0' }],
        ['#job-timeout', { value: '30' }],
        ['#job-enabled', { checked: false }]
    ]);
    const form = {
        querySelector(selector) { return controls.get(selector) || null; },
        querySelectorAll() { return []; }
    };

    await controller.saveJob({ preventDefault() {}, currentTarget: form }, null);

    assert.equal(app.calls.length, 1);
    assert.equal(app.calls[0].url, '/api/v1/jobs');
    assert.equal(app.calls[0].method, 'POST');
    assert.equal(app.calls[0].body.projectPath, '/derived/current-repo');
    assert.equal(app.calls[0].body.llm, 6);
    assert.equal(app.calls[0].body.environmentId, 42);
    assert.equal(app.calls[0].body.prompt, 'Perform the worker-owned security review.');
    assert.deepEqual(app.calls[0].body.triggers, [{ kind: 1 }]);
});

test('New rules reject base CLIs while an existing legacy base Job retains cached worker fields', () => {
    const app = createApp();
    app.data = { configs: { rootPath: '/derived/current-repo' }, isInGit: true };
    const controller = new JobController(app);
    const controls = new Map([
        ['#job-llm-selection', { value: 'base:kimi-k3' }],
        ['#job-trigger-schedule', { checked: false }],
        ['#job-trigger-vca', { checked: false }],
        ['#job-trigger-commit', { checked: false }],
        ['#job-name', { value: 'Kimi review' }],
        ['#job-mode', { value: '0' }],
        ['#job-timeout', { value: '30' }],
        ['#job-enabled', { checked: true }]
    ]);
    const form = {
        querySelector(selector) { return controls.get(selector) || null; },
        querySelectorAll() { return []; }
    };

    assert.throws(
        () => controller.captureEditorState(form, { validate: true }),
        /Choose a custom Environment \/ Worker/
    );

    const legacyJob = {
        id: 99,
        name: 'Legacy Kimi review',
        projectPath: '/old/repository',
        llm: 8,
        environmentId: null,
        prompt: 'Keep this cached legacy security-review prompt.',
        executionMode: 0,
        timeoutMinutes: 30,
        enabled: true,
        triggers: []
    };
    controller.activeEditorJob = legacyJob;
    controller.activeEditorSource = legacyJob;
    const payload = controller.captureEditorState(form, { validate: true });

    assert.equal(payload.llm, 8);
    assert.equal(payload.environmentId, null);
    assert.equal(payload.prompt, legacyJob.prompt);
    assert.equal(payload.projectPath, '/derived/current-repo');
});

test('Jobs trigger labels distinguish non-blocking pre-commit VCA from post-successful-commit', () => {
    const controller = new JobController(createApp());

    assert.equal(controller.formatTrigger({ kind: 1 }), 'Pre-commit VCA · non-blocking Job');
    assert.equal(controller.formatTrigger({ kind: 2 }), 'After successful commit');
});

test('environmentChanged rerenders the shared worker table, automation cards, and active picker', async () => {
    const app = createApp();
    const nextEnvironments = [{ id: 73, name: 'Updated worker', cli: 'codex', customPrompt: 'Review security.' }];
    app.data.environments = nextEnvironments;
    const controller = new JobController(app);
    const calls = [];
    controller.renderEnvironments = () => calls.push('workers');
    controller.renderJobs = () => calls.push('rules');
    controller.refreshEditorWorkerPicker = environmentId => calls.push(`picker:${environmentId}`);

    await controller.environmentChanged({ selectedEnvironmentId: 73 });

    assert.equal(controller.environments, nextEnvironments);
    assert.deepEqual(calls, ['workers', 'rules', 'picker:73']);
});

test('Jobs destroys its Tom Select picker on Escape and navigation unload', (t) => {
    const originalDocument = globalThis.document;
    const originalWindow = globalThis.window;
    t.after(() => {
        globalThis.document = originalDocument;
        globalThis.window = originalWindow;
    });

    let escapeHandler = null;
    const keyboardTarget = {
        addEventListener(type, handler, capture) {
            if (type === 'keydown' && capture === true) escapeHandler = handler;
        },
        removeEventListener(type, handler, capture) {
            if (type === 'keydown' && capture === true && escapeHandler === handler) escapeHandler = null;
        }
    };
    globalThis.document = keyboardTarget;
    globalThis.window = keyboardTarget;

    const controller = new JobController(createApp());
    let escapeDestroyCount = 0;
    controller.registerEditorModalCleanup({
        tomselect: { destroy() { escapeDestroyCount += 1; } }
    });
    const handlerForEscape = escapeHandler;

    handlerForEscape({ key: 'Escape' });
    assert.equal(escapeDestroyCount, 1);
    assert.equal(escapeHandler, null);

    let unloadDestroyCount = 0;
    controller.registerEditorModalCleanup({
        tomselect: { destroy() { unloadDestroyCount += 1; } }
    });
    controller.unload();

    assert.equal(unloadDestroyCount, 1);
    assert.equal(escapeHandler, null);
});

function installEnvironmentFormDom(extraElements = {}) {
    const listeners = new Map();
    const closeListeners = new Set();
    const form = {
        addEventListener(type, handler) { listeners.set(type, handler); }
    };
    const closeButton = {
        addEventListener(type, handler) {
            if (type === 'click') closeListeners.add(handler);
        },
        removeEventListener(type, handler) {
            if (type === 'click') closeListeners.delete(handler);
        }
    };
    const modal = {};
    const modalContainer = {
        firstElementChild: modal,
        contains(node) { return node === form; },
        querySelectorAll(selector) {
            return selector === '[data-action="close-modal"]' ? [closeButton] : [];
        }
    };
    const keydownListeners = new Set();
    const document = {
        getElementById(id) {
            if (Object.hasOwn(extraElements, id)) return extraElements[id];
            if (id === 'modal-container') return modalContainer;
            if (id === 'env-form') return form;
            return null;
        },
        querySelector(selector) {
            return selector === '[data-cli-settings-slot]' ? { innerHTML: '' } : null;
        },
        addEventListener(type, handler, capture) {
            if (type === 'keydown' && capture === true) keydownListeners.add(handler);
        },
        removeEventListener(type, handler, capture) {
            if (type === 'keydown' && capture === true) keydownListeners.delete(handler);
        }
    };

    return { document, form, modalContainer, keydownListeners, closeListeners, listeners };
}

function createEnvironmentControllerForForm(appOverrides = {}) {
    const app = {
        currentView: 'jobs',
        data: { environments: [] },
        escapeHtml: value => String(value),
        showModal() {},
        closeModal() {},
        ...appOverrides
    };
    const controller = new EnvironmentController(app);
    controller.buildCliSettingsHtml = () => '';
    controller.bindCliSettingsInteractions = () => {};
    return controller;
}

test('Escape from environment settings restores the unsaved Jobs draft without navigating away', (t) => {
    const originalDocument = globalThis.document;
    const originalWindow = globalThis.window;
    t.after(() => {
        globalThis.document = originalDocument;
        globalThis.window = originalWindow;
    });

    const dom = installEnvironmentFormDom();
    globalThis.document = dom.document;
    globalThis.window = dom.document;
    let closeCount = 0;
    let restoreDraftCount = 0;
    const controller = createEnvironmentControllerForForm({
        closeModal() { closeCount += 1; }
    });

    controller.showEnvironmentForm({
        mode: 'edit',
        env: { name: 'Review profile', cli: 'codex', customArgs: '' },
        onCancel() { restoreDraftCount += 1; }
    });

    const escapeHandler = [...dom.keydownListeners][0];
    let prevented = false;
    let propagationStopped = false;
    escapeHandler({
        key: 'Escape',
        preventDefault() { prevented = true; },
        stopImmediatePropagation() { propagationStopped = true; }
    });

    assert.equal(closeCount, 1);
    assert.equal(restoreDraftCount, 1);
    assert.equal(prevented, true);
    assert.equal(propagationStopped, true, 'the app-level Escape/goBack handler must not run');
    assert.equal(dom.keydownListeners.size, 0);
});

test('Navigation cleanup cannot reopen a stale Jobs draft', (t) => {
    const originalDocument = globalThis.document;
    const originalWindow = globalThis.window;
    t.after(() => {
        globalThis.document = originalDocument;
        globalThis.window = originalWindow;
    });

    const dom = installEnvironmentFormDom();
    globalThis.document = dom.document;
    globalThis.window = dom.document;
    let restoreDraftCount = 0;
    const controller = createEnvironmentControllerForForm();
    controller.showEnvironmentForm({
        mode: 'edit',
        env: { name: 'Review profile', cli: 'codex', customArgs: '' },
        onCancel() { restoreDraftCount += 1; }
    });
    const staleEscapeHandler = [...dom.keydownListeners][0];

    controller.unload();
    staleEscapeHandler({
        key: 'Escape',
        preventDefault() {},
        stopImmediatePropagation() {}
    });

    assert.equal(restoreDraftCount, 0);
    assert.equal(dom.keydownListeners.size, 0);
});

test('An environment settings load cannot replace a newer modal or navigated view', async (t) => {
    const originalDocument = globalThis.document;
    t.after(() => { globalThis.document = originalDocument; });

    const sourceModal = {};
    const modalContainer = { firstElementChild: sourceModal };
    globalThis.document = {
        getElementById(id) { return id === 'modal-container' ? modalContainer : null; }
    };

    const app = {
        currentView: 'jobs',
        data: { environments: [{ id: 7, name: 'Review profile', cli: 'codex' }] }
    };
    const controller = new EnvironmentController(app);
    let resolveSettings;
    controller.loadCliSettings = () => new Promise(resolve => { resolveSettings = resolve; });
    let opened = 0;
    controller.showEnvironmentForm = () => { opened += 1; };

    const pending = controller.editEnvironment('Review profile', {
        onCancel() { throw new Error('A stale draft must not be reopened.'); }
    });
    app.currentView = 'dashboard';
    modalContainer.firstElementChild = {};
    resolveSettings({});

    assert.equal(await pending, false);
    assert.equal(opened, 0);
});

test('Creating an environment uses one trimmed name for both the record and CLI settings', async (t) => {
    const originalDocument = globalThis.document;
    const originalWindow = globalThis.window;
    t.after(() => {
        globalThis.document = originalDocument;
        globalThis.window = originalWindow;
    });

    const cliSelect = { value: 'codex', addEventListener() {} };
    const dom = installEnvironmentFormDom({
        'env-name': { value: '  Nightly review  ' },
        'env-cli': cliSelect
    });
    globalThis.document = dom.document;
    globalThis.window = dom.document;

    const apiCalls = [];
    const settingsNames = [];
    const controller = createEnvironmentControllerForForm({
        async apiCall(url, method, body) { apiCalls.push({ url, method, body }); },
        showError(error) { throw new Error(error); }
    });
    controller.extractCliSettingsPayload = () => ({ model: 'gpt-5.4' });
    controller.buildEnvironmentSavePayload = () => ({ customArgs: '--model gpt-5.4' });
    controller.saveCliSettings = async (_cli, name) => { settingsNames.push(name); };
    controller.refreshEnvironments = async () => {};

    controller.showEnvironmentForm({ mode: 'create', onChanged() {} });
    await dom.listeners.get('submit')({ preventDefault() {} });

    assert.equal(apiCalls.length, 1);
    assert.deepEqual(apiCalls[0], {
        url: '/api/v1/environments',
        method: 'POST',
        body: {
            name: 'Nightly review',
            cli: 'codex',
            customArgs: '--model gpt-5.4'
        }
    });
    assert.deepEqual(settingsNames, ['Nightly review']);
});

test('Run now and enable actions call the durable Jobs API with stable trigger fields', async () => {
    const app = createApp();
    const controller = new JobController(app);
    controller.refreshRuns = async () => {};
    controller.refreshAll = async () => {};
    controller.jobs = [{
        id: 12,
        name: 'Review',
        projectPath: '/repo',
        llm: 1,
        environmentId: null,
        prompt: 'Review changes.',
        executionMode: 0,
        timeoutMinutes: 30,
        enabled: false,
        triggers: [{
            id: 99,
            kind: 0,
            scheduleKind: 1,
            intervalMinutes: null,
            localTime: '09:00',
            daysOfWeekMask: 0,
            timeZoneId: 'America/Chicago',
            nextRunUtc: '2026-07-20T14:00:00Z'
        }]
    }];
    const button = { innerHTML: 'Run now', textContent: '', disabled: false, isConnected: true };

    await controller.runNow(12, button);
    await controller.toggleJob(12, button);

    assert.deepEqual(app.calls[0], {
        url: '/api/v1/jobs/12/run',
        method: 'POST',
        body: null
    });
    assert.equal(app.calls[1].url, '/api/v1/jobs/12');
    assert.equal(app.calls[1].method, 'PUT');
    assert.equal(app.calls[1].body.enabled, true);
    assert.deepEqual(app.calls[1].body.triggers, [{
        kind: 0,
        scheduleKind: 1,
        intervalMinutes: null,
        localTime: '09:00',
        daysOfWeekMask: 0,
        timeZoneId: 'America/Chicago'
    }]);
});

test('Run history exposes cancel only while active and retry after completion', () => {
    const controller = new JobController(createApp());
    const base = {
        id: 'run<&>',
        jobName: 'Security <review>',
        llm: 2,
        triggerKind: 2,
        queuedUtc: '2026-07-19T12:00:00Z',
        startedUtc: '2026-07-19T12:00:00Z',
        endedUtc: '2026-07-19T12:00:10Z'
    };

    const running = controller.renderRunRow({ ...base, status: 1 });
    const failed = controller.renderRunRow({ ...base, status: 3 });

    assert.match(running, /Cancel/);
    assert.doesNotMatch(running, /Retry/);
    assert.match(failed, /Retry/);
    assert.doesNotMatch(failed, /Cancel/);
    assert.match(failed, /Security &lt;review&gt;/);
    assert.doesNotMatch(failed, /run<&>/);
});

test('Opening another run invalidates callbacks queued by the previous modal', async (t) => {
    const intervalCallbacks = [];
    const originalWindow = globalThis.window;
    const originalDocument = globalThis.document;
    t.after(() => {
        globalThis.window = originalWindow;
        globalThis.document = originalDocument;
    });

    globalThis.window = {
        setInterval(callback) {
            intervalCallbacks.push(callback);
            return intervalCallbacks.length;
        },
        clearInterval() {}
    };

    let currentDetail = null;
    globalThis.document = {
        querySelector(selector) {
            return selector === '[data-job-run-detail]' ? currentDetail : null;
        }
    };

    const app = createApp();
    app.showModal = () => {
        const summary = { innerHTML: '', textContent: '' };
        const log = { textContent: '', scrollTop: 0, scrollHeight: 0 };
        const actions = { innerHTML: '', querySelector: () => null };
        currentDetail = {
            querySelector(selector) {
                if (selector === '[data-run-summary]') return summary;
                if (selector === '[data-run-log]') return log;
                if (selector === '[data-run-modal-actions]') return actions;
                if (selector === '.job-run-result') return null;
                return null;
            }
        };
    };
    app.closeModal = () => {};
    app.apiCall = async (url) => {
        app.calls.push({ url });
        if (url.includes('/logs?')) return { logs: [] };
        const runId = url.includes('/run-a') ? 'run-a' : 'run-b';
        return {
            id: runId,
            jobName: runId,
            status: 1,
            workspacePath: '/repo',
            projectPath: '/repo'
        };
    };

    const controller = new JobController(app);
    await controller.openRun('run-a');
    const staleCallback = intervalCallbacks[0];
    await controller.openRun('run-b');
    const callsBeforeStaleTick = app.calls.length;

    await staleCallback();

    assert.equal(app.calls.length, callsBeforeStaleTick, 'the stale callback must not fetch or mutate the new modal');
});

test('An in-flight refresh cannot render after another run replaces its modal', async (t) => {
    const originalWindow = globalThis.window;
    const originalDocument = globalThis.document;
    t.after(() => {
        globalThis.window = originalWindow;
        globalThis.document = originalDocument;
    });

    globalThis.window = {
        setInterval() { return 1; },
        clearInterval() {}
    };

    const modalDetails = [];
    let currentDetail = null;
    globalThis.document = {
        querySelector(selector) {
            return selector === '[data-job-run-detail]' ? currentDetail : null;
        }
    };

    const app = createApp();
    app.showModal = () => {
        const summary = { innerHTML: 'Loading run…', textContent: '' };
        const log = { textContent: '', scrollTop: 0, scrollHeight: 0 };
        const actions = { innerHTML: '', querySelector: () => null };
        currentDetail = {
            summary,
            log,
            actions,
            querySelector(selector) {
                if (selector === '[data-run-summary]') return summary;
                if (selector === '[data-run-log]') return log;
                if (selector === '[data-run-modal-actions]') return actions;
                if (selector === '.job-run-result') return null;
                return null;
            }
        };
        modalDetails.push(currentDetail);
    };
    app.closeModal = () => {};

    let resolveRunA;
    let resolveLogsA;
    const runAResponse = new Promise(resolve => { resolveRunA = resolve; });
    const runALogs = new Promise(resolve => { resolveLogsA = resolve; });
    app.apiCall = (url) => {
        if (url.includes('/run-a/logs?')) return runALogs;
        if (url.includes('/run-a')) return runAResponse;
        if (url.includes('/run-b/logs?')) {
            return Promise.resolve({ logs: [{ sequence: 1, content: 'run B log' }] });
        }
        return Promise.resolve({
            id: 'run-b',
            jobName: 'run-b',
            status: 1,
            workspacePath: '/repo',
            projectPath: '/repo'
        });
    };

    const controller = new JobController(app);
    const openingRunA = controller.openRun('run-a');
    const runADetail = modalDetails[0];

    await controller.openRun('run-b');
    const runBDetail = modalDetails[1];
    const runBSummary = runBDetail.summary.innerHTML;
    const runBLog = runBDetail.log.textContent;

    resolveRunA({
        id: 'run-a',
        jobName: 'run-a',
        status: 1,
        workspacePath: '/repo',
        projectPath: '/repo'
    });
    resolveLogsA({ logs: [{ sequence: 1, content: 'stale run A log' }] });
    await openingRunA;

    assert.equal(runADetail.summary.innerHTML, 'Loading run…', 'stale data must stop before rendering');
    assert.equal(runADetail.log.textContent, '');
    assert.equal(runBDetail.summary.innerHTML, runBSummary);
    assert.equal(runBDetail.log.textContent, runBLog);
    assert.match(runBDetail.summary.innerHTML, /run-b/);
    assert.equal(runBDetail.log.textContent, 'run B log');
});
