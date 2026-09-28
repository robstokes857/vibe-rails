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

// Every 1M-capable model is pinned twice: the bare ID and its "[1m]" form. Behind the VibeRails
// LLM proxy Claude Code budgets 200K for the bare ID, so the [1m] entry is how a Board launch
// keeps the 1M window (verified 2026-09-27). Haiku 4.5 has no 1M variant.
const PINNED_CLAUDE_MODELS = [
    '',
    'claude-fable-5-1',
    'claude-fable-5-1[1m]',
    'claude-fable-5',
    'claude-fable-5[1m]',
    'claude-opus-5-5',
    'claude-opus-5-5[1m]',
    'claude-opus-5',
    'claude-opus-5[1m]',
    'claude-opus-4-8',
    'claude-opus-4-8[1m]',
    'claude-opus-4-7',
    'claude-opus-4-7[1m]',
    'claude-sonnet-5',
    'claude-sonnet-5[1m]',
    'claude-sonnet-4-6',
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

test('each 1M-capable Claude model is followed by its [1m] entry, and Haiku has none', () => {
    const models = LLM_MODEL_OPTIONS.claude.map(([model]) => model).filter(Boolean);
    for (const model of models.filter(model => !model.endsWith('[1m]') && model !== 'claude-haiku-4-5')) {
        assert.equal(models[models.indexOf(model) + 1], `${model}[1m]`, `${model} needs a [1m] entry right after it`);
    }
    assert.equal(models.at(-1), 'claude-haiku-4-5');
    assert.ok(!models.includes('claude-haiku-4-5[1m]'));
    for (const [model, label] of LLM_MODEL_OPTIONS.claude.filter(([model]) => model.endsWith('[1m]'))) {
        assert.equal(label, `${model} (1M context)`);
    }
});

test('Default omits --model so Claude Code picks its own default', () => {
    const controller = createController();
    assert.equal(controller.buildClaudeCustomArgs({}), '');
    assert.equal(controller.buildClaudeCustomArgs({ model: '  ' }), '');
    assert.equal(controller.buildClaudeCustomArgs({ model: 'claude-opus-5-5' }), '--model claude-opus-5-5');
});

test('saved --model claude-opus-5-5 reopens as the pinned entry, not custom', () => {
    const controller = createController();
    const settings = controller.mergeClaudeSettingsFromCustomArgs({}, '--model claude-opus-5-5 --effort high');
    assert.equal(settings.model, 'claude-opus-5-5');
    assert.equal(controller.buildClaudeCustomArgs(settings), '--model claude-opus-5-5 --effort high');

    const html = controller.buildCliSettingsHtml('claude', settings);
    assert.match(html, /<option value="claude-opus-5-5" selected>claude-opus-5-5<\/option>/);
    assert.doesNotMatch(html, /\(custom\)/);
});

test('Board base Claude launch can select Opus 5.5', () => {
    assert.match(renderBoardLaunchOptions('base:claude', { model: 'claude-opus-5-5' }), /value="claude-opus-5-5" selected/);
    assert.equal(normalizeBoardLaunchOptions('base:claude', { model: 'claude-opus-5-5' }).model, 'claude-opus-5-5');
});

test('Board base Claude launch can select the 1M-context form and it reopens as pinned', () => {
    const html = renderBoardLaunchOptions('base:claude', { model: 'claude-fable-5-1[1m]' });
    assert.match(html, /<option value="claude-fable-5-1\[1m\]" selected>claude-fable-5-1\[1m\] \(1M context\)<\/option>/);
    assert.doesNotMatch(html, /\(custom\)/);
    assert.equal(normalizeBoardLaunchOptions('base:claude', { model: 'claude-fable-5-1[1m]' }).model, 'claude-fable-5-1[1m]');

    const controller = createController();
    const settings = controller.mergeClaudeSettingsFromCustomArgs({}, '--model claude-fable-5-1[1m] --effort xhigh');
    assert.equal(settings.model, 'claude-fable-5-1[1m]');
    assert.equal(controller.buildClaudeCustomArgs(settings), '--model claude-fable-5-1[1m] --effort xhigh');
    assert.doesNotMatch(controller.buildCliSettingsHtml('claude', settings), /\(custom\)/);
});
