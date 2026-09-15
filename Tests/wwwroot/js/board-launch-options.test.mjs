import test from 'node:test';
import assert from 'node:assert/strict';
import { pathToFileURL } from 'node:url';
import path from 'node:path';

const modules = path.resolve('VibeRails/wwwroot/js/modules');
const { renderBoardLaunchOptions, normalizeBoardLaunchOptions, bindBoardLaunchOptions } = await import(pathToFileURL(path.join(modules, 'board-launch-options.js')));
const { LLM_MODEL_OPTIONS, renderLlmModelOptions } = await import(pathToFileURL(path.join(modules, 'llm-model-catalog.js')));
const { EnvironmentController } = await import(pathToFileURL(path.join(modules, 'environment-controller.js')));

test('base selections expose provider options; saved environments keep their settings', () => {
    assert.equal(renderBoardLaunchOptions('env:7:codex'), '');
    assert.equal(normalizeBoardLaunchOptions('env:7:codex', { model: 'gpt-5.5' }), null);
    assert.match(renderBoardLaunchOptions('base:codex'), /data-board-launch-effort/);
    // Codex offers no Start mode at all: /plan is a TUI command with no launch flag, and nothing
    // types into a running TUI. Every mode the board offers becomes a real CLI argument.
    // The delimiter matters: data-board-launch-model has data-board-launch-mode as a prefix.
    assert.doesNotMatch(renderBoardLaunchOptions('base:codex'), /data-board-launch-mode[\s>]/);
    assert.doesNotMatch(renderBoardLaunchOptions('base:codex'), /Start mode/);
    assert.equal(normalizeBoardLaunchOptions('base:codex', { mode: 'plan' }).mode, '');
    assert.match(renderBoardLaunchOptions('base:copilot'), /value="autopilot"/);
    assert.match(renderBoardLaunchOptions('base:antigravity'), /value="accept-edits"/);
});

test('model lists come from the exact same catalog as the environment editor', () => {
    const controller = new EnvironmentController({});
    for (const [cli, method] of [['codex', 'renderCodexModelOptions'], ['claude', 'renderClaudeModelOptions'], ['copilot', 'renderCopilotModelOptions'], ['antigravity', 'renderAntigravityModelOptions'], ['opencode', 'renderOpencodeModelOptions']]) {
        assert.equal(controller[method](''), renderLlmModelOptions(cli));
        for (const [model] of LLM_MODEL_OPTIONS[cli]) assert.ok(renderBoardLaunchOptions(`base:${cli}`).includes(`value="${model}"`), `${cli}: ${model}`);
    }
    assert.deepEqual(LLM_MODEL_OPTIONS.codex.map(([model]) => model), ['', 'gpt-6-astra', 'gpt-5.6-sol', 'gpt-5.6-terra', 'gpt-5.6-luna', 'gpt-5.5']);
});

test('OpenCode-backed providers display fixed models and omit unsupported effort', () => {
    for (const cli of ['glm-5.2', 'glm-5.3', 'deepseek-v4-pro', 'kimi-k3']) {
        const html = renderBoardLaunchOptions(`base:${cli}`);
        assert.match(html, /Fixed model/);
        assert.doesNotMatch(html, /data-board-launch-effort/);
        assert.match(html, /value="build"/);
        assert.equal(normalizeBoardLaunchOptions(`base:${cli}`, { model: 'wrong', effort: 'high' }).model, '');
    }
});

test('Codex max effort normalizes when selecting gpt-5.5', () => {
    assert.equal(normalizeBoardLaunchOptions('base:codex', { model: 'gpt-5.5', effort: 'max' }).effort, 'xhigh');
    const max = { disabled: false };
    const effort = { value: 'max', querySelector: () => max };
    const model = { value: 'gpt-5.5', addEventListener() {}, removeEventListener() {} };
    bindBoardLaunchOptions({ querySelector: selector => selector.includes('model') ? model : effort }, 'base:codex')();
    assert.equal(effort.value, 'xhigh');
    assert.equal(max.disabled, true);
});

test('saved/custom model text is escaped before becoming dropdown markup', () => {
    const html = renderBoardLaunchOptions('base:claude', { model: '"><img src=x onerror=alert(1)>' });
    assert.doesNotMatch(html, /<img|<script/);
    assert.match(html, /&lt;img/);
});
