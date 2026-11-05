import test from 'node:test';
import assert from 'node:assert/strict';
import { SettingsController } from '../../../VibeRails/wwwroot/js/modules/settings-controller.js';

function fixture() {
    globalThis.window = { VibeRailsPerformance: null };
    const toggle = { checked: false };
    const save = { disabled: true };
    const root = { querySelector: selector => selector === '#setting-vscode-spell-check' ? toggle
        : selector === '#settings-save-button' ? save : null };
    const writes = [];
    const controller = new SettingsController({
        apiCall: async (url, method, body) => { writes.push({ url, method, body }); return body; },
        setAppSettings() {}, showToast() {}, showError: message => assert.fail(message)
    });
    return { controller, toggle, save, root, writes };
}

test('spell check is off for absent settings and participates in dirty/save state', () => {
    const { controller, toggle, save, root } = fixture();
    assert.ok(controller._trackedSettingsSelector().split(',').includes('#setting-vscode-spell-check'));
    controller._applySavedSettingsToControls(root, {});
    assert.equal(toggle.checked, false);
    controller._markSettingsClean(root);
    toggle.checked = true;
    controller._updateDirtyState(root);
    assert.equal(save.disabled, false);
    controller._applySavedSettingsToControls(root, { vsCodeSpellCheck: false });
    controller._updateDirtyState(root);
    assert.equal(save.disabled, true);
    controller._applySavedSettingsToControls(root, { vsCodeSpellCheck: true });
    assert.equal(toggle.checked, true);
});

test('spell check sends explicit on/off choices through the existing settings save', async () => {
    for (const enabled of [true, false]) {
        const { controller, writes } = fixture();
        await controller.saveSettings(false, '', false, true, '', '', 'subscription', '', '', '',
            'subscription', true, true, true, true, false, true, false, {}, enabled);
        assert.equal(writes.length, 1);
        assert.equal(writes[0].url, '/api/v1/settings');
        assert.equal(writes[0].body.vsCodeSpellCheck, enabled);
        assert.equal(writes[0].body.presenceProof, null);
    }
});
