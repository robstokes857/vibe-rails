import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

// Exercise the shipped sandbox's layout module: the whole-snapshot scope and the field's ambient link budget.
const bundle = readFileSync(new URL('../../../VibeRails/wwwroot/js/modules/code-report/vendor/atlas/code-atlas.mjs', import.meta.url), 'utf8');
const template = JSON.parse(bundle.match(/^const rendererTemplate = (.*);$/m)[1]);
const layoutScript = [...template.matchAll(/<script>([\s\S]*?)<\/script>/g)]
    .find(match => match[1].includes('root.CodeAtlasLayout ='))[1];
const sandbox = {};
vm.runInNewContext(layoutScript, sandbox);
const { scopeNodes, ambientLinks, layout, detail, VISIBLE_LIMIT, DENSE_VIEW_NODES, AMBIENT_LINK_LIMIT, DETAIL_ZOOM } = sandbox.CodeAtlasLayout;

function fixture(files = 500) {
    const nodes = [{ id: 'src', kind: 'module' }, { id: 'nested', kind: 'module', parentId: 'src' }];
    for (let i = 0; i < files; i++) {
        nodes.push({ id: `file-${i}`, kind: 'file', parentId: 'nested' });
        nodes.push({ id: `class-${i}`, kind: 'class', parentId: `file-${i}` });
    }
    return { graph: { nodes }, index: { nodes: new Map(nodes.map(node => [node.id, node])) } };
}

test('the overview is the whole snapshot, bounded only by the renderer limit', () => {
    const { graph, index } = fixture();
    assert.equal(VISIBLE_LIMIT, 3000);
    assert.equal(DENSE_VIEW_NODES, 600);
    const view = scopeNodes(graph, index, null);
    assert.equal(view.nodes.length, graph.nodes.length);
    assert.equal(view.total, graph.nodes.length);
    assert.deepEqual(scopeNodes(graph, index, 'file-499').nodes.map(node => node.id), ['class-499']);
    assert.equal(scopeNodes(graph, index, null, 'classes').nodes.filter(node => node.kind === 'file').length, 0);
});

test('the ambient link budget keeps every tree link, samples references deterministically and discloses the rest through size', () => {
    assert.equal(AMBIENT_LINK_LIMIT, 4500);
    const edges = [];
    for (let i = 0; i < 300; i++) edges.push({ source: 'dir', target: `file-${i}`, kind: 'contains' });
    for (let i = 0; i < 200; i++) edges.push({ source: `file-${i}`, target: `file-${(i * 7) % 300}`, kind: 'references' });
    edges.push({ source: 'file-1', target: 'file-2', kind: 'contains', structural: true });
    assert.deepEqual(ambientLinks(edges, 1000), edges, 'under budget every link is ambient');
    assert.notEqual(ambientLinks(edges, 1000), edges, 'the result is a copy');
    const limited = ambientLinks(edges, 350);
    assert.equal(limited.length, 350);
    assert.equal(limited.filter(edge => edge.kind === 'contains').length, 301, 'tree links come first');
    assert.equal(limited.filter(edge => edge.kind === 'references').length, 49);
    assert.deepEqual(ambientLinks(edges, 350), limited, 'the sample is deterministic');
    const sampled = new Set(limited.filter(edge => edge.kind === 'references').map(edge => edges.indexOf(edge)));
    const firstBlock = [...sampled].filter(index => index < 300 + 49).length;
    assert.ok(firstBlock < 49, 'references are sampled by hash, not taken in supplier order');
    assert.equal(ambientLinks(edges, 100).length, 100, 'a budget below the tree count keeps the first tree links');
    assert.ok(ambientLinks(edges, 100).every(edge => edge.kind === 'contains'));
});

test('semantic zoom fades declarations in by family as the camera closes and never touches the structure', () => {
    assert.deepEqual(structuredClone(DETAIL_ZOOM), { class: [.4, .65], function: [.55, .9] }); // Cloned: the sandbox realm has its own Object.
    for (const family of ['module', 'file', 'data']) for (const zoom of [.12, .42, 1, 2.4]) assert.equal(detail(family, zoom), 1, `${family} at ${zoom}`);
    assert.equal(detail('function', .42), 0, 'functions are hidden at a dense overview');
    assert.equal(detail('class', .42), 0, 'a sliver of alpha at the start of the range counts as hidden');
    assert.equal(detail('class', .525), .5, 'the midpoint of a range is half alpha');
    assert.equal(detail('function', .725), .5);
    assert.equal(detail('class', .65), 1);
    assert.equal(detail('function', .9), 1);
    assert.equal(detail('function', 2.4), 1);
    let previous = 0;
    for (let zoom = .12; zoom <= 1; zoom += .01) {
        const value = detail('function', zoom);
        assert.ok(value >= previous && value >= 0 && value <= 1, `monotonic at ${zoom.toFixed(2)}`);
        previous = value;
    }
});

test('the force layout places every node of a dense snapshot in bounded time', () => {
    const { graph, index } = fixture(700);
    const view = scopeNodes(graph, index, null);
    const edges = graph.nodes.filter(node => node.parentId).map(node => ({ source: node.parentId, target: node.id, kind: 'contains' }));
    const started = performance.now();
    const positions = layout(view.nodes, edges, index);
    const elapsed = performance.now() - started;
    assert.equal(positions.size, graph.nodes.length);
    for (const item of positions.values()) assert.ok(Number.isFinite(item.x) && Number.isFinite(item.y) && Number.isFinite(item.z));
    assert.ok(elapsed < 10_000, `layout of ${graph.nodes.length} nodes took ${elapsed.toFixed(0)} ms`);
});
