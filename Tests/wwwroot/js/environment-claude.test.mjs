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

// Every pinned model is its "[1m]" form. Behind the VibeRails LLM proxy Claude Code budgeted 200K
// for the bare ID, so the [1m] entry is how a launch keeps the 1M window (verified 2026-09-27); the
// bare twin was dropped 2026-09-28. Only generation-5 models are pinned (owner request 2026-10-02:
// Opus 4.8/4.7, Sonnet 4.6 and Haiku 4.5 removed), and each is labelled by name, not by its ID.
const PINNED_CLAUDE_MODELS = [
    '',
    'claude-fable-5-1[1m]',
    'claude-fable-5[1m]',
    'claude-opus-5-5[1m]',
    'claude-opus-5[1m]',
    'claude-sonnet-5-5[1m]',
    'claude-sonnet-5[1m]'
];
const CLAUDE_MODEL_LABELS = ['Default (Claude recommended)', 'Fable 5.1', 'Fable 5', 'Opus 5.5', 'Opus 5', 'Sonnet 5.5', 'Sonnet 5'];

// Visible <option> text only; values legitimately carry the [1m] launch suffix.
function optionTexts(html) {
    return [...html.matchAll(/<option[^>]*>([^<]*)<\/option>/g)].map(match => match[1]);
}

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

test('each Claude model is pinned only in its [1m] form and labelled by its name', () => {
    const models = LLM_MODEL_OPTIONS.claude.map(([model]) => model).filter(Boolean);
    for (const model of models) {
        assert.ok(model.endsWith('[1m]'), `${model} must be pinned as its [1m] form`);
        assert.ok(!models.includes(model.slice(0, -'[1m]'.length)), `${model} must not also be pinned bare`);
    }
    // The value is the --model argument; the label is what people read, so it never shows the ID.
    assert.deepEqual(LLM_MODEL_OPTIONS.claude.map(([, label]) => label), CLAUDE_MODEL_LABELS);
    const html = createController().buildCliSettingsHtml('claude', {});
    for (const text of [...optionTexts(html), ...optionTexts(renderBoardLaunchOptions('base:claude'))]) {
        assert.doesNotMatch(text, /\[1m\]|claude-/, text);
    }
});

test('a save on a model older than generation 5 still opens, as custom, and keeps its value', () => {
    // The dropdown no longer offers Opus 4.8, but an environment or card saved with it is not
    // rewritten: it reopens as "(custom)" and launches the same --model until someone picks again.
    const controller = createController();
    const settings = controller.mergeClaudeSettingsFromCustomArgs({}, '--model claude-opus-4-8[1m] --effort high');
    assert.equal(settings.model, 'claude-opus-4-8[1m]');
    assert.equal(controller.buildClaudeCustomArgs(settings), '--model claude-opus-4-8[1m] --effort high');
    assert.match(controller.buildCliSettingsHtml('claude', settings), /<option value="claude-opus-4-8\[1m\]" selected>claude-opus-4-8\[1m\] \(custom\)<\/option>/);
    assert.equal(normalizeBoardLaunchOptions('base:claude', { model: 'claude-opus-4-8' }).model, 'claude-opus-4-8');
    assert.match(renderBoardLaunchOptions('base:claude', { model: 'claude-opus-4-8' }), /claude-opus-4-8 \(custom\)/);
});

test('Default omits --model so Claude Code picks its own default', () => {
    const controller = createController();
    assert.equal(controller.buildClaudeCustomArgs({}), '');
    assert.equal(controller.buildClaudeCustomArgs({ model: '  ' }), '');
    assert.equal(controller.buildClaudeCustomArgs({ model: 'claude-opus-5-5[1m]' }), '--model claude-opus-5-5[1m]');
});

test('saved --model claude-opus-5-5[1m] reopens as the pinned entry, not custom', () => {
    const controller = createController();
    const settings = controller.mergeClaudeSettingsFromCustomArgs({}, '--model claude-opus-5-5[1m] --effort high');
    assert.equal(settings.model, 'claude-opus-5-5[1m]');
    assert.equal(controller.buildClaudeCustomArgs(settings), '--model claude-opus-5-5[1m] --effort high');

    const html = controller.buildCliSettingsHtml('claude', settings);
    assert.match(html, /<option value="claude-opus-5-5\[1m\]" selected>Opus 5\.5<\/option>/);
    assert.doesNotMatch(html, /\(custom\)/);
});

test('a bare 1M-capable ID saved before 2026-09-28 upgrades to its pinned [1m] form on reopen', () => {
    // Environments saved --model claude-opus-5-5 while the dropdown still offered the bare form.
    // Behind the proxy that ID budgets 200K, so reopening selects the [1m] entry and the next save
    // writes it back; unpinned IDs are left alone and still render as custom.
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

test('Sonnet 5.5 sits above Sonnet 5, saves as its [1m] form, and a bare saved ID reopens as pinned', () => {
    const models = LLM_MODEL_OPTIONS.claude.map(([model]) => model);
    assert.equal(models.indexOf('claude-sonnet-5-5[1m]') + 1, models.indexOf('claude-sonnet-5[1m]'));

    const controller = createController();
    const settings = controller.mergeClaudeSettingsFromCustomArgs({}, '--model claude-sonnet-5-5 --effort medium');
    assert.equal(settings.model, 'claude-sonnet-5-5[1m]');
    assert.equal(controller.buildClaudeCustomArgs(settings), '--model claude-sonnet-5-5[1m] --effort medium');
    assert.match(controller.buildCliSettingsHtml('claude', settings), /<option value="claude-sonnet-5-5\[1m\]" selected>/);
    assert.doesNotMatch(controller.buildCliSettingsHtml('claude', settings), /\(custom\)/);

    assert.equal(normalizeBoardLaunchOptions('base:claude', { model: 'claude-sonnet-5-5[1m]' }).model, 'claude-sonnet-5-5[1m]');
    assert.match(renderBoardLaunchOptions('base:claude', { model: 'claude-sonnet-5-5[1m]' }), /value="claude-sonnet-5-5\[1m\]" selected/);
});

test('Board base Claude launch keeps the 1M-context form and it reopens as pinned', () => {
    const html = renderBoardLaunchOptions('base:claude', { model: 'claude-fable-5-1[1m]' });
    assert.match(html, /<option value="claude-fable-5-1\[1m\]" selected>Fable 5\.1<\/option>/);
    assert.doesNotMatch(html, /\(custom\)/);
    assert.equal(normalizeBoardLaunchOptions('base:claude', { model: 'claude-fable-5-1[1m]' }).model, 'claude-fable-5-1[1m]');

    const controller = createController();
    const settings = controller.mergeClaudeSettingsFromCustomArgs({}, '--model claude-fable-5-1[1m] --effort xhigh');
    assert.equal(settings.model, 'claude-fable-5-1[1m]');
    assert.equal(controller.buildClaudeCustomArgs(settings), '--model claude-fable-5-1[1m] --effort xhigh');
    assert.doesNotMatch(controller.buildCliSettingsHtml('claude', settings), /\(custom\)/);
});
