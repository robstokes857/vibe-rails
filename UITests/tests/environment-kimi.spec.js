// @ts-check
//
// Playwright specs for the Kimi K3 environment creation form.
// Kimi K3 is an OpenCode-backed pseudo-CLI: the Model field is a
// read-only display pinned to moonshotai/kimi-k3.

const { test, expect } = require('./fixtures');

async function openKimiEnvironmentForm(page) {
    await page.goto('/');

    const environmentsNav = page.locator('.app-subnav-link[data-view="environments"]:visible');
    await expect(environmentsNav).toBeVisible({ timeout: 15_000 });
    await environmentsNav.click();
    await expect(page.getByRole('heading', { name: /environment \/ workers/i })).toBeVisible({ timeout: 10_000 });

    await page.locator('[data-action="create-environment"]').click();
    await expect(page.locator('#env-form')).toBeVisible({ timeout: 5_000 });

    await page.locator('#env-cli').selectOption('kimi-k3');
    await page.waitForSelector('#opencode-model', { timeout: 5_000 });
}

test.describe('Kimi K3 environment form – pinned model', () => {
    test('model field is a disabled input pinned to moonshotai/kimi-k3', async ({ page }) => {
        await openKimiEnvironmentForm(page);

        const modelField = page.locator('#opencode-model');
        await expect(modelField).toBeVisible();
        await expect(modelField).toBeDisabled();
        await expect(modelField).toHaveValue('moonshotai/kimi-k3');
    });

    test('agent, yolo, and additional-args controls are present', async ({ page }) => {
        await openKimiEnvironmentForm(page);

        await expect(page.locator('#opencode-agent')).toBeVisible();
        await expect(page.locator('#opencode-yolo')).toBeVisible();
        await expect(page.locator('#opencode-additional-args')).toBeVisible();
    });

    test('create saves the pinned model in CustomArgs', async ({ page }) => {
        await openKimiEnvironmentForm(page);

        let createPayload = null;
        await page.route('**/api/v1/environments', async (route) => {
            if (route.request().method() !== 'POST') return route.fallback();
            createPayload = route.request().postDataJSON();
            await route.fulfill({
                status: 200,
                contentType: 'application/json',
                body: JSON.stringify({
                    id: 424244, name: createPayload.name, cli: 'kimi-k3', path: '',
                    customArgs: createPayload.customArgs || '', customPrompt: '',
                    defaultPrompt: '', lastUsedUTC: new Date().toISOString(), hidden: true
                })
            });
        });

        await page.locator('#env-name').fill('e2e-kimi-env');
        await page.locator('#env-form button[type="submit"]').click();

        await expect.poll(() => createPayload).not.toBeNull();
        expect(createPayload.customArgs).toContain('--model');
        expect(createPayload.customArgs).toContain('moonshotai/kimi-k3');
        await expect(page.locator('#env-form')).toHaveCount(0);
    });
});
