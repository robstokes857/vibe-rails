import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const modulePath = path.resolve('VibeRails/wwwroot/js/modules/jobs-controller.js');
const environmentModulePath = path.resolve('VibeRails/wwwroot/js/modules/environment-controller.js');
const indexPath = path.resolve('VibeRails/wwwroot/index.html');
const rulesWorkspacePath = path.resolve('VibeRails/wwwroot/js/modules/rules-workspace.js');
const ruleControllerPath = path.resolve('VibeRails/wwwroot/js/modules/rule-controller.js');
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

test('Automation page makes automations primary and explains its active-instance runtime', () => {
    const controller = new JobController(createApp());
    const html = controller.renderPage();

    assert.match(html, /text-gradient">Automation/);
    assert.match(html, /Automations run only while VibeRails is open/);
    assert.match(html, /single active instance claims each run/);
    assert.match(html, /id="jobs-list-title">Automations/);
    assert.match(html, /Run history/);
    assert.doesNotMatch(html, /Run jobs while VibeRails is closed/);
    assert.doesNotMatch(html, /data-jobs-scheduler-status/);
    assert.doesNotMatch(html, /data-job-environments-table/);
});

test('Navigation calls the feature Automation and the Rules page stays out of it', () => {
    const index = readFileSync(indexPath, 'utf8');
    const rulesWorkspace = readFileSync(rulesWorkspacePath, 'utf8');
    const ruleController = readFileSync(ruleControllerPath, 'utf8');
    const jobsController = readFileSync(modulePath, 'utf8');

    assert.match(index, /title="Scheduled and Git-triggered Automations"/);
    assert.doesNotMatch(index, />Open Jobs<|previews never queue jobs|>Jobs<\/span>/i);
    // The Rules page no longer hosts an Automation section — the standalone
    // Automation page owns the whole feature.
    assert.doesNotMatch(index, /data-rules-section="automate"|data-jobs-automation-host/);
    assert.doesNotMatch(rulesWorkspace, /syncAutomate|automation-host/);
    assert.doesNotMatch(jobsController, /attachRulesAutomation/);
    assert.doesNotMatch(ruleController, /post-commit Jobs/);
});

test('Automation inline editor derives repository and prompt from its Environment', () => {
    const source = readFileSync(modulePath, 'utf8');

    assert.doesNotMatch(source, /id=["']job-project["']/);
    assert.doesNotMatch(source, /id=["']job-prompt["']/);
    assert.match(source, /Runs in the current VibeRails repository/);
    assert.match(source, /includeBase:\s*false/);
    assert.match(source, />Environment<\/label>/);
    assert.doesNotMatch(source, /Environment \/ Worker/);
});

test('Automation maps every non-shell Environment CLI to the shared LLM enum values', () => {
    const expected = [
        ['codex', 1],
        ['claude', 2],
        ['antigravity', 3],
        ['copilot', 4],
        ['opencode', 6],
        ['glm-5.2', 7]
    ];

    for (const [cli, llm] of expected) {
        assert.equal(getJobLlmForCli(cli), llm);
        assert.equal(getJobCliForLlm(llm), cli);
    }
    assert.equal(getJobLlmForCli('shell'), null);
    assert.equal(getJobCliForLlm(5), null);
});

test('Automation editor does not promise read-only or clone isolation', () => {
    // Execution modes (read-only / throwaway-clone) were removed: an Automation runs its Environment
    // directly with the user's own permissions. The editor must never claim otherwise, so this
    // guards the absence of every isolation promise rather than any particular wording.
    const source = readFileSync(modulePath, 'utf8');

    assert.doesNotMatch(source, /cannot edit files/);
    assert.doesNotMatch(source, /edits a private clone/);
    assert.doesNotMatch(source, /read-only behavior/i);
    assert.doesNotMatch(source, /throwaway clone/i);
    assert.doesNotMatch(source, /isolated write/i);
    // The execution-mode picker went with them; nothing may re-offer one.
    assert.doesNotMatch(source, /id=["']job-mode["']/);
    assert.doesNotMatch(source, /executionMode/);
});

test('Automation renderer escapes data and shows Environment, triggers, and next run', () => {
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
    controller.environments = [{
        id: 42,
        name: '<script>alert(1)</script>',
        cli: 'codex',
        customPrompt: 'Review changes.'
    }];
    const nextRunUtc = new Date(Date.now() + (12 * 60_000)).toISOString();
    controller.jobs = [{
        id: 7,
        name: '<Security review>',
        projectPath: 'C:\\repo&one',
        llm: 1,
        environmentId: 42,
        environmentName: 'Review Environment',
        timeoutMinutes: 30,
        enabled: true,
        triggers: [
            { kind: 0, scheduleKind: 0, intervalMinutes: 15, nextRunUtc },
            { kind: 2 },
            { kind: 3 }
        ]
    }];

    controller.renderJobs();

    assert.equal(count.textContent, '1 automation');
    assert.match(list.innerHTML, /&lt;Security review&gt;/);
    assert.match(list.innerHTML, /&lt;script&gt;alert\(1\)&lt;\/script&gt;/);
    assert.doesNotMatch(list.innerHTML, /<script>/);
    assert.match(list.innerHTML, />Environment</);
    assert.match(list.innerHTML, /Every 15 min/);
    assert.match(list.innerHTML, /After successful commit/);
    assert.match(list.innerHTML, /Manual/);
    assert.match(list.innerHTML, /Next run/);
    assert.match(list.innerHTML, /in 12 min/);
    assert.match(list.innerHTML, /Run now/);
});

test('Automation renderer escapes ids used in data attributes', () => {
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

test('Automation rows display every supported Environment provider', () => {
    const controller = new JobController(createApp());
    const list = { innerHTML: '' };
    controller.root = {
        querySelector(selector) {
            if (selector === '[data-jobs-list]') return list;
            return null;
        }
    };
    const providers = [
        [1, 'Codex'], [2, 'Claude'], [3, 'Antigravity'], [4, 'Copilot'],
        [6, 'OpenCode'], [7, 'GLM 5.2']
    ];
    controller.environments = providers.map(([llm, name], index) => ({
        id: index + 1,
        name: `${name} Environment`,
        cli: getJobCliForLlm(llm),
        customPrompt: 'Review.'
    }));
    controller.jobs = providers.map(([llm, name], index) => ({
        id: index + 1,
        name: `${name} review`,
        projectPath: '/repo',
        llm,
        environmentId: index + 1,
        timeoutMinutes: 30,
        enabled: true,
        triggers: []
    }));

    controller.renderJobs();

    for (const name of ['Codex', 'Claude', 'Antigravity', 'Copilot', 'OpenCode', 'GLM 5.2']) {
        assert.match(list.innerHTML, new RegExp(`>${name}<`));
    }
});

test('New Automations derive repository, LLM, and prompt from the current Environment', async () => {
    const app = createApp();
    app.data = { configs: { rootPath: '/derived/current-repo' }, isInGit: true };
    app.closeModal = () => {};
    const controller = new JobController(app);
    controller.environments = [{
        id: 42,
        name: 'OpenCode review',
        cli: 'opencode',
        customPrompt: 'Perform the Environment-owned security review.'
    }];
    controller.refreshAll = async () => {};

    const submit = { disabled: false, textContent: '' };
    const controls = new Map([
        ['[type="submit"]', submit],
        ['#job-llm-selection', { value: 'env:42:opencode' }],
        ['#job-trigger-schedule', { checked: false }],
        ['#job-trigger-commit', { checked: true }],
        ['#job-name', { value: 'OpenCode review' }],
        ['#job-timeout', { value: '30' }],
        ['#job-enabled', { checked: false }],
        ['#job-launch-minimized', { checked: true }]
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
    assert.equal(app.calls[0].body.prompt, 'Perform the Environment-owned security review.');
    assert.equal(app.calls[0].body.launchMinimized, true);
    assert.deepEqual(app.calls[0].body.triggers, [{ kind: 2 }]);
});

test('Automations require a saved Environment and do not retain legacy base-CLI support', () => {
    const app = createApp();
    app.data = { configs: { rootPath: '/derived/current-repo' }, isInGit: true };
    const controller = new JobController(app);
    const controls = new Map([
        ['#job-llm-selection', { value: 'base:opencode' }],
        ['#job-trigger-schedule', { checked: false }],
        ['#job-trigger-commit', { checked: false }],
        ['#job-name', { value: 'Legacy review' }],
        ['#job-timeout', { value: '30' }],
        ['#job-enabled', { checked: true }]
    ]);
    const form = {
        querySelector(selector) { return controls.get(selector) || null; },
        querySelectorAll() { return []; }
    };

    assert.throws(
        () => controller.captureEditorState(form, { validate: true }),
        /Choose an Environment/
    );

    const legacyJob = {
        id: 99,
        name: 'Legacy review',
        projectPath: '/old/repository',
        llm: 6,
        environmentId: null,
        prompt: 'Keep this cached legacy security-review prompt.',
        executionMode: 0,
        timeoutMinutes: 30,
        enabled: true,
        triggers: []
    };
    controller.activeEditorJob = legacyJob;
    controller.activeEditorSource = legacyJob;
    assert.throws(
        () => controller.captureEditorState(form, { validate: true }),
        /Choose an Environment/
    );

    const source = readFileSync(modulePath, 'utf8');
    assert.doesNotMatch(source, /default \(legacy\)/);
    assert.doesNotMatch(source, /isLegacyBase/);
});

test('Automation trigger labels cover every live kind and degrade the retired VCA kind to Manual', () => {
    const controller = new JobController(createApp());

    assert.equal(controller.formatTrigger({ kind: 2 }), 'After successful commit');
    assert.equal(controller.formatTrigger({ kind: 3 }), 'Manual');
    assert.equal(
        controller.formatTrigger({ kind: 0, scheduleKind: 0, intervalMinutes: 15 }),
        'Every 15 min');
    assert.equal(
        controller.formatTrigger({ kind: 0, scheduleKind: 1, localTime: '09:00' }),
        'Daily 09:00');

    // Kind 1 was the pre-commit VCA trigger. The value is retired but rows persisted before its
    // removal must still render as something sane instead of throwing or printing "undefined".
    assert.equal(controller.formatTrigger({ kind: 1 }), 'Manual');
});

test('Automation formats future runs as minutes, hours, tomorrow, or a locale date', () => {
    const controller = new JobController(createApp());
    const now = new Date(2026, 6, 26, 10, 0, 0);

    assert.equal(
        controller.formatFutureTime(new Date(now.getTime() + (12 * 60_000)), now.getTime()),
        'in 12 min');
    assert.equal(
        controller.formatFutureTime(new Date(now.getTime() + (3 * 3_600_000)), now.getTime()),
        'in 3 hr');
    // Floor, not ceil: 1h05m must read "in 1 hr", never the overstated "in 2 hr".
    assert.equal(
        controller.formatFutureTime(new Date(now.getTime() + (65 * 60_000)), now.getTime()),
        'in 1 hr');
    // Ceil crosses to 60 minutes just before the hour; the hour formatter must never emit zero.
    assert.equal(
        controller.formatFutureTime(new Date(now.getTime() + (59 * 60_000) + 1), now.getTime()),
        'in 1 hr');

    const tomorrow = new Date(2026, 6, 27, 9, 30, 0);
    assert.match(controller.formatFutureTime(tomorrow, now.getTime()), /^Tomorrow /);

    const later = new Date(2026, 7, 4, 9, 30, 0);
    assert.equal(
        controller.formatFutureTime(later, now.getTime()),
        later.toLocaleString(undefined, {
            month: 'short',
            day: 'numeric',
            hour: 'numeric',
            minute: '2-digit'
        }));
});

test('The poll keeps Next run honest: it refetches jobs, and identical markup skips the DOM', async () => {
    const app = createApp();
    app.data = { configs: { rootPath: '/repo' }, isInGit: true };
    const served = [];
    app.apiCall = async (url) => {
        served.push(url);
        return { jobs: [{ id: 1, name: 'Nightly', llm: 1, environmentId: 42, timeoutMinutes: null, enabled: true, triggers: [] }] };
    };
    const controller = new JobController(app);
    controller.environments = [{ id: 42, name: 'Env', cli: 'codex', customPrompt: 'Go.' }];
    let assignments = 0;
    const list = {
        _html: '',
        get innerHTML() { return this._html; },
        set innerHTML(value) { this._html = value; assignments += 1; }
    };
    controller.root = {
        querySelector(selector) {
            return selector === '[data-jobs-list]' ? list : null;
        }
    };

    await controller.refreshJobs({ quiet: true });
    assert.equal(served.length, 1);
    assert.match(served[0], /\/api\/v1\/jobs\?projectPath=/);
    assert.equal(assignments, 1);
    assert.match(list.innerHTML, /Nightly/);

    // Same data → same markup → the DOM must not be touched again (a 5s poll that
    // rewrites innerHTML wipes hover/focus and any busy Run-now button).
    await controller.refreshJobs({ quiet: true });
    assert.equal(served.length, 2);
    assert.equal(assignments, 1);

    // A stale cache must never survive a page rebuild: after the reset, the same
    // markup renders again over the loading placeholder.
    controller._lastJobsListHtml = null;
    controller.renderJobs();
    assert.equal(assignments, 2);
});

test('environmentChanged rerenders automations and the active Environment picker', async () => {
    const app = createApp();
    const nextEnvironments = [{ id: 73, name: 'Updated Environment', cli: 'codex', customPrompt: 'Review security.' }];
    app.data.environments = nextEnvironments;
    const controller = new JobController(app);
    const calls = [];
    controller.renderJobs = () => calls.push('automations');
    controller.refreshEditorEnvironmentPicker = environmentId => calls.push(`picker:${environmentId}`);

    await controller.environmentChanged({ selectedEnvironmentId: 73 });

    assert.equal(controller.environments, nextEnvironments);
    assert.deepEqual(calls, ['automations', 'picker:73']);
});

test('Automation destroys its Tom Select picker on Escape and navigation unload', (t) => {
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

test('Escape from Environment settings restores the unsaved Automation draft without navigating away', (t) => {
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

test('Navigation cleanup cannot reopen a stale Automation draft', (t) => {
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
            customArgs: '--model gpt-5.4',
            // environment-controller.js always sends an explicit visibility flag on create.
            hidden: false
        }
    });
    assert.deepEqual(settingsNames, ['Nightly review']);
});

test('Run now and enable actions call the durable Automation API with Environment-owned fields', async () => {
    const app = createApp();
    const controller = new JobController(app);
    controller.refreshRuns = async () => {};
    controller.refreshAll = async () => {};
    controller.environments = [{
        id: 42,
        name: 'Review Environment',
        cli: 'codex',
        customPrompt: 'Review changes.'
    }];
    controller.jobs = [{
        id: 12,
        name: 'Review',
        projectPath: '/repo',
        llm: 1,
        environmentId: 42,
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
    assert.equal(app.calls[1].body.environmentId, 42);
    assert.equal(app.calls[1].body.prompt, 'Review changes.');
    assert.deepEqual(app.calls[1].body.triggers, [{
        kind: 0,
        scheduleKind: 1,
        intervalMinutes: null,
        localTime: '09:00',
        daysOfWeekMask: 0,
        timeZoneId: 'America/Chicago'
    }]);
});

test('Run history exposes stop only while active and run-again after completion', () => {
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

    const running = controller.renderHistoryRow({ ...base, status: 1 });
    const failed = controller.renderHistoryRow({ ...base, status: 3 });

    // Row actions are icon-only: "Replay" and "Retry" both read as "run this again", so
    // the labels moved into tooltips. Assert the action hook, not the rendered wording.
    assert.match(running, /data-history-action="stop"/);
    assert.doesNotMatch(running, /data-history-action="run-again"/);
    assert.match(failed, /data-history-action="run-again"/);
    assert.doesNotMatch(failed, /data-history-action="stop"/);

    // Watching the recording is offered on every row, whatever the run's status.
    assert.match(running, /job-run-watch/);
    assert.match(failed, /job-run-watch/);

    // A live run has no delete control and its checkbox is disabled, so "select all" can never
    // arm a delete the server is going to refuse.
    assert.doesNotMatch(running, /data-history-action="delete"/);
    assert.match(running, /data-run-select="run&lt;&amp;&gt;" disabled/);
    assert.match(failed, /data-history-action="delete"/);

    assert.doesNotMatch(failed, /run<&>/);
});

test('The run-history table lists one row per automation, not one per run', () => {
    const controller = new JobController(createApp());
    controller.runSummaries = [{
        jobId: 4,
        jobName: 'Security <review>',
        totalRuns: 27,
        activeRuns: 1,
        lastRunId: 'run-1',
        lastStatus: 1,
        lastTriggerKind: 0,
        lastLlm: 2,
        lastEnvironmentName: 'Review env',
        lastQueuedUtc: '2026-07-19T12:00:00Z',
        lastStartedUtc: '2026-07-19T12:00:00Z',
        lastEndedUtc: null,
        lastExitCode: null,
        lastErrorMessage: null
    }];

    const html = controller.renderRunsHtml();

    // The whole point of the change: 27 runs collapse to a single row carrying the count.
    assert.equal(html.match(/<tr>/g).length, 2); // header + one automation
    assert.match(html, /27/);
    assert.match(html, /data-job-action="view-history" data-job-id="4"/);
    assert.match(html, /1 active/);

    // The name reaches the DOM here, so escaping is this row's responsibility.
    assert.match(html, /Security &lt;review&gt;/);
    assert.doesNotMatch(html, /Security <review>/);
});

test('Run history paginates and states the visible range versus the total', () => {
    const controller = new JobController(createApp());
    controller.historyPage = 2;
    controller.historyPageSize = 50;
    controller.historyTotalRuns = 52;
    controller.historyRuns = [
        {
            id: 'run-51', status: 2, triggerKind: 0,
            queuedUtc: '2026-07-19T12:00:00Z', startedUtc: '2026-07-19T12:00:00Z', endedUtc: '2026-07-19T12:00:10Z'
        },
        {
            id: 'run-52', status: 3, triggerKind: 2,
            queuedUtc: '2026-07-19T11:00:00Z', startedUtc: '2026-07-19T11:00:00Z', endedUtc: '2026-07-19T11:00:05Z'
        }
    ];
    const root = {
        innerHTML: '',
        querySelector() { return null; },
        querySelectorAll() { return []; }
    };

    controller.renderHistory(root);

    assert.match(root.innerHTML, /Showing <strong>51–52<\/strong> of <strong>52<\/strong> runs/);
    assert.match(root.innerHTML, /Page 2 of 2/);
    assert.match(root.innerHTML, /data-history-page="1"/);
    assert.match(root.innerHTML, /data-history-page="3" disabled/);
});

test('A late history response cannot replace a newer automation modal', async t => {
    const originalDocument = globalThis.document;
    t.after(() => { globalThis.document = originalDocument; });

    const app = createApp();
    const pending = new Map();
    let currentRoot = null;
    app.showModal = (_title, html) => {
        const generation = html.match(/data-history-generation="(\d+)"/)?.[1];
        currentRoot = {
            dataset: { historyGeneration: generation },
            innerHTML: '',
            addEventListener() {},
            querySelector() { return null; },
            querySelectorAll() { return []; }
        };
    };
    app.apiCall = url => new Promise(resolve => {
        const jobId = new URL(`http://localhost${url}`).searchParams.get('jobId');
        pending.set(jobId, resolve);
    });
    globalThis.document = {
        querySelector(selector) {
            if (!currentRoot) return null;
            if (selector === '[data-job-history]') return currentRoot;
            const requested = selector.match(/data-history-generation="(\d+)"/)?.[1];
            return requested === currentRoot.dataset.historyGeneration ? currentRoot : null;
        }
    };

    const controller = new JobController(app);
    controller.runSummaries = [
        { jobId: 1, jobName: 'First' },
        { jobId: 2, jobName: 'Second' }
    ];
    const run = id => ({
        id, status: 2, triggerKind: 0,
        queuedUtc: '2026-07-19T12:00:00Z', startedUtc: '2026-07-19T12:00:00Z', endedUtc: '2026-07-19T12:00:10Z'
    });

    const firstOpen = controller.openRunHistory(1);
    const secondOpen = controller.openRunHistory(2);
    pending.get('2')({ runs: [run('second-run')], totalRuns: 1, page: 1, pageSize: 50 });
    await secondOpen;
    assert.deepEqual(controller.historyRuns.map(item => item.id), ['second-run']);

    pending.get('1')({ runs: [run('first-run')], totalRuns: 1, page: 1, pageSize: 50 });
    await firstOpen;
    assert.equal(controller.historyJobId, 2);
    assert.deepEqual(controller.historyRuns.map(item => item.id), ['second-run']);
    assert.doesNotMatch(currentRoot.innerHTML, /first-run/);
});

test('The five-second poll refreshes an open run-history modal, and identical markup skips the DOM', async t => {
    const originalDocument = globalThis.document;
    t.after(() => { globalThis.document = originalDocument; });

    const root = {
        innerHTML: '',
        writes: 0,
        querySelector() { return null; },
        querySelectorAll() { return []; }
    };
    // Count only real DOM writes so the "no change" case is observable.
    Object.defineProperty(root, 'innerHTML', {
        get() { return this._html || ''; },
        set(value) { this._html = value; this.writes += 1; }
    });
    globalThis.document = { querySelector: () => root };

    const app = createApp();
    app.data = { configs: { rootPath: 'C:\\repo' }, isInGit: true };
    let runStatus = 1; // Running
    app.apiCall = async url => {
        if (url.startsWith('/api/v1/jobs/runs/summary')) return { summaries: [] };
        return {
            runs: [{
                id: 'run-1', status: runStatus, triggerKind: 0,
                queuedUtc: '2026-07-19T12:00:00Z', startedUtc: '2026-07-19T12:00:00Z',
                endedUtc: runStatus === 1 ? null : '2026-07-19T12:00:10Z'
            }],
            totalRuns: 1, page: 1, pageSize: 50
        };
    };

    const controller = new JobController(app);
    controller.historyJobId = 7;

    await controller.refreshRuns({ quiet: true });
    const writesAfterFirst = root.writes;
    assert.match(root.innerHTML, /data-history-action="stop"/);

    // Nothing changed server-side: the poll must not rewrite identical markup and blow away focus.
    await controller.refreshRuns({ quiet: true });
    assert.equal(root.writes, writesAfterFirst);

    // The run finishes. Without the modal refresh this row would still read "Running".
    runStatus = 2;
    await controller.refreshRuns({ quiet: true });
    assert.ok(root.writes > writesAfterFirst);
    assert.doesNotMatch(root.innerHTML, /data-history-action="stop"/);
});

test('A polled history refresh leaves the visible rows alone when the request fails', async t => {
    const originalDocument = globalThis.document;
    t.after(() => { globalThis.document = originalDocument; });

    const root = { innerHTML: 'existing rows', querySelector: () => null, querySelectorAll: () => [] };
    globalThis.document = { querySelector: () => root };

    const app = createApp();
    app.apiCall = async () => { throw new Error('Network unreachable'); };
    const controller = new JobController(app);
    controller.historyJobId = 7;

    await controller.loadHistoryRuns({ quiet: true });

    assert.equal(root.innerHTML, 'existing rows');
});

// Bounded: without the in-flight guard the polled call awaits a request that is never answered,
// so this would hang rather than fail.
test('A polled history refresh never overrides a page the user just navigated to', { timeout: 5000 }, async t => {
    const originalDocument = globalThis.document;
    t.after(() => { globalThis.document = originalDocument; });

    const root = { innerHTML: '', querySelector: () => null, querySelectorAll: () => [] };
    globalThis.document = { querySelector: () => root };

    const app = createApp();
    const requested = [];
    let resolvePending;
    app.apiCall = url => {
        requested.push(Number(new URL(`http://localhost${url}`).searchParams.get('page')));
        return new Promise(resolve => { resolvePending = resolve; });
    };
    const controller = new JobController(app);
    controller.historyJobId = 7;

    // The user clicks "next page"; the poll fires before that response lands. historyPage is still
    // 1 at that moment, so an unguarded poll would request page 1 and win the generation race.
    const navigation = controller.loadHistoryRuns({ page: 2 });
    await controller.loadHistoryRuns({ quiet: true });
    assert.deepEqual(requested, [2]);

    resolvePending({ runs: [], totalRuns: 0, page: 2, pageSize: 50 });
    await navigation;
    assert.equal(controller.historyPage, 2);
});

test('The runs table distinguishes "no project open" from "no runs yet"', () => {
    const app = createApp();
    const controller = new JobController(app);

    app.data = { configs: { rootPath: 'C:\\repo' }, isInGit: true };
    assert.match(controller.renderRunsHtml(), /No automation runs yet/);

    app.data = { configs: { rootPath: '' }, isInGit: true };
    assert.match(controller.renderRunsHtml(), /Open a project/);
});

test('A long initial message is truncated in the hover tooltip but not in the card', () => {
    const controller = new JobController(createApp());
    controller.environments = [{ id: 3, name: 'Env', cli: 'claude' }];
    controller.jobs = [{
        id: 1, name: 'Job', enabled: true, llm: 0, environmentId: 3,
        prompt: 'x'.repeat(1200), triggers: []
    }];

    const html = controller.renderJobsListHtml();
    const title = html.match(/<p title="([^"]*)"/)[1];

    assert.ok(title.length < 500, `tooltip was ${title.length} chars`);
    assert.ok(title.endsWith('…'));
    assert.match(html, new RegExp(`>${'x'.repeat(1200)}</p>`));
});

test('The five-second summary request is scoped to the current repository', async () => {
    const app = createApp();
    app.data = { configs: { rootPath: 'C:\\source\\current repo' }, isInGit: true };
    app.apiCall = async url => {
        app.calls.push({ url });
        return { summaries: [] };
    };
    const controller = new JobController(app);

    await controller.refreshRuns({ quiet: true });

    assert.equal(
        app.calls[0].url,
        '/api/v1/jobs/runs/summary?projectPath=C%3A%5Csource%5Ccurrent%20repo');
});

test('An automation card shows the initial message that carries its logic', () => {
    const controller = new JobController(createApp());
    controller.environments = [{ id: 3, name: 'Review env', cli: 'codex' }];
    controller.jobs = [{
        id: 1,
        name: 'Nightly',
        llm: 2,
        environmentId: 3,
        enabled: true,
        prompt: 'Review the diff for <script>alert(1)</script> issues.',
        triggers: []
    }];

    const html = controller.renderJobsListHtml();

    assert.match(html, /Initial message/);
    assert.match(html, /Review the diff for &lt;script&gt;/);
    assert.doesNotMatch(html, /<script>alert/);

    // An Environment with no initial message must not leave an empty labelled block behind.
    controller.jobs[0].prompt = '   ';
    assert.doesNotMatch(controller.renderJobsListHtml(), /Initial message/);
});

test('Retry is offered on failures but never on a run that already succeeded', () => {
    const controller = new JobController(createApp());
    const base = {
        id: 'run-1',
        jobName: 'Nightly',
        llm: 2,
        triggerKind: 0,
        queuedUtc: '2026-07-19T12:00:00Z',
        startedUtc: '2026-07-19T12:00:00Z',
        endedUtc: '2026-07-19T12:00:10Z'
    };

    // Succeeded is terminal, but re-running work that already landed is not a repair.
    assert.doesNotMatch(controller.renderHistoryRow({ ...base, status: 2 }), /data-history-action="run-again"/);

    for (const status of [3, 4, 5, 6]) {
        assert.match(
            controller.renderHistoryRow({ ...base, status }),
            /data-history-action="run-again"/,
            `status ${status} should be retryable`);
    }
});

test('A run row explains why it ended instead of showing a bare status word', () => {
    const controller = new JobController(createApp());
    const base = {
        id: 'run-1',
        jobName: 'Nightly',
        llm: 2,
        triggerKind: 0,
        queuedUtc: '2026-07-19T12:00:00Z',
        startedUtc: '2026-07-19T12:00:00Z',
        endedUtc: '2026-07-19T12:00:10Z'
    };

    const interrupted = controller.renderHistoryRow({
        ...base,
        status: 6,
        errorMessage: 'The terminal running this Automation is no longer open.'
    });
    assert.match(interrupted, /no longer open/);

    // A non-zero exit is the only detail available when the CLI failed silently.
    const failed = controller.renderHistoryRow({ ...base, status: 3, errorMessage: null, exitCode: 1 });
    assert.match(failed, /exit 1/);

    // Exit 0 beside a success is noise, so the detail line stays off entirely.
    const succeeded = controller.renderHistoryRow({ ...base, status: 2, errorMessage: null, exitCode: 0 });
    assert.doesNotMatch(succeeded, /job-run-detail/);
    assert.doesNotMatch(succeeded, /exit 0/);

    // Escaping still applies to text that now reaches the DOM for the first time.
    const nasty = controller.renderHistoryRow({ ...base, status: 3, errorMessage: '<img src=x>' });
    assert.match(nasty, /&lt;img src=x&gt;/);
    assert.doesNotMatch(nasty, /<img src=x>/);
});

test('An interrupted run reports its duration as approximate', () => {
    const controller = new JobController(createApp());
    const base = {
        id: 'run-1',
        jobName: 'Nightly',
        llm: 2,
        triggerKind: 0,
        queuedUtc: '2026-07-19T12:00:00Z',
        startedUtc: '2026-07-19T12:00:00Z',
        endedUtc: '2026-07-19T12:00:10Z'
    };

    // EndedUTC on a reaped run is when the reaper noticed, not when the process died,
    // so the row must not present that gap as measured runtime.
    const interrupted = controller.renderHistoryRow({ ...base, status: 6 });
    assert.match(interrupted, /~10s/);
    assert.match(interrupted, /Approximate/);

    // Every status that records its own end time keeps an exact duration.
    const succeeded = controller.renderHistoryRow({ ...base, status: 2 });
    assert.match(succeeded, />10s</);
    assert.doesNotMatch(succeeded, /~10s/);
    assert.doesNotMatch(succeeded, /Approximate/);
});

test('The run-history poll skips the DOM when the markup is unchanged', async () => {
    const app = createApp();
    const summaries = [{
        jobId: 9,
        jobName: 'Nightly',
        totalRuns: 3,
        activeRuns: 0,
        lastRunId: 'run-1',
        lastStatus: 2,
        lastTriggerKind: 0,
        lastLlm: 2,
        lastEnvironmentName: null,
        lastQueuedUtc: '2026-07-19T12:00:00Z',
        lastStartedUtc: '2026-07-19T12:00:00Z',
        lastEndedUtc: '2026-07-19T12:00:10Z',
        lastExitCode: null,
        lastErrorMessage: null
    }];
    app.apiCall = async () => ({ summaries });

    const controller = new JobController(app);
    let assignments = 0;
    const target = {
        _html: '',
        get innerHTML() { return this._html; },
        set innerHTML(value) { this._html = value; assignments += 1; }
    };
    controller.root = {
        querySelector(selector) {
            return selector === '[data-job-runs]' ? target : null;
        }
    };

    await controller.refreshRuns({ quiet: true });
    assert.equal(assignments, 1);
    assert.match(target.innerHTML, /Nightly/);

    // A 5s poll that rewrites innerHTML tears out hover, focus, and a just-clicked
    // Cancel/Retry button in the row under the pointer. Same data must not touch the DOM.
    await controller.refreshRuns({ quiet: true });
    assert.equal(assignments, 1);

    // A page rebuild resets the cache, so the same markup renders again over the placeholder.
    controller._lastRunsHtml = null;
    controller.renderRuns();
    assert.equal(assignments, 2);
});

// An Automation run IS a recorded terminal session, so opening one hands off to the shared xterm replay
// player. The ad-hoc polling modal it replaced is gone; these two tests pin that split, which is
// also what makes the old "stale poll callback writes into the replacement modal" bug unreachable.
test('Opening a run that has a recorded session replays it instead of building a modal', async (t) => {
    const originalDocument = globalThis.document;
    t.after(() => { globalThis.document = originalDocument; });
    globalThis.document = { querySelector: () => null };

    const app = createApp();
    const errors = [];
    app.showError = message => errors.push(message);
    app.showModal = () => assert.fail('a run with a session must replay, not open the fallback modal');
    app.apiCall = async () => ({
        id: 'run-a',
        jobName: 'Nightly review',
        status: 2,
        sessionId: 'session-123',
        projectPath: '/repo'
    });

    await new JobController(app).openRun('run-a');

    // showReplayModal needs a real DOM, so it fails here — reaching its error path is itself the
    // proof that openRun took the replay branch rather than rendering the fallback modal.
    assert.equal(errors.length, 1);
});

test('Opening a session-less run shows a static modal that arms no timers', async (t) => {
    const originalWindow = globalThis.window;
    const originalDocument = globalThis.document;
    t.after(() => {
        globalThis.window = originalWindow;
        globalThis.document = originalDocument;
    });

    const timers = [];
    globalThis.window = {
        setInterval(callback) { timers.push(callback); return timers.length; },
        clearInterval() {}
    };
    globalThis.document = { querySelector: () => null };

    const app = createApp();
    const modals = [];
    app.showModal = (title, html) => modals.push({ title, html });
    app.apiCall = async () => ({
        id: 'run<&>',
        jobName: 'Security <review>',
        status: 0,
        sessionId: null,
        projectPath: 'C:\\repo&one'
    });

    await new JobController(app).openRun('run<&>');

    assert.equal(modals.length, 1);
    const { html } = modals[0];
    assert.match(html, /Security &lt;review&gt;/);
    assert.doesNotMatch(html, /<review>/);
    assert.match(html, /Queued/);
    // Queued counts as active: cancellable, not yet retryable.
    assert.match(html, /data-run-cancel/);
    assert.doesNotMatch(html, /data-run-retry/);
    assert.equal(timers.length, 0, 'the modal is static — no unmanaged polling');
});

test('A finished session-less run offers retry instead of cancel', async (t) => {
    const originalWindow = globalThis.window;
    const originalDocument = globalThis.document;
    t.after(() => {
        globalThis.window = originalWindow;
        globalThis.document = originalDocument;
    });
    globalThis.window = { setInterval() { return 1; }, clearInterval() {} };
    globalThis.document = { querySelector: () => null };

    const app = createApp();
    const modals = [];
    app.showModal = (title, html) => modals.push({ title, html });
    app.apiCall = async () => ({
        id: 'run-b',
        jobName: 'Nightly review',
        status: 3,
        sessionId: null,
        errorMessage: 'Claude exited with code 1.',
        projectPath: '/repo'
    });

    await new JobController(app).openRun('run-b');

    const { html } = modals[0];
    assert.match(html, /Failed/);
    assert.match(html, /Claude exited with code 1\./);
    assert.match(html, /data-run-retry/);
    assert.doesNotMatch(html, /data-run-cancel/);
});

test('A time limit is opt-in: unchecked sends null, checked sends the number', () => {
    // The default is no limit — the run lives until its CLI exits or the user closes its window.
    const app = createApp();
    app.data = { configs: { rootPath: '/derived/current-repo' }, isInGit: true };
    const controller = new JobController(app);
    controller.environments = [
        { id: 42, name: 'Nightly Codex', cli: 'codex', customPrompt: 'Review the diff.' }
    ];

    const controls = new Map([
        ['#job-llm-selection', { value: 'env:42' }],
        ['#job-trigger-schedule', { checked: false }],
        ['#job-trigger-commit', { checked: false }],
        ['#job-name', { value: 'Nightly review' }],
        ['#job-timeout', { value: '45' }],
        ['#job-timeout-enabled', { checked: false }],
        ['#job-enabled', { checked: true }]
    ]);
    const form = {
        querySelector(selector) { return controls.get(selector) || null; },
        querySelectorAll() { return []; }
    };

    assert.equal(controller.captureEditorState(form, { validate: true }).timeoutMinutes, null);

    controls.get('#job-timeout-enabled').checked = true;
    assert.equal(controller.captureEditorState(form, { validate: true }).timeoutMinutes, 45);
});

test('Automation rows show the next scheduled run and disabled state', () => {
    const controller = new JobController(createApp());
    const nextRunUtc = new Date(Date.now() + (12 * 60_000)).toISOString();
    controller.environments = [
        { id: 42, name: 'Nightly Codex', cli: 'codex', customPrompt: 'Review the diff.' }
    ];
    controller.jobs = [
        {
            id: 1, name: 'Nightly review', llm: 1, environmentId: 42,
            timeoutMinutes: null, enabled: true,
            triggers: [{ kind: 0, scheduleKind: 1, localTime: '09:00', nextRunUtc }]
        },
        {
            id: 2, name: 'Paused review', llm: 1, environmentId: 42,
            timeoutMinutes: null, enabled: false,
            triggers: [{ kind: 0, scheduleKind: 0, intervalMinutes: 30, nextRunUtc: '2026-07-20T14:30:00Z' }]
        }
    ];
    const list = { innerHTML: '' };
    controller.root = {
        querySelector(selector) {
            return selector === '[data-jobs-list]' ? list : null;
        }
    };

    controller.renderJobs();

    assert.match(list.innerHTML, /Next run/);
    assert.match(list.innerHTML, /in 12 min/);
    assert.match(list.innerHTML, /Paused/);
    assert.doesNotMatch(list.innerHTML, /No time limit/);
});

test('Recipe import confirmation discloses and escapes executable Environment content', (t) => {
    const originalDocument = globalThis.document;
    t.after(() => { globalThis.document = originalDocument; });
    globalThis.document = { getElementById() { return null; } };

    const app = createApp();
    const modals = [];
    app.showModal = (title, html) => modals.push({ title, html });
    const controller = new JobController(app);
    controller.environments = [];

    controller.confirmImportRecipe({
        name: 'Untrusted <Environment>',
        llm: 'Claude',
        cli: 'claude',
        customArgs: '--dangerously-skip-permissions <script>alert("args")</script>',
        prompt: 'Ignore safeguards.</pre><script>alert("prompt")</script>',
        timeoutMinutes: null,
        triggers: []
    });

    assert.equal(modals.length, 1);
    assert.equal(modals[0].title, 'Import recipe');
    assert.match(modals[0].html, /Custom arguments/);
    assert.match(modals[0].html, /--dangerously-skip-permissions/);
    assert.match(modals[0].html, /Initial message/);
    assert.match(modals[0].html, /Ignore safeguards/);
    assert.match(modals[0].html, /approval or sandbox permissions/);
    assert.match(modals[0].html, /Review the Environment content/);
    assert.match(modals[0].html, /import these fields exactly as shown/);
    assert.match(modals[0].html, /created disabled/);
    assert.match(modals[0].html, /&lt;script&gt;/);
    assert.doesNotMatch(modals[0].html, /<script>/);
});

test('The editor defaults the time-limit checkbox off and hides its input', () => {
    const source = readFileSync(modulePath, 'utf8');

    assert.match(source, /id="job-timeout-enabled"/);
    // The checkbox reflects the saved value, so an Automation with no limit renders it unchecked.
    assert.match(source, /\$\{source\.timeoutMinutes \? 'checked' : ''\}/);
    assert.match(source, /data-timeout-field \$\{source\.timeoutMinutes \? '' : 'hidden'\}/);
    // The old copy promised a stop that no longer happens by default.
    assert.doesNotMatch(source, /The run is stopped if it hasn't finished in this long/);
});

test('The editor exposes and persists the Launch minimized option', () => {
    const source = readFileSync(modulePath, 'utf8');

    assert.match(source, /id="job-launch-minimized"/);
    assert.match(source, />Launch minimized</);
    assert.match(source, /launchMinimized: form\.querySelector\('#job-launch-minimized'\)/);
});

test('Automation frontend has no OS scheduler controls or API calls', () => {
    const controller = new JobController(createApp());
    const html = controller.renderPage();
    const source = readFileSync(modulePath, 'utf8');

    assert.match(html, /Automations run only while VibeRails is open/);
    assert.doesNotMatch(html, /install-scheduler|uninstall-scheduler|Task Scheduler|background task/i);
    assert.doesNotMatch(source, /\/api\/v1\/jobs\/scheduler/);
    assert.doesNotMatch(source, /renderSchedulerStatus|setSchedulerInstalled|refreshSchedulerStatus/);
});
