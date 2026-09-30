const { test, expect } = process.env.VIBERAILS_REMOTE_LINK_STATIC === '1'
    ? require('@playwright/test')
    : require('./fixtures');

const MASK = '••••••••abcd';
const LINK_URL = 'https://viberails.ai/link';

async function openSettings(page, { bridge = false } = {}) {
    await page.addInitScript(({ bridge }) => {
        sessionStorage.setItem('viberails_tab', 'remote-link-fixture');
        if (bridge) {
            window.__openedSignInPages = [];
            window.__viberails_openExternal__ = url => window.__openedSignInPages.push(url);
        }
    }, { bridge });
    await page.routeWebSocket('**/api/v1/events/ws*', () => {});
    let state = { status: 'idle' };
    let settings = { apiKey: '', computerName: '', machineName: 'Fixture machine', mcpEnabled: true };
    let nextStart;
    const calls = [];
    await page.route('**/api/v1/**', async route => {
        const request = route.request();
        const pathname = new URL(request.url()).pathname;
        const method = request.method();
        calls.push({ pathname, method, body: request.postDataJSON() });
        if (pathname === '/api/v1/settings/remote-link') {
            if (method === 'POST') state = nextStart || {
                status: 'pending', userCode: 'BXQK-2M7T', verificationUri: LINK_URL,
                expiresAt: new Date(Date.now() + 600_000).toISOString(), interval: 3
            };
            if (method === 'DELETE') state = { status: 'cancelled' };
            return route.fulfill({ json: state });
        }
        if (pathname === '/api/v1/settings') {
            if (method === 'POST') {
                const saved = request.postDataJSON();
                const previousKey = settings.apiKey;
                settings = { ...settings, ...saved, apiKey: saved.clearApiKey ? '' : saved.apiKey || settings.apiKey };
                if (settings.apiKey !== previousKey) state = { status: 'idle' };
            }
            return route.fulfill({ json: settings });
        }
        const payloads = {
            '/api/v1/context': { isInGit: false, rootPath: 'C:/fixture', launchDirectory: 'C:/fixture' },
            '/api/v1/settings/pin/status': { isSet: false },
            '/api/v1/settings/db-size': { bytes: 0 },
            '/api/v1/environments': { environments: [] },
            '/api/v1/llm-picker/preferences': { items: [] }
        };
        return route.fulfill({ json: payloads[pathname] || {} });
    });
    await page.goto('/?view=settings', { waitUntil: 'domcontentloaded' });
    const root = page.locator('#app-content [data-view="settings"]');
    await expect(root).toBeVisible();
    await expect(page.locator('[data-action=remote-account]:visible')).toBeVisible();
    return {
        root, calls,
        approve: () => {
            settings.apiKey = MASK;
            state = { status: 'linked', keyHint: MASK, account: { email: 'rob@example.com' } };
        },
        nextStart: value => { nextStart = value; }
    };
}

