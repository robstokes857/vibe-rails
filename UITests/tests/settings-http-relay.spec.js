// @ts-check

const { test, expect } = require('./fixtures');

const MASKED_API_KEY = '••••••••relay';

function buildSettings(routeThroughVibeRailsAi = false, apiKey = MASKED_API_KEY) {
    return {
        remoteAccess: false,
        apiKey,
        dataExportConfigured: false,
        routeThroughVibeRailsAi,
        useVsCodeTheme: false,
        mcpEnabled: true,
        computerName: '',
        codexLlmProxyEnabled: false,
        codexLlmProxyMode: 'subscription',
        claudeLlmProxyEnabled: false,
        openCodeLlmProxyEnabled: false,
        claudeTokenSaverEnabled: true,
        codexTokenSaverEnabled: true,
        openCodeTokenSaverEnabled: true,
        tokenSaverCaptureEnabled: false,
        removeCoAuthorTrailers: true,
        machineName: 'Playwright machine'
    };
}

async function installSettingsApi(page, initialSettings) {
    let settings = { ...initialSettings };
    const writes = [];

    await page.route('**/api/v1/settings/db-size', route => route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ bytes: 0 })
    }));

    await page.route('**/api/v1/settings/pin/status', route => route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ isSet: false })
    }));

    await page.route('**/api/v1/settings', async route => {
        const request = route.request();
        if (request.method() === 'POST') {
            const body = request.postDataJSON();
            writes.push(body);
            settings = {
                ...settings,
                ...body,
                apiKey: body.clearApiKey ? '' : (body.apiKey || settings.apiKey)
            };
        }

        await route.fulfill({
            status: 200,
            contentType: 'application/json',
            body: JSON.stringify(settings)
        });
    });

    return { writes };
}

async function openSettings(page) {
    await page.goto('/');
    await expect(page.locator('#terminal-settings-btn')).toBeVisible({ timeout: 15_000 });
    await page.locator('.app-subnav-link[data-action="navigate-settings"]:visible').click();

    const root = page.locator('[data-view="settings"]');
    await expect(root).toBeVisible({ timeout: 10_000 });
    await expect(page.locator('#loading-overlay')).toHaveClass(/\bd-none\b/);
    return {
        root,
        toggle: root.locator('#setting-route-through-viberails-ai'),
        save: root.locator('#settings-save-button')
    };
}

test('relay routing can be enabled without exposing test controls', async ({ page }) => {
    const api = await installSettingsApi(page, buildSettings(false));
    const ui = await openSettings(page);

    await expect(ui.root.locator('#settings-http-relay-test-button')).toHaveCount(0);
    await expect(page.locator('#http-relay-test-modal')).toHaveCount(0);
    await ui.toggle.check();

    await ui.save.click();
    await expect.poll(() => api.writes.length).toBe(1);
    expect(api.writes[0].routeThroughVibeRailsAi).toBe(true);
});
