const { test, expect } = require('@playwright/test');

for (const width of [1100, 390]) {
    test(`private script terminal accepts input and fits at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 850 });
        await page.goto('/UITests/fixtures/script-run.html');
        await page.getByRole('button', { name: 'Run test_script.py' }).click();
        const dialog = page.getByRole('dialog');
        await expect(dialog).toBeVisible();
        await expect(dialog.getByRole('status')).toHaveText('Running');
        await expect.poll(() => page.evaluate(() => window.scriptFixture.scripts.runWindow.terminal.getPlainText())).toContain('Enter something');
        await expect(dialog.locator('.xterm-helper-textarea')).toBeFocused();
        await page.keyboard.type('hello from the little terminal');
        await page.keyboard.press('Enter');
        await expect(dialog.getByRole('status')).toHaveText('Finished · exit 0');
        await expect.poll(() => page.evaluate(() => window.scriptFixture.scripts.runWindow.terminal.getPlainText())).toContain('hello from the little terminal');
        expect(await dialog.evaluate(node => node.scrollWidth <= window.innerWidth)).toBe(true);
        expect(await page.evaluate(() => Object.keys(localStorage).filter(key => key.startsWith('viberails.pythonRun.')))).toEqual([]);
        await page.screenshot({ path: testInfo.outputPath(`script-terminal-${width}.png`) });
        await dialog.getByRole('button', { name: 'Run again' }).click();
        await expect(dialog.getByRole('status')).toHaveText('Running');
        await dialog.getByRole('button', { name: 'Stop', exact: true }).click();
        await expect(dialog.getByRole('status')).toHaveText('Stopped');
        await dialog.getByRole('button', { name: 'Close', exact: true }).click();
        await expect(dialog).toHaveCount(0);
        await expect(page.getByRole('button', { name: 'Run test_script.py' })).toBeFocused();
    });
}

test('closing a pending PIN clears its field and launches no socket', async ({ page }) => {
    await page.goto('/UITests/fixtures/script-run.html');
    await page.evaluate(() => { window.scriptFixture.scripts.state.requirePinEachRun = true; });
    await page.getByRole('button', { name: 'Run test_script.py' }).click();
    const pin = page.locator('.python-scripts-pin-modal');
    await expect(pin).toBeVisible();
    await pin.getByLabel('Signing PIN').fill('1234');
    await pin.getByRole('button', { name: 'Cancel', exact: true }).last().click();
    await expect(pin).toHaveCount(0);
    await expect(page.locator('[data-run-status]')).toHaveText('Run cancelled');
    expect(await page.evaluate(() => window.scriptFixture.connections.length)).toBe(0);
    await page.getByRole('button', { name: 'Close', exact: true }).click();
    expect(await page.evaluate(() => window.scriptFixture.scripts.runningNames.size)).toBe(0);
});

test('minimize keeps the same session alive while the page and other dialogs remain usable', async ({ page }, testInfo) => {
    await page.setViewportSize({ width: 1100, height: 850 });
    await page.goto('/UITests/fixtures/script-run.html');
    await page.getByRole('button', { name: 'Run test_script.py' }).click();
    const dialog = page.getByRole('dialog');
    await expect(dialog.getByRole('status')).toHaveText('Running');
    await page.keyboard.type('partly typed');
    await dialog.getByRole('button', { name: 'Minimize' }).click();
    await expect(dialog).toBeHidden();
    await page.getByLabel('Keep using the app').fill('The app is still usable');
    // Closing ordinary app dialogs / navigating within the SPA must not own this terminal.
    await page.evaluate(() => window.scriptFixture.scripts.unmount());
    await expect(page.locator('#script-run-dock button')).toHaveText('test_script.py · Running');
    await page.screenshot({ path: testInfo.outputPath('script-terminal-minimized.png') });
    await page.locator('#script-run-dock button').click();
    await expect(dialog).toBeVisible();
    await expect(dialog.locator('.xterm-helper-textarea')).toBeFocused();
    expect(await page.evaluate(() => window.scriptFixture.connections.length)).toBe(1);
    await expect.poll(() => page.evaluate(() => window.scriptFixture.scripts.runWindow.terminal.getPlainText())).toContain('partly typed');
    await page.keyboard.type(' and restored');
    await page.keyboard.press('Enter');
    await expect(dialog.getByRole('status')).toHaveText('Finished · exit 0');
    await expect(page.getByLabel('Keep using the app')).toHaveValue('The app is still usable');
    await page.screenshot({ path: testInfo.outputPath('script-terminal-restored.png') });
});

test('another script can run while the first stays minimized, and restoring never starts a duplicate', async ({ page }) => {
    await page.goto('/UITests/fixtures/script-run.html');
    await page.getByRole('button', { name: 'Run test_script.py' }).click();
    await expect(page.getByRole('status')).toHaveText('Running');
    await page.evaluate(() => {
        const scripts = window.scriptFixture.scripts;
        scripts.state.scripts.push({ name: 'second-script', fileName: 'second.py', displayName: 'second.py', status: 'approved' });
        void scripts.run('second-script');
    });
    await expect(page.getByRole('dialog', { name: 'second.py' })).toBeVisible();
    await expect(page.locator('#script-run-dock button')).toHaveText('test_script.py · Running');
    await page.locator('#script-run-dock button').click();
    await expect(page.getByRole('dialog', { name: 'test_script.py' })).toBeVisible();
    await expect(page.locator('#script-run-dock button')).toHaveText('second.py · Running');
    expect(await page.evaluate(() => window.scriptFixture.connections.length)).toBe(2);
    await page.getByRole('dialog', { name: 'test_script.py' }).getByRole('button', { name: 'Close', exact: true }).click();
    await expect(page.locator('#script-run-dock button')).toHaveText('second.py · Running');
});
