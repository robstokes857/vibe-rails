const { test, expect } = require('@playwright/test');

test('notifications use the app appearance without a separate setting', async ({ page }) => {
    await page.emulateMedia({ colorScheme: 'light' });
    await page.addInitScript(() => {
        sessionStorage.setItem('viberails_tab', 'toast-fixture');
        localStorage.setItem('viberails.toast.theme', 'light');
    });
    await page.routeWebSocket('**/api/v1/events/ws*', () => {});
    await page.route('**/api/v1/**', route => route.fulfill({ json: route.request().url().includes('/context') ? { isInGit: true, rootPath: 'C:/fixture' } : {} }));
    await page.goto('/?view=settings', { waitUntil: 'domcontentloaded' });
    await expect(page.locator('[data-view="settings"]')).toBeVisible();
    await expect(page.getByLabel('Notification appearance')).toHaveCount(0);
    await page.evaluate(async () => {
        const { showAppToast } = await import('/js/modules/toast-service.js');
        showAppToast('Settings', 'Your changes have been saved.', 'success');
    });
    const dark = page.locator('.notAToasttoast-container > .notAToastvr-dark').last();
    await expect(dark).toBeVisible();
    const style = await dark.evaluate(element => {
        const css = getComputedStyle(element);
        return { background: css.backgroundColor, blur: css.backdropFilter, width: element.offsetWidth, height: element.offsetHeight };
    });
    expect(style.background).toBe('rgb(30, 30, 42)');
    expect(style.blur).toBe('none');
    expect(style.width).toBeLessThanOrEqual(360);
    expect(style.height).toBeLessThan(90);
    await expect(dark).toHaveCSS('opacity', '1');
    await page.screenshot({ path: 'test-results/vb29-toast-midnight.png' });
    // Shared palette changes (including the VS Code theme adapter) apply to notifications too.
    await page.evaluate(() => document.documentElement.style.setProperty('--color-bg-surface-hover', '#213141'));
    await expect(dark).toHaveCSS('background-color', 'rgb(33, 49, 65)');
    await page.goto('/?view=settings', { waitUntil: 'domcontentloaded' });
    await expect(page.locator('[data-view="settings"]')).toBeVisible();
    await expect(page.getByLabel('Notification appearance')).toHaveCount(0);
    expect(await page.evaluate(() => localStorage.getItem('viberails.toast.theme'))).toBe('light');
    await page.setViewportSize({ width: 375, height: 720 });
    await page.evaluate(async () => {
        const { showAppToast } = await import('/js/modules/toast-service.js');
        showAppToast('Run failed', '<img src=x onerror="window.toastInjected=true"> ' + 'long-session-id-'.repeat(12), 'error', { requireDismiss: true });
    });
    const notification = page.locator('.notAToasttoast-container > .notAToastvr-dark').last();
    await expect(notification).toBeVisible();
    await expect(notification).toHaveCSS('background-color', 'rgb(30, 30, 42)');
    await expect(notification).toHaveCSS('opacity', '1');
    await expect(notification).toContainText('<img src=x');
    expect(await page.evaluate(() => window.toastInjected)).toBeUndefined();
    expect(await notification.evaluate(element => element.getBoundingClientRect().right)).toBeLessThanOrEqual(375);
    await page.screenshot({ path: 'test-results/notification-settings-mobile.png' });
    await notification.locator('.notAToastclsBtn').click();
    await expect(notification).toHaveCount(0);
});
