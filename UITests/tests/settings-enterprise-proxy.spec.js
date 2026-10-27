const { test, expect } = require('@playwright/test');

for (const width of [1280, 390]) {
    test(`enterprise gateway settings save, reload and clear at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 900 });
        await page.addInitScript(() => sessionStorage.setItem('viberails_tab', 'enterprise-fixture'));
        await page.routeWebSocket('**/api/v1/events/ws*', () => {});
        let settings = { apiKey: '', computerName: '', mcpEnabled: true, codexTokenSaverEnabled: false };
        const saves = [];
        let challenges = 0;
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
            if (path === '/api/v1/settings/security-challenge') {
                challenges++;
                return route.fulfill({ json: { pinRequired: false, code: 'C0FFEE01' } });
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
        await root.locator('#settings-tab-llm').click();
        const save = root.locator('#settings-save-button');
        await expect(root.getByLabel(/Enable .* proxy/)).toHaveCount(0);
        await expect(root.getByLabel('Enterprise Network Proxy', { exact: true })).toHaveCount(4);
        const values = {
            codex: 'https://genai.example.test/v1',
            claude: 'https://gateway.example/anthropic/v1',
            opencode: 'https://gateway.example/opencode/v1',
            grok: 'https://gateway.example/grok/v1'
        };
        for (const [provider, value] of Object.entries(values))
            await root.locator(`#setting-${provider}-enterprise-network-proxy`).fill(value);
        await expect(root.locator('#setting-token-saver-codex')).not.toBeChecked();
        await expect(save).toBeEnabled();
        await save.click();
        // A new gateway receives that CLI's credentials, so saving one needs confirmation.
        await page.locator('#settings-presence-proof').fill('C0FFEE01');
        await page.locator('#settings-presence-confirm').click();
        await expect(save).toBeDisabled();
        expect(challenges).toBe(1);
        expect(saves[0].presenceProof).toBe('C0FFEE01');
        expect(saves[0].codexEnterpriseNetworkProxy).toBe(values.codex);
        expect(saves[0].claudeEnterpriseNetworkProxy).toBe(values.claude);
        expect(saves[0].openCodeEnterpriseNetworkProxy).toBe(values.opencode);
        expect(saves[0].grokEnterpriseNetworkProxy).toBe(values.grok);
        expect(saves[0].codexLlmProxyEnabled).toBeUndefined();
        expect(saves[0].codexTokenSaverEnabled).toBe(false);
        await root.locator('#settings-panel-llm').screenshot({ path: testInfo.outputPath('llm-settings.png') });

        await page.reload({ waitUntil: 'domcontentloaded' });
        await expect(page.locator('#app-content > *').first()).toBeAttached();
        await page.locator('[data-action="navigate-settings"]:visible').click();
        await root.locator('#settings-tab-llm').click();
        for (const [provider, value] of Object.entries(values))
            await expect(root.locator(`#setting-${provider}-enterprise-network-proxy`)).toHaveValue(value);
        await root.locator('#setting-codex-enterprise-network-proxy').fill('');
        await save.click();
        await expect(save).toBeDisabled();
        expect(saves[1].codexEnterpriseNetworkProxy).toBe('');
        // Clearing restores the provider default and does not ask again.
        expect(challenges).toBe(1);
        expect(saves[1].presenceProof).toBeNull();
        expect(saves[1].claudeEnterpriseNetworkProxy).toBe(values.claude);
        await expect(root.locator('#setting-token-saver-codex')).not.toBeChecked();
    });
}
