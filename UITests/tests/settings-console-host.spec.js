const { test, expect } = require('@playwright/test');

// VIBE-354, VIBE-372: new terminal tabs run on the bundled Windows console host by default. App
// settings has one switch for it, "Windows built-in console host", which opts out. The retired
// "Modern Windows console host" switch and its field are gone from the page.

async function openSettings(page, initial) {
    await page.addInitScript(() => sessionStorage.setItem('viberails_tab', 'console-host-fixture'));
    await page.routeWebSocket('**/api/v1/events/ws*', () => {});
    let settings = { apiKey: '', computerName: '', mcpEnabled: true, ...initial };
    const saves = [];
    await page.route('**/api/v1/**', async route => {
        const request = route.request();
        const path = new URL(request.url()).pathname;
        if (path === '/api/v1/settings') {
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
        return route.fulfill({ json: payloads[path] || {} });
    });
    await page.goto('/?view=settings', { waitUntil: 'domcontentloaded' });
    const root = page.locator('#app-content [data-view="settings"]');
    await expect(root.locator('#settings-save-button')).toBeVisible();
    return { root, saves };
}

for (const width of [1280, 390]) {
    test(`the built-in console host switch is off by default and saves the opt-out at ${width}px`, async ({ page }) => {
        await page.setViewportSize({ width, height: 900 });
        // A settings answer that still carries the retired opt-in must not read as an opt-out.
        const { root, saves } = await openSettings(page, { bundledConsoleHostAvailable: true, modernConsoleHost: true });
        const row = root.locator('#setting-built-in-console-host-row');
        const toggle = root.getByRole('switch', { name: 'Windows built-in console host', exact: true });
        const save = root.locator('#settings-save-button');

        await expect(row).toBeVisible();
        await expect(toggle).not.toBeChecked();
        await expect(toggle).toBeEnabled();
        await expect(row).toContainText('New terminal tabs run on the console host that ships with Vibe Rails');
        await expect(root.locator('#setting-built-in-console-host-unavailable')).toBeHidden();
        await expect(root.locator('#setting-modern-console-host, #setting-modern-console-host-row')).toHaveCount(0);
        await expect(root.getByText('Modern Windows console host')).toHaveCount(0);
        expect(await row.evaluate(element => element.scrollWidth <= element.clientWidth + 1)).toBe(true);

        await toggle.check();
        await expect(save).toBeEnabled();
        await save.click();
        await expect(root.locator('#settings-save-state')).toHaveText('All changes saved');
        expect(saves).toHaveLength(1);
        expect(saves[0].builtInConsoleHost).toBe(true);
        expect('modernConsoleHost' in saves[0]).toBe(false);
        // How new local terminals are hosted is not a presence-gated change.
        expect(saves[0].presenceProof).toBeNull();
        await expect(toggle).toBeChecked();

        await toggle.uncheck();
        await save.click();
        await expect(root.locator('#settings-save-state')).toHaveText('All changes saved');
        expect(saves[1].builtInConsoleHost).toBe(false);
    });
}

test('the switch is hidden where the bundled host cannot apply, and another save leaves the choice alone', async ({ page }) => {
    // macOS and Linux report no availability at all.
    const { root, saves } = await openSettings(page, { builtInConsoleHost: true });
    await expect(root.locator('#setting-built-in-console-host-row')).toBeHidden();
    await root.getByRole('switch', { name: 'VS Code Spell Check', exact: true }).check();
    await root.locator('#settings-save-button').click();
    await expect(root.locator('#settings-save-state')).toHaveText('All changes saved');
    expect(saves[0].vsCodeSpellCheck).toBe(true);
    expect('builtInConsoleHost' in saves[0]).toBe(false);
});

test('a build without the bundled host says so and disables the switch', async ({ page }) => {
    const { root } = await openSettings(page, { bundledConsoleHostAvailable: false,
        bundledConsoleHostUnavailableReason: 'This build does not include conpty.dll and OpenConsole.exe.' });
    const toggle = root.getByRole('switch', { name: 'Windows built-in console host', exact: true });
    await expect(root.locator('#setting-built-in-console-host-row')).toBeVisible();
    await expect(toggle).toBeDisabled();
    await expect(root.locator('#setting-built-in-console-host-unavailable')).toHaveText(
        'This build does not include conpty.dll and OpenConsole.exe. Terminals use the console host built into Windows.');
});
