// Production frontend with intercepted settings persistence; no backend, database or CLI.
const { test, expect } = require('@playwright/test');

for (const baseEnabled of [false, true]) {
    for (const customEnabled of [false, true]) {
        test(`independent Board nudges save and reload: base=${baseEnabled}, custom=${customEnabled}`, async ({ page }) => {
            await page.addInitScript(() => sessionStorage.setItem('viberails_tab', 'board-nudge-fixture'));
            await page.routeWebSocket('**/api/v1/events/ws*', () => {});
            let settings = { apiKey: '', computerName: '', mcpEnabled: true };
            const saves = [];
            await page.route('**/api/v1/**', async route => {
                const request = route.request();
                const pathname = new URL(request.url()).pathname;
                if (pathname === '/api/v1/settings') {
                    if (request.method() === 'POST') {
                        saves.push(request.postDataJSON());
                        settings = { ...settings, ...saves.at(-1) };
                    }
                    return route.fulfill({ json: settings });
                }
                const payloads = {
                    '/api/v1/context': { isInGit: false, rootPath: 'C:/fixture', launchDirectory: 'C:/fixture' },
                    '/api/v1/settings/pin/status': { isSet: false },
                    '/api/v1/settings/remote-link': { status: 'idle' },
                    '/api/v1/environments': { environments: [] },
                    '/api/v1/llm-picker/preferences': { items: [] }
                };
                return route.fulfill({ json: payloads[pathname] || {} });
            });

            await page.goto('/?view=settings', { waitUntil: 'domcontentloaded' });
            const root = page.locator('#app-content [data-view="settings"]');
            const base = root.getByRole('checkbox', { name: 'Vibe Board nudge — built-in CLIs', exact: true });
            const custom = root.getByRole('checkbox', { name: 'Vibe Board nudge — Quick Launches', exact: true });
            const save = root.locator('#settings-save-button');
            await expect(base).toBeChecked();
            await expect(custom).not.toBeChecked();
            await expect(save).toBeDisabled();

            // Each switch alone changes the dirty state, and restoring it clears the draft.
            await base.uncheck();
            await expect(save).toBeEnabled();
            await base.check();
            await expect(save).toBeDisabled();
            await custom.check();
            await expect(save).toBeEnabled();
            await custom.uncheck();
            await expect(save).toBeDisabled();

            await base.setChecked(baseEnabled);
            await custom.setChecked(customEnabled);
            // Save even the default combination through an unrelated change.
            await root.locator('#setting-computer-name').fill('Fixture machine');
            await save.click();
            await expect(save).toBeDisabled();
            expect(saves).toHaveLength(1);
            expect(saves[0].createVibeStoryTracking).toBe(baseEnabled);
            expect(saves[0].createVibeStoryTrackingCustomEnvs).toBe(customEnabled);
            await expect(base).toBeChecked({ checked: baseEnabled });
            await expect(custom).toBeChecked({ checked: customEnabled });

            await page.reload({ waitUntil: 'domcontentloaded' });
            // Navigation is bound only after startup has loaded its settings and initial view,
            // so a click on the already visible button before then is lost.
            await expect(page.locator('#app-content > *').first()).toBeAttached();
            await page.locator('[data-action="navigate-settings"]:visible').click();
            await expect(base).toBeChecked({ checked: baseEnabled });
            await expect(custom).toBeChecked({ checked: customEnabled });
            await expect(save).toBeDisabled();
        });
    }
}

// VIBE-229: App context is edited in Settings with the same editor a board's Context tab uses.
test('Settings Context tab edits App context alone and keeps the Settings form clean', async ({ page }, testInfo) => {
    await page.addInitScript(() => sessionStorage.setItem('viberails_tab', 'board-nudge-fixture'));
    await page.routeWebSocket('**/api/v1/events/ws*', () => {});
    let appContext = { message: 'Existing rule', revision: 2 };
    const settingsSaves = [];
    await page.route('**/api/v1/**', async route => {
        const request = route.request();
        const pathname = new URL(request.url()).pathname;
        if (pathname === '/api/v1/board/app-context') {
            if (request.method() === 'PUT') {
                const body = request.postDataJSON();
                expect(body.expectedRevision).toBe(appContext.revision);
                appContext = { message: body.message, revision: appContext.revision + 1 };
            }
            return route.fulfill({ json: appContext });
        }
        if (pathname === '/api/v1/settings' && request.method() === 'POST') settingsSaves.push(request.postDataJSON());
        const payloads = {
            '/api/v1/settings': { apiKey: '', computerName: '', mcpEnabled: true },
            '/api/v1/context': { isInGit: false, rootPath: 'C:/fixture', launchDirectory: 'C:/fixture' },
            '/api/v1/settings/pin/status': { isSet: false },
            '/api/v1/settings/remote-link': { status: 'idle' },
            '/api/v1/environments': { environments: [] },
            '/api/v1/llm-picker/preferences': { items: [] }
        };
        return route.fulfill({ json: payloads[pathname] || {} });
    });

    await page.goto('/?view=settings', { waitUntil: 'domcontentloaded' });
    const root = page.locator('#app-content [data-view="settings"]');
    await root.getByRole('tab', { name: 'Context', exact: true }).click();
    const panel = root.locator('#settings-panel-context');
    await expect(panel.locator('[data-context-level] > h6')).toHaveText(['App context']);
    await expect(panel.locator('[data-context-reach]')).toContainText('launches with no board');
    const message = panel.getByLabel('App context', { exact: true });
    await expect(message).toHaveValue('Existing rule');
    await message.fill('Answer in British English.');
    await panel.getByRole('button', { name: 'Save App context', exact: true }).click();
    await expect.poll(() => appContext.message).toBe('Answer in British English.');
    await expect(page.getByText('App context saved for every launch.', { exact: true })).toBeVisible();
    // The editor saves on its own; the Settings form has nothing pending and posted nothing.
    await expect(root.locator('#settings-save-button')).toBeDisabled();
    expect(settingsSaves).toHaveLength(0);
    await page.screenshot({ path: testInfo.outputPath('settings-app-context.png'), animations: 'disabled' });
});
