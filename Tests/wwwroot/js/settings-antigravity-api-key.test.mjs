import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { SettingsController } from '../../../VibeRails/wwwroot/js/modules/settings-controller.js';

function createControl(initial = {}) {
    return {
        value: '',
        checked: false,
        hidden: false,
        placeholder: '',
        textContent: '',
        handlers: {},
        addEventListener(type, fn) { (this.handlers[type] ||= []).push(fn); },
        dispatchEvent(event) { for (const fn of this.handlers[event.type] || []) fn(event); },
        ...initial
    };
}

function fixture() {
    globalThis.window = { VibeRailsPerformance: null };
    const input = createControl();
    const clear = createControl();
    const clearRow = createControl({ hidden: true });
    const status = createControl();
    const save = { disabled: true };
    const elements = {
        '#setting-antigravity-api-key': input,
        '#setting-antigravity-api-key-clear': clear,
        '#setting-antigravity-api-key-clear-row': clearRow,
        '#setting-antigravity-api-key-status': status,
        '#settings-save-button': save
    };
    const root = { querySelector: selector => elements[selector] || null };
    const writes = [];
    const controller = new SettingsController({
        apiCall: async (url, method, body) => { writes.push({ url, method, body }); return body; },
        setAppSettings() {},
        showToast() {},
        showError: message => assert.fail(message)
    });
    return { controller, input, clear, clearRow, status, save, root, writes };
}

test('Antigravity Settings card lives in the LLMs tab with a password input and explicit removal', () => {
    globalThis.window = { VibeRailsPerformance: null };
    const html = readFileSync('VibeRails/wwwroot/index.html', 'utf8');
    const llmPanelStart = html.indexOf('id="settings-panel-llm"');
    const generalPanelStart = html.indexOf('id="settings-panel-general"');
    const llmPanel = html.slice(llmPanelStart, generalPanelStart);

    assert.match(llmPanel, /Antigravity Settings/);
    assert.match(llmPanel, /type="password" id="setting-antigravity-api-key"/);
    assert.match(llmPanel, /id="setting-antigravity-api-key-clear"/);
    assert.match(llmPanel, /id="setting-antigravity-api-key-status"[^>]*role="status"/);

    const tracked = new SettingsController({})._trackedSettingsSelector().split(',');
    assert.ok(tracked.includes('#setting-antigravity-api-key'));
    assert.ok(tracked.includes('#setting-antigravity-api-key-clear'));
});

test('saved Antigravity API key stays write-only, updates status, and participates in dirty tracking', () => {
    const { controller, input, clear, clearRow, status, save, root } = fixture();

    controller._initAntigravitySettings(root, { hasAntigravityApiKey: false });
    assert.equal(input.value, '');
    assert.equal(clear.checked, false);
    assert.equal(clearRow.hidden, true);
    assert.equal(status.hidden, true);
    assert.equal(status.textContent, '');

    controller._markSettingsClean(root);
    input.value = 'AIzaSyNewKey123';
    controller._updateDirtyState(root);
    assert.equal(save.disabled, false);

    // After saving, the password field clears while the status and clear checkbox reflect saved state.
    controller._applySavedSettingsToControls(root, { hasAntigravityApiKey: true });
    controller._markSettingsClean(root);
    assert.equal(input.value, '');
    assert.match(input.placeholder, /Saved — enter a new key to replace/);
    assert.equal(clear.checked, false);
    assert.equal(clearRow.hidden, false);
    assert.equal(status.hidden, false);
    assert.match(status.textContent, /API key saved/);
    assert.equal(save.disabled, true);

    // Checking clear marks dirty; typing a replacement unchecks clear and vice versa.
    clear.checked = true;
    clear.dispatchEvent(new Event('change'));
    controller._updateDirtyState(root);
    assert.equal(save.disabled, false);

    input.value = 'AIzaSyReplacement';
    input.dispatchEvent(new Event('input'));
    assert.equal(clear.checked, false);

    clear.checked = true;
    clear.dispatchEvent(new Event('change'));
    assert.equal(input.value, '');
});

test('saveSettings sends Antigravity API key edits without presence proof and omits them when absent', async () => {
    const { controller, writes } = fixture();
    let presenceCalls = 0;
    controller._requestPresenceProof = async () => { presenceCalls++; return 'unused'; };

    await controller.saveSettings(
        false, '', false, true, '', '', 'subscription', '', '', '', 'subscription',
        true, true, true, true, false, {}, false, null, 'AIzaSySecret123', false);
    assert.equal(presenceCalls, 0);
    assert.equal(writes[0].body.presenceProof, null);
    assert.equal(writes[0].body.antigravityApiKey, 'AIzaSySecret123');
    assert.equal(writes[0].body.clearAntigravityApiKey, false);

    await controller.saveSettings(
        false, '', false, true, '', '', 'subscription', '', '', '', 'subscription',
        true, true, true, true, false, {}, false, null, '', true);
    assert.equal(writes[1].body.antigravityApiKey, '');
    assert.equal(writes[1].body.clearAntigravityApiKey, true);

    await controller.saveSettings(
        false, '', false, true, '', '', 'subscription', '', '', '', 'subscription',
        true, true, true, true, false, {}, false, null);
    assert.equal('antigravityApiKey' in writes[2].body, false);
    assert.equal('clearAntigravityApiKey' in writes[2].body, false);
});
