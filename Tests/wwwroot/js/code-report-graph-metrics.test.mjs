import test from 'node:test';
import assert from 'node:assert/strict';
import { withReportMetrics } from '../../../VibeRails/wwwroot/js/modules/code-report/graph-metrics.js';

const graph = {
    nodes: [
        { id: 'file', kind: 'file', path: 'src/Example.cs' },
        { id: 'symbol', kind: 'class', path: 'src/Example.cs:12', parentId: 'file' },
        { id: 'directory', kind: 'module', path: 'src' },
        { id: 'unscanned', kind: 'file', path: 'src/Other.cs' }
    ],
    edges: [{ source: 'file', target: 'symbol', kind: 'contains' }]
};
const file = metrics => ({ file: './src\\Example.cs', score: 62, rating: 'NeedsWork', categories: [{ name: 'Measured', metrics }] });
const metric = (name, value, limits = {}) => ({ name, value, score: 99, ...limits });

test('maps raw saved file measurements and provenance without assigning file values to symbols or folders', () => {
    const input = structuredClone(graph);
    const result = withReportMetrics(input, [file([
        metric('lines_of_code', 240), metric('cyclomatic_complexity', 17), metric('cognitive_complexity', 42)
    ])], '2026-10-08T12:00:00Z');
    assert.deepEqual(result.nodes[0].metrics, { loc: 240, complexity: 17 });
    assert.equal(result.nodes[0].summary, `Measured in the scan from ${new Date('2026-10-08T12:00:00Z').toLocaleString()}.`);
    assert.equal(result.nodes[0].quality, undefined, 'without saved limits nothing is judged');
    for (const node of result.nodes.slice(1)) {
        assert.equal(node.metrics, undefined);
        assert.equal(node.quality, undefined);
    }
    assert.equal(result.nodes[1].summary, undefined, 'declarations are never described as measured');
    assert.equal(result.nodes[2].summary, undefined, 'directories are never described as measured');
    assert.equal(result.nodes[3].summary, 'Not in this scan.', 'an unscanned file never reads as clean');
    assert.deepEqual(input, graph, 'the shared graph cache must remain unchanged');
    assert.equal(result.edges, input.edges, 'graph connections are unaffected');
});

test('a scanned file carries its problems, worst first, for the inspector', () => {
    const result = withReportMetrics(graph, [file([
        metric('lines_of_code', 420, { warn: 300, critical: 600 }),
        metric('cyclomatic_complexity', 31, { warn: 10, critical: 20, source: 'Process', line: 28 }),
        metric('nesting_depth', 2, { warn: 4, critical: 6, source: 'Process', line: 28 })
    ])], '2026-10-08T12:00:00Z');
    assert.deepEqual(result.nodes[0].quality, {
        rating: 'Needs work', tone: 'warning', headline: '2 problems, 1 critical',
        findings: [
            { level: 'critical', title: 'Cyclomatic complexity 31', detail: 'Process, line 28 · critical at 20' },
            { level: 'warning', title: 'Lines of code 420', detail: 'Whole file · warning above 300' }
        ]
    });
    assert.deepEqual(result.nodes[0].metrics, { loc: 420, complexity: 31 });
    const clean = withReportMetrics(graph, [file([metric('lines_of_code', 40, { warn: 300, critical: 600 })])]);
    assert.equal(clean.nodes[0].quality.headline, 'No problems: every measurement is within its limits.');
    assert.deepEqual(clean.nodes[0].quality.findings, []);
});

test('keeps measured zero values and omits null, nonnumeric, negative and nonfinite values', () => {
    assert.deepEqual(withReportMetrics(graph, [file([
        metric('lines_of_code', 0), metric('cyclomatic_complexity', 0)
    ])]).nodes[0].metrics, { loc: 0, complexity: 0 });
    for (const value of [null, undefined, '23', '', false, -1, NaN, Infinity, -Infinity]) {
        const result = withReportMetrics(graph, [file([
            metric('lines_of_code', value), metric('cyclomatic_complexity', 6)
        ])], 'invalid');
        assert.deepEqual(result.nodes[0].metrics, { complexity: 6 });
        assert.equal(result.nodes[0].summary, 'Measured in the saved scan.');
    }
});

test('partial reports do not invent metrics, problems or coverage and path matching remains case sensitive', () => {
    for (const files of [[], [null], [{ file: '' }]]) {
        assert.deepEqual(withReportMetrics(graph, files).nodes, graph.nodes, 'no report leaves the map untouched');
    }
    for (const files of [[file([])], [file([metric('cognitive_complexity', 12)])],
        [{ file: 'SRC/Example.cs', categories: file([metric('lines_of_code', 12)]).categories }],
        [{ file: 'src/Example.cs', categories: [null, { metrics: [null] }, { metrics: null }] }]]) {
        const nodes = withReportMetrics(graph, files).nodes;
        for (const node of nodes) {
            assert.equal(node.metrics, undefined);
            assert.equal(node.quality, undefined);
            assert.equal(node.coverage, undefined);
        }
        assert.deepEqual(nodes.slice(1, 3), graph.nodes.slice(1, 3), 'symbols and directories are untouched');
        assert.equal(nodes[3].summary, 'Not in this scan.');
    }
    const result = withReportMetrics(graph, [file([metric('lines_of_code', 4)])]);
    assert.deepEqual(result.nodes[0].metrics, { loc: 4 });
    assert.equal(result.nodes[0].summary, 'Measured in the saved scan.');
});

test('reusing the cached graph never retains a previous scan overlay', () => {
    const first = withReportMetrics(graph, [file([metric('lines_of_code', 12)])]);
    const second = withReportMetrics(graph, [file([metric('lines_of_code', 24)])]);
    const empty = withReportMetrics(graph, []);
    assert.equal(first.nodes[0].metrics.loc, 12);
    assert.equal(second.nodes[0].metrics.loc, 24);
    assert.equal(empty.nodes[0].metrics, undefined);
    assert.equal(empty.nodes[3].summary, undefined);
    assert.equal(graph.nodes[0].metrics, undefined);
});
