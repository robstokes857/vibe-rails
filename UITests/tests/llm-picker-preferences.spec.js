// @ts-check

const { test, expect, newApiContext } = require('./fixtures');

const PREFERENCES = '/api/v1/llm-picker/preferences';

async function resetPickerPreferences(context) {
    try { await context.request.delete(PREFERENCES); } catch { /* best effort */ }
}

// The per-test resets above operate on MACHINE-WIDE preferences in the developer's real
// backend state. Snapshot whatever was saved before this file runs and put it back in
// afterAll, so running the suite locally cannot destroy the developer's own list.
let savedPickerItems = null;

test.beforeAll(async ({ playwright }) => {
    const api = await newApiContext(playwright);
    try {
        const response = await api.get(PREFERENCES);
        if (response.ok()) savedPickerItems = (await response.json()).items;
    } finally {
        await api.dispose();
    }
});

test.afterAll(async ({ playwright }) => {
    const api = await newApiContext(playwright);
    try {
        if (savedPickerItems) {
            const restored = await api.put(PREFERENCES, { data: { items: savedPickerItems } });
            if (!restored.ok()) {
                console.warn(`[llm-picker spec] preference restore failed: ${restored.status()}`);
            }
        } else {
            await api.delete(PREFERENCES);
        }
    } catch (error) {
        console.warn('[llm-picker spec] preference restore failed:', error);
    } finally {
        await api.dispose();
    }
});

async function openTerminal(page) {
    await page.goto('/?view=terminal-focus', { waitUntil: 'domcontentloaded' });
    await expect(page.locator('#terminal-header-select')).toHaveCount(1, { timeout: 15_000 });
    await page.waitForFunction(() => Boolean(document.querySelector('#terminal-header-select')?.tomselect));
}

async function openTomSelect(page, selector) {
    await page.locator(selector).evaluate((element) => element.tomselect.open());
    await expect(page.locator('.ts-dropdown:visible')).toBeVisible();
}

async function optionValues(page, selector) {
    return page.locator(selector).evaluate((element) =>
        Array.from(element.querySelectorAll('option')).map((option) => option.value).filter(Boolean));
}

async function openMultiRun(page) {
    await page.locator('#terminal-settings-btn').click();
    await page.locator('#terminal-multirun-btn').click();
    await expect(page.locator('#vb-multirun-cli-1')).toHaveCount(1);
    await page.waitForFunction(() => Boolean(document.querySelector('#vb-multirun-cli-1')?.tomselect));
}

test.beforeEach(async ({ context }) => resetPickerPreferences(context));
test.afterEach(async ({ context }) => resetPickerPreferences(context));

