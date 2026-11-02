const { test, expect } = process.env.VIBERAILS_REMOTE_LINK_STATIC === '1'
    ? require('@playwright/test')
    : require('./fixtures');

const MASK = '••••••••abcd';
const LINK_URL = 'https://viberails.ai/link';
const COMPLETE_LINK_URL = `${LINK_URL}#code=BXQK-2M7T`;
const PRESENCE_CODE = 'C0FFEE01';

// Clearing or replacing the account key needs the same presence confirmation as the server (VIBE-182).
async function confirmPresence(page) {
    await page.locator('#settings-presence-proof').fill(PRESENCE_CODE);
    await page.locator('#settings-presence-confirm').click();
}

async function openSettings(page, { bridge = false, signedIn = false, email = 'rob@example.com', organization } = {}) {
    await page.addInitScript(({ bridge }) => {
        sessionStorage.setItem('viberails_tab', 'remote-link-fixture');
        if (bridge) {
            window.__openedSignInPages = [];
            window.__viberails_openExternal__ = url => window.__openedSignInPages.push(url);
        }
    }, { bridge });
    await page.routeWebSocket('**/api/v1/events/ws*', () => {});
    let state = { status: 'idle' };
    let settings = { apiKey: signedIn ? MASK : '', remoteAccountEmail: signedIn ? email : null,
        computerName: '', machineName: 'Fixture machine', mcpEnabled: true };
    // Nothing supplies an organization yet; the field is the managed-account placeholder.
    if (organization) settings.remoteAccountOrganization = organization;
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
                if (settings.apiKey !== previousKey) {
                    state = { status: 'idle' };
                    settings.remoteAccountEmail = null;
                }
            }
            return route.fulfill({ json: settings });
        }
        const payloads = {
            '/api/v1/context': { isInGit: false, rootPath: 'C:/fixture', launchDirectory: 'C:/fixture' },
            '/api/v1/settings/pin/status': { isSet: false },
            '/api/v1/settings/security-challenge': { pinRequired: false, code: PRESENCE_CODE },
            '/api/v1/environments': { environments: [] },
            '/api/v1/llm-picker/preferences': { items: [] }
        };
        return route.fulfill({ json: payloads[pathname] || {} });
    });
    await page.goto('/?view=settings', { waitUntil: 'domcontentloaded' });
    const root = page.locator('#app-content [data-view="settings"]');
    await expect(root).toBeVisible();
    await expect(root.locator('[data-account-settings-action]')).toBeVisible();
    return {
        root, calls,
        approve: () => {
            settings.apiKey = MASK;
            settings.remoteAccountEmail = 'rob@example.com';
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
    await page.locator('[data-account-nav]:visible').click();
    await page.locator('[data-remote-link-start]').click();
    await expect(page.locator('[data-remote-link-code]')).toHaveText('BXQK-2M7T');
    expect(context.pages()).toHaveLength(1);
    const link = page.getByRole('link', { name: 'Open sign-in page' });
    await expect(link).toHaveAttribute('href', COMPLETE_LINK_URL);
    await expect(link).toHaveAttribute('rel', 'noopener noreferrer');
    await page.getByRole('button', { name: 'Copy code', exact: true }).click();
    await expect(page.locator('[data-remote-link-copy-status]')).toHaveText('Code copied.');
    expect(await page.evaluate(() => navigator.clipboard.readText())).toBe('BXQK-2M7T');
    await page.locator('[data-remote-account-link]').scrollIntoViewIfNeeded();
    await page.screenshot({ path: testInfo.outputPath('browser-sign-in.png') });
    const popupPromise = page.waitForEvent('popup');
    await link.click();
    const popup = await popupPromise;
    await expect(popup).toHaveURL(COMPLETE_LINK_URL);
    await popup.close();
    approve();
    await expect(page.locator('[data-remote-link-status]')).toHaveText('Logged in rob@example.com', { timeout: 7000 });
    await page.getByRole('button', { name: 'Close dialog' }).click();
    await expect(page.locator('[data-account-nav]:visible')).toHaveCount(0);
    await expect(page.locator('[data-action="account-menu"]')).toHaveAccessibleName('Account menu. Signed in as rob@example.com');
    await expect(root.locator('[data-account-status]')).toHaveText('Logged in rob@example.com');
    await expect(root.locator('[data-account-settings-action]')).toHaveText('Switch account');
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
    expect(save.body.presenceProof).toBeNull();
    await key.fill('');
    await root.locator('#settings-save-button').click();
    await confirmPresence(page);
    await expect(page.locator('[data-account-nav]:visible')).toBeVisible();
    const clear = calls.filter(call => call.pathname === '/api/v1/settings' && call.method === 'POST').at(-1);
    expect(clear.body.clearApiKey).toBe(true);
    expect(clear.body.presenceProof).toBe(PRESENCE_CODE);
    await page.locator('[data-account-nav]:visible').click();
    await expect(page.locator('[data-remote-link-status]')).toContainText('Sign in to connect');
    await page.evaluate(mask => window.app.appEventClient._handleMessage(JSON.stringify({
        type: 'remote-account-linked', payload: { keyHint: mask, account: { email: 'old@example.com' } }
    })), MASK);
    await expect(page.locator('[data-remote-link-start]')).toBeEnabled();
    await expect(key).toHaveValue('');
    await expect(page.locator('[data-remote-link-status]')).not.toContainText('Logged in');
    await expect(root.locator('[data-account-status]')).toHaveText('Sign in to your viberails.ai account.');
});

