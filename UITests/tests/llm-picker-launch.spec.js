const { test, expect } = process.env.VIBERAILS_QUALITY_STATIC === '1'
    ? require('@playwright/test')
    : require('./fixtures');

const PREFERENCES = '/api/v1/llm-picker/preferences';
const ENVIRONMENT_NAME = 'Rare profile (personal)';
const items = [
    { key: 'base:claude', kind: 'base', group: 'Base CLIs', label: 'Claude', cli: 'claude', enabled: true, order: 0 },
    { key: 'base:codex', kind: 'base', group: 'Base CLIs', label: 'Codex', cli: 'codex', enabled: false, order: 1 },
    { key: 'base:shell', kind: 'base', group: 'Base CLIs', label: 'Terminal', cli: 'shell', enabled: false, order: 2 },
    { key: 'env:902:codex', kind: 'environment', group: 'Custom Environments', label: `${ENVIRONMENT_NAME} (Codex)`, cli: 'codex', environmentId: 902, enabled: false, order: 0 }
];

async function openPicker(page, { nested = false, workingDirectory = null } = {}) {
    const preferenceWrites = [];
    await page.addInitScript(() => sessionStorage.setItem('viberails_tab', 'picker-fixture'));
    await page.routeWebSocket('**/api/v1/events/ws*', () => {});
    await page.route('**/api/v1/**', route => {
        const path = new URL(route.request().url()).pathname;
        if (path === PREFERENCES && route.request().method() !== 'GET') {
            preferenceWrites.push(route.request().method());
        }
        const payloads = {
            '/api/v1/context': { isInGit: true, rootPath: 'C:/source/vibe-rails', launchDirectory: 'C:/source/vibe-rails' },
            '/api/v1/environments': { environments: [
                { id: 901, name: 'Different profile', cli: 'codex' },
                { id: 902, name: ENVIRONMENT_NAME, cli: 'codex', hidden: true }
            ] },
            '/api/v1/agents': { agents: [] },
            '/api/v1/rules/details': { rules: [] },
            '/api/v1/sandboxes': { sandboxes: [] },
            [PREFERENCES]: { items }
        };
        return route.fulfill({ json: payloads[path] || {} });
    });
    await page.goto('/?view=agents', { waitUntil: 'domcontentloaded' });
    await expect(page.locator('[data-vca-quality-brief]')).toBeVisible({ timeout: 15_000 });
    await expect(page.locator('#loading-overlay')).toHaveClass(/\bd-none\b/);
    await page.waitForFunction(() => Boolean(window.app?.llmPickerController?.catalog));
    await page.evaluate(async ({ nested, workingDirectory }) => {
        window.__pickerLaunches = [];
        window.app.terminalController.launchInFocus = options => {
            window.__pickerLaunches.push(options);
            return true;
        };
        const { mountLlmPicker } = await import('/js/modules/pickers/llm-picker.js');
        const content = '<select id="launch-test-picker"></select>';
        if (nested) window.app.showModal('Parent dialog', content);
        else document.getElementById('app-content').innerHTML = content;
        const select = document.getElementById('launch-test-picker');
        mountLlmPicker(window.app, select, {
            context: 'terminal',
            getLaunchWorkingDirectory: () => workingDirectory
        });
        select.tomselect.open();
    }, { nested, workingDirectory });
    await page.getByRole('button', { name: 'View/Edit all LLMs', exact: true }).click();
    const modal = page.getByRole('dialog', { name: 'View/Edit all LLMs', exact: true });
    await expect(modal).toBeVisible();
    return { modal, preferenceWrites };
}

