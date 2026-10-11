import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const environmentModule = path.resolve('VibeRails/wwwroot/js/modules/environment-controller.js');
const { EnvironmentController } = await import(pathToFileURL(environmentModule).href);

test('an Agent or Quick Launch opened from its card runs in the dedicated terminal view', async () => {
    const launches = [];
    const controller = new EnvironmentController({
        terminalController: {
            launchInFocus(options, data) { launches.push({ options, data }); }
        },
        showToast() {}
    });

    await controller.launchInWebUI(17, 'Review agent', 'codex');

    assert.deepEqual(launches, [{
        options: {
            cli: 'codex',
            environmentName: 'Review agent',
            title: 'Review agent',
            tabLabel: 'Review agent',
            forceNewTab: true
        },
        data: { preselectedEnvId: 17 }
    }]);
});