test('VS Code bridge carries the public code on a narrow panel, and Cancel leaves paste usable', async ({ page, context }, testInfo) => {
    await page.setViewportSize({ width: 420, height: 900 });
    const { root, calls } = await openSettings(page, { bridge: true });
    await root.locator('[data-account-settings-action]').click();
    await page.locator('[data-remote-link-start]').click();
    await expect(page.locator('[data-remote-link-code]')).toHaveText('BXQK-2M7T');
    await page.getByRole('link', { name: 'Open sign-in page' }).click();
    expect(await page.evaluate(() => window.__openedSignInPages)).toEqual([COMPLETE_LINK_URL]);
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
    await root.locator('[data-account-settings-action]').click();
    for (const [status, message] of [['unavailable', 'not available'], ['denied', 'denied'], ['expired', 'expired']]) {
        nextStart({ status });
        await page.locator('[data-remote-link-start]').click();
        await expect(page.locator('[data-remote-link-status]')).toContainText(message);
        await expect(page.locator('[data-remote-link-start]')).toBeEnabled();
        await expect(root.locator('#setting-api-key')).toHaveValue('draft-key');
        await expect(page.locator('[data-remote-link-pending]')).not.toBeVisible();
    }
});

for (const width of [1440, 768, 375, 320]) {
    test(`signed-out navigation keeps Settings and sign in reachable at ${width}px`, async ({ page }) => {
        await page.setViewportSize({ width, height: 900 });
        const { root } = await openSettings(page);
        const settingsLink = page.locator('[data-settings-nav]');
        await expect(page.locator('.app-sidebar, [data-action="switch-nav-layout"]')).toHaveCount(0);
        await expect(settingsLink).toBeVisible();
        await expect(settingsLink).toHaveAccessibleName('Settings');
        await expect(settingsLink.locator('i, svg')).toHaveCount(0);
        await expect(settingsLink).toHaveAttribute('aria-current', 'page');
        const bounds = await settingsLink.boundingBox();
        expect(bounds.x).toBeGreaterThanOrEqual(0);
        expect(bounds.x + bounds.width).toBeLessThanOrEqual(width);
        expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
        await page.locator('[data-action="navigate-home"]').click();
        await expect(root).toHaveCount(0);
        await settingsLink.focus();
        await settingsLink.press('Enter');
        await expect(root).toBeVisible();
        await expect(page.locator('[data-account-nav]:visible')).toHaveText('Sign in / Sign up');
        await expect(page.locator('[data-account-nav]:visible')).toHaveAccessibleName('Sign in or sign up to viberails.ai');
        await expect(root.locator('[data-account-settings-action]')).toHaveText('Sign in');
        await expect(page.locator('[data-account-menu-host]')).toBeHidden();
        await page.locator('[data-account-nav]:visible').click();
        await expect(page.locator('[data-remote-link-start]')).toBeEnabled();
        await expect(page.locator('[data-remote-link-signup]')).toBeVisible();
    });
}

