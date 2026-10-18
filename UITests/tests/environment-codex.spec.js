// @ts-check

const { test, expect } = require('./fixtures');

async function openCodexEnvironmentForm(page) {
    await page.goto('/');

    // The saved CLI setup form is shared by Agents and Quick Launches. Open it the way the Agents
    // page's card Edit and the picker's "Manage quick launches" do, through the controller.
    await page.waitForFunction(() => Boolean(window.app?.environmentController));
    await page.evaluate(() => window.app.environmentController.createEnvironment({ variant: 'agent' }));
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
            'gpt-6-astra',
            'gpt-6.1-sol',
        ]);
    });

    for (const model of ['gpt-6-astra', 'gpt-6.1-sol']) {
        test(`${model} saves the exact launch value and reopens as a pinned model`, async ({ page }) => {
            let savedEnvironment = null;
            let savedSettings = {};
            // Keep environment and CLI settings writes in this browser fixture.
            await page.route('**/api/v1/environments', async route => {
                if (route.request().method() === 'GET') {
                    return route.fulfill({ json: { environments: savedEnvironment ? [savedEnvironment] : [] } });
                }
                expect(route.request().method()).toBe('POST');
                savedEnvironment = {
                    ...route.request().postDataJSON(), id: 424242, path: '',
                    lastUsedUTC: new Date().toISOString()
                };
                await route.fulfill({ json: savedEnvironment });
            });
            await page.route('**/api/v1/codex/settings/**', async route => {
                if (route.request().method() === 'PUT') savedSettings = route.request().postDataJSON();
                await route.fulfill({ json: savedSettings });
            });

            await openCodexEnvironmentForm(page);
            await page.locator('#codex-model').selectOption(model);
            await page.locator('#env-name').fill(`e2e-${model}`);
            await page.locator('#env-form button[type="submit"]').click();
            await expect(page.locator('#env-form')).toHaveCount(0);
            expect(savedEnvironment.customArgs).toBe(`--model ${model}`);
            expect(savedSettings.model).toBe(model);

            await expect.poll(() => page.evaluate(() => window.app.data.environments.map(env => env.name))).toContain(`e2e-${model}`);
            await page.evaluate(name => window.app.environmentController.editEnvironment(name, { variant: 'agent' }), `e2e-${model}`);
            await expect(page.locator('#env-form')).toBeVisible();
            await expect(page.locator('#codex-model')).toHaveValue(model);
            await expect(page.locator('#codex-model option:checked')).toHaveText(model);
        });
    }

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

    test('current pinned models retain Max effort when switching models', async ({ page }) => {
        await openCodexEnvironmentForm(page);

        const modelSelect = page.locator('#codex-model');
        const effortSelect = page.locator('#codex-effort');
        const maxOption = effortSelect.locator('option[value="max"]');

        await effortSelect.selectOption('max');
        for (const model of ['gpt-6-astra', 'gpt-6.1-sol']) {
            await modelSelect.selectOption(model);
            await expect(maxOption).toBeEnabled();
            await expect(effortSelect).toHaveValue('max');
        }
    });
});

test.describe('Environment form – visibility switch', () => {
    test('the switch is present, defaults to visible, and posts hidden on create', async ({ page }) => {
        await openCodexEnvironmentForm(page);

        const hiddenSwitch = page.locator('#env-hidden');
        await expect(hiddenSwitch).toBeVisible();
        await expect(hiddenSwitch).not.toBeChecked();
        await expect(page.locator('label[for="env-hidden"]')).toHaveText('Hide from Board pickers');

        // Intercept the writes so the test never mutates real machine-wide state;
        // the assertion is on the request payload, not the backend result.
        let createPayload = null;
        await page.route('**/api/v1/environments', async (route) => {
            if (route.request().method() !== 'POST') return route.fallback();
            createPayload = route.request().postDataJSON();
            await route.fulfill({
                status: 200,
                contentType: 'application/json',
                body: JSON.stringify({
                    id: 424242, name: createPayload.name, cli: 'codex', path: '',
                    customArgs: createPayload.customArgs || '', customPrompt: '',
                    defaultPrompt: '', lastUsedUTC: new Date().toISOString(), hidden: true
                })
            });
        });
        await page.route('**/api/v1/codex/settings/**', (route) => route.fulfill({
            status: 200, contentType: 'application/json', body: '{}'
        }));

        await page.locator('#env-name').fill('e2e-hidden-switch');
        await hiddenSwitch.check();
        await page.locator('#env-form button[type="submit"]').click();

        await expect.poll(() => createPayload).not.toBeNull();
        expect(createPayload.hidden).toBe(true);
        await expect(page.locator('#env-form')).toHaveCount(0);
    });
});
