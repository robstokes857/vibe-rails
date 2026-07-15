import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const modulePath = path.resolve('VibeRails/wwwroot/js/modules/code-analyzer-dashboard.js');
const {
    buildCodeAnalyzerDashboardModel,
    createCodeEvidenceEditor,
    disposeCodeAnalyzerDashboard,
    getMonacoLanguageForPath,
    renderCodeAnalyzerDashboard
} = await import(pathToFileURL(modulePath).href);

function sampleResponse() {
    return {
        healthScore: 64,
        rating: 'NeedsWork',
        analyzedFileCount: 2,
        skippedFileCount: 1,
        durationMs: 1840,
        report: {
            score: 36,
            rating: 'NeedsWork',
            files: [{
                file: 'src/Payments/PaymentProcessor.cs',
                score: 58,
                rating: 'AtRisk',
                referencedByCount: 4,
                priority: 91.5,
                baselineScore: 42,
                introducedScore: 16,
                categories: [{
                    name: 'Complexity',
                    score: 82,
                    weight: 1,
                    weightedScore: 82,
                    metrics: [{
                        name: 'cognitive_complexity',
                        value: 31,
                        score: 82,
                        warn: 15,
                        critical: 25,
                        higherIsBetter: false,
                        source: 'ProcessAsync',
                        line: 42,
                        snippet: 'if (payment.IsPending) { }'
                    }]
                }]
            }],
            worstMetrics: [{
                name: 'cognitive_complexity',
                file: 'src/Payments/PaymentProcessor.cs',
                value: 31,
                score: 82,
                warn: 15,
                critical: 25,
                higherIsBetter: false,
                source: 'ProcessAsync',
                line: 42,
                snippet: 'if (payment.IsPending) { }'
            }]
        }
    };
}

test('code analyzer dashboard converts concern scores into scan and file quality', () => {
    const model = buildCodeAnalyzerDashboardModel(sampleResponse());

    assert.equal(model.health, 64);
    assert.equal(model.healthLabel, 'Needs attention');
    assert.equal(model.rating, 'Needs Work');
    assert.equal(model.qualityGrade, 'D');
    assert.equal(model.analyzedFileCount, 2);
    assert.equal(model.skippedFileCount, 1);
    assert.equal(model.criticalCount, 1);
    assert.equal(model.warningCount, 0);
    assert.equal(model.duration, '1.8 s');

    const file = model.files[0];
    assert.equal(file.folder, 'src/Payments');
    assert.equal(file.name, 'PaymentProcessor.cs');
    assert.equal(file.health, 42);
    assert.equal(file.qualityGrade, 'F');
    assert.equal(file.priorityMetric.label, 'Cognitive complexity');
    assert.equal(file.priorityMetric.snippet, 'if (payment.IsPending) { }');
    assert.deepEqual(model.categories, [{
        name: 'Complexity',
        concern: 82,
        worst: 'Cognitive complexity · 31'
    }]);
});

test('code analyzer dashboard handles an empty report without inventing files', () => {
    const model = buildCodeAnalyzerDashboardModel({
        analyzedFileCount: 0,
        skippedFileCount: 3,
        report: { score: 0, rating: 'Clean', files: [], worstMetrics: [] }
    });

    assert.equal(model.files.length, 0);
    assert.equal(model.health, 100);
    assert.equal(model.healthyFileCount, 0);
    assert.equal(model.skippedFileCount, 3);
});

test('code analyzer dashboard selects Monaco languages from source paths', () => {
    assert.equal(getMonacoLanguageForPath('src/Worker.cs'), 'csharp');
    assert.equal(getMonacoLanguageForPath('web/widget.tsx'), 'typescript');
    assert.equal(getMonacoLanguageForPath('Dockerfile'), 'dockerfile');
    assert.equal(getMonacoLanguageForPath('unknown.extension'), 'plaintext');
});

test('code evidence creates a read-only Monaco editor with source line numbers and highlighting', () => {
    let editorOptions;
    let decorationEntries;
    let selectedRange;
    const model = { dispose() { } };
    const editor = {
        getModel: () => model,
        createDecorationsCollection(entries) {
            decorationEntries = entries;
            return { dispose() { } };
        },
        setSelection(range) { selectedRange = range; },
        revealLineNearTop() { }
    };
    class Range {
        constructor(startLineNumber, startColumn, endLineNumber, endColumn) {
            Object.assign(this, { startLineNumber, startColumn, endLineNumber, endColumn });
        }
    }
    const monaco = {
        Range,
        editor: {
            OverviewRulerLane: { Full: 7 },
            MinimapPosition: { Inline: 1 },
            create(_host, options) {
                editorOptions = options;
                return editor;
            }
        }
    };
    const metric = {
        label: 'Cognitive complexity',
        snippet: 'if (payment.IsPending) { }',
        line: 42,
        score: 82
    };

    const mounted = createCodeEvidenceEditor(
        monaco,
        {},
        { path: 'src/Payments/PaymentProcessor.cs' },
        metric);

    assert.equal(mounted.editor, editor);
    assert.equal(mounted.model, model);
    assert.equal(editorOptions.value, metric.snippet);
    assert.equal(editorOptions.language, 'csharp');
    assert.equal(editorOptions.readOnly, true);
    assert.equal(editorOptions.domReadOnly, true);
    assert.equal(editorOptions.lineNumbers(1), '42');
    assert.equal(editorOptions.lineNumbers(3), '44');
    assert.equal(decorationEntries[0].options.className, 'mintlint-monaco-line mintlint-monaco-line-danger');
    assert.equal(selectedRange.startLineNumber, 1);
});

class FakeClassList {
    add() { }
    toggle() { }
}

class FakeElement {
    constructor(tagName) {
        this.tagName = tagName;
        this.children = [];
        this.textContent = '';
        this.className = '';
        this.classList = new FakeClassList();
        this.dataset = {};
        this.style = { setProperty() { } };
        this.value = '';
    }

    append(...children) {
        this.children.push(...children);
    }

    replaceChildren(...children) {
        this.children = [...children];
    }

    setAttribute() { }

    addEventListener() { }
}

test('code analyzer dashboard renders the full scan workspace with plain DOM APIs', () => {
    const container = new FakeElement('div');
    const documentRef = { createElement: tagName => new FakeElement(tagName) };

    const rendered = renderCodeAnalyzerDashboard(container, sampleResponse(), documentRef);

    assert.equal(rendered, 1);
    assert.equal(container.children.length, 2);
    assert.equal(container.children[0].className, 'code-analyzer-scan-banner');
    assert.equal(container.children[1].className, 'code-analyzer-workspace');
    assert.equal(container.children[1].children[0].className, 'code-analyzer-panel-surface code-analyzer-file-rail');
    const review = container.children[1].children[1];
    assert.equal(review.className, 'code-analyzer-review-column');
    assert.equal(review.children[2].className, 'code-analyzer-panel-surface code-analyzer-code-panel');
    assert.equal(review.children[2].children[1].className, 'code-analyzer-monaco-shell');
    assert.equal(disposeCodeAnalyzerDashboard(container), true);
    assert.equal(disposeCodeAnalyzerDashboard(container), false);
});
