const { test, expect } = require('@playwright/test');

test('notifications default to compact dark and remember the selected appearance', async ({ page }) => {
    await page.addInitScript(() => sessionStorage.setItem('viberails_tab', 'toast-fixture'));
    await page.routeWebSocket('**/api/v1/events/ws*', () => {});
    await page.route('**/api/v1/**', route => route.fulfill({ json: route.request().url().includes('/context') ? { isInGit: true, rootPath: 'C:/fixture' } : {} }));
    await page.goto('/?view=settings', { waitUntil: 'domcontentloaded' });
    const select = page.locator('#setting-toast-theme');
    await expect(select).toHaveValue('dark');
    await select.selectOption('dark');
    const dark = page.locator('.notAToasttoast-container > .notAToastvr-dark').last();
    await expect(dark).toBeVisible();
    const style = await dark.evaluate(element => {
        const css = getComputedStyle(element);
        return { background: css.backgroundColor, blur: css.backdropFilter, width: element.offsetWidth, height: element.offsetHeight };
    });
    expect(style.background).toBe('rgb(24, 29, 39)');
    expect(style.blur).toBe('none');
    expect(style.width).toBeLessThanOrEqual(360);
    expect(style.height).toBeLessThan(90);
    await expect(dark).toHaveCSS('opacity', '1');
    await page.screenshot({ path: 'test-results/vb29-toast-midnight.png' });
    await select.selectOption('light');
    await expect(page.locator('.notAToasttoast-container > .notAToastvr-light').last()).toBeVisible();
    await page.goto('/?view=settings', { waitUntil: 'domcontentloaded' });
    await expect(select).toHaveValue('light');
    await page.setViewportSize({ width: 375, height: 720 });
    await page.evaluate(async () => {
        const { showAppToast } = await import('/js/modules/toast-service.js');
        showAppToast('Run failed', '<img src=x onerror="window.toastInjected=true"> ' + 'long-session-id-'.repeat(12), 'error', { requireDismiss: true });
    });
    const light = page.locator('.notAToasttoast-container > .notAToastvr-light').last();
    await expect(light).toContainText('<img src=x');
    expect(await page.evaluate(() => window.toastInjected)).toBeUndefined();
    expect(await light.evaluate(element => element.getBoundingClientRect().right)).toBeLessThanOrEqual(375);
    await light.locator('.notAToastclsBtn').click();
    await expect(light).toHaveCount(0);
});
