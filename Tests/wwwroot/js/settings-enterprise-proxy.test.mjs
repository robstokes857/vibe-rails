import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { SettingsController } from '../../../VibeRails/wwwroot/js/modules/settings-controller.js';

test('LLM settings replace enable switches with enterprise API URL inputs', () => {
    const html = readFileSync('VibeRails/wwwroot/index.html', 'utf8');
    assert.doesNotMatch(html, /setting-\w+-llm-proxy-enabled|Each switch needs/);
    for (const provider of ['codex', 'claude', 'opencode', 'grok']) {
        assert.match(html, new RegExp(`type="url" id="setting-${provider}-enterprise-network-proxy"`));
        assert.match(html, new RegExp(`id="setting-token-saver-${provider}"`));
    }
});

test('enterprise URLs participate in dirty tracking and restore saved values independently', () => {
    globalThis.window = { VibeRailsPerformance: null };
    const inputs = {};
    for (const provider of ['codex', 'claude', 'opencode', 'grok'])
        inputs[`#setting-${provider}-enterprise-network-proxy`] = { value: '' };
    const saver = { checked: false };
    inputs['#setting-token-saver-codex'] = saver;
    const root = { querySelector: selector => inputs[selector] || null };
    const controller = new SettingsController({});
    const original = controller._captureSettingsSnapshot(root);
    inputs['#setting-codex-enterprise-network-proxy'].value = 'https://genai.example.test/v1';
    assert.notEqual(controller._captureSettingsSnapshot(root), original);
    assert.equal(saver.checked, false);
    controller._applySavedSettingsToControls(root, {
        codexEnterpriseNetworkProxy: 'https://saved.example/v1',
        claudeEnterpriseNetworkProxy: 'https://claude.example/v1',
        codexTokenSaverEnabled: false
    });
    assert.equal(inputs['#setting-codex-enterprise-network-proxy'].value, 'https://saved.example/v1');
    assert.equal(inputs['#setting-claude-enterprise-network-proxy'].value, 'https://claude.example/v1');
    assert.equal(inputs['#setting-grok-enterprise-network-proxy'].value, '');
    assert.equal(saver.checked, false);
});
