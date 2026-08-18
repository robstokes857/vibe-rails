// @ts-check
//
// Nav Automation launcher: the "Launch" nav button's flyout and its order/show-hide
// preferences (/api/v1/automation-nav/preferences), which mirror the LLM picker's
// Custom Environment treatment. The spec is written to pass whether or not the
// developer's project has automations: with none it asserts the empty states, with
// some it asserts flyout contents track the saved preferences.

const { test, expect, newApiContext } = require('./fixtures');

const PREFERENCES = '/api/v1/automation-nav/preferences';

// Preferences are MACHINE-WIDE state in the developer's real backend. Snapshot before
// the file runs and restore in afterAll so local runs never destroy the real list.
let savedItems = null;

test.beforeAll(async ({ playwright }) => {
    const api = await newApiContext(playwright);
    try {
        const response = await api.get(PREFERENCES);
        if (response.ok()) savedItems = (await response.json()).items;
    } finally {
        await api.dispose();
    }
});

test.afterAll(async ({ playwright }) => {
    const api = await newApiContext(playwright);
    try {
        if (savedItems && savedItems.length > 0) {
            const restored = await api.put(PREFERENCES, { data: { items: savedItems } });
            if (!restored.ok()) {
                console.warn(`[automation-nav spec] preference restore failed: ${restored.status()}`);
            }
        } else {
            await api.delete(PREFERENCES);
        }
    } catch (error) {
        console.warn('[automation-nav spec] preference restore failed:', error);
    } finally {
        await api.dispose();
    }
});

async function openApp(page) {
    await page.goto('/?view=terminal-focus', { waitUntil: 'domcontentloaded' });
    await expect(page.locator('[data-action="automation-launcher"]:visible')).toBeVisible({ timeout: 15_000 });
}

async function openFlyout(page) {
    await page.locator('[data-action="automation-launcher"]:visible').click();
    await expect(page.locator('.automation-launch-flyout')).toBeVisible();
    // Wait for the item list to settle past its loading spinner.
    await expect(page.locator('.automation-launch-flyout .spinner-border')).toHaveCount(0, { timeout: 10_000 });
}

test('nav Launch button opens and closes the automation flyout', async ({ page, context }) => {
    const prefs = (await (await context.request.get(PREFERENCES)).json()).items;
    await openApp(page);
    await openFlyout(page);

    const enabled = prefs.filter((item) => item.enabled);
    if (enabled.length > 0) {
        const labels = await page.locator('.automation-launch-item-label').allTextContents();
        expect(labels).toEqual(enabled.map((item) => item.label));
    } else {
        await expect(page.locator('.automation-launch-flyout-empty')).toBeVisible();
    }
    await expect(page.locator('[data-automation-launch-action="customize"]')).toBeVisible();
    await expect(page.locator('[data-automation-launch-action="manage"]')).toBeVisible();

    // Toggling the trigger again closes the flyout.
    await page.locator('[data-action="automation-launcher"]:visible').click();
    await expect(page.locator('.automation-launch-flyout')).toHaveCount(0);
});

test('customize modal lists every automation and cancels cleanly', async ({ page, context }) => {
    const prefs = (await (await context.request.get(PREFERENCES)).json()).items;
    await openApp(page);
    await openFlyout(page);

    await page.locator('[data-automation-launch-action="customize"]').click();
    await expect(page.locator('.automation-launch-flyout')).toHaveCount(0);
    const modal = page.locator('.llm-picker-customization-modal:visible');
    await expect(modal).toBeVisible();
    await expect(modal.locator('#automation-nav-modal-title')).toHaveText('Customize Automation launcher');

    if (prefs.length > 0) {
        await expect(modal.locator('[data-automation-nav-key]')).toHaveCount(prefs.length);
    } else {
        await expect(modal.locator('.llm-picker-group-empty')).toBeVisible();
    }

    await modal.locator('[data-automation-nav-action="cancel"]').last().click();
    await expect(page.locator('.llm-picker-customization-modal')).toHaveCount(0);
});

test('preferences API round-trips order and visibility and reset restores defaults', async ({ context }) => {
    const request = context.request;
    const items = (await (await request.get(PREFERENCES)).json()).items;
    test.skip(items.length < 2, 'needs at least two automations to exercise reorder');

    const reordered = [...items].reverse().map((item, index) => ({
        ...item,
        order: index,
        enabled: index !== 0
    }));
    const putResponse = await request.put(PREFERENCES, { data: { items: reordered } });
    expect(putResponse.ok()).toBeTruthy();

    const roundTrip = (await (await request.get(PREFERENCES)).json()).items;
    expect(roundTrip.map((item) => item.key)).toEqual(reordered.map((item) => item.key));
    expect(roundTrip[0].enabled).toBe(false);

    const resetResponse = await request.delete(PREFERENCES);
    expect(resetResponse.ok()).toBeTruthy();
    const defaults = (await (await request.get(PREFERENCES)).json()).items;
    expect(defaults.every((item) => item.enabled)).toBeTruthy();
});

test('rejects a snapshot that does not match the automation catalog', async ({ context }) => {
    const response = await context.request.put(PREFERENCES, {
        data: {
            items: [{ key: 'job:999999', label: 'Ghost', jobId: 999999, enabled: true, order: 0 }]
        }
    });
    expect(response.status()).toBe(400);
});
