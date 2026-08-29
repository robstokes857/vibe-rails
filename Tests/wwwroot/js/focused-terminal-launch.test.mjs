import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const environmentModule = path.resolve('VibeRails/wwwroot/js/modules/environment-controller.js');
const sandboxModule = path.resolve('VibeRails/wwwroot/js/modules/sandbox-controller.js');
const { EnvironmentController } = await import(pathToFileURL(environmentModule).href);
const { SandboxController } = await import(pathToFileURL(sandboxModule).href);

test('Environment Web launch opens its managed CLI in the dedicated terminal view', async () => {
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

test('Sandbox Web launch carries its working directory into the dedicated terminal view', async () => {
    const launches = [];
    const errors = [];
    const controller = new SandboxController({
        data: {
            sandboxes: [{ id: 4, name: 'quality-fix', path: 'C:/sandboxes/quality-fix' }]
        },
        terminalController: {
            launchInFocus(options) { launches.push(options); }
        },
        showToast() {},
        showError(message) { errors.push(message); }
    });

    await controller.launchInWebUI(4, 'quality-fix', 'claude', null);

    assert.deepEqual(errors, []);
    assert.deepEqual(launches, [{
        cli: 'claude',
        environmentName: null,
        workingDirectory: 'C:/sandboxes/quality-fix',
        title: 'Sandbox: quality-fix',
        tabLabel: 'quality-fix',
        forceNewTab: true
    }]);
});
