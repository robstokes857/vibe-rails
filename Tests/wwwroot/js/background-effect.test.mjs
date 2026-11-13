import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../../../VibeRails/wwwroot/js/background-effect.js', import.meta.url), 'utf8');

// A minimal page: one canvas whose 2D context counts drawn particles, a manual animation-frame
// queue, and a MutationObserver the test can trigger after toggling <html> classes.
function loadPage(seed = 1) {
    const frames = new Map();
    let nextFrame = 1;
    const observers = [];
    const htmlClasses = new Set();
    const listeners = {};
    const counts = { arcs: [], clears: 0 };
    const context = {
        clearRect() { counts.clears++; counts.arcs = []; },
        beginPath() {}, arc(x, y) { counts.arcs.push([x, y]); }, fill() {}, stroke() {}, moveTo() {}, lineTo() {}
    };
    // Seeded, so two pages start with the same particles.
    let state = seed;
    const seededMath = Object.create(Math);
    seededMath.random = () => ((state = (state * 1103515245 + 12345) % 2147483648) / 2147483648);
    const canvas = { style: {}, getContext: () => context };
    const document = {
        hidden: false,
        createElement: () => canvas,
        body: { prepend() {} },
        documentElement: { classList: { contains: name => htmlClasses.has(name) } },
        addEventListener(type, handler) { listeners[type] = handler; }
    };
    const window = { innerWidth: 1200, innerHeight: 800, addEventListener() {}, VibeRailsPerformance: null };
    const sandbox = {
        window, document, Math: seededMath, Array,
        getComputedStyle: () => ({ getPropertyValue: () => '' }),
        requestAnimationFrame(callback) { const id = nextFrame++; frames.set(id, callback); return id; },
        cancelAnimationFrame(id) { frames.delete(id); },
        MutationObserver: class {
            constructor(callback) { this.callback = callback; observers.push(this); }
            observe() {}
        }
    };
    vm.runInNewContext(source, sandbox);
    return {
        counts,
        pending: () => frames.size,
        // Runs every queued frame at the given time, as the browser would at one vsync.
        frame(timestamp) {
            const queued = [...frames.values()];
            frames.clear();
            for (const callback of queued) callback(timestamp);
        },
        setViewClass(name, on) {
            if (on) htmlClasses.add(name); else htmlClasses.delete(name);
            for (const observer of observers) observer.callback([]);
        }
    };
}

test('draws at about 30 fps on a 60 Hz display', () => {
    const page = loadPage();
    for (let i = 0; i <= 60; i++) page.frame(i * (1000 / 60));
    // One second of 60 Hz frames: the first draws immediately, then every other frame.
    assert.ok(page.counts.clears >= 30 && page.counts.clears <= 32, `drew ${page.counts.clears} frames`);
});

test('keeps particle speed: a frame advances the particles by the 60 Hz steps that elapsed', () => {
    const sixty = loadPage(7);
    for (let i = 0; i <= 60; i++) sixty.frame(i * (1000 / 60));
    const hundredTwenty = loadPage(7);
    for (let i = 0; i <= 120; i++) hundredTwenty.frame(i * (1000 / 120));
    // A 120 Hz display also draws about 30 times a second, and both end one second of motion in the same place.
    assert.ok(hundredTwenty.counts.clears >= 30 && hundredTwenty.counts.clears <= 32, `drew ${hundredTwenty.counts.clears} frames`);
    assert.equal(sixty.counts.arcs.length, 82);
    assert.deepEqual(hundredTwenty.counts.arcs, sixty.counts.arcs);
});

test('pauses while terminal focus covers the canvas and resumes when it leaves', () => {
    const page = loadPage();
    page.frame(0);
    assert.equal(page.pending(), 1);

    page.setViewClass('vb-terminal-focus-active', true);
    assert.equal(page.pending(), 0, 'no frame stays queued behind the terminal');
    const drawn = page.counts.clears;
    page.frame(1000);
    assert.equal(page.counts.clears, drawn);

    page.setViewClass('vb-terminal-focus-active', false);
    assert.equal(page.pending(), 1);
    page.frame(5000);
    assert.equal(page.counts.clears, drawn + 1, 'the first frame after resuming draws at once');
});
