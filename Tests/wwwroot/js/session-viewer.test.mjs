import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';

const viewerPath = path.resolve('VibeRails/wwwroot/js/modules/session-viewer.js');
// Browser lifecycle and seek coverage lives in UITests/tests/session-viewer.spec.js.

test('the replay title is assigned with textContent, never innerHTML interpolation', () => {
    const source = readFileSync(viewerPath, 'utf8');
    assert.match(source, /titleEl\.textContent = title/);
    assert.doesNotMatch(source, /hdr\.innerHTML\s*=/);
    assert.doesNotMatch(source, /innerHTML = `[^`]*\$\{title\}/);
});
