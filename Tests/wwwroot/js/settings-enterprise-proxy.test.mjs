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

test('every gateway field has a Test button and a result line', () => {
    const html = readFileSync('VibeRails/wwwroot/index.html', 'utf8');
    for (const provider of ['codex', 'claude', 'opencode', 'grok']) {
        assert.match(html, new RegExp(`data-enterprise-proxy-test="${provider}"`));
        assert.match(html, new RegExp(`id="setting-${provider}-enterprise-network-proxy-result"[^>]*role="status"`));
    }
});

function testFixture(result) {
    globalThis.window = { VibeRailsPerformance: null };
    const created = [];
    globalThis.document = {
        createElement() {
            const element = { listeners: {}, addEventListener(type, fn) { this.listeners[type] = fn; } };
            created.push(element);
            return element;
        }
    };
    globalThis.Event = class { constructor(type, init) { this.type = type; Object.assign(this, init); } };
    const calls = [];
    const app = {
        async apiCall(url, method, body, options) {
            calls.push({ url, method, body, options });
            if (result instanceof Error) throw result;
            return result;
        }
    };
    const input = { value: '', dispatched: [], dispatchEvent(event) { this.dispatched.push(event.type); } };
    const output = { hidden: true, className: '', textContent: '', appended: [], append(...items) { this.appended.push(...items); } };
    const button = { disabled: false };
    return { controller: new SettingsController(app), calls, created, input, output, button };
}

test('Test posts the entered URL without the loading overlay and shows the outcome', async () => {
    const f = testFixture({ ok: true, status: 'reachable', message: 'Reached genai.example.test.', suggestedUrl: null });
    f.input.value = ' https://genai.example.test ';

    await f.controller._runEnterpriseProxyTest('claude', f.input, f.output, f.button);

    assert.deepEqual(f.calls[0].body, { provider: 'claude', url: 'https://genai.example.test' });
    assert.equal(f.calls[0].url, '/api/v1/settings/enterprise-network-proxy/test');
    assert.equal(f.calls[0].method, 'POST');
    assert.equal(f.calls[0].options.showLoading, false);
    assert.equal(f.calls[0].options.preferErrorResponseMessage, true);
    assert.equal(f.output.hidden, false);
    assert.equal(f.output.textContent, 'Reached genai.example.test.');
    assert.match(f.output.className, /text-success/);
    assert.equal(f.button.disabled, false);
});

test('a suggested URL can be applied as an ordinary edit', async () => {
    const f = testFixture({ ok: false, status: 'not-found', message: 'Nothing at /responses.', suggestedUrl: 'https://genai.example.test/v1' });
    f.input.value = 'https://genai.example.test';

    await f.controller._runEnterpriseProxyTest('codex', f.input, f.output, f.button);

    assert.match(f.output.className, /text-danger/);
    const use = f.created[0];
    assert.equal(use.textContent, 'Use https://genai.example.test/v1');
    assert.ok(f.output.appended.includes(use));
    use.listeners.click();
    assert.equal(f.input.value, 'https://genai.example.test/v1');
    assert.deepEqual(f.input.dispatched, ['input']);
});

test('a blank field is not sent, and a stale reply is dropped', async () => {
    const blank = testFixture({ ok: true, message: 'unused' });
    await blank.controller._runEnterpriseProxyTest('codex', blank.input, blank.output, blank.button);
    assert.equal(blank.calls.length, 0);
    assert.match(blank.output.textContent, /Enter a gateway URL/);

    const stale = testFixture({ ok: true, message: 'For the old URL' });
    stale.input.value = 'https://old.example/v1';
    const pending = stale.controller._runEnterpriseProxyTest('codex', stale.input, stale.output, stale.button);
    stale.input.value = 'https://new.example/v1';
    await pending;
    assert.notEqual(stale.output.textContent, 'For the old URL');
});

test('a failed test call is reported under the field', async () => {
    const f = testFixture(new Error('Unknown provider.'));
    f.input.value = 'https://gw.example/v1';

    await f.controller._runEnterpriseProxyTest('codex', f.input, f.output, f.button);

    assert.equal(f.output.textContent, 'Test failed: Unknown provider.');
    assert.equal(f.button.disabled, false);
});
