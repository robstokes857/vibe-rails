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
const { scopeNodes, ambientLinks, layout, detail, VISIBLE_LIMIT, DENSE_VIEW_NODES, AMBIENT_LINK_LIMIT, DETAIL_ZOOM, FILE_FLOOR } = sandbox.CodeAtlasLayout;
// The camera module, the same way: the globe's perspective and depth fog live here.
const spaceScript = [...template.matchAll(/<script>([\s\S]*?)<\/script>/g)]
    .find(match => match[1].includes('root.CodeAtlasSpace ='))[1];
const spaceSandbox = {};
vm.runInNewContext(spaceScript, spaceSandbox);
const Space = spaceSandbox.CodeAtlasSpace;

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

test('semantic zoom settles files to dust, then brings files, classes and functions in as the camera closes', () => {
    // Cloned: the sandbox realm has its own Object. Directories and data are drawn at every zoom.
    assert.deepEqual(structuredClone(DETAIL_ZOOM), { file: [.45, .8], class: [.7, 1], function: [.9, 1.3] });
    assert.equal(FILE_FLOOR, .35);
    for (const family of ['module', 'data']) for (const zoom of [.12, .42, 1, 2.4]) assert.equal(detail(family, zoom), 1, `${family} at ${zoom}`);
    assert.equal(detail('file', .12), .35, 'zoomed out, a file is dust, never hidden');
    assert.equal(detail('file', .42), .35);
    assert.equal(detail('file', .625), .68, 'the midpoint of the file range is halfway between dust and full');
    assert.equal(detail('file', .8), 1);
    assert.equal(detail('file', 2.4), 1);
    assert.equal(detail('function', .42), 0, 'functions are hidden at a dense overview');
    assert.equal(detail('class', .42), 0, 'classes are hidden while the files are still dust');
    assert.equal(detail('class', .7), 0, 'a sliver of alpha at the start of the range counts as hidden');
    assert.equal(detail('class', .85), .5, 'the midpoint of a range is half alpha');
    assert.equal(detail('function', 1.1), .5);
    assert.equal(detail('class', 1), 1);
    assert.equal(detail('function', 1.3), 1);
    assert.equal(detail('function', 2.4), 1);
    assert.ok(detail('file', .7) > detail('class', .7), 'files always lead the declarations in');
    for (const family of ['file', 'class', 'function']) {
        let previous = 0;
        for (let zoom = .12; zoom <= 1.4; zoom += .01) {
            const value = detail(family, zoom);
            assert.ok(value >= previous && value >= 0 && value <= 1, `${family} monotonic at ${zoom.toFixed(2)}`);
            previous = value;
        }
    }
});

// The shipped placeLabels, run with a stub state: the field's app script is not a module, so the
// function is cut out by its anchors the same way the renderer template is.
function shippedPlaceLabels(zoom, items, field = {}) {
    const app = [...template.matchAll(/<script>([\s\S]*?)<\/script>/g)].find(match => match[1].includes('function placeLabels('))[1];
    const source = app.slice(app.indexOf('    function placeLabels('), app.indexOf('    function fittedCamera('));
    // No canvas context here: label widths fall back to the estimate; points are 8px; type is 12px.
    const context = { state: { layout: items, zoom, panX: 0, panY: 0, query: '', filter: 'all' }, field: { candidates: null, ...field },
        dense: () => true, detailFor: () => 1, projectItem: item => item.lastPosition, labelSize: () => 12, nodeRadius: () => 8,
        $: () => ({ clientWidth: 1000, clientHeight: 800 }) };
    vm.runInNewContext(source + '\nthis.placeLabels = placeLabels;', context);
    context.placeLabels({ width: 1000, height: 800 });
    return items;
}

