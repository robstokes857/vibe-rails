import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const viewerPath = path.resolve('VibeRails/wwwroot/js/modules/session-viewer.js');
const moduleUrl = pathToFileURL(viewerPath).href;
const { resolveReplaySeekIndex } = await import(moduleUrl);

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
