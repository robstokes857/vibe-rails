import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const modulePath = path.resolve('VibeRails/wwwroot/js/modules/utils.js');
const { getCliBrand } = await import(pathToFileURL(modulePath).href);

test('Kimi uses contrasting logo and accent variants for VS Code light themes', () => {
    const themeClasses = new Set();
    const previousWindow = globalThis.window;
    const previousDocument = globalThis.document;

    globalThis.window = {};
    globalThis.document = {
        body: {
            classList: {
                contains: className => themeClasses.has(className)
            }
        }
    };

    try {
        const darkBrand = getCliBrand('kimi-k3');
        assert.equal(darkBrand.logo, 'assets/img/kimi.png');
        assert.equal(darkBrand.accentColor, '#ffffff');

        themeClasses.add('vscode-light');
        const lightBrand = getCliBrand('kimi-k3');
        assert.equal(lightBrand.logo, 'assets/img/kimi_dark.png');
        assert.equal(lightBrand.accentColor, '#000000');

        themeClasses.delete('vscode-light');
        themeClasses.add('vscode-high-contrast-light');
        const highContrastLightBrand = getCliBrand('kimi-k3');
        assert.equal(highContrastLightBrand.logo, 'assets/img/kimi_dark.png');
        assert.equal(highContrastLightBrand.accentColor, '#000000');
    } finally {
        if (previousWindow === undefined) delete globalThis.window;
        else globalThis.window = previousWindow;

        if (previousDocument === undefined) delete globalThis.document;
        else globalThis.document = previousDocument;
    }
});
