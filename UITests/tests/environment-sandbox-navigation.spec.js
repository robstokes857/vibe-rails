// @ts-check

const { test, expect } = require('./fixtures');

test('sandbox create and delete keep the Environments view active', async ({ page }) => {
    await page.goto('/?view=environments', { waitUntil: 'domcontentloaded' });
    await expect(page.locator('.view[data-view="environments"]')).toBeVisible({ timeout: 15_000 });

    await page.evaluate(() => {
        window.__sandboxNavigationCalls = {
            environments: 0,
            dashboard: 0,
            requests: []
        };

        window.app.sandboxController.refreshSandboxes = async () => {};
        window.app.environmentController.loadEnvironments = () => {
            window.__sandboxNavigationCalls.environments += 1;
        };
        window.app.dashboardController.loadDashboard = () => {
            window.__sandboxNavigationCalls.dashboard += 1;
        };
        window.app.apiCall = async (url, method, body) => {
            window.__sandboxNavigationCalls.requests.push({ url, method, body });
            return {};
        };
    });

    await page.locator('.view[data-view="environments"] [data-action="create-sandbox"]').click();
    await page.locator('#sandbox-name').fill('navigation-regression');
    await page.locator('#create-sandbox-form').evaluate(form => form.requestSubmit());

    await expect.poll(() => page.evaluate(() => ({
        environments: window.__sandboxNavigationCalls.environments,
        dashboard: window.__sandboxNavigationCalls.dashboard
    }))).toEqual({ environments: 1, dashboard: 0 });
    await expect(page.locator('.view[data-view="environments"]')).toBeVisible();
    expect(await page.evaluate(() => window.app.currentView)).toBe('environments');

    await page.evaluate(() => window.app.sandboxController.deleteSandbox(321, 'navigation-regression'));
    await page.locator('#confirm-delete-sandbox-btn').click();

    await expect.poll(() => page.evaluate(() => ({
        environments: window.__sandboxNavigationCalls.environments,
        dashboard: window.__sandboxNavigationCalls.dashboard
    }))).toEqual({ environments: 2, dashboard: 0 });
    await expect(page.locator('.view[data-view="environments"]')).toBeVisible();
    expect(await page.evaluate(() => window.app.currentView)).toBe('environments');

    expect(await page.evaluate(() => window.__sandboxNavigationCalls.requests)).toEqual([
        {
            url: '/api/v1/sandboxes',
            method: 'POST',
            body: { name: 'navigation-regression' }
        },
        {
            url: '/api/v1/sandboxes/321',
            method: 'DELETE',
            body: undefined
        }
    ]);
});