test.describe('customizable LLM launch pickers', () => {
    test('save propagates across mounted pickers, persists, and restores focus in a nested modal', async ({ page }) => {
        await openTerminal(page);
        await openMultiRun(page);
        await openTomSelect(page, '#vb-multirun-cli-1');
        await page.locator('.ts-dropdown:visible .llm-picker-customize-button').click();

        const customizer = page.locator('.llm-picker-customization-modal');
        await expect(customizer).toBeVisible();
        const claudeRow = customizer.locator('[data-llm-picker-key="base:claude"]');
        await claudeRow.locator('[data-llm-picker-enabled]').uncheck();

        // The move buttons are the keyboard-authoritative alternative to drag/drop.
        const moveDown = claudeRow.locator('[data-llm-picker-move="down"]');
        await moveDown.focus();
        await moveDown.press('Enter');
        await expect(customizer.locator('[data-llm-picker-group="Base CLIs"] .llm-picker-modal-row').first())
            .toHaveAttribute('data-llm-picker-key', 'base:codex');

        await customizer.locator('[data-llm-picker-action="save"]').click();
        await expect(customizer).toHaveCount(0);

        // The originating Multi Run modal stays mounted. Its first saved selection
        // survives with a hidden label, while the other live picker no longer offers it.
        expect(await page.locator('#vb-multirun-cli-1').evaluate((element) => element.tomselect.getValue()))
            .toBe('base:claude');
        await expect.poll(() => page.locator('#vb-multirun-cli-1').evaluate((element) =>
            element.tomselect.wrapper.contains(document.activeElement))).toBe(true);
        expect(await optionValues(page, '#vb-multirun-cli-2')).not.toContain('base:claude');
        expect(await optionValues(page, '#terminal-header-select')).not.toContain('base:claude');

        await page.reload({ waitUntil: 'domcontentloaded' });
        await page.waitForFunction(() => Boolean(document.querySelector('#terminal-header-select')?.tomselect));
        expect(await optionValues(page, '#terminal-header-select')).not.toContain('base:claude');
        expect(await optionValues(page, '#terminal-header-select')).toContain('base:shell');
    });

    test('a short viewport scrolls the catalog instead of clipping it', async ({ page }) => {
        // Short enough that the Base CLIs group alone overflows the dialog. The
        // <form> wrapping .modal-body + .modal-footer used to break Bootstrap's
        // modal-dialog-scrollable flex chain, clipping the list with no scrollbar.
        await page.setViewportSize({ width: 900, height: 420 });
        await openTerminal(page);
        await openTomSelect(page, '#terminal-header-select');
        await page.locator('.ts-dropdown:visible .llm-picker-customize-button').click();
        const customizer = page.locator('.llm-picker-customization-modal');
        await expect(customizer).toBeVisible();

        // The footer must stay on-screen because the body absorbs the overflow…
        await expect(customizer.locator('[data-llm-picker-action="save"]')).toBeInViewport();
        expect(await customizer.locator('.modal-body').evaluate(
            (element) => element.scrollHeight > element.clientHeight + 1)).toBe(true);

        // …and every row must be reachable by scrolling that body.
        const lastRow = customizer.locator('.llm-picker-modal-row').last();
        await lastRow.scrollIntoViewIfNeeded();
        await expect(lastRow).toBeInViewport();
    });

    test('Cancel and a failed Save leave live picker state unchanged', async ({ page }) => {
        await openTerminal(page);
        const original = await optionValues(page, '#terminal-header-select');

        await openTomSelect(page, '#terminal-header-select');
        await page.locator('.ts-dropdown:visible .llm-picker-customize-button').click();
        await page.locator('[data-llm-picker-key="base:codex"] [data-llm-picker-enabled]').uncheck();
        await page.locator('[data-llm-picker-action="cancel"]').last().click();
        await expect(page.locator('.llm-picker-customization-modal')).toHaveCount(0);
        expect(await optionValues(page, '#terminal-header-select')).toEqual(original);
        await expect.poll(() => page.locator('#terminal-header-select').evaluate((element) =>
            element.tomselect.wrapper.contains(document.activeElement))).toBe(true);

        await page.route('**/api/v1/llm-picker/preferences', async (route) => {
            if (route.request().method() === 'PUT') {
                await route.fulfill({ status: 500, contentType: 'application/json', body: '{"error":"simulated"}' });
            } else {
                await route.fallback();
            }
        });
        await openTomSelect(page, '#terminal-header-select');
        await page.locator('.ts-dropdown:visible .llm-picker-customize-button').click();
        await page.locator('[data-llm-picker-key="base:codex"] [data-llm-picker-enabled]').uncheck();
        await page.locator('[data-llm-picker-action="save"]').click();

        await expect(page.locator('.llm-picker-customization-modal')).toBeVisible();
        await expect(page.locator('[data-llm-picker-error]')).toBeVisible();
        expect(await optionValues(page, '#terminal-header-select')).toEqual(original);
    });

    test('the footer is keyboard reachable and opens the customization modal', async ({ page }) => {
        await openTerminal(page);
        await page.locator('#terminal-header-select').evaluate((element) => {
            (element.tomselect.focus_node || element.tomselect.control).focus();
            element.tomselect.open();
        });
        await expect(page.locator('.ts-dropdown:visible .llm-picker-customize-button')).toBeVisible();

        await page.keyboard.press('Tab');
        const footerButton = page.locator('.ts-dropdown:visible .llm-picker-customize-button');
        await expect(footerButton).toBeFocused();
        await page.keyboard.press('Enter');
        await expect(page.locator('.llm-picker-customization-modal')).toBeVisible();
    });

    test('an all-hidden list still opens the footer and Reset recovers every item', async ({ context, page }) => {
        const current = await context.request.get(PREFERENCES);
        expect(current.ok()).toBe(true);
        const catalog = await current.json();
        const hiddenSnapshot = catalog.items.map((item) => ({ ...item, enabled: false }));
        const saved = await context.request.put(PREFERENCES, { data: { items: hiddenSnapshot } });
        expect(saved.ok()).toBe(true);

        await openTerminal(page);
        expect(await optionValues(page, '#terminal-header-select')).toEqual([]);
        await openTomSelect(page, '#terminal-header-select');
        await expect(page.locator('.ts-dropdown:visible .llm-picker-customize-button')).toBeVisible();
        await page.locator('.ts-dropdown:visible .llm-picker-customize-button').click();
        await page.locator('[data-llm-picker-action="reset"]').click();
        await expect(page.locator('.llm-picker-customization-modal')).toHaveCount(0);

        const restored = await optionValues(page, '#terminal-header-select');
        expect(restored).toContain('base:claude');
        expect(restored).toContain('base:shell');
    });

    test('each picker context filters the catalog and only launch pickers receive the footer', async ({ page }) => {
        await openTerminal(page);
        expect(await optionValues(page, '#terminal-header-select')).toContain('base:shell');

        await page.evaluate(() => {
            const select = document.createElement('select');
            select.id = 'llm-picker-test-sandbox';
            document.body.appendChild(select);
            window.app.sandboxController.populateSandboxCliSelect(select);
        });
        expect(await optionValues(page, '#llm-picker-test-sandbox')).not.toContain('base:shell');
        await openTomSelect(page, '#llm-picker-test-sandbox');
        await expect(page.locator('.ts-dropdown:visible .llm-picker-customize-button')).toBeVisible();
        await page.locator('#llm-picker-test-sandbox').evaluate((element) => element.tomselect.close());

        // The automation editor's Worker picker is a separate component: only
        // env:{id}:{cli} values (workers), never base CLIs, and no customization
        // footer — it is not a launch picker.
        await page.evaluate(() => window.app.navigate('jobs', {}, { resetStack: true }));
        await expect(page.locator('.jobs-view[data-view="jobs"]')).toBeVisible({ timeout: 15_000 });
        await page.evaluate(() => window.app.jobController.openEditor());
        await expect(page.locator('#job-llm-selection')).toHaveCount(1);
        expect((await optionValues(page, '#job-llm-selection')).every((value) => value.startsWith('env:'))).toBe(true);
        expect(await page.locator('#job-llm-selection').evaluate((element) =>
            Boolean(element.tomselect.dropdown.querySelector('.llm-picker-customize-button')))).toBe(false);

        await page.evaluate(() => window.app.navigate('terminal-focus', {}, { resetStack: true }));
        await expect(page.locator('#terminal-settings-btn')).toBeVisible({ timeout: 15_000 });
        await openMultiRun(page);
        const multiValues = await optionValues(page, '#vb-multirun-cli-1');
        expect(multiValues.every((value) => value.startsWith('base:'))).toBe(true);
        expect(multiValues).not.toContain('base:shell');
        await page.locator('#modal-container [data-action="close-modal"]').last().click();

        await page.evaluate(() => window.app.navigate('environments', {}, { resetStack: true }));
        await expect(page.locator('.view[data-view="environments"]')).toBeVisible({ timeout: 15_000 });

        // The Environments screen has its own header entry point to the customizer.
        await page.locator('[data-action="customize-llm-list"]').click();
        await expect(page.locator('.llm-picker-customization-modal')).toBeVisible();
        await page.locator('[data-llm-picker-action="cancel"]').last().click();
        await expect(page.locator('.llm-picker-customization-modal')).toHaveCount(0);

        await page.locator('[data-action="create-environment"]').click();
        await page.waitForFunction(() => Boolean(document.querySelector('#env-cli')?.tomselect));
        const providerValues = await optionValues(page, '#env-cli');
        expect(providerValues).toContain('claude');
        expect(providerValues).toContain('codex');
        expect(providerValues).not.toContain('shell');
        await openTomSelect(page, '#env-cli');
        await expect(page.locator('.ts-dropdown:visible .llm-picker-customize-button')).toHaveCount(0);
    });

    test('the Worker picker ignores hidden and retains a legacy environment selection', async ({ page }) => {
        await openTerminal(page);
        const snapshot = await page.evaluate(async () => {
            const { mountWorkerPicker } = await import('/js/modules/pickers/worker-picker.js');
            const app = window.app;
            app.data.environments = [
                ...(app.data.environments || []),
                { id: 987, name: 'Hidden worker', cli: 'codex', hidden: true, automationWorker: true },
                { id: 988, name: 'Legacy env', cli: 'claude', hidden: false, automationWorker: false }
            ];
            const select = document.createElement('select');
            select.id = 'worker-picker-test';
            document.body.appendChild(select);
            mountWorkerPicker(app, select, { selectedValue: 'env:988:claude' });
            return {
                values: Array.from(select.querySelectorAll('option'))
                    .map((option) => option.value).filter(Boolean),
                value: select.tomselect.getValue(),
                legacyLabel: select.querySelector('option[value="env:988:claude"]')?.textContent,
                hasFooter: Boolean(select.tomselect.dropdown.querySelector('.llm-picker-customize-button'))
            };
        });

        // `hidden` has no power over the Worker picker…
        expect(snapshot.values).toContain('env:987:codex');
        // …a pre-flag environment already referenced by an automation keeps
        // resolving with its real name…
        expect(snapshot.value).toBe('env:988:claude');
        expect(snapshot.legacyLabel).toContain('Legacy env');
        // …and there is no customization footer (not a launch picker).
        expect(snapshot.hasFooter).toBe(false);
    });
});
