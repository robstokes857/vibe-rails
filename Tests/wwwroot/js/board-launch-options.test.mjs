import test from 'node:test';
import assert from 'node:assert/strict';
import { pathToFileURL } from 'node:url';
import path from 'node:path';

const modules = path.resolve('VibeRails/wwwroot/js/modules');
const { renderBoardLaunchOptions, normalizeBoardLaunchOptions, readBoardLaunchOptions, bindBoardLaunchOptions } = await import(pathToFileURL(path.join(modules, 'board-launch-options.js')));
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
    for (const cli of ['claude', 'codex', 'antigravity', 'copilot', 'grok', 'opencode', 'glm-5.2', 'glm-5.3', 'deepseek-v4-pro', 'kimi-k3']) {
        assert.match(renderBoardLaunchOptions(`base:${cli}`), /data-board-launch-yolo/);
        assert.equal(normalizeBoardLaunchOptions(`base:${cli}`, { yolo: true }).yolo, true);
    }
    assert.match(renderBoardLaunchOptions('base:codex', { yolo: true }), /data-board-launch-yolo checked/);
    assert.equal(normalizeBoardLaunchOptions('base:codex', { yolo: false }).yolo, false);
    const values = new Map([
        ['[data-board-launch-model]', { value: 'gpt-6-astra' }],
        ['[data-board-launch-effort]', { value: 'high' }],
        ['[data-board-launch-yolo]', { checked: true }]
    ]);
    assert.deepEqual(readBoardLaunchOptions({ querySelector: selector => values.get(selector) || null }, 'base:codex'), {
        model: 'gpt-6-astra', effort: 'high', mode: '', yolo: true, speed: ''
    });
});

test('model lists come from the exact same catalog as the environment editor', () => {
    const controller = new EnvironmentController({});
    for (const [cli, method] of [['codex', 'renderCodexModelOptions'], ['claude', 'renderClaudeModelOptions'], ['copilot', 'renderCopilotModelOptions'], ['antigravity', 'renderAntigravityModelOptions'], ['opencode', 'renderOpencodeModelOptions'], ['grok', 'renderGrokModelOptions']]) {
        assert.equal(controller[method](''), renderLlmModelOptions(cli));
        for (const [model] of LLM_MODEL_OPTIONS[cli]) assert.ok(renderBoardLaunchOptions(`base:${cli}`).includes(`value="${model}"`), `${cli}: ${model}`);
    }
    assert.deepEqual(LLM_MODEL_OPTIONS.codex.map(([model]) => model), ['', 'gpt-6-astra', 'gpt-6.1-sol', 'gpt-6-sol', 'gpt-6-luna', 'gpt-5.6-sol', 'gpt-5.6-terra', 'gpt-5.6-luna', 'gpt-5.5']);
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

test('Grok model is selectable and effort is low/medium/high/xhigh', () => {
    const html = renderBoardLaunchOptions('base:grok');
    assert.match(html, /data-board-launch-model/);
    assert.match(html, /value="grok-4.7"/);
    assert.match(html, /value="grok-4.6"/);
    assert.doesNotMatch(html, /Fixed model/);
    assert.match(html, /value="low"/);
    assert.match(html, /value="xhigh"/);
    assert.doesNotMatch(html, /value="none"/);
    assert.doesNotMatch(html, /value="minimal"/);
    assert.doesNotMatch(html, /value="max"/);
    assert.equal(normalizeBoardLaunchOptions('base:grok', { model: 'grok-4.7', effort: 'xhigh' }).model, 'grok-4.7');
    assert.equal(normalizeBoardLaunchOptions('base:grok', { effort: 'xhigh' }).effort, 'xhigh');
    assert.equal(normalizeBoardLaunchOptions('base:grok', { effort: 'none' }).effort, '');
    assert.match(renderBoardLaunchOptions('base:grok-4.6'), /value="grok-4.7"/);
    assert.equal(normalizeBoardLaunchOptions('base:grok-4.6', { effort: 'high' }).effort, 'high');
});

test('Codex max effort normalizes when selecting gpt-5.5', () => {
    assert.equal(normalizeBoardLaunchOptions('base:codex', { model: 'gpt-5.5', effort: 'max' }).effort, 'xhigh');
    const max = { disabled: false };
    const effort = { value: 'max', querySelector: () => max };
    const model = { value: 'gpt-5.5', addEventListener() {}, removeEventListener() {} };
    bindBoardLaunchOptions({ querySelector: selector => selector.includes('model') ? model : selector.includes('effort') ? effort : null }, 'base:codex')();
    assert.equal(effort.value, 'xhigh');
    assert.equal(max.disabled, true);
});

test('saved/custom model text is escaped before becoming dropdown markup', () => {
    const html = renderBoardLaunchOptions('base:claude', { model: '"><img src=x onerror=alert(1)>' });
    assert.doesNotMatch(html, /<img|<script/);
    assert.match(html, /&lt;img/);
});

test('Codex speeds follow model capabilities and round-trip through the form', () => {
    for (const model of ['gpt-6-astra', 'gpt-6.1-sol', 'gpt-6-sol', 'gpt-6-luna', 'gpt-5.6-sol', 'gpt-5.6-terra', 'gpt-5.6-luna', 'gpt-5.5']) {
        assert.equal(normalizeBoardLaunchOptions('base:codex', { model, speed: 'fast' }).speed, 'fast');
        assert.equal(normalizeBoardLaunchOptions('base:codex', { model, speed: 'ultrafast' }).speed,
            model === 'gpt-6-astra' ? 'ultrafast' : '');
    }
    for (const model of ['', 'unknown-model', 'constructor', '__proto__']) {
        for (const speed of ['fast', 'ultrafast', 'turbo']) {
            assert.equal(normalizeBoardLaunchOptions('base:codex', { model, speed }).speed, '');
        }
    }
    assert.equal(normalizeBoardLaunchOptions('base:claude', { model: 'gpt-6-astra', speed: 'fast' }).speed, '');
    assert.doesNotMatch(renderBoardLaunchOptions('base:claude'), /data-board-launch-speed/);
    assert.match(renderBoardLaunchOptions('base:codex', { model: 'gpt-6-astra', speed: 'ultrafast' }), /value="ultrafast" selected/);
    const values = new Map([
        ['[data-board-launch-model]', { value: 'gpt-6-astra' }],
        ['[data-board-launch-speed]', { value: 'ultrafast' }]
    ]);
    assert.equal(readBoardLaunchOptions({ querySelector: key => values.get(key) }, 'base:codex').speed, 'ultrafast');
});

test('changing the Codex model clears unsupported speeds and disposes the listener', () => {
    let listener;
    const model = { value: 'gpt-6-astra', addEventListener: (_, fn) => { listener = fn; },
        removeEventListener: (_, fn) => { assert.equal(fn, listener); listener = null; } };
    const fast = { disabled: false }, ultrafast = { disabled: false };
    const speed = { value: 'ultrafast', querySelector: selector => selector.includes('"fast"') ? fast : ultrafast };
    const dispose = bindBoardLaunchOptions({ querySelector: selector => selector.includes('model') ? model : selector.includes('speed') ? speed : null }, 'base:codex');
    assert.equal(speed.value, 'ultrafast');
    model.value = 'gpt-6.1-sol';
    listener();
    assert.equal(speed.value, '');
    assert.equal(ultrafast.disabled, true);
    assert.equal(fast.disabled, false);
    speed.value = 'fast';
    model.value = '';
    listener();
    assert.equal(speed.value, '');
    assert.equal(fast.disabled, true);
    dispose();
    assert.equal(listener, null);
});
