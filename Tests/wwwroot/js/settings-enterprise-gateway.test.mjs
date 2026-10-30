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
            'gateway-auth-mode', 'gateway-api-key-header', 'gateway-api-key-row', 'gateway-auth-preview', 'gateway-token-label',
            'gateway-tool-search']) {
            controls[`#setting-${provider}-${suffix}`] = {
                value: '', checked: false, handlers: {},
                addEventListener(type, fn) { (this.handlers[type] ||= []).push(fn); },
                dispatchEvent(event) { for (const fn of this.handlers[event.type] || []) fn(event); }
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
        claudeGatewayAuthMode: 'bearer', claudeGatewayApiKeyHeader: 'api-key',
        claudeGatewaySupportsToolSearch: false
    });
    const controller = new SettingsController({});
    const before = controller._captureSettingsSnapshot(root);
    controls['#setting-claude-gateway-token'].value = '';
    assert.notEqual(controller._captureSettingsSnapshot(root), before);
    applyGatewaySettings(root, { hasClaudeGatewayBearerToken: true });
    assert.equal(controls['#setting-codex-gateway-token-clear'].checked, false);
});

test('moving a gateway to another origin says the saved credential will be removed before Save', () => {
    const { root, controls } = fixture();
    const url = controls['#setting-codex-enterprise-network-proxy'];
    url.value = 'https://genai.corp.example/v1';
    initGatewaySettings(root, { codexEnterpriseNetworkProxy: 'https://genai.corp.example/v1', hasCodexGatewayBearerToken: true });
    const token = controls['#setting-codex-gateway-token'];
    assert.match(token.placeholder, /Credential saved/);

    // Same origin, another path: the server keeps the credential (AppSettingsRoutes.SameGatewayOrigin).
    url.value = 'https://GENAI.corp.example:443/openai/v1';
    url.dispatchEvent(new Event('input'));
    assert.match(token.placeholder, /Credential saved/);

    // The OpenAI API key choice points the URL at api.openai.com: the corporate token goes away.
    const mode = controls['#setting-codex-gateway-auth-mode'];
    mode.value = 'openai-api-key';
    mode.dispatchEvent(new Event('change'));
    assert.equal(url.value, 'https://api.openai.com/v1');
    assert.match(token.placeholder, /removes the credential saved for the previous gateway/);

    url.value = '';
    url.dispatchEvent(new Event('input'));
    assert.match(token.placeholder, /removes the credential/);
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

test('an inherited base that bypasses the relay shows a direct route until a gateway is entered', () => {
    const { root, controls } = fixture();
    initGatewaySettings(root, { codexInheritedBaseBypassesRelay: true, claudeInheritedBaseBypassesRelay: true });
    for (const provider of ['codex', 'claude']) {
        const route = controls[`#setting-${provider}-gateway-route`];
        assert.match(route.textContent, /CLI → inherited base from the environment \(direct, Token Saver off\)/);
        assert.doesNotMatch(route.textContent, /provider default/);
        assert.equal(controls[`[data-use-inherited-gateway="${provider}"]`].hidden, true);
        const url = controls[`#setting-${provider}-enterprise-network-proxy`];
        url.value = 'https://saved.example/v1';
        url.dispatchEvent(new Event('input'));
        assert.match(route.textContent, /After saving: CLI → Token Saver → https:\/\/saved.example\/v1/);
        url.value = '';
        url.dispatchEvent(new Event('input'));
        assert.match(route.textContent, /direct, Token Saver off/);
    }
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

test('a credential typed with a blank URL fills in the inherited URL visibly, and removing it undoes the fill', () => {
    const { root, controls } = fixture();
    initGatewaySettings(root, { claudeInheritedApiBaseUrl: 'https://corp.example' });
    const url = controls['#setting-claude-enterprise-network-proxy'];
    const token = controls['#setting-claude-gateway-token'];
    token.value = 'corp-key';
    token.dispatchEvent(new Event('input'));
    assert.equal(url.value, 'https://corp.example');
    assert.match(controls['#setting-claude-gateway-route'].textContent,
        /^Default managed route: After saving: CLI → Token Saver → https:\/\/corp.example \(filled in from the environment\)$/);
    token.value = '';
    token.dispatchEvent(new Event('input'));
    assert.equal(url.value, '');
    assert.match(controls['#setting-claude-gateway-route'].textContent, /\(from environment\)$/);

    // A URL the user chose is theirs: neither the credential nor its removal changes it.
    token.value = 'corp-key';
    token.dispatchEvent(new Event('input'));
    url.value = 'https://other.example';
    url.dispatchEvent(new Event('input'));
    token.value = '';
    token.dispatchEvent(new Event('input'));
    assert.equal(url.value, 'https://other.example');
    assert.doesNotMatch(controls['#setting-claude-gateway-route'].textContent, /filled in/);
});

test('a credential with no URL and nothing inherited says what it needs, naming the provider key choice', () => {
    const { root, controls } = fixture();
    initGatewaySettings(root, {});
    controls['#setting-claude-gateway-token'].value = 'sk-ant-key';
    controls['#setting-claude-gateway-token'].dispatchEvent(new Event('input'));
    assert.equal(controls['#setting-claude-enterprise-network-proxy'].value, '');
    assert.match(controls['#setting-claude-gateway-route'].textContent,
        /provider default\. A credential needs a gateway URL: enter one, or choose Anthropic API key\.$/);
});

test('the Anthropic API key choice saves api.anthropic.com with x-api-key in one go and is recognized on reload', () => {
    const { root, controls } = fixture();
    initGatewaySettings(root, {});
    const mode = controls['#setting-claude-gateway-auth-mode'];
    mode.value = 'anthropic-api-key';
    mode.dispatchEvent(new Event('change'));
    controls['#setting-claude-gateway-token'].value = 'sk-ant-key';
    assert.equal(controls['#setting-claude-enterprise-network-proxy'].value, 'https://api.anthropic.com');
    assert.equal(controls['#setting-claude-gateway-api-key-row'].hidden, true);
    assert.equal(controls['#setting-claude-gateway-token-label'].textContent, 'Anthropic API key');
    assert.equal(controls['#setting-claude-gateway-auth-preview'].textContent, 'x-api-key: <API key>');
    assert.match(controls['#setting-claude-gateway-route'].textContent, /After saving: CLI → Token Saver → https:\/\/api.anthropic.com$/);
    const edits = gatewayCredentialEdits(root);
    assert.equal(edits.claudeGatewayAuthMode, 'api-key');
    assert.equal(edits.claudeGatewayApiKeyHeader, 'x-api-key');
    assert.equal(edits.claudeGatewayBearerToken, 'sk-ant-key');

    for (const saved of ['https://api.anthropic.com', 'https://api.anthropic.com/v1']) {
        applyGatewaySettings(root, { claudeEnterpriseNetworkProxy: saved, claudeGatewayAuthMode: 'api-key', claudeGatewayApiKeyHeader: 'x-api-key' });
        assert.equal(mode.value, 'anthropic-api-key');
    }
    applyGatewaySettings(root, { claudeEnterpriseNetworkProxy: 'https://api.anthropic.com', claudeGatewayAuthMode: 'api-key', claudeGatewayApiKeyHeader: 'api-key' });
    assert.equal(mode.value, 'api-key');
});

test('pointing a provider key choice elsewhere turns it into the explicit format it stands for', () => {
    const { root, controls } = fixture();
    initGatewaySettings(root, {});
    const mode = controls['#setting-claude-gateway-auth-mode'];
    mode.value = 'anthropic-api-key';
    mode.dispatchEvent(new Event('change'));
    const url = controls['#setting-claude-enterprise-network-proxy'];
    url.value = 'https://gateway.example';
    url.dispatchEvent(new Event('input'));
    assert.equal(mode.value, 'api-key');
    assert.equal(controls['#setting-claude-gateway-api-key-header'].value, 'x-api-key');
    assert.equal(controls['#setting-claude-gateway-api-key-row'].hidden, false);
    assert.equal(url.value, 'https://gateway.example');
});

test('the OpenAI API key choice saves api.openai.com/v1 with Bearer and keeps the saved header name', () => {
    const { root, controls } = fixture();
    initGatewaySettings(root, { codexGatewayApiKeyHeader: 'x-company-key' });
    const mode = controls['#setting-codex-gateway-auth-mode'];
    mode.value = 'openai-api-key';
    mode.dispatchEvent(new Event('change'));
    assert.equal(controls['#setting-codex-enterprise-network-proxy'].value, 'https://api.openai.com/v1');
    assert.equal(controls['#setting-codex-gateway-auth-preview'].textContent, 'Authorization: Bearer <token>');
    const edits = gatewayCredentialEdits(root);
    assert.equal(edits.codexGatewayAuthMode, 'bearer');
    assert.equal(edits.codexGatewayApiKeyHeader, 'x-company-key');
    applyGatewaySettings(root, { codexEnterpriseNetworkProxy: 'https://api.openai.com/v1', codexGatewayAuthMode: 'bearer' });
    assert.equal(mode.value, 'openai-api-key');
});

test('the Claude tool-search switch restores and saves as an explicit value', () => {
    const { root, controls } = fixture();
    initGatewaySettings(root, { claudeGatewaySupportsToolSearch: true });
    assert.equal(controls['#setting-claude-gateway-tool-search'].checked, true);
    controls['#setting-claude-gateway-tool-search'].checked = false;
    assert.equal(gatewayCredentialEdits(root).claudeGatewaySupportsToolSearch, false);
    applyGatewaySettings(root, {});
    assert.equal(controls['#setting-claude-gateway-tool-search'].checked, false);
});

test('provider key choices and the tool-search switch are in the markup and tracked for Save', () => {
    const html = readFileSync('VibeRails/wwwroot/index.html', 'utf8');
    assert.match(html, /<option value="anthropic-api-key">Anthropic API key \(api\.anthropic\.com\)<\/option>/);
    assert.match(html, /<option value="openai-api-key">OpenAI API key \(api\.openai\.com\)<\/option>/);
    assert.match(html, /type="checkbox" id="setting-claude-gateway-tool-search"/);
    assert.match(new SettingsController({})._trackedSettingsSelector(), /#setting-claude-gateway-tool-search/);
});
