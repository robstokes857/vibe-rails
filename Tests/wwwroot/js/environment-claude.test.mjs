import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const modules = path.resolve('VibeRails/wwwroot/js/modules');
const { EnvironmentController } = await import(pathToFileURL(path.join(modules, 'environment-controller.js')).href);
const { LLM_MODEL_OPTIONS } = await import(pathToFileURL(path.join(modules, 'llm-model-catalog.js')).href);
const { renderBoardLaunchOptions, normalizeBoardLaunchOptions } = await import(pathToFileURL(path.join(modules, 'board-launch-options.js')).href);

function createController() {
    return new EnvironmentController({
        escapeHtml(value) {
            return String(value)
                .replaceAll('&', '&amp;')
                .replaceAll('<', '&lt;')
                .replaceAll('>', '&gt;')
                .replaceAll('"', '&quot;');
        }
    });
}

// Every 1M-capable model is pinned once, in its "[1m]" form. Behind the VibeRails LLM proxy
// Claude Code budgets 200K for the bare ID, so the [1m] entry is how a launch keeps the 1M window
// (verified 2026-09-27); the bare twin was dropped 2026-09-28 because it was never the right
// choice behind the proxy and only cluttered the dropdown. Haiku 4.5 has no 1M variant.
const PINNED_CLAUDE_MODELS = [
    '',
    'claude-fable-5-1[1m]',
    'claude-fable-5[1m]',
    'claude-opus-5-5[1m]',
    'claude-opus-5[1m]',
    'claude-opus-4-8[1m]',
    'claude-opus-4-7[1m]',
    'claude-sonnet-5[1m]',
    'claude-sonnet-4-6[1m]',
    'claude-haiku-4-5'
];

function optionValues(html, id) {
    const start = html.indexOf(`id="${id}"`);
    const slice = html.slice(start, html.indexOf('</select>', start));
    return [...slice.matchAll(/<option value="([^"]*)"/g)].map(match => match[1]);
}

test('Claude model dropdown lists Opus 5.5 between Fable 5 and Opus 5', () => {
    const html = createController().buildCliSettingsHtml('claude', {});
    assert.deepEqual(optionValues(html, 'claude-model'), PINNED_CLAUDE_MODELS);
    assert.deepEqual(LLM_MODEL_OPTIONS.claude.map(([model]) => model), PINNED_CLAUDE_MODELS);
});

test('pinned Claude IDs are hyphenated, with at most a trailing [1m] suffix', () => {
    // claude-opus-5-5, not claude-opus-5.5. "[1m]" is a documented --model suffix that selects
    // the 1M context window; it is only ever the tail of the ID, never elsewhere.
    for (const [model] of LLM_MODEL_OPTIONS.claude.filter(([model]) => model)) {
        assert.match(model, /^claude-[a-z]+(-\d+)+(\[1m\])?$/, model);
    }
});

test('each 1M-capable Claude model is pinned only in its [1m] form, and Haiku only bare', () => {
    const models = LLM_MODEL_OPTIONS.claude.map(([model]) => model).filter(Boolean);
    for (const model of models.filter(model => model !== 'claude-haiku-4-5')) {
        assert.ok(model.endsWith('[1m]'), `${model} must be pinned as its [1m] form`);
        assert.ok(!models.includes(model.slice(0, -'[1m]'.length)), `${model} must not also be pinned bare`);
    }
    assert.equal(models.at(-1), 'claude-haiku-4-5');
    assert.ok(!models.includes('claude-haiku-4-5[1m]'));
    // The label is the exact --model value, like every other entry in the catalog.
    for (const [model, label] of LLM_MODEL_OPTIONS.claude.filter(([model]) => model)) {
        assert.equal(label, model);
    }
});

test('Default omits --model so Claude Code picks its own default', () => {
    const controller = createController();
    assert.equal(controller.buildClaudeCustomArgs({}), '');
    assert.equal(controller.buildClaudeCustomArgs({ model: '  ' }), '');
    assert.equal(controller.buildClaudeCustomArgs({ model: 'claude-opus-5-5[1m]' }), '--model claude-opus-5-5[1m]');
    assert.equal(controller.buildClaudeCustomArgs({ model: 'claude-haiku-4-5' }), '--model claude-haiku-4-5');
});

