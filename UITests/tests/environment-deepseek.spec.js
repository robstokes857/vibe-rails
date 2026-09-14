// @ts-check
//
// Playwright specs for the DeepSeek V4 Pro environment creation form.
// DeepSeek V4 Pro is an OpenCode-backed pseudo-CLI: the Model field is a
// read-only display pinned to deepseek/deepseek-v4-pro.

const { test, expect } = require('./fixtures');

async function openDeepSeekEnvironmentForm(page) {
    await page.goto('/');

    const environmentsNav = page.locator('.app-subnav-link[data-view="environments"]:visible');
    await expect(environmentsNav).toBeVisible({ timeout: 15_000 });
    await environmentsNav.click();
    await expect(page.getByRole('heading', { name: /environment \/ workers/i })).toBeVisible({ timeout: 10_000 });

    await page.locator('[data-action="create-environment"]').click();
    await expect(page.locator('#env-form')).toBeVisible({ timeout: 5_000 });

    await page.locator('#env-cli').selectOption('deepseek-v4-pro');
    await page.waitForSelector('#opencode-model', { timeout: 5_000 });
}

test.describe('DeepSeek V4 Pro environment form – pinned model', () => {
    test('model field is a disabled input pinned to deepseek/deepseek-v4-pro', async ({ page }) => {
        await openDeepSeekEnvironmentForm(page);

        const modelField = page.locator('#opencode-model');
        await expect(modelField).toBeVisible();
        await expect(modelField).toBeDisabled();
        await expect(modelField).toHaveValue('deepseek/deepseek-v4-pro');
    });

    test('agent, yolo, and additional-args controls are present', async ({ page }) => {
        await openDeepSeekEnvironmentForm(page);

        await expect(page.locator('#opencode-agent')).toBeVisible();
        await expect(page.locator('#opencode-yolo')).toBeVisible();
        await expect(page.locator('#opencode-additional-args')).toBeVisible();
    });

    test('create saves the pinned model in CustomArgs', async ({ page }) => {
        await openDeepSeekEnvironmentForm(page);

        let createPayload = null;
        await page.route('**/api/v1/environments', async (route) => {
            if (route.request().method() !== 'POST') return route.fallback();
            createPayload = route.request().postDataJSON();
            await route.fulfill({
                status: 200,
                contentType: 'application/json',
                body: JSON.stringify({
                    id: 424243, name: createPayload.name, cli: 'deepseek-v4-pro', path: '',
                    customArgs: createPayload.customArgs || '', customPrompt: '',
                    defaultPrompt: '', lastUsedUTC: new Date().toISOString(), hidden: true
                })
            });
        });

        await page.locator('#env-name').fill('e2e-deepseek-env');
        await page.locator('#env-form button[type="submit"]').click();

        await expect.poll(() => createPayload).not.toBeNull();
        expect(createPayload.customArgs).toContain('--model');
        expect(createPayload.customArgs).toContain('deepseek/deepseek-v4-pro');
        await expect(page.locator('#env-form')).toHaveCount(0);
    });
});
