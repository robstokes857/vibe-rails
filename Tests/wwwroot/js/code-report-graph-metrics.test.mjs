import test from 'node:test';
import assert from 'node:assert/strict';
import { withReportMetrics } from '../../../VibeRails/wwwroot/js/modules/code-report/graph-metrics.js';

const graph = {
    nodes: [
        { id: 'file', kind: 'file', path: 'src/Example.cs', summary: 'Current working tree.' },
        { id: 'symbol', kind: 'class', path: 'src/Example.cs:12', parentId: 'file' },
        { id: 'directory', kind: 'module', path: 'src' },
        { id: 'unscanned', kind: 'file', path: 'src/Other.cs' }
    ],
    edges: [{ source: 'file', target: 'symbol', kind: 'contains' }]
};
const file = metrics => ({ file: './src\\Example.cs', categories: [{ name: 'Measured', metrics }] });
const metric = (name, value) => ({ name, value, score: 99 });

test('maps raw saved file measurements and provenance without assigning file values to symbols or folders', () => {
    const input = structuredClone(graph);
    const result = withReportMetrics(input, [file([
        metric('lines_of_code', 240), metric('cyclomatic_complexity', 17), metric('cognitive_complexity', 42)
    ])], '2026-10-08T12:00:00Z');
    assert.deepEqual(result.nodes[0].metrics, { loc: 240, complexity: 17 });
    assert.match(result.nodes[0].summary, /Current working tree\. Measurements from the scan captured/);
    assert.ok(result.nodes[0].summary.includes(new Date('2026-10-08T12:00:00Z').toLocaleString()));
    assert.match(result.nodes[0].summary, /highest function cyclomatic complexity/);
    for (const node of result.nodes.slice(1)) assert.equal(node.metrics, undefined);
    assert.deepEqual(input, graph, 'the shared graph cache must remain unchanged');
    assert.equal(result.edges, input.edges, 'graph connections are unaffected');
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
        assert.match(result.nodes[0].summary, /Measurements from the saved scan/);
        assert.ok(!result.nodes[0].summary.includes('Invalid Date'));
    }
});

test('partial reports do not invent metrics or coverage and path matching remains case sensitive', () => {
    for (const files of [[], [null], [file([])], [file([metric('cognitive_complexity', 12)])],
        [{ file: 'SRC/Example.cs', categories: file([metric('lines_of_code', 12)]).categories }],
        [{ file: 'src/Example.cs', categories: [null, { metrics: [null] }, { metrics: null }] }]]) {
        assert.deepEqual(withReportMetrics(graph, files).nodes, graph.nodes);
    }
    const result = withReportMetrics(graph, [file([metric('lines_of_code', 4)])]);
    assert.deepEqual(result.nodes[0].metrics, { loc: 4 });
    assert.ok(!result.nodes[0].summary.includes('cyclomatic'));
});

test('reusing the cached graph never retains a previous scan overlay', () => {
    const first = withReportMetrics(graph, [file([metric('lines_of_code', 12)])]);
    const second = withReportMetrics(graph, [file([metric('lines_of_code', 24)])]);
    const empty = withReportMetrics(graph, []);
    assert.equal(first.nodes[0].metrics.loc, 12);
    assert.equal(second.nodes[0].metrics.loc, 24);
    assert.equal(empty.nodes[0].metrics, undefined);
    assert.equal(graph.nodes[0].metrics, undefined);
});
