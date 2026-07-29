// @ts-check

const { test, expect } = require('./fixtures');

const MASKED_API_KEY = '••••••••test';
const DATABASE_SIZE_BYTES = 12.5 * 1024 * 1024;
const EXPORT_BUTTON_LABEL = 'Export Data (12.5 MB)';
const EXPORTING_BUTTON_LABEL = 'Exporting… (12.5 MB)';

function buildSettings(apiKey, dataExportConfigured = true) {
    return {
        remoteAccess: false,
        apiKey,
        dataExportConfigured,
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
    savedApiKey = MASKED_API_KEY,
    dataExportConfigured = true
) {
    let settings = buildSettings(initialApiKey, dataExportConfigured);
    const writes = [];

    await page.route('**/api/v1/settings/db-size', async route => {
        await route.fulfill({
            status: 200,
            contentType: 'application/json',
            body: JSON.stringify({ bytes: DATABASE_SIZE_BYTES })
        });
    });

    await page.route('**/api/v1/settings', async route => {
        const request = route.request();
        if (request.method() === 'POST') {
            const body = request.postDataJSON();
            writes.push(body);
            settings = {
                ...settings,
                ...body,
                apiKey: savedApiKey,
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

    return { writes };
}

async function openSettings(page) {
    await page.goto('/');

    // The shell navigation renders before the app's asynchronous startup has mounted
    // the requested initial view. Clicking Settings in that window can be overwritten
    // by the late initial load, so wait for the terminal view's stable render boundary.
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
        wrapper: root.locator('#settings-export-data-wrapper'),
        exportButton: root.locator('#settings-export-data-button'),
        saveButton: root.locator('#settings-save-button')
    };
}

async function installToastSpy(page) {
    await page.evaluate(() => {
        window.__settingsDataExportToasts = [];
        const originalShowToast = window.app.showToast.bind(window.app);
        window.app.showToast = (...args) => {
            window.__settingsDataExportToasts.push(args.slice(0, 3));
            return originalShowToast(...args);
        };
    });
}

async function readToastCalls(page) {
    return page.evaluate(() => window.__settingsDataExportToasts || []);
}

for (const scenario of [
    { name: 'empty API key', apiKey: '', visible: false },
    { name: 'whitespace-only API key', apiKey: '   \t', visible: false },
    { name: 'masked saved API key', apiKey: MASKED_API_KEY, visible: true }
]) {
    test(`Export Data availability for ${scenario.name}`, async ({ page }) => {
        await installSettingsApi(page, scenario.apiKey);
        const ui = await openSettings(page);

        if (scenario.visible) {
            await expect(ui.wrapper).toBeVisible();
            await expect(ui.exportButton).toBeVisible();
            await expect(ui.exportButton).toBeEnabled();
            await expect(ui.exportButton).toHaveText(EXPORT_BUTTON_LABEL);
        } else {
            await expect(ui.wrapper).toBeHidden();
            await expect(ui.exportButton).toBeHidden();
        }
    });
}

test('a saved key alone is not enough when the export URL is unconfigured', async ({ page }) => {
    // appsettings.json ships ExportUrl as the "EXPORT_URL_HERE" placeholder, which the server
    // rejects. Gating on the key alone would show every user an enabled button whose only
    // possible outcome is "Data export is not configured."
    await installSettingsApi(page, MASKED_API_KEY, MASKED_API_KEY, false);
    const ui = await openSettings(page);

    await expect(ui.apiKey).toHaveValue(MASKED_API_KEY);
    await expect(ui.wrapper).toBeHidden();
    await expect(ui.exportButton).toBeHidden();
});

test('emptying a populated API key sends the explicit clear flag', async ({ page }) => {
    // A blank apiKey means "unchanged" server-side, so without this flag a saved key could
    // never be removed from the UI.
    const api = await installSettingsApi(page, MASKED_API_KEY);
    const ui = await openSettings(page);

    await ui.apiKey.fill('');
    await expect(ui.saveButton).toBeEnabled();
    await ui.saveButton.click();

    await expect.poll(() => api.writes.length).toBe(1);
    expect(api.writes[0].clearApiKey).toBe(true);
    expect(api.writes[0].apiKey).toBe('');
});

test('an ordinary save does not set the clear flag', async ({ page }) => {
    const api = await installSettingsApi(page, MASKED_API_KEY);
    const ui = await openSettings(page);

    await ui.apiKey.fill('a-replacement-key');
    await ui.saveButton.click();

    await expect.poll(() => api.writes.length).toBe(1);
    expect(api.writes[0].clearApiKey).toBe(false);
    expect(api.writes[0].apiKey).toBe('a-replacement-key');
});

test('unsaved API-key edits stay unavailable and a successful save re-evaluates availability', async ({ page }) => {
    const api = await installSettingsApi(page, '');
    const ui = await openSettings(page);

    await expect(ui.wrapper).toBeHidden();

    await ui.apiKey.fill('newly-entered-unsaved-key');
    await expect(ui.wrapper).toBeHidden();
    await expect(ui.saveButton).toBeEnabled();

    await ui.saveButton.click();
    await expect(ui.apiKey).toHaveValue(MASKED_API_KEY);
    await expect(ui.wrapper).toBeVisible();
    await expect(ui.exportButton).toBeEnabled();
    expect(api.writes).toHaveLength(1);
    expect(api.writes[0].apiKey).toBe('newly-entered-unsaved-key');

    await ui.apiKey.fill('edited-but-unsaved-key');
    await expect(ui.wrapper).toBeHidden();

    await ui.apiKey.fill(MASKED_API_KEY);
    await expect(ui.wrapper).toBeVisible();
    await expect(ui.exportButton).toBeEnabled();
});

test('one pending POST owns progress without showing the global loading overlay', async ({ page }) => {
    await installSettingsApi(page, MASKED_API_KEY);

    let releaseExport;
    const exportGate = new Promise(resolve => {
        releaseExport = resolve;
    });
    const requests = [];

    await page.route('**/api/v1/settings/export-data', async route => {
        const request = route.request();
        requests.push({
            method: request.method(),
            postData: request.postData()
        });
        await exportGate;
        await route.fulfill({
            status: 200,
            contentType: 'application/json',
            body: JSON.stringify({
                success: true,
                status: 'ok',
                message: 'Data exported successfully.',
                sha256: 'a'.repeat(64)
            })
        });
    });

    const ui = await openSettings(page);

    // Two synchronous programmatic clicks verify that progress state is set before
    // the request yields and prevents a duplicate export.
    await ui.exportButton.evaluate(button => {
        button.click();
        button.click();
    });

    await expect.poll(() => requests.length).toBe(1);
    expect(requests[0]).toEqual({ method: 'POST', postData: null });
    await expect(ui.exportButton).toBeDisabled();
    await expect(ui.exportButton).toHaveText(EXPORTING_BUTTON_LABEL);

    // apiCall normally reveals this overlay after 250 ms. It must remain hidden
    // because the export button itself communicates the long-running state.
    await page.waitForTimeout(400);
    await expect(page.locator('#loading-overlay')).toHaveClass(/\bd-none\b/);

    releaseExport();
    await expect(ui.exportButton).toHaveText(EXPORT_BUTTON_LABEL);
    await expect(ui.exportButton).toBeEnabled();
});

test('structured success and failure results use the backend message and toast tone', async ({ page }) => {
    await installSettingsApi(page, MASKED_API_KEY);
    const responses = [
        {
            success: true,
            status: 'ok',
            message: 'Snapshot uploaded.',
            sha256: 'b'.repeat(64)
        },
        {
            success: false,
            status: 'invalid_api_key',
            message: 'The saved API key was rejected.',
            sha256: null
        }
    ];

    await page.route('**/api/v1/settings/export-data', async route => {
        const response = responses.shift();
        await route.fulfill({
            status: 200,
            contentType: 'application/json',
            body: JSON.stringify(response)
        });
    });

    const ui = await openSettings(page);
    await installToastSpy(page);

    await ui.exportButton.click();
    await expect.poll(() => readToastCalls(page)).toEqual([
        ['Data Export', 'Snapshot uploaded.', 'success']
    ]);
    await expect(
        page.locator('.vr-toast-body').filter({ hasText: 'Snapshot uploaded.' })
    ).toBeVisible();
    await expect(ui.exportButton).toBeEnabled();

    await ui.exportButton.click();
    await expect.poll(() => readToastCalls(page)).toEqual([
        ['Data Export', 'Snapshot uploaded.', 'success'],
        ['Data Export', 'The saved API key was rejected.', 'error']
    ]);
    await expect(
        page.locator('.vr-toast-body').filter({ hasText: 'The saved API key was rejected.' })
    ).toBeVisible();
    await expect(ui.exportButton).toHaveText(EXPORT_BUTTON_LABEL);
    await expect(ui.exportButton).toBeEnabled();
});

test('a thrown request error restores the button and shows an error toast', async ({ page }) => {
    await installSettingsApi(page, MASKED_API_KEY);
    const ui = await openSettings(page);

    await page.evaluate(() => {
        window.__settingsDataExportToasts = [];
        const originalApiCall = window.app.apiCall.bind(window.app);
        const originalShowToast = window.app.showToast.bind(window.app);

        window.app.showToast = (...args) => {
            window.__settingsDataExportToasts.push(args.slice(0, 3));
            return originalShowToast(...args);
        };
        window.app.apiCall = (endpoint, ...args) => {
            if (endpoint !== '/api/v1/settings/export-data') {
                return originalApiCall(endpoint, ...args);
            }

            return new Promise((resolve, reject) => {
                window.__rejectSettingsDataExport = () => {
                    reject(new Error('Synthetic export request failure.'));
                };
            });
        };
    });

    await ui.exportButton.click();
    await expect(ui.exportButton).toBeDisabled();
    await expect(ui.exportButton).toHaveText(EXPORTING_BUTTON_LABEL);

    await page.evaluate(() => window.__rejectSettingsDataExport());

    await expect.poll(() => readToastCalls(page)).toEqual([
        ['Data Export', 'Synthetic export request failure.', 'error']
    ]);
    await expect(ui.exportButton).toHaveText(EXPORT_BUTTON_LABEL);
    await expect(ui.exportButton).toBeEnabled();
    await expect(ui.wrapper).toBeVisible();
});

test('leaving and re-entering Settings during export restores the replacement button', async ({ page }) => {
    await installSettingsApi(page, MASKED_API_KEY);

    let releaseExport;
    const exportGate = new Promise(resolve => {
        releaseExport = resolve;
    });
    await page.route('**/api/v1/settings/export-data', async route => {
        await exportGate;
        await route.fulfill({
            status: 200,
            contentType: 'application/json',
            body: JSON.stringify({
                success: true,
                status: 'ok',
                message: 'Data exported successfully.',
                sha256: 'c'.repeat(64)
            })
        });
    });

    const firstUi = await openSettings(page);
    await firstUi.exportButton.click();
    await expect(firstUi.exportButton).toHaveText(EXPORTING_BUTTON_LABEL);

    await page.evaluate(() => window.app.navigate('terminal-focus'));
    await expect(page.locator('.view[data-view="terminal-focus"]')).toBeVisible();
    await page.evaluate(() => window.app.navigate('settings'));

    const replacementButton = page.locator(
        '[data-view="settings"] #settings-export-data-button'
    );
    await expect(replacementButton).toBeVisible();
    await expect(replacementButton).toBeDisabled();
    await expect(replacementButton).toHaveText(EXPORTING_BUTTON_LABEL);

    releaseExport();
    await expect(replacementButton).toHaveText(EXPORT_BUTTON_LABEL);
    await expect(replacementButton).toBeEnabled();
});
