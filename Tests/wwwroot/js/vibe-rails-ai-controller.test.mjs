import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';

const modulePath = path.resolve('VibeRails/wwwroot/js/modules/vibe-rails-ai-controller.js');

test('Vibe AI learning card separates LLM retrieval savings from Token Saver', () => {
    const source = readFileSync(modulePath, 'utf8');

    assert.match(source, /LLM tokens saved/);
    assert.match(source, /excludes Token Saver/);
    assert.match(source, /all-time tokens kept off the LLM/);
    assert.match(source, /\/api\/v1\/token-savings/);
    assert.doesNotMatch(source, /<div class="k">Tokens Saved<\/div>/);
});
