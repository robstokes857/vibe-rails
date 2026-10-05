import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { maxSpeedTarget, upperBound } from '../../../VibeRails/wwwroot/session-replay/timeline.mjs';

const read = path => readFileSync(new URL(`../../../VibeRails/wwwroot/${path}`, import.meta.url), 'utf8');
const frames = sizes => sizes.map((size, index) => ({ at: 1000 + index * 100, data: new Uint8Array(size) }));

test('max speed writes bounded batches and then lands on the recording end', () => {
    const recording = frames([100, 100, 100, 100, 100]);
    let frameIndex = 0, position = 1000;
    const targets = [];
    while (position < 9000) {
        position = maxSpeedTarget(recording, frameIndex, 9000, 250);
        frameIndex = upperBound(recording, position);
        targets.push(position);
    }
    // 250 bytes takes three 100-byte frames, then the remaining two, then the end.
    assert.deepEqual(targets, [1200, 9000]);
    assert.equal(frameIndex, recording.length);
});

test('max speed always advances, even past a frame larger than the budget', () => {
    const recording = frames([10, 1_000_000, 10]);
    assert.equal(maxSpeedTarget(recording, 0, 5000, 64), 1100);
    assert.equal(maxSpeedTarget(recording, 1, 5000, 64), 1100);
    assert.equal(maxSpeedTarget(recording, 2, 5000, 64), 5000);
    assert.equal(maxSpeedTarget([], 0, 5000), 5000);
});

test('the only replay view is the full one, and Max replaces 100x', () => {
    const html = read('session-replay/index.html'), css = read('session-replay/style.css'), app = read('session-replay/app.js');
    for (const retired of ['view-simple', 'view-advanced', 'view-switch', 'advanced-only', 'data-view']) {
        assert.ok(!html.includes(retired), `index.html still has ${retired}`);
        assert.ok(!css.includes(retired), `style.css still has ${retired}`);
    }
    assert.doesNotMatch(app, /setView|viewMode|replay-view/);
    const speeds = [...html.match(/<select id="speed"[^]*?<\/select>/)[0].matchAll(/value="([^"]+)"/g)].map(match => match[1]);
    assert.deepEqual(speeds, ['1', '2', '5', '10', '25', 'max']);
    assert.match(app, /\[1,2,5,10,25,'max'\]\.includes\(value\)/);
});

test('the replay declares the app theme tokens with the app values', () => {
    const appRoot = read('style.css').match(/:root\s*{([^}]*)}/)[1];
    const replay = read('session-replay/style.css');
    const tokens = ['--color-bg-base', '--color-bg-surface', '--color-bg-surface-hover', '--color-bg-elevated', '--color-primary',
        '--color-primary-dark', '--color-accent', '--color-text', '--color-text-muted', '--color-success', '--color-warning',
        '--color-danger', '--color-border', '--border-radius'];
    for (const token of tokens) {
        const pattern = new RegExp(`${token}:\\s*([^;]+);`);
        const expected = appRoot.match(pattern)?.[1].trim();
        assert.ok(expected, `app style.css no longer declares ${token}`);
        assert.equal(replay.match(pattern)?.[1].trim(), expected, `${token} differs from the app theme`);
    }
    // The previous website-styled palette must not creep back in.
    assert.doesNotMatch(replay, /#b4a3ff|#a89bff|linear-gradient/i);
});
