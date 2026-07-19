import test from 'node:test';
import assert from 'node:assert/strict';
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

test('Jobs page explains durable timer, VCA, commit, and manual automation', () => {
    const controller = new JobController(createApp());
    const html = controller.renderPage();

    assert.match(html, /Durable local automation/);
    assert.match(html, /schedule/);
    assert.match(html, /VCA checks/);
    assert.match(html, /successful commits/);
    assert.match(html, /Run now/i);
    assert.match(html, /Recent runs/);
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
