import test from 'node:test';
import assert from 'node:assert/strict';
import { SettingsController } from '../../../VibeRails/wwwroot/js/modules/settings-controller.js';

// VIBE-354, VIBE-372: new terminal tabs run on the bundled Windows console host by default. The
// App settings page has one switch for it, "Windows built-in console host", which opts out.

function fixture() {
    globalThis.window = { VibeRailsPerformance: null };
    const toggle = { checked: false, disabled: false };
    const row = { hidden: true };
    const unavailable = { hidden: true, textContent: '' };
    const save = { disabled: true };
    const elements = {
        '#setting-built-in-console-host': toggle,
        '#setting-built-in-console-host-row': row,
        '#setting-built-in-console-host-unavailable': unavailable,
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

test('the opt-out is off by default and takes part in the dirty and save state', () => {
    const { controller, toggle, row, save, root } = fixture();
    assert.ok(controller._trackedSettingsSelector().split(',').includes('#setting-built-in-console-host'));

    controller._applySavedSettingsToControls(root, { bundledConsoleHostAvailable: true });
    assert.equal(toggle.checked, false);
    assert.equal(toggle.disabled, false);
    assert.equal(row.hidden, false);

    controller._markSettingsClean(root);
    toggle.checked = true;
    controller._updateDirtyState(root);
    assert.equal(save.disabled, false);

    controller._applySavedSettingsToControls(root, { builtInConsoleHost: true, bundledConsoleHostAvailable: true });
    controller._markSettingsClean(root);
    controller._updateDirtyState(root);
    assert.equal(toggle.checked, true);
    assert.equal(save.disabled, true);
});

test('the retired opt-in no longer sets the switch', () => {
    // A server answer from before the change, or a settings file that still carries the old
    // field, must not read as "built-in host chosen".
    const { controller, toggle, root } = fixture();
    controller._applySavedSettingsToControls(root, { modernConsoleHost: true, bundledConsoleHostAvailable: true });
    assert.equal(toggle.checked, false);
});

test('where the bundled host cannot apply the row is not shown at all', () => {
    // macOS and Linux, and a server that predates the setting, report no availability.
    for (const settings of [{}, { bundledConsoleHostAvailable: null }, { builtInConsoleHost: true }]) {
        const { controller, row, unavailable, root } = fixture();
        row.hidden = false;
        controller._applySavedSettingsToControls(root, settings);
        assert.equal(row.hidden, true);
        assert.equal(unavailable.hidden, true);
    }
});

test('a build without the files says why, and the switch is disabled because it changes nothing', () => {
    for (const builtInConsoleHost of [false, true]) {
        const { controller, toggle, row, unavailable, root } = fixture();
        controller._applySavedSettingsToControls(root, {
            builtInConsoleHost,
            bundledConsoleHostAvailable: false,
            bundledConsoleHostUnavailableReason: 'This build does not include conpty.dll and OpenConsole.exe.'
        });

        assert.equal(row.hidden, false);
        assert.equal(toggle.checked, builtInConsoleHost, 'the saved choice still shows');
        assert.equal(toggle.disabled, true);
        assert.equal(unavailable.hidden, false);
        assert.equal(unavailable.textContent,
            'This build does not include conpty.dll and OpenConsole.exe. Terminals use the console host built into Windows.');
    }

    const { controller, unavailable, root } = fixture();
    controller._applySavedSettingsToControls(root, { bundledConsoleHostAvailable: false, bundledConsoleHostUnavailableReason: null });
    assert.equal(unavailable.textContent,
        'This build does not include the bundled console host. Terminals use the console host built into Windows.');
});

test('the choice is saved explicitly, and left alone when the page does not show the switch', async () => {
    const save = async (controller, builtInConsoleHost) => controller.saveSettings(
        false, '', false, true, '', '', 'subscription', '', '', '', 'subscription',
        true, true, true, true, false, {}, false, builtInConsoleHost);

    for (const chosen of [true, false]) {
        const { controller, writes } = fixture();
        await save(controller, chosen);
        assert.equal(writes[0].url, '/api/v1/settings');
        assert.equal(writes[0].body.builtInConsoleHost, chosen);
        assert.equal('modernConsoleHost' in writes[0].body, false, 'the retired field is never sent');
        // Not a presence-gated change: it only affects how new local terminals are hosted.
        assert.equal(writes[0].body.presenceProof, null);
    }

    const { controller, writes } = fixture();
    await save(controller, null);
    assert.equal('builtInConsoleHost' in writes[0].body, false, 'an omitted value keeps the saved choice');
    await controller.saveSettings(false, '', false, true, '', '', 'subscription', '', '', '', 'subscription',
        true, true, true, true, false, {}, false);
    assert.equal('builtInConsoleHost' in writes[1].body, false);
});
