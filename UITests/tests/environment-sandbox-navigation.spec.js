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

test('dashboard refresh preserves the last known environments when the API is temporarily unavailable', async ({ page }) => {
    await page.goto('/', { waitUntil: 'domcontentloaded' });
    await page.waitForFunction(() => Boolean(window.app));

    const environments = await page.evaluate(async () => {
        window.app.data.environments = [{ id: 42, name: 'cached-environment', cli: 'codex' }];
        window.app.data.isInGit = false;
        const originalApiCall = window.app.apiCall;
        window.app.apiCall = async (url) => {
            if (url === '/api/v1/environments') {
                throw new Error('temporary environment outage');
            }
            return {};
        };

        try {
            await window.app.refreshDashboardData();
            return window.app.data.environments;
        } finally {
            window.app.apiCall = originalApiCall;
        }
    });

    expect(environments).toEqual([{ id: 42, name: 'cached-environment', cli: 'codex' }]);
});

test('Escape disposes the sandbox diff editor before the modal is removed', async ({ page }) => {
    await page.goto('/', { waitUntil: 'domcontentloaded' });
    await page.waitForFunction(() => Boolean(window.app));

    await page.evaluate(() => {
        window.__sandboxDiffDisposals = { editor: 0, original: 0, modified: 0 };
        const modalContainer = document.getElementById('modal-container');
        modalContainer.innerHTML = '<div class="modal d-block"><button id="sandbox-diff-test-focus">Close</button></div>';

        const controller = window.app.sandboxController;
        controller._diffEditor = {
            getModel: () => ({
                original: { dispose: () => { window.__sandboxDiffDisposals.original += 1; } },
                modified: { dispose: () => { window.__sandboxDiffDisposals.modified += 1; } }
            }),
            dispose: () => { window.__sandboxDiffDisposals.editor += 1; }
        };
        controller._installDiffEscapeCleanup();
        document.getElementById('sandbox-diff-test-focus').focus();
    });

    await page.keyboard.press('Escape');

    await expect(page.locator('#modal-container')).toBeEmpty();
    expect(await page.evaluate(() => window.__sandboxDiffDisposals)).toEqual({
        editor: 1,
        original: 1,
        modified: 1
    });
});
