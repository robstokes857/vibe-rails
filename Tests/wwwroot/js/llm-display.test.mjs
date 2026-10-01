import test from 'node:test';
import assert from 'node:assert/strict';
import { friendlyModelName, workerModelSummary } from '../../../VibeRails/wwwroot/js/modules/llm-display.js';

test('worker labels read the saved argv, including quoting, aliases, equals syntax and overrides', () => {
    assert.equal(workerModelSummary('codex', '-m gpt-6.1-sol -c model_reasoning_effort="xhigh"'), 'GPT 6.1 Sol · Extra high effort');
    assert.equal(workerModelSummary('codex', '--model=gpt-5.5 --config=\'model_reasoning_effort="high"\' -c model=gpt-6-astra'), 'GPT 6 Astra · High effort');
    assert.equal(workerModelSummary('claude', '--model "claude-opus-5-5[1m]" --effort max'), 'Claude Opus 5.5 (1M context) · Maximum effort');
    assert.equal(workerModelSummary('grok', '-m grok-4.6 --reasoning-effort=low --effort high'), 'Grok 4.6 · High effort');
    assert.equal(workerModelSummary('antigravity', '--model "Gemini 3.5 Flash (High)"'), 'Gemini 3.5 Flash (High)');
    assert.equal(workerModelSummary('opencode', '--model openai/gpt-5.5 --variant high'), 'GPT 5.5 · High effort');
});

test('unset settings stay absent, and unknown models stay recognizable', () => {
    assert.equal(workerModelSummary('codex', '--yolo'), '');
    assert.equal(workerModelSummary('codex'), '');
    assert.equal(workerModelSummary('claude', '--effort medium'), 'Medium effort');
    assert.equal(workerModelSummary('codex', '-- --model gpt-6-astra'), '');
    assert.equal(friendlyModelName('opencode', 'my-provider/custom-model-v7'), 'my-provider/custom-model-v7');
    assert.equal(friendlyModelName('claude', 'claude-opus-5-5'), 'Claude Opus 5.5');
    assert.equal(workerModelSummary('codex', '--model "<img src=x>"'), '<img src=x>'); // Escaped by the renderer.
});
