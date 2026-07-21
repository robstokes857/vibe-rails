import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const modulePath = path.resolve('VibeRails/wwwroot/js/modules/jobs-controller.js');
const { JobController } = await import(pathToFileURL(modulePath).href);

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

test('Jobs page uses the shared shimmering title and explains its automation triggers', () => {
    const controller = new JobController(createApp());
    const html = controller.renderPage();

    assert.match(html, /text-gradient">Jobs/);
    assert.doesNotMatch(html, /Durable local automation/);
    assert.match(html, /schedule/);
    assert.match(html, /VCA checks/);
    assert.match(html, /successful commits/);
    assert.match(html, /Run now/i);
    assert.match(html, /Recent runs/);
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

    assert.equal(count.textContent, '1 job');
    assert.match(list.innerHTML, /&lt;Security review&gt;/);
    assert.match(list.innerHTML, /&lt;script&gt;alert\(1\)&lt;\/script&gt;/);
    assert.doesNotMatch(list.innerHTML, /<script>/);
    assert.match(list.innerHTML, /Every 15 min/);
    assert.match(list.innerHTML, /Every VCA check/);
    assert.match(list.innerHTML, /Every commit/);
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
