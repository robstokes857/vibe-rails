// @ts-check
//
// Playwright specs for the native Grok environment creation form.
// Verifies the model dropdown (grok-4.7 / grok-4.6) and the Effort dropdown.

const { test, expect } = require('./fixtures');

async function openGrokEnvironmentForm(page) {
    await page.goto('/');

    const environmentsNav = page.locator('.app-subnav-link[data-view="environments"]:visible');
    await expect(environmentsNav).toBeVisible({ timeout: 15_000 });
    await environmentsNav.click();
    await expect(page.getByRole('heading', { name: /environment \/ workers/i })).toBeVisible({ timeout: 10_000 });

    await page.locator('[data-action="create-environment"]').click();
    await expect(page.locator('#env-form')).toBeVisible({ timeout: 5_000 });

    await page.locator('#env-cli').selectOption('grok');
    await page.waitForSelector('#grok-effort', { timeout: 5_000 });
}

test.describe('Grok environment form – model and effort', () => {
    test('effort field is a <select> with the canonical thinking levels', async ({ page }) => {
        await openGrokEnvironmentForm(page);

        const effortField = page.locator('#grok-effort');
        await expect(effortField).toBeVisible();

        const tagName = await effortField.evaluate(el => el.tagName.toLowerCase());
        expect(tagName).toBe('select');

        const values = await page.locator('#grok-effort option').evaluateAll(options =>
            options.map(option => option.value));

        expect(values).toEqual([
            '',
            'low',
            'medium',
            'high',
            'xhigh',
        ]);
    });

    test('effort select defaults to empty (Grok default) and can be set to xhigh', async ({ page }) => {
        await openGrokEnvironmentForm(page);

        const effortSelect = page.locator('#grok-effort');
        await expect(effortSelect).toHaveValue('');

        await effortSelect.selectOption('xhigh');
        await expect(effortSelect).toHaveValue('xhigh');
    });

    test('model is a select of grok-4.7 then grok-4.6, and yolo / additional-args remain', async ({ page }) => {
        await openGrokEnvironmentForm(page);

        const modelField = page.locator('#grok-model');
        await expect(modelField).toBeVisible();
        await expect(modelField).toBeEnabled();

        const values = await page.locator('#grok-model option').evaluateAll(options =>
            options.map(option => option.value));
        expect(values).toEqual(['', 'grok-4.7', 'grok-4.6']);
        await expect(modelField).toHaveValue('');

        await modelField.selectOption('grok-4.7');
        await expect(modelField).toHaveValue('grok-4.7');

        await expect(page.locator('#grok-yolo')).toBeVisible();
        await expect(page.locator('#grok-additional-args')).toBeVisible();
    });
});
