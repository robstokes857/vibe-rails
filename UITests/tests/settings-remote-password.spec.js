const { test, expect } = require('@playwright/test');

async function openSettings(page, credential = null) {
    await page.addInitScript(() => sessionStorage.setItem('viberails_tab', 'password-fixture'));
    await page.routeWebSocket('**/api/v1/events/ws*', () => {});
    const writes = [];
    let failSave = false;
    await page.route('**/api/v1/**', async route => {
        const request = route.request();
        const path = new URL(request.url()).pathname;
        if (request.method() === 'POST' || request.method() === 'DELETE') {
            const body = request.postDataJSON();
            if (path.startsWith('/api/v1/settings')) writes.push({ path, body });
            if (path === '/api/v1/settings/pin') {
                if (failSave || body.presenceProof !== (credential ?? 'CONFIRM01')) {
                    return route.fulfill({ status: 403, json: { error: 'Current password or PIN required.' } });
                }
                credential = request.method() === 'DELETE' ? null : body.pin;
            }
            return route.fulfill({ json: {} });
        }
        const payloads = {
            '/api/v1/context': { isInGit: false, rootPath: 'C:/fixture', launchDirectory: 'C:/fixture' },
            '/api/v1/settings': { apiKey: '', computerName: '', machineName: 'Fixture', mcpEnabled: true },
            '/api/v1/settings/pin/status': { isSet: credential !== null },
            '/api/v1/settings/security-challenge': { pinRequired: credential !== null, code: credential === null ? 'CONFIRM01' : null },
            '/api/v1/environments': { environments: [] },
            '/api/v1/llm-picker/preferences': { items: [] }
        };
        return route.fulfill({ json: payloads[path] || {} });
    });
    await page.goto('/?view=settings', { waitUntil: 'domcontentloaded' });
    await expect(page.locator('#btn-set-pin')).toBeVisible();
    return { writes, failSave: value => { failSave = value; } };
}

async function startChange(page, proof) {
    await page.locator('#btn-set-pin').click();
    await page.locator('#settings-presence-proof').fill(proof);
    await page.locator('#settings-presence-proof').press('Enter');
    await expect(page.locator('#remote-access-password')).toBeFocused();
}

test('setup enforces the six-character rule and saves exact password text', async ({ page }) => {
    const { writes } = await openSettings(page);
    await startChange(page, 'CONFIRM01');
    const input = page.getByLabel('New password (required)');
    await expect(input).toHaveAttribute('type', 'password');
    await expect(input).toHaveAttribute('autocomplete', 'new-password');
    await expect(input).toHaveAttribute('maxlength', '128');
    for (const invalid of ['1234', '123456', 'abcdef', 'abcd1']) {
        await input.fill(invalid);
        await input.press('Enter');
        await expect(input).toHaveAttribute('aria-invalid', 'true');
        await expect(page.locator('#remote-password-error')).toContainText('at least 6 characters');
        expect(writes).toHaveLength(0);
        await expect(input).toHaveValue(invalid);
    }
    await input.fill(' pass1 ');
    await input.press('Enter');
    await expect(page.locator('#remote-password-form')).toHaveCount(0);
    expect(writes).toEqual([{ path: '/api/v1/settings/pin', body: { pin: ' pass1 ', presenceProof: 'CONFIRM01' } }]);
    await expect(page.locator('#pin-status-badge')).toContainText('Password or PIN set');
});

for (const oldCredential of ['1234', '123456', 'Current password1']) {
    test(`change accepts an existing ${oldCredential.length}-character credential without setup validation`, async ({ page }) => {
        const { writes } = await openSettings(page, oldCredential);
        await startChange(page, oldCredential);
        await page.locator('#remote-access-password').fill('abcde1');
        await page.locator('#btn-pin-confirm').click();
        await expect(page.locator('#remote-password-form')).toHaveCount(0);
        expect(writes[0].body).toEqual({ pin: 'abcde1', presenceProof: oldCredential });
    });
}

test('failed change is recoverable; Escape clears secrets and restores focus at narrow width', async ({ page }, testInfo) => {
    await page.setViewportSize({ width: 375, height: 812 });
    const state = await openSettings(page, '1234');
    await startChange(page, 'wrong1');
    const input = page.locator('#remote-access-password');
    await input.fill('abcde1');
    await input.press('Enter');
    await expect(page.locator('#remote-password-error')).toBeVisible();
    await expect(input).toBeEnabled();
    await expect(input).toHaveValue('abcde1');
    await expect(page.locator('#btn-pin-confirm')).toBeEnabled();
    expect(await page.locator('.modal-content').evaluate(el => el.scrollWidth <= el.clientWidth + 1)).toBe(true);
    // Do not capture typed credentials, even though these are synthetic test values.
    await input.fill('');
    await page.screenshot({ path: testInfo.outputPath('remote-password-narrow.png') });
    await page.evaluate(() => { window.closedPasswordInput = document.querySelector('#remote-access-password'); });
    await input.press('Escape');
    await expect(page.locator('#btn-set-pin')).toBeFocused();
    expect(await page.evaluate(() => window.closedPasswordInput.value)).toBe('');
    await startChange(page, '1234');
    await expect(input).toHaveValue('');
    await input.fill('abcde1');
    await input.press('Enter');
    await expect(page.locator('#remote-password-form')).toHaveCount(0);
    expect(state.writes).toHaveLength(2);
});

test('unrelated settings save preserves a legacy PIN without asking to replace it', async ({ page }) => {
    const { writes } = await openSettings(page, '1234');
    await page.locator('#setting-computer-name').fill('Changed name');
    await page.locator('#settings-save-button').click();
    await expect(page.locator('#settings-save-button')).toBeDisabled();
    expect(writes).toHaveLength(1);
    expect(writes[0].path).toBe('/api/v1/settings');
    expect(writes[0].body.presenceProof).toBeNull();
    expect(writes[0].body).not.toHaveProperty('pin');
    await expect(page.locator('#settings-presence-form')).toHaveCount(0);
});
