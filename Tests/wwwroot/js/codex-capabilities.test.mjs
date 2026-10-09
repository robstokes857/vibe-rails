import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { codexModelSupportsSpeed } from '../../../VibeRails/wwwroot/js/modules/llm-model-catalog.js';

test('speed validation reads the shared catalog and rejects unknown models and speeds', async () => {
    const catalog = JSON.parse(await readFile('VibeRails/wwwroot/js/modules/codex-model-capabilities.json', 'utf8'));
    for (const [model, speeds] of Object.entries(catalog)) {
        for (const speed of ['fast', 'ultrafast', 'turbo']) {
            assert.equal(codexModelSupportsSpeed(` ${model.toUpperCase()} `, speed), speeds.includes(speed));
        }
    }
    for (const model of ['', 'custom', '__proto__', 'toString']) {
        assert.equal(codexModelSupportsSpeed(model, 'fast'), false);
    }
});
