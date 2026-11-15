import test from 'node:test';
import assert from 'node:assert/strict';
import { applyRemoteModelCatalog, renderLlmModelOptions, refreshLlmModelCatalog, codexModelSupportsSpeed } from '../../../VibeRails/wwwroot/js/modules/llm-model-catalog.js';

test('remote names replace pinned lists, preserve saved custom choices and escape labels', () => {
    assert.equal(applyRemoteModelCatalog({ version: 1, revision: 2, models: [
        { cli: 'codex', value: 'gpt-67', label: '<Codex 67>' },
        { cli: 'claude', value: 'claude-fable-67[1m]', label: 'Fable 67' }
    ] }), true);
    const rendered = renderLlmModelOptions('codex', 'gpt-old');
    assert.match(rendered, /&lt;Codex 67&gt;/);
    assert.match(rendered, /gpt-old \(custom\)/);
    assert.doesNotMatch(rendered, /gpt-6-astra/);
    assert.equal(codexModelSupportsSpeed('gpt-67', 'ultrafast'), false, 'names never create new launch capabilities');
    assert.equal(applyRemoteModelCatalog({ version: 1, revision: 1, models: [] }), false, 'old revisions cannot roll back');
    assert.equal(applyRemoteModelCatalog({ version: 1, revision: 3, models: [{ cli: '__proto__', value: 'bad', label: 'Bad' }] }), false);
    assert.match(renderLlmModelOptions('codex'), /gpt-67/);
    assert.equal(applyRemoteModelCatalog({ version: 1, revision: 3, models: [] }), true);
    assert.doesNotMatch(renderLlmModelOptions('codex'), /gpt-67/, 'empty is a valid managed list');
});

test('concurrent UI refreshes share one authenticated local read', async () => {
    let calls = 0;
    let finish;
    const app = { apiCall(path) { calls++; assert.equal(path, '/api/v1/llm-picker/models'); return new Promise(resolve => { finish = resolve; }); } };
    const first = refreshLlmModelCatalog(app);
    const second = refreshLlmModelCatalog(app);
    await Promise.resolve();
    finish({ version: 1, revision: 4, models: [{ cli: 'codex', value: 'gpt-68', label: 'Next' }] });
    await Promise.all([first, second]);
    await refreshLlmModelCatalog(app);
    assert.equal(calls, 1);
    assert.match(renderLlmModelOptions('codex'), /gpt-68/);
});