test('signed-in top bar shows the account and a menu that closes back to its chip', async ({ page }, testInfo) => {
    const { root } = await openSettings(page, { signedIn: true });
    await expect(page.locator('[data-settings-nav]')).toBeHidden();
    const chip = page.locator('[data-action="account-menu"]');
    const menu = page.getByRole('dialog', { name: 'viberails.ai account' });
    await expect(chip).toHaveAccessibleName('Account menu. Signed in as rob@example.com');
    await expect(chip).toContainText('rob@example.com');
    await chip.click();
    await expect(menu).toBeVisible();
    await expect(chip).toHaveAttribute('aria-expanded', 'true');
    await expect(menu).toContainText('Signed in');
    await expect(menu).toContainText('Personal account');
    await expect(menu.locator('[data-account-key]')).toHaveText('••••abcd');
    await expect(menu.locator('[data-account-badge="managed"]')).toBeHidden();
    await expect(menu.locator('[data-account-org-block]')).toBeHidden();
    const site = menu.getByRole('link', { name: 'Manage account on viberails.ai' });
    await expect(site).toHaveAttribute('href', 'https://viberails.ai/Account');
    await expect(site).toHaveAttribute('rel', 'noopener noreferrer');
    await expect(menu.getByRole('button', { name: 'Account settings' })).toBeFocused();
    await page.screenshot({ path: testInfo.outputPath('account-menu-personal.png') });
    await page.keyboard.press('Escape');
    await expect(menu).toBeHidden();
    await expect(chip).toBeFocused();
    await expect(chip).toHaveAttribute('aria-expanded', 'false');

    await chip.click();
    await page.locator('#app-content').click({ position: { x: 5, y: 5 } });
    await expect(menu).toBeHidden();

    await root.locator('#setting-computer-name').fill('Unsaved laptop');
    await chip.click();
    await menu.getByRole('button', { name: 'Account settings' }).click();
    await expect(menu).toBeHidden();
    await expect(root.locator('#setting-computer-name')).toHaveValue('Unsaved laptop');
    await expect(root.locator('#settings-panel-general')).toBeVisible();

    await chip.click();
    await menu.getByRole('button', { name: 'Switch account' }).click();
    await expect(page.locator('[data-remote-link-status]')).toHaveText('Logged in rob@example.com');
    await expect(page.locator('[data-remote-link-start]')).toHaveText('Switch account');
});

test('a managed account names its organization and leaves switching to the administrator', async ({ page }, testInfo) => {
    const email = 'robert.stokes@example-company.com';
    const { root } = await openSettings(page, { signedIn: true, email, organization: 'Contoso <b>Ltd</b>' });
    const chip = page.locator('[data-action="account-menu"]');
    const menu = page.getByRole('dialog', { name: 'viberails.ai account' });
    await expect(chip).toHaveAccessibleName(`Account menu. Signed in as ${email}, managed by Contoso <b>Ltd</b>`);
    await expect(chip.locator('[data-account-chip-org]')).toHaveText('Contoso <b>Ltd</b>');
    // At 1280px the compact bar shows the organization icon only and keeps the links clear of the brand.
    await expect(chip.locator('[data-account-chip-org] [data-account-org-name]')).toBeHidden();
    const brand = await page.locator('.app-subnav-brand').boundingBox();
    const firstLink = await page.locator('.app-subnav-links .app-subnav-link').first().boundingBox();
    expect(brand.x + brand.width).toBeLessThanOrEqual(firstLink.x);
    await page.setViewportSize({ width: 1600, height: 720 });
    await expect(chip.locator('[data-account-chip-org] [data-account-org-name]')).toBeVisible();
    await page.setViewportSize({ width: 1280, height: 720 });
    await chip.click();
    await expect(menu.locator('[data-account-badge="managed"]')).toBeVisible();
    await expect(menu.locator('[data-account-badge="personal"]')).toBeHidden();
    await expect(menu.locator('[data-account-org-block]')).toContainText('Contoso <b>Ltd</b>');
    await expect(menu.locator('b')).toHaveCount(0);
    const switchAccount = menu.getByRole('button', { name: 'Switch account' });
    await expect(switchAccount).toBeDisabled();
    await expect(switchAccount).toHaveAccessibleDescription('Your administrator controls which account this computer uses.');
    await page.screenshot({ path: testInfo.outputPath('account-menu-managed.png') });
    await expect(root.locator('[data-account-status]')).toHaveText(`Logged in ${email}, managed by Contoso <b>Ltd</b>`);
    await expect(root.locator('[data-account-settings-action]')).toBeDisabled();
});

test('a manual key without account details offers sign-in from the menu', async ({ page }) => {
    await openSettings(page, { signedIn: true, email: null });
    const chip = page.locator('[data-action="account-menu"]');
    const menu = page.getByRole('dialog', { name: 'viberails.ai account' });
    await expect(chip).toHaveAccessibleName('Account menu. API key configured');
    await expect(chip).toContainText('API key');
    await chip.click();
    await expect(menu.locator('[data-account-menu-title]')).toHaveText('API key configured');
    await expect(menu.getByText('Signed in', { exact: true })).toBeHidden();
    await expect(menu.getByRole('link', { name: 'Manage account on viberails.ai' })).toBeHidden();
    await expect(menu.getByRole('button', { name: 'Switch account' })).toBeHidden();
    await menu.getByRole('button', { name: 'Sign in to viberails.ai' }).click();
    await expect(page.locator('[data-remote-link-start]')).toBeEnabled();
});

