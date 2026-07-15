import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const modulePath = path.resolve('VibeRails/wwwroot/js/modules/git-guard-preflight.js');
const {
    GitPreflightRunner,
    buildMintLintDetailModel,
    createAutorunOnce,
    createGitPreflightState,
    createSseParser,
    reduceGitPreflightEvent,
    renderMintLintDetails,
    streamGitPreflight
} = await import(pathToFileURL(modulePath).href);

test('SSE parser preserves JSON events across arbitrary CRLF chunks', () => {
    const events = [];
    const parser = createSseParser(event => events.push(event));

    parser.push(': keepalive\r\ndata: {"type":"run_started","message":"<unsafe>",');
    parser.push('"sequence":1}\r\n\r\ndata: {"type":"step_started","stepId":"vca"}\n');
    parser.push('\ndata: plain text event');
    parser.finish();

    assert.deepEqual(events, [
        { type: 'run_started', message: '<unsafe>', sequence: 1 },
        { type: 'step_started', stepId: 'vca' },
        { type: 'message', message: 'plain text event' }
    ]);
});

test('stream client uses authenticated POST fetch and emits every event', async () => {
    const encoder = new TextEncoder();
    const chunks = [
        encoder.encode('data: {"type":"run_started","sequence":1}\n\n'),
        encoder.encode('data: {"type":"run_finished","sequence":2,"commitAllowed":true}\n\n')
    ];
    let request;
    const events = [];
    const fetchImpl = async (url, options) => {
        request = { url, options };
        return {
            ok: true,
            status: 200,
            body: {
                getReader() {
                    return {
                        async read() {
                            return chunks.length ? { value: chunks.shift(), done: false } : { done: true };
                        },
                        releaseLock() { }
                    };
                }
            }
        };
    };

    await streamGitPreflight({
        url: '/api/v1/git/preflight/stream',
        fetchImpl,
        headers: { viberails_tab: 'tab-token' },
        onEvent: event => events.push(event)
    });

    assert.equal(request.url, '/api/v1/git/preflight/stream');
    assert.equal(request.options.method, 'POST');
    assert.equal(request.options.credentials, 'include');
    assert.equal(request.options.headers.Accept, 'text/event-stream');
    assert.equal(request.options.headers.viberails_tab, 'tab-token');
    assert.equal(events.length, 2);
    assert.equal(events[1].commitAllowed, true);
});

test('preflight reducer tracks staged files, fixed steps, and final commit decision', () => {
    let state = createGitPreflightState();
    const events = [
        {
            type: 'run_started', runId: 'run-1', sequence: 1, status: 'running',
            message: 'Checking 2 staged file(s).',
            details: { stagedFileCount: '2', stagedFiles: 'src/a.cs\nsrc/<unsafe>.js' }
        },
        { type: 'step_started', stepId: 'vca', sequence: 2, status: 'running', message: 'VCA rules' },
        { type: 'step_output', stepId: 'vca', sequence: 3, status: 'running', message: 'Validated AGENTS.md' },
        {
            type: 'step_finished', stepId: 'vca', sequence: 4, status: 'passed',
            message: 'VCA passed.', durationMs: 31, blocking: true
        },
        {
            type: 'step_finished', stepId: 'mintlint', sequence: 5, status: 'warning',
            message: 'MintLint found concerns.', durationMs: 8,
            details: { worstFiles: 'src/a.cs: 42.0 NeedsWork · Complexity 12, Duplication 4' }
        },
        {
            type: 'run_finished', sequence: 6, status: 'blocked', message: 'Commit blocked.',
            durationMs: 50, blocking: true, commitAllowed: false
        }
    ];
    for (const event of events) state = reduceGitPreflightEvent(state, event);

    assert.equal(state.runId, 'run-1');
    assert.deepEqual(state.staged, { count: 2, files: ['src/a.cs', 'src/<unsafe>.js'] });
    assert.equal(state.steps.vca.status, 'passed');
    assert.equal(state.steps.vca.output, 'Validated AGENTS.md');
    assert.equal(state.steps.mintlint.status, 'warning');
    assert.equal(state.status, 'blocked');
    assert.equal(state.commitAllowed, false);
    assert.equal(state.durationMs, 50);

    const mintFiles = buildMintLintDetailModel(state.steps.mintlint.details);
    assert.equal(mintFiles[0].path, 'src/a.cs');
    assert.equal(mintFiles[0].grade, '42.0 NeedsWork');
    assert.deepEqual(mintFiles[0].categories.map(category => category.name), ['Complexity', 'Duplication']);
});

test('focused autorun gate schedules exactly one run', () => {
    const scheduled = [];
    let runs = 0;
    const autorun = createAutorunOnce(() => { runs += 1; }, callback => scheduled.push(callback));

    assert.equal(autorun(), true);
    assert.equal(autorun(), false);
    assert.equal(scheduled.length, 1);
    scheduled[0]();
    assert.equal(runs, 1);
});

test('preflight runner aborts the active streamed request', async () => {
    let observedSignal;
    const fetchImpl = (_url, options) => new Promise((_resolve, reject) => {
        observedSignal = options.signal;
        options.signal.addEventListener('abort', () => {
            const error = new Error('aborted');
            error.name = 'AbortError';
            reject(error);
        }, { once: true });
    });
    const runner = new GitPreflightRunner({ url: '/stream', fetchImpl });
    const running = runner.run(() => { });
    await Promise.resolve();

    assert.equal(runner.isRunning, true);
    assert.equal(runner.cancel(), true);
    const result = await running;
    assert.equal(observedSignal.aborted, true);
    assert.deepEqual(result, { started: true, cancelled: true });
    assert.equal(runner.isRunning, false);
    assert.equal(runner.cancel(), false);
});

class FakeElement {
    constructor(tagName) {
        this.tagName = tagName;
        this.children = [];
        this.textContent = '';
        this.className = '';
    }

    append(...children) {
        this.children.push(...children);
    }

    replaceChildren(...children) {
        this.children = [...children];
    }
}

test('MintLint renderer writes untrusted filenames and categories through textContent', () => {
    const container = new FakeElement('div');
    const documentRef = { createElement: tagName => new FakeElement(tagName) };
    const rendered = renderMintLintDetails(container, {
        files: [{
            path: '<img src=x onerror=alert(1)>',
            grade: 'NeedsWork',
            categories: [{ name: '<script>', message: 'literal <b>message</b>', score: 9 }]
        }]
    }, documentRef);

    assert.equal(rendered, 1);
    const fileDetails = container.children[0];
    const summary = fileDetails.children[0];
    assert.equal(summary.children[0].textContent, '<img src=x onerror=alert(1)>');
    assert.equal(summary.children[1].textContent, 'NeedsWork');
    const categoryList = fileDetails.children[1];
    assert.equal(categoryList.children[0].textContent, '<script>');
    assert.equal(categoryList.children[1].textContent, 'literal <b>message</b>');
    assert.equal(Object.hasOwn(summary.children[0], 'innerHTML'), false);
});
