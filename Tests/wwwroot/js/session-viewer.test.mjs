import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const viewerPath = path.resolve('VibeRails/wwwroot/js/modules/session-viewer.js');
const moduleUrl = pathToFileURL(viewerPath).href;
const { resolveReplaySeekIndex, showReplayModal } = await import(moduleUrl);

test('the replay title is assigned with textContent, never innerHTML interpolation', () => {
    const source = readFileSync(viewerPath, 'utf8');
    assert.match(source, /titleEl\.textContent = title/);
    assert.doesNotMatch(source, /hdr\.innerHTML\s*=/);
    assert.doesNotMatch(source, /innerHTML = `[^`]*\$\{title\}/);
});

test('seeking past the last frame stays at the completed index', () => {
    const frames = [
        { delayMs: 0 },
        { delayMs: 400 },
        { delayMs: 900 }
    ];
    assert.equal(resolveReplaySeekIndex(frames, 0), 0);
    assert.equal(resolveReplaySeekIndex(frames, 400), 0);
    assert.equal(resolveReplaySeekIndex(frames, 1900), 1);
    assert.equal(resolveReplaySeekIndex(frames, 10_000), frames.length);
});

test('a finished seek must not call play, which would restart from zero', () => {
    const source = readFileSync(viewerPath, 'utf8');
    const seek = source.slice(source.indexOf('if (seekToMs != null)'), source.lastIndexOf('play();') + 8);
    assert.match(seek, /resolveReplaySeekIndex\(frames, seekToMs\)/);
    assert.match(seek, /if \(frameIndex >= frames\.length\)/);
    assert.match(seek, /playBtn\.title = 'Restart'/);
    assert.match(seek, /return;/);
});

test('the modal speed selector reschedules playback and closing cancels it', async t => {
    const originals = Object.fromEntries(['document', 'window', 'fetch', 'performance', 'setTimeout', 'clearTimeout']
        .map(key => [key, Object.getOwnPropertyDescriptor(globalThis, key)]));
    t.after(() => {
        for (const [key, descriptor] of Object.entries(originals)) {
            if (descriptor) Object.defineProperty(globalThis, key, descriptor);
            else delete globalThis[key];
        }
    });
    const nodes = [];
    function element(tag) {
        const node = {
            tag, style: {}, children: [], listeners: {},
            setAttribute() {}, remove() {},
            append(...items) { this.children.push(...items); },
            appendChild(item) { this.children.push(item); },
            addEventListener(event, callback) { this.listeners[event] = callback; },
            get value() { return this.selectedValue ?? this.children.find(child => child.selected)?.value; },
            set value(value) { this.selectedValue = String(value); }
        };
        nodes.push(node);
        return node;
    }
    let now = 0;
    let timerId = 0;
    const timers = new Map();
    const writes = [];
    let disposed = false;
    globalThis.document = { createElement: element, body: element('body') };
    globalThis.window = {
        sessionStorage: { getItem: () => null },
        addEventListener() {}, removeEventListener() {},
        Terminal: class {
            constructor(options) { this.options = options; }
            open() {} resize() {} reset() { writes.length = 0; }
            write(data) { writes.push(data); }
            dispose() { disposed = true; }
        }
    };
    globalThis.fetch = async () => ({ ok: true, json: async () => ({ frames: [0, 400, 800].map(delayMs => ({ delayMs, data: 'YQ==' })) }) });
    Object.defineProperty(globalThis, 'performance', { configurable: true, value: { now: () => now } });
    globalThis.setTimeout = (callback, delay) => { timers.set(++timerId, { callback, at: now + delay }); return timerId; };
    globalThis.clearTimeout = id => timers.delete(id);

    await showReplayModal('debug-session');
    assert.equal(writes.length, 1);
    assert.equal([...timers.values()][0].at, 80, 'the selected default is 5x');
    now = 50;
    const speed = nodes.find(node => node.tag === 'select');
    speed.value = '10';
    speed.listeners.change();
    assert.equal(timers.size, 1);
    assert.equal([...timers.values()][0].at, 65, 'switching to 10x changes the current wait immediately');
    nodes.find(node => node.textContent === '\u00d7').listeners.click();
    assert.equal(timers.size, 0);
    assert.equal(disposed, true);
});
