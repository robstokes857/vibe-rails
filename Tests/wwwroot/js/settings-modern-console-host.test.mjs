import test from 'node:test';
import assert from 'node:assert/strict';
import { SettingsController } from '../../../VibeRails/wwwroot/js/modules/settings-controller.js';

// VIBE-354: the opt-in switch for the bundled Windows console host on the App settings page.

function fixture() {
    globalThis.window = { VibeRailsPerformance: null };
    const toggle = { checked: false, disabled: false };
    const row = { hidden: true };
    const unavailable = { hidden: true, textContent: '' };
    const save = { disabled: true };
    const elements = {
        '#setting-modern-console-host': toggle,
        '#setting-modern-console-host-row': row,
        '#setting-modern-console-host-unavailable': unavailable,
        '#settings-save-button': save
    };
    const root = { querySelector: (selector) => elements[selector] || null };
    const writes = [];
    const controller = new SettingsController({
        apiCall: async (url, method, body) => { writes.push({ url, method, body }); return body; },
        setAppSettings() {}, showToast() {}, showError: (message) => assert.fail(message)
    });
    return { controller, toggle, row, unavailable, save, root, writes };
}

test('the switch is off by default and takes part in the dirty and save state', () => {
    const { controller, toggle, row, save, root } = fixture();
    assert.ok(controller._trackedSettingsSelector().split(',').includes('#setting-modern-console-host'));

    controller._applySavedSettingsToControls(root, { modernConsoleHostAvailable: true });
    assert.equal(toggle.checked, false);
    assert.equal(toggle.disabled, false);
    assert.equal(row.hidden, false);

    controller._markSettingsClean(root);
    toggle.checked = true;
    controller._updateDirtyState(root);
    assert.equal(save.disabled, false);

    controller._applySavedSettingsToControls(root, { modernConsoleHost: true, modernConsoleHostAvailable: true });
    controller._markSettingsClean(root);
    controller._updateDirtyState(root);
    assert.equal(toggle.checked, true);
    assert.equal(save.disabled, true);
});

test('where the host cannot apply the row is not shown at all', () => {
    // macOS and Linux, and a server that predates the setting, report no availability.
    for (const settings of [{}, { modernConsoleHostAvailable: null }, { modernConsoleHost: true }]) {
        const { controller, row, unavailable, root } = fixture();
        row.hidden = false;
        controller._applySavedSettingsToControls(root, settings);
        assert.equal(row.hidden, true);
        assert.equal(unavailable.hidden, true);
    }
});

test('a build without the files says why and cannot be switched on', () => {
    const { controller, toggle, row, unavailable, root } = fixture();
    controller._applySavedSettingsToControls(root, {
        modernConsoleHost: false,
        modernConsoleHostAvailable: false,
        modernConsoleHostUnavailableReason: 'This build does not include conpty.dll and OpenConsole.exe.'
    });

    assert.equal(row.hidden, false);
    assert.equal(toggle.disabled, true);
    assert.equal(unavailable.hidden, false);
    assert.equal(unavailable.textContent,
        'This build does not include conpty.dll and OpenConsole.exe. Terminals use the built-in console host.');
});

test('a saved "on" in a build without the files can still be switched off', () => {
    const { controller, toggle, unavailable, root } = fixture();
    controller._applySavedSettingsToControls(root, {
        modernConsoleHost: true, modernConsoleHostAvailable: false, modernConsoleHostUnavailableReason: null
    });

    assert.equal(toggle.checked, true);
    assert.equal(toggle.disabled, false);
    assert.equal(unavailable.textContent, 'This build does not include it. Terminals use the built-in console host.');
});

test('the choice is saved explicitly, and left alone when the page does not show the switch', async () => {
    const save = async (controller, modernConsoleHost) => controller.saveSettings(
        false, '', false, true, '', '', 'subscription', '', '', '', 'subscription',
        true, true, true, true, false, {}, false, modernConsoleHost);

    for (const enabled of [true, false]) {
        const { controller, writes } = fixture();
        await save(controller, enabled);
        assert.equal(writes[0].url, '/api/v1/settings');
        assert.equal(writes[0].body.modernConsoleHost, enabled);
        // Not a presence-gated change: it only affects how new local terminals are hosted.
        assert.equal(writes[0].body.presenceProof, null);
    }

    const { controller, writes } = fixture();
    await save(controller, null);
    assert.equal('modernConsoleHost' in writes[0].body, false, 'an omitted value keeps the saved choice');
    await controller.saveSettings(false, '', false, true, '', '', 'subscription', '', '', '', 'subscription',
        true, true, true, true, false, {}, false);
    assert.equal('modernConsoleHost' in writes[1].body, false);
});
