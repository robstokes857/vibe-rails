import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { applyGatewaySettings, gatewayCredentialEdits, initGatewaySettings } from '../../../VibeRails/wwwroot/js/modules/settings-enterprise-gateway.js';
import { SettingsController } from '../../../VibeRails/wwwroot/js/modules/settings-controller.js';

function fixture() {
    globalThis.window = { VibeRailsPerformance: null };
    const controls = {};
    for (const provider of ['codex', 'claude']) {
        for (const suffix of ['gateway-token', 'gateway-token-clear', 'gateway-route', 'enterprise-network-proxy',
            'gateway-auth-mode', 'gateway-api-key-header', 'gateway-api-key-row', 'gateway-auth-preview', 'gateway-token-label']) {
            controls[`#setting-${provider}-${suffix}`] = {
                value: '', checked: false, handlers: {},
                addEventListener(type, fn) { this.handlers[type] = fn; },
                dispatchEvent(event) { this.handlers[event.type]?.(event); }
            };
        }
        controls[`#setting-token-saver-${provider}`] = { checked: true, addEventListener() {} };
        controls[`[data-use-inherited-gateway="${provider}"]`] = { addEventListener(type, fn) { this.click = fn; } };
    }
    return { controls, root: { querySelector: selector => controls[selector] || null } };
}

test('saved gateway credentials never populate password controls and edits can be cleared independently', () => {
    const { root, controls } = fixture();
    applyGatewaySettings(root, { hasCodexGatewayBearerToken: true });
    assert.equal(controls['#setting-codex-gateway-token'].value, '');
    assert.match(controls['#setting-codex-gateway-token'].placeholder, /Credential saved/);
    controls['#setting-claude-gateway-token'].value = 'Bearer new-secret';
    controls['#setting-codex-gateway-token-clear'].checked = true;
    assert.deepEqual(gatewayCredentialEdits(root), {
        codexGatewayBearerToken: '', clearCodexGatewayBearerToken: true,
        claudeGatewayBearerToken: 'Bearer new-secret', clearClaudeGatewayBearerToken: false,
        codexGatewayAuthMode: 'bearer', codexGatewayApiKeyHeader: 'api-key',
        claudeGatewayAuthMode: 'bearer', claudeGatewayApiKeyHeader: 'api-key'
    });
    const controller = new SettingsController({});
    const before = controller._captureSettingsSnapshot(root);
    controls['#setting-claude-gateway-token'].value = '';
    assert.notEqual(controller._captureSettingsSnapshot(root), before);
    applyGatewaySettings(root, { hasClaudeGatewayBearerToken: true });
    assert.equal(controls['#setting-codex-gateway-token-clear'].checked, false);
});

test('inherited route is visible and can be adopted for a saved gateway credential', () => {
    const { root, controls } = fixture();
    initGatewaySettings(root, { codexInheritedApiBaseUrl: 'https://corp.example/v1' });
    assert.match(controls['#setting-codex-gateway-route'].textContent, /CLI → Token Saver → https:\/\/corp.example\/v1 \(from environment\)/);
    assert.match(controls['#setting-codex-gateway-route'].textContent, /^Default managed route:/);
    controls['[data-use-inherited-gateway="codex"]'].click();
    assert.equal(controls['#setting-codex-enterprise-network-proxy'].value, 'https://corp.example/v1');
    assert.equal(controls['[data-use-inherited-gateway="codex"]'].hidden, true);
    assert.doesNotMatch(controls['#setting-codex-gateway-route'].textContent, /from environment/);
});

test('API key header mode restores, previews, saves, and preserves a typed secret while switching', () => {
    const { root, controls } = fixture();
    initGatewaySettings(root, { codexGatewayAuthMode: 'api-key', codexGatewayApiKeyHeader: 'x-company-key', hasCodexGatewayBearerToken: true });
    assert.equal(controls['#setting-codex-gateway-api-key-row'].hidden, false);
    assert.equal(controls['#setting-codex-gateway-token-label'].textContent, 'Gateway API key');
    assert.equal(controls['#setting-codex-gateway-auth-preview'].textContent, 'x-company-key: <API key>');
    controls['#setting-codex-gateway-token'].value = 'new-secret';
    assert.equal(gatewayCredentialEdits(root).codexGatewayApiKeyHeader, 'x-company-key');
    controls['#setting-codex-gateway-auth-mode'].value = 'bearer';
    controls['#setting-codex-gateway-auth-mode'].dispatchEvent(new Event('change'));
    assert.equal(controls['#setting-codex-gateway-token'].value, 'new-secret');
    assert.equal(controls['#setting-codex-gateway-api-key-row'].hidden, true);
    assert.equal(controls['#setting-codex-gateway-auth-preview'].textContent, 'Authorization: Bearer <token>');
});

test('a saved route updates the preview baseline used by input handlers', () => {
    const { root, controls } = fixture();
    initGatewaySettings(root, {});
    controls['#setting-codex-enterprise-network-proxy'].value = 'https://saved.example/v1';
    applyGatewaySettings(root, { codexEnterpriseNetworkProxy: 'https://saved.example/v1' });
    controls['#setting-codex-enterprise-network-proxy'].dispatchEvent(new Event('input'));
    assert.doesNotMatch(controls['#setting-codex-gateway-route'].textContent, /After saving/);
});

test('LLM gateway credentials use password fields with explicit removal', () => {
    const html = readFileSync('VibeRails/wwwroot/index.html', 'utf8');
    for (const provider of ['codex', 'claude']) {
        assert.match(html, new RegExp(`type="password" id="setting-${provider}-gateway-token"`));
        assert.match(html, new RegExp(`id="setting-${provider}-gateway-token-clear"`));
        assert.match(html, new RegExp(`id="setting-${provider}-gateway-route-caveat"`));
    }
});
