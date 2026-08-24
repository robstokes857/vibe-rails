// @ts-check
//
// Playwright specs for the Copilot environment creation form.
// Verifies that the Model field is rendered as a <select> with the expected
// options (not a free-text <input>), consistent with the Codex model picker.

const { test, expect } = require('./fixtures');

async function openCopilotEnvironmentForm(page) {
    await page.goto('/');

    // Navigate to Environments view
    const environmentsNav = page.locator('.app-subnav-link[data-view="environments"]:visible');
    await expect(environmentsNav).toBeVisible({ timeout: 15_000 });
    await environmentsNav.click();
    await expect(page.getByRole('heading', { name: /environment \/ workers/i })).toBeVisible({ timeout: 10_000 });

    // Open the "Create Environment" modal
    await page.locator('[data-action="create-environment"]').click();
    await expect(page.locator('#env-form')).toBeVisible({ timeout: 5_000 });

    // Select "Copilot" as the CLI type
    const cliSelect = page.locator('#env-cli');
    await cliSelect.selectOption('copilot');
    // Give the change handler time to re-render the settings slot
    await page.waitForSelector('#copilot-model', { timeout: 5_000 });
}

test.describe('Copilot environment form – Model field', () => {

    test('model field is a <select>, not a text input', async ({ page }) => {
        await openCopilotEnvironmentForm(page);

        const modelField = page.locator('#copilot-model');
        await expect(modelField).toBeVisible();

        const tagName = await modelField.evaluate(el => el.tagName.toLowerCase());
        expect(tagName).toBe('select');
    });

    test('model select contains the Default option', async ({ page }) => {
        await openCopilotEnvironmentForm(page);
        const defaultOpt = page.locator('#copilot-model option[value=""]');
        await expect(defaultOpt).toHaveCount(1);
        await expect(defaultOpt).toContainText(/default/i);
    });

    test('model select contains expected Claude and GPT options', async ({ page }) => {
        await openCopilotEnvironmentForm(page);

        const expectedValues = [
            'claude-fable-5',
            'claude-opus-5',
            'claude-sonnet-5',
            'claude-sonnet-4.6',
            'claude-haiku-4.5',
            'claude-opus-4.8',
            'claude-opus-4.7',
            'gpt-5.6-sol',
            'gpt-5.6-terra',
            'gpt-5.6-luna',
            'gpt-5.5',
            'gpt-5.4',
            'gpt-5.4-mini',
            'gpt-5-mini',
            'grok-4.6',
        ];

        for (const value of expectedValues) {
            await expect(page.locator(`#copilot-model option[value="${value}"]`),
                `expected option for ${value}`).toHaveCount(1);
        }
    });

    test('model select defaults to empty (Copilot default)', async ({ page }) => {
        await openCopilotEnvironmentForm(page);
        const selected = await page.locator('#copilot-model').inputValue();
        expect(selected).toBe('');
    });

    test('mode, permissions, and no-ask-user controls are also present', async ({ page }) => {
        await openCopilotEnvironmentForm(page);

        await expect(page.locator('#copilot-mode')).toBeVisible();
        await expect(page.locator('#copilot-permission-preset')).toBeVisible();
        await expect(page.locator('#copilot-no-ask-user')).toBeVisible();
        await expect(page.locator('#copilot-additional-args')).toBeVisible();
    });
});
