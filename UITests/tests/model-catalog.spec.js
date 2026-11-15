const { test, expect } = require('@playwright/test');

test('late catalog responses preserve live selection and update every subsequent picker', async ({ page }) => {
    await page.route('**/catalog-fixture', route => route.fulfill({ contentType: 'text/html', body: '<!doctype html><div id="picker"></div>' }));
    await page.goto('/catalog-fixture');
    await page.evaluate(async () => {
        const ui = await import('/js/modules/board-launch-options.js');
        window.ui = ui;
        const root = document.getElementById('picker');
        root.innerHTML = ui.renderBoardLaunchOptions('base:codex', { model: 'gpt-6-astra', effort: 'high' });
        window.apiReads = 0;
        const app = { apiCall: () => { window.apiReads++; return new Promise(resolve => { window.catalogDone = resolve; }); } };
        window.cleanup = ui.bindBoardLaunchOptions(root, 'base:codex', { app });
    });
    await page.locator('[data-board-launch-model]').selectOption('gpt-6-luna');
    await page.evaluate(() => window.catalogDone({ version: 1, revision: 1, models: [{ cli: 'codex', value: 'gpt-67', label: 'Next Codex' }] }));
    await expect(page.locator('[data-board-launch-model] option[value="gpt-67"]')).toHaveText('Next Codex');
    await expect(page.locator('[data-board-launch-model]')).toHaveValue('gpt-6-luna');
    await expect(page.locator('[data-board-launch-effort]')).toHaveValue('high');
    await page.locator('[data-board-launch-model]').selectOption('gpt-67');
    expect(await page.evaluate(() => window.ui.readBoardLaunchOptions(document.getElementById('picker'), 'base:codex').model)).toBe('gpt-67');
    expect(await page.evaluate(() => window.apiReads)).toBe(1);
});
