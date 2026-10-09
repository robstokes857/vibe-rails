import test from 'node:test';
import assert from 'node:assert/strict';
import { SettingsController } from '../../../VibeRails/wwwroot/js/modules/settings-controller.js';

function fixture() {
    globalThis.window = { VibeRailsPerformance: null };
    const toggle = { checked: true };
    const saveButton = { disabled: true };
    const root = {
        querySelector(selector) {
            if (selector === '#setting-create-vibe-story-tracking') return toggle;
            if (selector === '#settings-save-button') return saveButton;
            return null;
        }
    };
    const calls = [];
    const controller = new SettingsController({
        async apiCall(url, method, body) { calls.push({ url, method, body }); return body; },
        setAppSettings() {},
        showToast() {},
        showError(message) { throw new Error(message); }
    });
    return { controller, toggle, saveButton, root, calls };
}

test('story tracking participates in dirty state and the save bar', () => {
    const { controller, toggle, saveButton, root } = fixture();
    assert.ok(controller._trackedSettingsSelector().split(',').includes('#setting-create-vibe-story-tracking'));
    controller._markSettingsClean(root);
    assert.equal(saveButton.disabled, true);
    toggle.checked = false;
    controller._updateDirtyState(root);
    assert.equal(controller._settingsDirty, true);
    assert.equal(saveButton.disabled, false);
    toggle.checked = true;
    controller._updateDirtyState(root);
    assert.equal(controller._settingsDirty, false);
});

test('story tracking restores the saved choice and defaults on for older responses', () => {
    const { controller, toggle, root } = fixture();
    controller._applySavedSettingsToControls(root, { createVibeStoryTracking: false });
    assert.equal(toggle.checked, false);
    controller._applySavedSettingsToControls(root, { createVibeStoryTracking: true });
    assert.equal(toggle.checked, true);
    toggle.checked = false;
    controller._applySavedSettingsToControls(root, {});
    assert.equal(toggle.checked, true);
});

test('saving story tracking sends explicit on and off choices without shifting other settings', async () => {
    for (const enabled of [true, false]) {
        const { controller, calls } = fixture();
        await controller.saveSettings(false, '', false, true, '', false, 'subscription', false, false,
            false, 'subscription', true, true, true, true, false, enabled);
        assert.equal(calls.length, 1);
        assert.equal(calls[0].url, '/api/v1/settings');
        assert.equal(calls[0].method, 'POST');
        assert.equal(calls[0].body.createVibeStoryTracking, enabled);
        assert.equal(calls[0].body.showVibeAiUi, undefined);
        assert.equal(calls[0].body.clearApiKey, false);
    }
});
