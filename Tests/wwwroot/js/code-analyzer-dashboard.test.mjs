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
    renderCodeAnalyzerBrief,
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
        worstConcern: 82,
        direction: 'NA',
        worstLabel: 'Cognitive complexity',
        worstValue: '31',
        worstDirection: 'LB',
        worstFile: 'src/Payments/PaymentProcessor.cs'
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
    remove() { }
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
        this.listeners = {};
    }

    append(...children) {
        this.children.push(...children);
    }

    replaceChildren(...children) {
        this.children = [...children];
    }

    setAttribute() { }

    addEventListener(type, handler) {
        this.listeners[type] = handler;
    }

    fire(type, event = {}) {
        this.listeners[type]?.(event);
    }
}

test('code analyzer dashboard renders the full scan workspace with plain DOM APIs', () => {
    const container = new FakeElement('div');
    const documentRef = { createElement: tagName => new FakeElement(tagName) };

    const rendered = renderCodeAnalyzerDashboard(container, sampleResponse(), documentRef);

    assert.equal(rendered, 1);
    // No internal tabs: the three-pane workspace IS the dashboard.
    assert.equal(container.children.length, 1);
    const workspace = container.children[0];
    assert.equal(workspace.className, 'code-analyzer-workspace');
    const railCell = workspace.children[0];
    assert.equal(railCell.className, 'code-analyzer-rail-cell');
    assert.equal(railCell.children[0].className, 'code-analyzer-panel-surface code-analyzer-file-rail');
    const review = workspace.children[1];
    assert.equal(review.className, 'code-analyzer-review-column');
    const sourceColumn = workspace.children[2];
    assert.equal(sourceColumn.className, 'code-analyzer-source-column');
    assert.equal(sourceColumn.children[0].className, 'code-analyzer-panel-surface code-analyzer-code-panel');
    assert.equal(sourceColumn.children[0].children[1].className, 'code-analyzer-monaco-shell');
    assert.equal(disposeCodeAnalyzerDashboard(container), true);
    assert.equal(disposeCodeAnalyzerDashboard(container), false);
});

