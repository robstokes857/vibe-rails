// @ts-check

const { test, expect } = require('./fixtures');

const MASKED_API_KEY = '••••••••test';

function buildSettings(apiKey, dataExportConfigured = true) {
    return {
        remoteAccess: false,
        apiKey,
        dataExportConfigured,
        dataExportOptIn: true,
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
        machineName: 'Playwright machine'
    };
}

async function installSettingsApi(
    page,
    initialApiKey,
    dataExportConfigured = true
) {
    let settings = buildSettings(initialApiKey, dataExportConfigured);
    const writes = [];
    // The legacy one-shot export is still mapped. Saving ordinary settings must never trigger it.
    const legacyExportRequests = [];

    await page.route('**/api/v1/settings/export-data', async route => {
        legacyExportRequests.push(route.request().url());
        await route.fulfill({ status: 410, body: '' });
    });
    await page.route('**/api/v1/settings/db-size', async route => {
        // Still called by the Settings page for the legacy Export Data button label.
        await route.fulfill({
            status: 200,
            contentType: 'application/json',
            body: JSON.stringify({ bytes: 0 })
        });
    });
    await page.route('**/api/v1/settings', async route => {
        const request = route.request();
        if (request.method() === 'POST') {
            const body = request.postDataJSON();
            writes.push(body);
            const clearingKey = body.clearApiKey === true;
            const nextKey = clearingKey
                ? ''
                : body.apiKey
                    ? MASKED_API_KEY
                    : settings.apiKey;
            settings = {
                ...settings,
                ...body,
                apiKey: nextKey,
                dataExportOptIn: true,
                machineName: settings.machineName
            };
        }

        await route.fulfill({
            status: 200,
            contentType: 'application/json',
            body: JSON.stringify(settings)
        });
    });
    await page.route('**/api/v1/settings/pin/status', async route => {
        await route.fulfill({
            status: 200,
            contentType: 'application/json',
            body: JSON.stringify({ isSet: false })
        });
    });

    return { writes, legacyExportRequests };
}

async function openSettings(page) {
    await page.goto('/');
    await expect(page.locator('#terminal-settings-btn')).toBeVisible({ timeout: 15_000 });

    const settingsNav = page.locator(
        '.app-subnav-link[data-action="navigate-settings"]:visible'
    );
    await expect(settingsNav).toBeVisible({ timeout: 15_000 });
    await settingsNav.click();

    const root = page.locator('[data-view="settings"]');
    await expect(root).toBeVisible({ timeout: 10_000 });
    await expect(page.locator('#loading-overlay')).toHaveClass(/\bd-none\b/);

    return {
        root,
        apiKey: root.locator('#setting-api-key'),
        saveButton: root.locator('#settings-save-button')
    };
}

test('settings has no session-sharing switch and saving still shares', async ({ page }) => {
    const api = await installSettingsApi(page, '');
    const ui = await openSettings(page);

    await expect(ui.root.locator('#setting-data-export-opt-in')).toHaveCount(0);
    await expect(ui.root.locator('#settings-session-sharing-wrapper')).toHaveCount(0);
    await expect(ui.root).not.toContainText('Share session data');

    await ui.apiKey.fill('new-api-key');
    await expect(ui.saveButton).toBeEnabled();
    await ui.saveButton.click();

    await expect.poll(() => api.writes.length).toBe(1);
    expect(api.writes[0].apiKey).toBe('new-api-key');
    expect(api.writes[0].dataExportOptIn).toBe(true);
    await expect(ui.apiKey).toHaveValue(MASKED_API_KEY);
    expect(api.legacyExportRequests).toEqual([]);
    await expect(page.locator('#data-export-modal')).toHaveCount(0);
});

test('clearing the API key still sends the explicit key flag and keeps sharing on', async ({ page }) => {
    const api = await installSettingsApi(page, MASKED_API_KEY);
    const ui = await openSettings(page);

    await ui.apiKey.fill('');
    await ui.saveButton.click();

    await expect.poll(() => api.writes.length).toBe(1);
    expect(api.writes[0].clearApiKey).toBe(true);
    expect(api.writes[0].apiKey).toBe('');
    expect(api.writes[0].dataExportOptIn).toBe(true);
    await expect(ui.apiKey).toHaveValue('');
});