test('the account menu stays inside a narrow window', async ({ page }, testInfo) => {
    await page.setViewportSize({ width: 375, height: 760 });
    await openSettings(page, { signedIn: true, email: 'a-very-long-account-email-for-narrow-layouts@example.com', organization: 'A Very Long Organization Name Incorporated' });
    const chip = page.locator('[data-action="account-menu"]');
    await expect(chip.locator('[data-account-chip-label]')).toBeHidden();
    await chip.click();
    const box = await page.getByRole('dialog', { name: 'viberails.ai account' }).boundingBox();
    expect(box.x).toBeGreaterThanOrEqual(0);
    expect(box.x + box.width).toBeLessThanOrEqual(375);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
    await page.screenshot({ path: testInfo.outputPath('account-menu-narrow.png') });
});

test('saved account stays in Settings after reload', async ({ page }, testInfo) => {
    const email = 'a-very-long-account-email-for-narrow-layouts@example.com';
    const { root } = await openSettings(page, { signedIn: true, email });
    await page.addInitScript(() => localStorage.setItem('viberails_nav_layout', 'side'));
    await page.reload();
    await expect(page.locator('.app-subnav')).toBeVisible();
    await expect(page.locator('.app-sidebar')).toHaveCount(0);
    await page.locator('[data-action="account-menu"]').click();
    await page.getByRole('button', { name: 'Account settings', exact: true }).click();
    await expect(page.locator('[data-account-nav]:visible')).toHaveCount(0);
    await expect(root.locator('[data-account-status]')).toHaveText(`Logged in ${email}`);
    await expect(root.locator('[data-account-settings-action]')).toHaveText('Switch account');
    await page.setViewportSize({ width: 420, height: 900 });
    const card = root.getByRole('region', { name: 'Account', exact: true });
    expect(await card.evaluate(element => element.scrollWidth <= element.clientWidth + 1)).toBe(true);
    await card.screenshot({ path: testInfo.outputPath('account-top-narrow.png') });
    await root.locator('[data-account-settings-action]').click();
    await expect(page.locator('[data-remote-link-status]')).toHaveText(`Logged in ${email}`);
    await page.locator('[data-remote-link-start]').click();
    await expect(page.locator('[data-remote-link-code]')).toHaveText('BXQK-2M7T');
});

test('sign-in completed from another tab updates Settings and nav without discarding drafts', async ({ page }) => {
    const { root, approve } = await openSettings(page);
    await root.locator('#setting-computer-name').fill('Unsaved laptop');
    approve();
    await page.evaluate(() => window.app.appEventClient._handleMessage(JSON.stringify({
        type: 'remote-account-linked', payload: { account: { email: 'untrusted-event@example.com' } }
    })));
    await expect(root.locator('[data-account-status]')).toHaveText('Logged in rob@example.com');
    await expect(page.locator('[data-account-nav]:visible')).toHaveCount(0);
    await expect(page.locator('[data-settings-nav]')).toBeHidden();
    await expect(root.locator('#setting-computer-name')).toHaveValue('Unsaved laptop');
    await expect(root.locator('#settings-save-button')).toBeEnabled();
    await expect(root.locator('#setting-api-key')).toHaveValue(MASK);
});

test('manual keys have an honest fallback and account emails render as text', async ({ page }) => {
    const { root, calls } = await openSettings(page, { signedIn: true, email: '<img src=x onerror=alert(1)>' });
    await expect(root.locator('[data-account-status]')).toHaveText('Logged in <img src=x onerror=alert(1)>');
    await expect(root.locator('[data-account-status] img')).toHaveCount(0);
    await root.locator('#setting-api-key').fill('different-manual-key');
    await root.locator('#settings-save-button').click();
    await confirmPresence(page);
    await expect(root.locator('[data-account-status]')).toHaveText('API key configured');
    await expect(page.locator('[data-account-nav]:visible')).toHaveCount(0);
    const save = calls.find(call => call.pathname === '/api/v1/settings' && call.method === 'POST');
    expect(save.body.apiKey).toBe('different-manual-key');
    expect(save.body.presenceProof).toBe(PRESENCE_CODE);
});
