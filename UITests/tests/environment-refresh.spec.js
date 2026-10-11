// @ts-check

const { test, expect } = require('./fixtures');

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