test('browser sign-in opens from an explicit click and saves only the linked key alongside a settings draft', async ({ page, context }, testInfo) => {
    await context.grantPermissions(['clipboard-read', 'clipboard-write']);
    await context.route(LINK_URL, route => route.fulfill({ contentType: 'text/html', body: '<h1>Enter your VibeRails code</h1>' }));
    const { root, calls, approve } = await openSettings(page);
    await root.locator('#setting-computer-name').fill('Unsaved laptop name');
    await page.locator('[data-action=remote-account]:visible').click();
    await page.locator('[data-remote-link-start]').click();
    await expect(page.locator('[data-remote-link-code]')).toHaveText('BXQK-2M7T');
    expect(context.pages()).toHaveLength(1);
    const link = page.getByRole('link', { name: 'Open sign-in page' });
    await expect(link).toHaveAttribute('href', LINK_URL);
    await expect(link).toHaveAttribute('rel', 'noopener noreferrer');
    await page.getByRole('button', { name: 'Copy code', exact: true }).click();
    await expect(page.locator('[data-remote-link-copy-status]')).toHaveText('Code copied.');
    expect(await page.evaluate(() => navigator.clipboard.readText())).toBe('BXQK-2M7T');
    await page.locator('[data-remote-account-link]').scrollIntoViewIfNeeded();
    await page.screenshot({ path: testInfo.outputPath('browser-sign-in.png') });
    const popupPromise = page.waitForEvent('popup');
    await link.click();
    const popup = await popupPromise;
    await expect(popup).toHaveURL(LINK_URL);
    await popup.close();
    approve();
    await expect(page.locator('[data-remote-link-status]')).toContainText('Connected as rob@example.com', { timeout: 7000 });
    await page.getByRole('button', { name: 'Close dialog' }).click();
    const key = root.locator('#setting-api-key');
    await expect(key).toHaveValue(MASK);
    expect(await key.getAttribute('data-original-value')).toBe(MASK);
    await expect(root.locator('#setting-computer-name')).toHaveValue('Unsaved laptop name');
    await expect(root.locator('#settings-save-button')).toBeEnabled();
    await root.locator('#settings-save-button').click();
    await expect(root.locator('#settings-save-button')).toBeDisabled();
    const save = calls.find(call => call.pathname === '/api/v1/settings' && call.method === 'POST');
    expect(save.body.apiKey).toBe('');
    expect(save.body.computerName).toBe('Unsaved laptop name');
    expect(save.body.clearApiKey).toBe(false);
    await key.fill('');
    await root.locator('#settings-save-button').click();
    await page.locator('[data-action=remote-account]:visible').click();
    await expect(page.locator('[data-remote-link-status]')).toContainText('Sign in to connect');
    await page.evaluate(mask => window.app.appEventClient._handleMessage(JSON.stringify({
        type: 'remote-account-linked', payload: { keyHint: mask, account: { email: 'old@example.com' } }
    })), MASK);
    await expect(page.locator('[data-remote-link-start]')).toBeEnabled();
    await expect(key).toHaveValue('');
    await expect(page.locator('[data-remote-link-status]')).not.toContainText('Connected');
});

test('VS Code bridge opens the same code-free page on a narrow panel, and Cancel leaves paste usable', async ({ page, context }, testInfo) => {
    await page.setViewportSize({ width: 420, height: 900 });
    const { root, calls } = await openSettings(page, { bridge: true });
    await page.locator('[data-action=remote-account]:visible').click();
    await page.locator('[data-remote-link-start]').click();
    await expect(page.locator('[data-remote-link-code]')).toHaveText('BXQK-2M7T');
    await page.getByRole('link', { name: 'Open sign-in page' }).click();
    expect(await page.evaluate(() => window.__openedSignInPages)).toEqual([LINK_URL]);
    expect(context.pages()).toHaveLength(1);
    const panel = page.locator('[data-remote-account-link]');
    await panel.scrollIntoViewIfNeeded();
    expect(await panel.evaluate(element => element.scrollWidth <= element.clientWidth + 1)).toBe(true);
    await page.screenshot({ path: testInfo.outputPath('vscode-sign-in-narrow.png') });
    await page.locator('[data-remote-link-cancel]').click();
    await expect(page.locator('[data-remote-link-status]')).toContainText('Sign-in cancelled');
    await expect(page.locator('[data-remote-link-pending]')).not.toBeVisible();
    await page.getByRole('button', { name: 'Close dialog' }).click();
    await root.locator('#setting-api-key').fill('manually-pasted-key');
    await expect(root.locator('#settings-save-button')).toBeEnabled();
    expect(calls.filter(call => call.pathname.endsWith('/remote-link') && call.method === 'DELETE')).toHaveLength(1);
    expect(calls.filter(call => call.pathname === '/api/v1/settings' && call.method === 'POST')).toHaveLength(0);
});

test('unavailable site and denied or expired requests offer retry while preserving the paste box', async ({ page }) => {
    const { root, nextStart } = await openSettings(page);
    await root.locator('#setting-api-key').fill('draft-key');
    await page.locator('[data-action=remote-account]:visible').click();
    for (const [status, message] of [['unavailable', 'not available'], ['denied', 'denied'], ['expired', 'expired']]) {
        nextStart({ status });
        await page.locator('[data-remote-link-start]').click();
        await expect(page.locator('[data-remote-link-status]')).toContainText(message);
        await expect(page.locator('[data-remote-link-start]')).toBeEnabled();
        await expect(root.locator('#setting-api-key')).toHaveValue('draft-key');
        await expect(page.locator('[data-remote-link-pending]')).not.toBeVisible();
    }
});
