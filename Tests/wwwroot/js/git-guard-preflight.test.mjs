import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const modulePath = path.resolve('VibeRails/wwwroot/js/modules/git-guard-preflight.js');
const {
    GitPreflightRunner,
    buildMintLintDetailModel,
    buildMintLintReportViewModel,
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

test('MintLint detail model prefers the full metric report over the legacy text summary', () => {
    const report = JSON.stringify({
        score: 78.4,
        rating: 'AtRisk',
        analyzedFileCount: 1,
        skippedFileCount: 2,
        files: [{
            file: 'src/a.cs',
            score: 100,
            rating: 'AtRisk',
            referencedByCount: 9,
            priority: 200,
            baselineScore: 100,
            introducedScore: 0,
            categories: [
                {
                    name: 'Complexity', score: 100, weight: 1, weightedScore: 100,
                    metrics: [{ name: 'cyclomatic_complexity', value: 27, score: 100, warn: 10, critical: 20, higherIsBetter: false, source: 'Evaluate', line: 12 }]
                },
                {
                    name: 'Size', score: 50, weight: 0.7, weightedScore: 35,
                    metrics: [{ name: 'parameter_count', value: 4, score: 50, warn: 4, critical: 7, higherIsBetter: false }]
                }
            ]
        }],
        worstMetrics: [{
            name: 'cyclomatic_complexity', file: 'src/a.cs', value: 27, score: 100,
            warn: 10, critical: 20, higherIsBetter: false, source: 'Evaluate', line: 12,
            snippet: 'public int Evaluate() {\n    if (a && b) { }\n}'
        }]
    });

    const files = buildMintLintDetailModel({ report, worstFiles: 'ignored: 1.0 Clean · nothing' });

    assert.equal(files.length, 1);
    assert.equal(files[0].path, 'src/a.cs');
    assert.equal(files[0].grade, '100.0 AtRisk');
    assert.equal(files[0].detailed, true);
    assert.equal(files[0].referencedBy, 9);
    assert.equal(files[0].priority, 200);
    assert.equal(files[0].baseline, 100);
    assert.equal(files[0].introduced, 0);
    assert.deepEqual(files[0].categories.map(category => category.name), ['Complexity', 'Size']);
    const cyclomatic = files[0].categories[0].metrics[0];
    assert.equal(cyclomatic.label, 'Cyclomatic complexity');
    assert.equal(cyclomatic.value, 27);
    assert.equal(cyclomatic.warn, 10);
    assert.equal(cyclomatic.source, 'Evaluate');
    assert.equal(cyclomatic.line, 12);
    assert.equal(files[0].categories[1].weightedScore, 35);

    const viewModel = buildMintLintReportViewModel({ report });
    assert.equal(viewModel.detailed, true);
    assert.equal(viewModel.worstMetrics.length, 1);
    assert.equal(viewModel.worstMetrics[0].label, 'Cyclomatic complexity');
    assert.equal(viewModel.worstMetrics[0].file, 'src/a.cs');
    assert.match(viewModel.worstMetrics[0].snippet, /Evaluate/);
});

test('MintLint renderer leads with worst offenders and keeps the file list collapsed', () => {
    const container = new FakeElement('div');
    const documentRef = { createElement: tagName => new FakeElement(tagName) };
    const report = {
        files: [{
            file: '<img src=x onerror=alert(1)>.cs',
            score: 100,
            rating: 'AtRisk',
            referencedByCount: 3,
            priority: 160.2,
            baselineScore: 82.5,
            introducedScore: 17.5,
            categories: [{
                name: '<script>Complexity',
                score: 100,
                weight: 1,
                weightedScore: 100,
                metrics: [{ name: 'cyclomatic_complexity', value: 27, score: 100, warn: 10, critical: 20, higherIsBetter: false, source: 'Evaluate', line: 12 }]
            }]
        }],
        worstMetrics: [{
            name: 'cyclomatic_complexity', file: '<img src=x onerror=alert(1)>.cs', value: 27, score: 100,
            warn: 10, critical: 20, higherIsBetter: false, source: 'Evaluate', line: 12,
            snippet: 'if (a && b) { alert("<script>") }'
        }]
    };

    const rendered = renderMintLintDetails(container, { report }, documentRef);

    assert.equal(rendered, 1);

    // Worst-offender table first, snippet written as text.
    const worstSection = container.children[0];
    assert.equal(worstSection.className, 'mintlint-worst');
    const worstRow = worstSection.children[1];
    const worstSummary = worstRow.children[0];
    assert.equal(worstSummary.children[0].textContent, 'Cyclomatic complexity');
    assert.equal(worstSummary.children[1].textContent, '27');
    assert.equal(worstSummary.children[2].textContent, '<img src=x onerror=alert(1)>.cs · Evaluate · line 12');
    assert.equal(worstSummary.children[3].textContent, '100.0');
    const worstDetail = worstRow.children[1];
    assert.equal(worstDetail.children[0].textContent, 'warn ≥ 10 · critical ≥ 20');
    assert.equal(worstDetail.children[1].tagName, 'pre');
    assert.equal(worstDetail.children[1].textContent, 'if (a && b) { alert("<script>") }');

    // Files live inside a collapsed disclosure, annotated with reach and priority.
    const disclosure = container.children[1];
    assert.equal(disclosure.className, 'mintlint-files');
    assert.equal(disclosure.open, undefined);
    const disclosureSummary = disclosure.children[0];
    assert.equal(disclosureSummary.children[0].textContent, 'Per-file breakdown');
    assert.equal(disclosureSummary.children[1].textContent, '1 file · ranked by concern × usage');
    const fileDetails = disclosure.children[1].children[0];
    const summary = fileDetails.children[0];
    assert.equal(summary.children[0].textContent, '<img src=x onerror=alert(1)>.cs');
    assert.equal(summary.children[1].textContent, 'used by 3 files · +17.5 added by this change (was 82.5) · priority 160.2');
    assert.equal(summary.children[2].textContent, '100.0 AtRisk');
    const categoryList = fileDetails.children[1];
    const category = categoryList.children[0];
    const head = category.children[0];
    assert.equal(head.children[0].textContent, '<script>Complexity');
    assert.equal(head.children[1].textContent, 'sets file score');
    assert.equal(head.children[2].textContent, '100.0 × 1.0 = 100.0');
    const metricRow = category.children[2].children[0];
    assert.equal(metricRow.children[0].textContent, 'Cyclomatic complexity');
    assert.equal(metricRow.children[0].children[0].textContent, ' · Evaluate L12');
    assert.equal(metricRow.children[1].textContent, '27');
    assert.equal(metricRow.children[2].textContent, 'warn ≥ 10 · critical ≥ 20');
    assert.equal(metricRow.children[3].textContent, '100.0');
    assert.equal(Object.hasOwn(summary.children[0], 'innerHTML'), false);
});

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
