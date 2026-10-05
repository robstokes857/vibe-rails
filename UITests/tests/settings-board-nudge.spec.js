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
                    '/api/v1/settings/db-size': { bytes: 0 },
                    '/api/v1/settings/remote-link': { status: 'idle' },
                    '/api/v1/environments': { environments: [] },
                    '/api/v1/llm-picker/preferences': { items: [] }
                };
                return route.fulfill({ json: payloads[pathname] || {} });
            });

            await page.goto('/?view=settings', { waitUntil: 'domcontentloaded' });
            const root = page.locator('#app-content [data-view="settings"]');
            const base = root.getByRole('checkbox', { name: 'Vibe Board Nudge', exact: true });
            const custom = root.getByRole('checkbox', { name: 'Vibe Board Nudge - Custom Envs', exact: true });
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
            await page.locator('[data-action="navigate-settings"]:visible').click();
            await expect(base).toBeChecked({ checked: baseEnabled });
            await expect(custom).toBeChecked({ checked: customEnabled });
            await expect(save).toBeDisabled();
        });
    }
}
