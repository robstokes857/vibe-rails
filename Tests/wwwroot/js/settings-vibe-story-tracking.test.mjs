import test from 'node:test';
import assert from 'node:assert/strict';
import { SettingsController } from '../../../VibeRails/wwwroot/js/modules/settings-controller.js';

test('general settings edits leave the launch nudge values owned by the Context workspace', async () => {
    globalThis.window = { VibeRailsPerformance: null };
    const calls = [];
    const controller = new SettingsController({
        appSettings: { createVibeStoryTracking: false, createVibeStoryTrackingCustomEnvs: true },
        async apiCall(url, method, body) { calls.push({ url, method, body }); return body; },
        setAppSettings() {},
        showToast() {},
        showError(message) { throw new Error(message); }
    });

    await controller.saveSettings(false, '', false, true, 'New machine name', '', 'subscription', '', '',
        '', 'subscription', true, true, true, true);

    assert.equal(calls.length, 1);
    assert.equal(calls[0].url, '/api/v1/settings');
    assert.equal(calls[0].body.computerName, 'New machine name');
    assert.equal(calls[0].body.createVibeStoryTracking, undefined);
    assert.equal(calls[0].body.createVibeStoryTrackingCustomEnvs, undefined);
});