for (const choice of [
    { key: 'base:codex', cli: 'codex', environmentName: null },
    { key: 'base:shell', cli: 'shell', environmentName: null },
    { key: 'env:902:codex', cli: 'codex', environmentName: ENVIRONMENT_NAME }
]) {
    test(`View/Edit launches hidden ${choice.key} without saving preferences`, async ({ page }) => {
        const { modal, preferenceWrites } = await openPicker(page);
        const row = modal.locator(`[data-llm-picker-key="${choice.key}"]`);
        await expect(row.locator('[data-llm-picker-enabled]')).not.toBeChecked();
        await expect(page.locator(`#launch-test-picker option[value="${choice.key}"]`)).toHaveCount(0);
        // An unrelated unsaved visibility edit must not be committed by Launch either.
        await modal.locator('[data-llm-picker-key="base:claude"] [data-llm-picker-enabled]').uncheck();
        const launch = row.locator('[data-llm-picker-launch]');
        await expect(launch).toBeEnabled();
        await launch.focus();
        await launch.press('Enter');
        await expect(modal).toHaveCount(0);
        const launches = await page.evaluate(() => window.__pickerLaunches);
        expect(launches).toHaveLength(1);
        expect(launches[0]).toMatchObject({
            cli: choice.cli,
            environmentName: choice.environmentName,
            workingDirectory: 'C:/source/vibe-rails',
            forceNewTab: true
        });
        expect(preferenceWrites).toEqual([]);
        expect(await page.evaluate(() => window.app.llmPickerController.catalog))
            .toEqual(items.map(item => ({ environmentId: null, ...item })));
        await expect(page.locator(`#launch-test-picker option[value="${choice.key}"]`)).toHaveCount(0);
    });
}

test('nested launch preserves the source directory and releases the parent modal', async ({ page }) => {
    const { modal, preferenceWrites } = await openPicker(page, {
        nested: true, workingDirectory: 'C:/sandboxes/rare-run'
    });
    await expect(page.locator('#modal-container > .modal')).toHaveAttribute('inert', '');
    await modal.locator('[data-llm-picker-key="base:codex"] [data-llm-picker-launch]').click();
    await expect(modal).toHaveCount(0);
    await expect(page.locator('#modal-container > .modal')).not.toHaveAttribute('inert');
    expect(await page.evaluate(() => window.__pickerLaunches[0].workingDirectory)).toBe('C:/sandboxes/rare-run');
    expect(preferenceWrites).toEqual([]);
});

test('a missing custom environment never falls back to its base CLI', async ({ page }) => {
    const { modal, preferenceWrites } = await openPicker(page);
    await page.evaluate(() => { window.app.data.environments = []; });
    await modal.locator('[data-llm-picker-key="env:902:codex"] [data-llm-picker-launch]').click();
    await expect(modal.locator('[data-llm-picker-error]')).toContainText('no longer available');
    expect(await page.evaluate(() => window.__pickerLaunches)).toEqual([]);
    expect(preferenceWrites).toEqual([]);
});

test('blocked navigation and launch errors leave the draft available for retry', async ({ page }) => {
    const { modal, preferenceWrites } = await openPicker(page);
    await modal.locator('[data-llm-picker-key="base:claude"] [data-llm-picker-enabled]').uncheck();
    await page.evaluate(() => { window.app.terminalController.launchInFocus = () => false; });
    const launch = modal.locator('[data-llm-picker-key="base:codex"] [data-llm-picker-launch]');
    await launch.click();
    await expect(modal).toBeVisible();
    await expect(launch).toBeEnabled();
    await expect(modal.locator('[data-llm-picker-key="base:claude"] [data-llm-picker-enabled]')).not.toBeChecked();
    await page.evaluate(() => {
        window.app.terminalController.launchInFocus = async () => { throw new Error('Launch unavailable'); };
    });
    await launch.click();
    await expect(modal.locator('[data-llm-picker-error]')).toHaveText('Launch unavailable');
    await expect(launch).toBeEnabled();
    expect(preferenceWrites).toEqual([]);
});

for (const viewport of [{ width: 1100, height: 720 }, { width: 390, height: 640 }]) {
    test(`all launch controls fit at ${viewport.width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize(viewport);
        const { modal } = await openPicker(page);
        expect(await modal.locator('.modal-body').evaluate(element => element.scrollWidth - element.clientWidth)).toBeLessThanOrEqual(1);
        for (const launch of await modal.locator('[data-llm-picker-launch]').all()) {
            await launch.scrollIntoViewIfNeeded();
            await expect(launch).toBeInViewport();
            await expect(launch).toBeEnabled();
            expect(await launch.evaluate(element => {
                const label = element.closest('.llm-picker-modal-row').querySelector('.llm-picker-row-label').getBoundingClientRect();
                const button = element.getBoundingClientRect();
                return button.left >= label.right || button.top >= label.bottom;
            })).toBe(true);
        }
        await page.screenshot({ path: testInfo.outputPath('llm-picker-launch.png') });
    });
}
