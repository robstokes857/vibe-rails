// @ts-check

const { test, expect } = require('./fixtures');

const MASKED_API_KEY = '••••••••test';

function buildSettings(apiKey, dataExportConfigured = true, dataExportOptIn = false) {
    return {
        remoteAccess: false,
        apiKey,
        dataExportConfigured,
        dataExportOptIn,
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
    dataExportConfigured = true,
    dataExportOptIn = false
) {
    let settings = buildSettings(initialApiKey, dataExportConfigured, dataExportOptIn);
    const writes = [];
    // The legacy one-shot export is still mapped in this build; the sharing flow must never
    // trigger it. Tracked so a regression wiring the opt-in to the bulk export fails here.
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
                dataExportOptIn: clearingKey ? false : body.dataExportOptIn === true,
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
        sharingWrapper: root.locator('#settings-session-sharing-wrapper'),
        shareToggle: root.locator('#setting-data-export-opt-in'),
        unavailable: root.locator('#setting-data-export-unavailable'),
        saveButton: root.locator('#settings-save-button')
    };
}

test('session sharing is explicit, default-off, and describes all shared content', async ({ page }) => {
    const api = await installSettingsApi(page, MASKED_API_KEY);
    const ui = await openSettings(page);

    await expect(ui.shareToggle).toBeEnabled();
    await expect(ui.shareToggle).not.toBeChecked();
    await expect(ui.sharingWrapper).toBeVisible();
    await expect(ui.unavailable).toBeHidden();
    await expect(ui.shareToggle).toHaveAttribute(
        'aria-describedby',
        'setting-data-export-description setting-data-export-unavailable'
    );
    await expect(ui.root).toContainText('existing and future completed sessions');
    await expect(ui.root).toContainText('typed inputs');
    await expect(ui.root).toContainText('file diffs');
    await expect(ui.root).toContainText('raw terminal output');
    await expect(ui.root).toContainText('terminal replay data');
    expect(api.legacyExportRequests).toEqual([]);
    // The legacy Export Data button is intentionally kept beside the sharing switch; its
    // flow is covered by settings-export-modal.spec.js. The modal itself must stay closed.
    await expect(page.locator('#data-export-modal')).toHaveCount(0);
});

test('saved consent is rendered on', async ({ page }) => {
    await installSettingsApi(page, MASKED_API_KEY, true, true);
    const ui = await openSettings(page);

    await expect(ui.shareToggle).toBeEnabled();
    await expect(ui.shareToggle).toBeChecked();
});

for (const scenario of [
    { name: 'no API key', apiKey: '' },
    { name: 'whitespace API key', apiKey: '  \t' }
]) {
    test(`session sharing is unavailable with ${scenario.name}`, async ({ page }) => {
        await installSettingsApi(page, scenario.apiKey);
        const ui = await openSettings(page);

        await expect(ui.sharingWrapper).toBeVisible();
        await expect(ui.shareToggle).toBeDisabled();
        await expect(ui.shareToggle).not.toBeChecked();
        await expect(ui.unavailable).toBeVisible();
        await expect(ui.unavailable).toContainText('Save an API key');
    });
}

test('session sharing is hidden when the export endpoint is unconfigured', async ({ page }) => {
    await installSettingsApi(page, MASKED_API_KEY, false);
    const ui = await openSettings(page);

    await expect(ui.sharingWrapper).toBeHidden();
    await expect(ui.shareToggle).toBeHidden();
    await expect(ui.unavailable).toBeHidden();
});

test('a newly entered API key must be saved before consent can be enabled', async ({ page }) => {
    const api = await installSettingsApi(page, '');
    const ui = await openSettings(page);

    await expect(ui.shareToggle).toBeDisabled();
    await ui.apiKey.fill('new-api-key');
    await expect(ui.shareToggle).toBeDisabled();
    await expect(ui.unavailable).toBeVisible();
    await expect(ui.saveButton).toBeEnabled();
    await ui.saveButton.click();

    await expect.poll(() => api.writes.length).toBe(1);
    expect(api.writes[0].apiKey).toBe('new-api-key');
    expect(api.writes[0].dataExportOptIn).toBe(false);
    await expect(ui.apiKey).toHaveValue(MASKED_API_KEY);
    await expect(ui.shareToggle).toBeEnabled();
    await expect(ui.shareToggle).not.toBeChecked();
    await expect(ui.unavailable).toBeHidden();

    await ui.shareToggle.check();
    await ui.saveButton.click();

    await expect.poll(() => api.writes.length).toBe(2);
    expect(api.writes[1].apiKey).toBe('');
    expect(api.writes[1].dataExportOptIn).toBe(true);
    await expect(ui.shareToggle).toBeChecked();
});

test('turning sharing off persists explicit opt-out', async ({ page }) => {
    const api = await installSettingsApi(page, MASKED_API_KEY, true, true);
    const ui = await openSettings(page);

    await ui.shareToggle.uncheck();
    await ui.saveButton.click();

    await expect.poll(() => api.writes.length).toBe(1);
    expect(api.writes[0].dataExportOptIn).toBe(false);
    await expect(ui.shareToggle).not.toBeChecked();
});

test('clearing the API key clears consent and sends the explicit key flag', async ({ page }) => {
    const api = await installSettingsApi(page, MASKED_API_KEY, true, true);
    const ui = await openSettings(page);

    await ui.apiKey.fill('');
    await expect(ui.shareToggle).toBeDisabled();
    await ui.saveButton.click();

    await expect.poll(() => api.writes.length).toBe(1);
    expect(api.writes[0].clearApiKey).toBe(true);
    expect(api.writes[0].apiKey).toBe('');
    await expect(ui.apiKey).toHaveValue('');
    await expect(ui.shareToggle).not.toBeChecked();
    await expect(ui.shareToggle).toBeDisabled();
});