test('saved --model claude-opus-5-5[1m] reopens as the pinned entry, not custom', () => {
    const controller = createController();
    const settings = controller.mergeClaudeSettingsFromCustomArgs({}, '--model claude-opus-5-5[1m] --effort high');
    assert.equal(settings.model, 'claude-opus-5-5[1m]');
    assert.equal(controller.buildClaudeCustomArgs(settings), '--model claude-opus-5-5[1m] --effort high');

    const html = controller.buildCliSettingsHtml('claude', settings);
    assert.match(html, /<option value="claude-opus-5-5\[1m\]" selected>claude-opus-5-5\[1m\]<\/option>/);
    assert.doesNotMatch(html, /\(custom\)/);
});

test('a bare 1M-capable ID saved before 2026-09-28 upgrades to its pinned [1m] form on reopen', () => {
    // Environments saved --model claude-opus-5-5 while the dropdown still offered the bare form.
    // Behind the proxy that ID budgets 200K, so reopening selects the [1m] entry and the next save
    // writes it back; Haiku and unknown IDs are left alone (unknown ones still render as custom).
    const controller = createController();
    const settings = controller.mergeClaudeSettingsFromCustomArgs({}, '--model claude-opus-5-5 --effort high');
    assert.equal(settings.model, 'claude-opus-5-5[1m]');
    assert.equal(controller.buildClaudeCustomArgs(settings), '--model claude-opus-5-5[1m] --effort high');
    assert.doesNotMatch(controller.buildCliSettingsHtml('claude', settings), /\(custom\)/);
    assert.equal(controller.normalizeClaudeModel('claude-haiku-4-5'), 'claude-haiku-4-5');
    assert.equal(controller.normalizeClaudeModel(' claude-opus-4-5 '), 'claude-opus-4-5');
    assert.match(controller.buildCliSettingsHtml('claude', { model: 'claude-opus-4-5' }), /claude-opus-4-5 \(custom\)/);

    // Board cards saved with the bare ID reopen the same way.
    assert.equal(normalizeBoardLaunchOptions('base:claude', { model: 'claude-fable-5-1' }).model, 'claude-fable-5-1[1m]');
    assert.match(renderBoardLaunchOptions('base:claude', { model: 'claude-fable-5-1' }), /value="claude-fable-5-1\[1m\]" selected/);
    assert.doesNotMatch(renderBoardLaunchOptions('base:claude', { model: 'claude-fable-5-1' }), /\(custom\)/);
    assert.equal(normalizeBoardLaunchOptions('base:claude', { model: 'claude-haiku-4-5' }).model, 'claude-haiku-4-5');
    assert.equal(normalizeBoardLaunchOptions('base:codex', { model: 'gpt-5.5' }).model, 'gpt-5.5');
});

test('Board base Claude launch can select Opus 5.5', () => {
    assert.match(renderBoardLaunchOptions('base:claude', { model: 'claude-opus-5-5[1m]' }), /value="claude-opus-5-5\[1m\]" selected/);
    assert.equal(normalizeBoardLaunchOptions('base:claude', { model: 'claude-opus-5-5[1m]' }).model, 'claude-opus-5-5[1m]');
});

test('Board base Claude launch keeps the 1M-context form and it reopens as pinned', () => {
    const html = renderBoardLaunchOptions('base:claude', { model: 'claude-fable-5-1[1m]' });
    assert.match(html, /<option value="claude-fable-5-1\[1m\]" selected>claude-fable-5-1\[1m\]<\/option>/);
    assert.doesNotMatch(html, /\(custom\)/);
    assert.equal(normalizeBoardLaunchOptions('base:claude', { model: 'claude-fable-5-1[1m]' }).model, 'claude-fable-5-1[1m]');

    const controller = createController();
    const settings = controller.mergeClaudeSettingsFromCustomArgs({}, '--model claude-fable-5-1[1m] --effort xhigh');
    assert.equal(settings.model, 'claude-fable-5-1[1m]');
    assert.equal(controller.buildClaudeCustomArgs(settings), '--model claude-fable-5-1[1m] --effort xhigh');
    assert.doesNotMatch(controller.buildCliSettingsHtml('claude', settings), /\(custom\)/);
});
