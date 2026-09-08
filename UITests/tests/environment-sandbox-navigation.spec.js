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

test('Escape disposes the shared diff editor before the layer is removed', async ({ page }) => {
    // The viewer moved out of sandbox-controller into the shared diff-modal.js, so this
    // now asserts the leak invariant through real Monaco rather than by stubbing a fake
    // editor onto the controller: after Escape there must be no diff editor and no
    // leftover models. Monaco does NOT dispose externally-set models with the editor,
    // which is the actual bug this guards.
    await page.goto('/', { waitUntil: 'domcontentloaded' });
    await page.waitForFunction(() => Boolean(window.app));

    await page.evaluate(async () => {
        const { openDiffModal } = await import('/js/modules/diff-modal.js');
        window.__diffHandle = openDiffModal({
            title: 'Test diff',
            files: [{
                fileName: 'src/a.txt',
                language: 'plaintext',
                originalContent: 'one\ntwo\n',
                modifiedContent: 'one\ntwo changed\n'
            }]
        });
        window.__diffReady = await window.__diffHandle.ready;
    });

    expect(await page.evaluate(() => window.__diffReady)).toBe(true);
    await expect(page.locator('.vb-diff-modal-layer')).toHaveCount(1);
    // Two models per open file: the "before" and "after" text.
    expect(await page.evaluate(() => window.monaco.editor.getModels().length)).toBe(2);

    // Escape raised from inside .monaco-editor is deliberately left to Monaco, so
    // press it from the close button instead.
    await page.locator('.vb-diff-modal [data-vb-diff-close]').focus();
    await page.keyboard.press('Escape');

    await expect(page.locator('.vb-diff-modal-layer')).toHaveCount(0);
    // getModels() is the leak signal that matters: a TextModel holds the whole file
    // text, and Monaco does not dispose externally-set models with the editor.
    // NOT getDiffEditors() — that registry accumulates and is never pruned on
    // dispose, so it grows by one per open/close even when nothing leaked.
    expect(await page.evaluate(() => window.monaco.editor.getModels().length)).toBe(0);
});

test('the diff layer opens over an app.showModal dialog and gives it back on close', async ({ page }) => {
    // The reason the viewer is a nested layer: opened from a Board card editor it must
    // not destroy the dialog underneath, and closing it must return there rather than
    // dismissing both.
    await page.goto('/', { waitUntil: 'domcontentloaded' });
    await page.waitForFunction(() => Boolean(window.app));

    await page.evaluate(async () => {
        window.app.showModal('Underlying dialog', '<button id="under-btn">Under</button>');
        const { openDiffModal } = await import('/js/modules/diff-modal.js');
        window.__diffHandle = openDiffModal({
            title: 'Nested diff',
            files: [{ fileName: 'a.txt', language: 'plaintext', originalContent: 'a\n', modifiedContent: 'b\n' }]
        });
        await window.__diffHandle.ready;
    });

    // Both are present, and the dialog underneath is inert while the diff is up.
    await expect(page.locator('#under-btn')).toHaveCount(1);
    await expect(page.locator('.vb-diff-modal-layer')).toHaveCount(1);
    expect(await page.evaluate(() =>
        document.getElementById('under-btn').closest('.modal').parentElement.inert)).toBe(true);

    await page.locator('.vb-diff-modal [data-vb-diff-close]').click();

    // The diff is gone; the dialog underneath survived and is interactive again.
    await expect(page.locator('.vb-diff-modal-layer')).toHaveCount(0);
    await expect(page.locator('#under-btn')).toHaveCount(1);
    expect(await page.evaluate(() =>
        document.getElementById('under-btn').closest('.modal').parentElement.inert)).toBe(false);
});