test('code analyzer file rail groups files by directory with collapse and kebab menus', () => {
    const container = new FakeElement('div');
    const documentRef = { createElement: tagName => new FakeElement(tagName) };
    const response = sampleResponse();
    // A second file in a test tree exercises grouping and the "tests" badge.
    response.report.files.push({
        file: 'Tests/Payments/PaymentProcessorTests.cs',
        score: 12,
        rating: 'Healthy',
        referencedByCount: 0,
        priority: 4,
        categories: [{
            name: 'Complexity',
            score: 10,
            weight: 1,
            weightedScore: 10,
            metrics: [{
                name: 'cognitive_complexity',
                value: 3,
                score: 10,
                warn: 15,
                critical: 25,
                higherIsBetter: false,
                source: 'ProcessAsyncTests',
                line: 5,
                snippet: 'await processor.ProcessAsync();'
            }]
        }]
    });

    const ignoredFilePaths = [];
    const ignoredDirectoryPayloads = [];
    renderCodeAnalyzerDashboard(container, response, documentRef, {
        onIgnoreFile: file => ignoredFilePaths.push(file.path),
        onIgnoreDirectory: payload => ignoredDirectoryPayloads.push(payload)
    });

    const rail = container.children[0].children[0].children[0];
    const [head, list] = rail.children;
    assert.equal(head.className, 'code-analyzer-rail-head');
    assert.equal(list.className, 'code-analyzer-file-list');
    const contextMenu = rail.children.at(-1);
    assert.equal(contextMenu.className, 'code-analyzer-context-menu');

    // Grouped: header, file, header, file — with full directory paths.
    assert.equal(list.children.length, 4);
    const [firstGroup, firstFile, secondGroup, secondFile] = list.children;
    assert.equal(firstGroup.className, 'code-analyzer-dir-head');
    assert.equal(firstGroup.children[1].children[1].textContent, 'src/Payments');
    assert.equal(firstFile.className, 'code-analyzer-file-item');
    // The row shows only the file name — its directory lives on the group header.
    assert.equal(firstFile.children[1].children[0].textContent, 'PaymentProcessor.cs');
    assert.equal(secondGroup.children[1].children[1].textContent, 'Tests/Payments');
    const testTag = secondGroup.children.find(child => child.className === 'code-analyzer-dir-tag');
    assert.equal(testTag?.textContent, 'tests');
    assert.equal(secondFile.children[1].children[0].textContent, 'PaymentProcessorTests.cs');

    // The file row's kebab opens the context menu; "Ignore file" ignores that file.
    const fileKebab = firstFile.children.at(-1);
    assert.equal(fileKebab.className, 'code-analyzer-kebab');
    fileKebab.fire('click', { stopPropagation() { } });
    assert.equal(contextMenu.children[0].children[1].textContent, 'Ignore file');
    // Re-clicking the active kebab toggles its menu closed; a third click reopens it.
    fileKebab.fire('click', { stopPropagation() { } });
    assert.equal(contextMenu.children.length, 0);
    fileKebab.fire('click', { stopPropagation() { } });
    assert.equal(contextMenu.children[0].children[1].textContent, 'Ignore file');
    contextMenu.children[0].fire('click');
    assert.deepEqual(ignoredFilePaths, ['src/Payments/PaymentProcessor.cs']);

    // The directory header's kebab offers "Ignore directory" for the whole group.
    const dirKebab = firstGroup.children.at(-1);
    assert.equal(dirKebab.className, 'code-analyzer-kebab');
    dirKebab.fire('click', { stopPropagation() { } });
    assert.equal(contextMenu.children[0].children[1].textContent, 'Ignore directory');
    contextMenu.children[0].fire('click');
    assert.deepEqual(ignoredDirectoryPayloads, [{ directoryPaths: ['src/Payments'] }]);

    // Enter/Space from a nested real button must not activate either role=button parent.
    let directoryKeyPrevented = false;
    firstGroup.fire('keydown', {
        key: 'Enter',
        target: dirKebab,
        currentTarget: firstGroup,
        preventDefault() { directoryKeyPrevented = true; }
    });
    let fileKeyPrevented = false;
    firstFile.fire('keydown', {
        key: ' ',
        target: fileKebab,
        currentTarget: firstFile,
        preventDefault() { fileKeyPrevented = true; }
    });
    assert.equal(directoryKeyPrevented, false);
    assert.equal(fileKeyPrevented, false);
    assert.equal(list.children.length, 4);

    // Clicking a group header collapses it — its files leave the list until reopened.
    firstGroup.fire('click', {});
    assert.equal(list.children.length, 3);
    assert.equal(list.children[0].className, 'code-analyzer-dir-head');
    assert.equal(list.children[1].className, 'code-analyzer-dir-head');
    assert.equal(list.children[2].className, 'code-analyzer-file-item');
    list.children[0].fire('click', {});
    assert.equal(list.children.length, 4);
    disposeCodeAnalyzerDashboard(container);
});

test('code analyzer brief summarizes the scan for the Validation screen', () => {
    const documentRef = { createElement: tagName => new FakeElement(tagName) };
    const host = new FakeElement('section');
    host.hidden = true;
    let opened = 0;

    const rendered = renderCodeAnalyzerBrief(host, sampleResponse(), documentRef, {
        onOpenDetails: () => { opened += 1; }
    });

    assert.equal(rendered, true);
    assert.equal(host.hidden, false);
    const brief = host.children[0];
    assert.equal(brief.className, 'code-analyzer-brief');
    assert.equal(brief.dataset.tone, 'warning');
    assert.equal(brief.children[0].className, 'code-analyzer-brief-ring');
    const copy = brief.children[1];
    assert.equal(copy.children[1].textContent, 'Needs attention');
    assert.equal(copy.children[2].textContent, '2 files analyzed');
    const stats = brief.children[2];
    assert.equal(stats.children[0].children[0].textContent, '1');
    assert.equal(stats.children[0].dataset.tone, 'danger');
    const categories = brief.children[3];
    assert.equal(categories.children[0].children[1].textContent, 'Complexity');

    brief.children[4].fire('click');
    assert.equal(opened, 1);

    assert.equal(renderCodeAnalyzerBrief(host, null, documentRef), false);
    assert.equal(host.hidden, true);
    assert.equal(host.children.length, 0);
});