test('zoomed out, a repeated directory name is labelled once, by a directory that actually receives the label', () => {
    const module = (id, name, links, x, y) => ({ id, family: 'module', links, hidden: false, node: { name }, lastPosition: { x, y } });
    const items = () => [
        module('offscreen-src', 'src', 50, -2000, -2000), // the best-linked src lies outside the viewport
        module('visible-src', 'src', 5, 600, 360),
        module('another-src', 'src', 4, 600, 700),
        module('tests', 'tests', 3, 900, 360)
    ];
    const zoomedOut = Object.fromEntries(shippedPlaceLabels(.5, items()).map(item => [item.id, item.named]));
    assert.deepEqual(zoomedOut, { 'offscreen-src': false, 'visible-src': true, 'another-src': false, tests: true },
        'an off-screen candidate never reserves the name; the first visible src carries it');
    const zoomedIn = Object.fromEntries(shippedPlaceLabels(.9, items()).map(item => [item.id, item.named]));
    assert.deepEqual(zoomedIn, { 'offscreen-src': false, 'visible-src': true, 'another-src': true, tests: true },
        'closer in, every visible directory keeps its own label');
});

test('no label goes under the camera rail, the kicker or the zoom notice', () => {
    const module = (id, name, links, x, y) => ({ id, family: 'module', links, hidden: false, node: { name }, lastPosition: { x, y } });
    const items = () => [
        module('under-rail', 'VibeRails', 9, 880, 120),   // its label box (x 892 to 964) crosses the rail at x >= 900
        module('under-kicker', 'Tests', 8, 40, 30),       // under REPOSITORY GRAPH at the top left
        module('under-notice', 'UITests', 7, 100, 780),   // under "Zoom in to show…" at the bottom left
        module('clear', 'Board', 6, 500, 400)
    ];
    const rail = { controls: { x: 900, y: 0, width: 100, height: 320 } };
    const named = Object.fromEntries(shippedPlaceLabels(1, items(), rail).map(item => [item.id, item.named]));
    assert.deepEqual(named, { 'under-rail': false, 'under-kicker': false, 'under-notice': false, clear: true });
    const noRail = Object.fromEntries(shippedPlaceLabels(1, items()).map(item => [item.id, item.named]));
    assert.equal(noRail['under-rail'], true, 'without a rail the right edge is free for labels');
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

// Five directories of forty files, each file with one class: enough for a shell with both sides.
function globeFixture(directories = 5, files = 40) {
    const nodes = [];
    for (let g = 0; g < directories; g++) {
        nodes.push({ id: `dir-${g}`, kind: 'module' });
        for (let i = 0; i < files; i++) {
            nodes.push({ id: `f-${g}-${i}`, kind: 'file', parentId: `dir-${g}` });
            nodes.push({ id: `c-${g}-${i}`, kind: 'class', parentId: `f-${g}-${i}` });
        }
    }
    const index = { nodes: new Map(nodes.map(node => [node.id, node])) };
    const edges = nodes.filter(node => node.parentId).map(node => ({ source: node.parentId, target: node.id, kind: 'contains', structural: true }));
    return { nodes, edges, index, positions: layout(nodes, edges, index) };
}

test('the layout is a globe of bubbles: clusters on one shell by turns, each a ball, declarations beside their files', () => {
    const { nodes, positions } = globeFixture();
    const { space } = positions;
    assert.ok(space.radius >= 260, 'the shell has a minimum radius');
    assert.equal(space.focal, space.radius * 3.2);
    assert.ok(Number.isFinite(space.pivot.x) && Number.isFinite(space.pivot.y) && space.pivot.z === 0);
    for (let g = 0; g < 5; g++) {
        const root = positions.get(`dir-${g}`);
        assert.equal(root.tone, g, 'a top-level directory owns the tone of its slot');
        const reach = Math.hypot(root.x - space.pivot.x, root.y - space.pivot.y);
        assert.ok(Math.abs(Math.hypot(reach, root.z) - space.radius) < 1e-6, `cluster ${g} sits on the shell`);
        assert.equal(Math.sign(root.z) || 1, g % 2 ? -1 : 1, `cluster ${g} is on the ${g % 2 ? 'far' : 'near'} side`);
        const members = nodes.filter(node => node.id.startsWith(`f-${g}-`)).map(node => positions.get(node.id));
        const spread = Math.max(...members.map(item => Math.hypot(item.x - root.x, item.y - root.y))) * 1.1;
        assert.ok(members.every(item => item.tone === g && Math.abs(item.z - root.z) <= spread + 1e-6), `cluster ${g} is a ball of its files`);
        assert.ok(members.some(item => item.z > root.z) && members.some(item => item.z < root.z), `cluster ${g} has files on both sides of its centre`);
        for (const file of members) {
            const declaration = positions.get(file.id.replace('f-', 'c-'));
            assert.ok(Math.abs(declaration.z - file.z) <= 15 + 1e-6, 'a declaration stays within 15 units of its file');
            assert.ok(declaration.depthSeed >= 0 && declaration.depthSeed < 1);
        }
    }
    for (const item of positions.values()) assert.ok(Number.isFinite(item.z), 'every point has a finite depth');
});

test('the front view is the flat map exactly; perspective and the fog deepen as the field turns', () => {
    const { positions } = globeFixture(3, 20);
    const { space } = positions, front = { yaw: 0, pitch: 0 };
    assert.equal(Space.TURN_FULL, .6);
    assert.equal(Space.turn(front), 0);
    assert.equal(Space.turn({ yaw: .3, pitch: 0 }), .5, 'halfway to the full turn is half the effect, eased');
    assert.equal(Space.turn({ yaw: 0, pitch: .6 }), 1);
    assert.equal(Space.turn({ yaw: Math.PI, pitch: 0 }), 1, 'the back view is fully turned');
    for (const item of positions.values()) {
        const at = Space.projectPoint(Space.position(item), front, space.pivot, space);
        assert.ok(Math.abs(at.x - item.x) < 1e-9 && Math.abs(at.y - item.y) < 1e-9, 'the front view projects every point to its layout position');
        assert.equal(at.scale, 1, 'no perspective at the front');
        assert.ok(at.brightness >= .62 - 1e-9 && at.brightness <= 1, `front fog stays gentle (${at.brightness})`);
    }
    const near = [...positions.values()].reduce((a, b) => a.z > b.z ? a : b), far = [...positions.values()].reduce((a, b) => a.z < b.z ? a : b);
    const turned = { yaw: 1.2, pitch: .3 };
    const scales = [...positions.values()].map(item => Space.projectPoint(Space.position(item), turned, space.pivot, space).scale);
    assert.ok(Math.max(...scales) > 1 && Math.min(...scales) < 1, 'a turned globe has nearer points larger and farther points smaller');
    assert.ok(scales.every(scale => scale >= .6 && scale <= 1.6));
    const brightness = [...positions.values()].map(item => Space.projectPoint(Space.position(item), turned, space.pivot, space).brightness);
    assert.ok(Math.min(...brightness) >= .45 && Math.min(...brightness) < .62, 'the far side of a turned globe fades to the deeper floor');
    // The front-most point of the front view is the brightest; a half-turn swaps the sides.
    assert.ok(Space.projectPoint(Space.position(near), front, space.pivot, space).brightness > Space.projectPoint(Space.position(far), front, space.pivot, space).brightness);
    assert.ok(Space.projectPoint(Space.position(near), { yaw: Math.PI, pitch: 0 }, space.pivot, space).brightness < Space.projectPoint(Space.position(far), { yaw: Math.PI, pitch: 0 }, space.pivot, space).brightness);
    // Dragging a node holds its camera depth: unproject inverts the projection under any orbit.
    for (const orbit of [front, turned, { yaw: -2.5, pitch: -.4 }]) for (const item of [near, far, positions.get('dir-1')]) {
        const world = Space.position(item), point = Space.projectPoint(world, orbit, space.pivot, space);
        const back = Space.unproject(point, orbit, space.pivot, space);
        for (const axis of ['x', 'y', 'z']) assert.ok(Math.abs(back[axis] - world[axis]) < 1e-6, `unproject restores ${axis} under yaw ${orbit.yaw}`);
    }
    // Without a shell (a camera that predates it), the projection is orthographic with the old fog.
    const flat = Space.projectPoint({ x: 700, y: 400, z: 300 }, turned, { x: 600, y: 400, z: 0 });
    assert.equal(flat.scale, 1);
    assert.ok(flat.brightness > .62 && flat.brightness <= 1);
});
