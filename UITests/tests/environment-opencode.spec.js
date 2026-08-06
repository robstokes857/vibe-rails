// @ts-check
//
// Playwright specs for the OpenCode environment creation form.
// Verifies that the Model field is rendered as a <select> with the expected
// provider/model options, and that the agent / YOLO / additional-args controls
// are present. Mirrors environment-copilot.spec.js.

const { test, expect } = require('./fixtures');

async function openOpencodeEnvironmentForm(page) {
    await page.goto('/');

    // Navigate to Environments view
    const environmentsNav = page.locator('.app-subnav-link[data-view="environments"]:visible');
    await expect(environmentsNav).toBeVisible({ timeout: 15_000 });
    await environmentsNav.click();
    await expect(page.getByRole('heading', { name: /environment \/ workers/i })).toBeVisible({ timeout: 10_000 });

    // Open the "Create Environment" modal
    await page.locator('[data-action="create-environment"]').click();
    await expect(page.locator('#env-form')).toBeVisible({ timeout: 5_000 });

    // Select "OpenCode" as the CLI type
    const cliSelect = page.locator('#env-cli');
    await cliSelect.selectOption('opencode');
    // Give the change handler time to re-render the settings slot
    await page.waitForSelector('#opencode-model', { timeout: 5_000 });
}

test.describe('OpenCode environment form – Model field', () => {

    test('model field is a <select>, not a text input', async ({ page }) => {
        await openOpencodeEnvironmentForm(page);

        const modelField = page.locator('#opencode-model');
        await expect(modelField).toBeVisible();

        const tagName = await modelField.evaluate(el => el.tagName.toLowerCase());
        expect(tagName).toBe('select');
    });

    test('model select contains the Default option', async ({ page }) => {
        await openOpencodeEnvironmentForm(page);
        const defaultOpt = page.locator('#opencode-model option[value=""]');
        await expect(defaultOpt).toHaveCount(1);
        await expect(defaultOpt).toContainText(/default/i);
    });

    test('model select contains expected provider/model options', async ({ page }) => {
        await openOpencodeEnvironmentForm(page);

        const expectedValues = [
            'anthropic/claude-opus-4-5',
            'anthropic/claude-sonnet-4-5',
            'openai/gpt-5.2',
            'openai/gpt-5.1-codex',
            'google/gemini-3-pro',
            'opencode/gpt-5.1-codex',
        ];

        for (const value of expectedValues) {
            await expect(page.locator(`#opencode-model option[value="${value}"]`),
                `expected option for ${value}`).toHaveCount(1);
        }
    });

    test('model select defaults to empty (OpenCode default)', async ({ page }) => {
        await openOpencodeEnvironmentForm(page);
        const selected = await page.locator('#opencode-model').inputValue();
        expect(selected).toBe('');
    });

    test('agent, yolo, and additional-args controls are also present', async ({ page }) => {
        await openOpencodeEnvironmentForm(page);

        await expect(page.locator('#opencode-agent')).toBeVisible();
        await expect(page.locator('#opencode-yolo')).toBeVisible();
        await expect(page.locator('#opencode-additional-args')).toBeVisible();
    });
});
