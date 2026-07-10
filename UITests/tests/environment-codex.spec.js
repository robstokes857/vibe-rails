// @ts-check

const { test, expect } = require('./fixtures');

async function openCodexEnvironmentForm(page) {
    await page.goto('/');

    const environmentsNav = page.locator('.app-subnav-link[data-view="environments"]');
    await expect(environmentsNav).toBeVisible({ timeout: 15_000 });
    await environmentsNav.click();
    await expect(page.getByRole('heading', { name: /environments/i })).toBeVisible({ timeout: 10_000 });

    await page.locator('[data-action="create-environment"]').click();
    await expect(page.locator('#env-form')).toBeVisible({ timeout: 5_000 });

    await page.locator('#env-cli').selectOption('codex');
    await page.waitForSelector('#codex-model', { timeout: 5_000 });
}

test.describe('Codex environment form – Model field', () => {
    test('model select contains only the owner-curated models in priority order', async ({ page }) => {
        await openCodexEnvironmentForm(page);

        const values = await page.locator('#codex-model option').evaluateAll(options =>
            options.map(option => option.value));

        expect(values).toEqual([
            '',
            'gpt-5.6-sol',
            'gpt-5.6-terra',
            'gpt-5.6-luna',
            'gpt-5.5',
        ]);
    });

    test('effort select includes all current Codex reasoning levels', async ({ page }) => {
        await openCodexEnvironmentForm(page);

        const values = await page.locator('#codex-effort option').evaluateAll(options =>
            options.map(option => option.value));

        expect(values).toEqual([
            '',
            'minimal',
            'low',
            'medium',
            'high',
            'xhigh',
            'max',
            'ultra',
        ]);
    });
});
