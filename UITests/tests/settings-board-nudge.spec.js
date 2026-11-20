const { test, expect } = require('@playwright/test');

async function installFixture(page, { isInGit = true, boards = [] } = {}) {
    await page.addInitScript(() => sessionStorage.setItem('viberails_tab', 'context-fixture'));
    await page.routeWebSocket('**/api/v1/events/ws*', () => {});
    const writes = [];
    const states = {
        '/api/v1/settings': { apiKey: '', computerName: 'Fixture machine', mcpEnabled: true },
        '/api/v1/board/app-context': { message: 'Existing app rule', revision: 2 },
        '/api/v1/board/project-context': { message: 'Existing repo rule', revision: 3 }
    };
    await page.route('**/api/v1/**', async route => {
        const request = route.request();
        const url = new URL(request.url());
        const path = url.pathname;
        if (path === '/api/v1/board/boards') {
            expect(url.searchParams.get('existingOnly')).toBe('true');
            return route.fulfill({ json: { boards } });
        }
        if (path === '/api/v1/settings/context-nudge') {
            const changes = request.postDataJSON();
            writes.push({ path, changes });
            Object.assign(states['/api/v1/settings'], changes);
            return route.fulfill({ json: states['/api/v1/settings'] });
        }
        if (states[path]) {
            if (request.method() === 'PUT') {
                const changes = request.postDataJSON();
                writes.push({ path, changes });
                expect(changes.expectedRevision).toBe(states[path].revision);
                states[path] = { message: changes.message, revision: states[path].revision + 1 };
            }
            return route.fulfill({ json: states[path] });
        }
        const payloads = {
            '/api/v1/context': { isInGit, rootPath: 'C:/fixture-repo', launchDirectory: 'C:/fixture-repo' },
            '/api/v1/settings/pin/status': { isSet: false },
            '/api/v1/settings/remote-link': { status: 'idle' },
            '/api/v1/llm-picker/preferences': { items: [] }
        };
        return route.fulfill({ json: payloads[path] || {} });
    });
    return { writes, states };
}

test('the gear opens App settings, whose Context tab saves both nudge choices independently', async ({ page }) => {
    const { writes, states } = await installFixture(page);
    await page.goto('/?view=settings', { waitUntil: 'domcontentloaded' });
    const nav = page.getByRole('navigation');
    const gear = nav.getByRole('button', { name: 'App settings', exact: true });
    await expect(nav.getByRole('button', { name: 'Context', exact: true })).toHaveCount(0);
    await expect(gear).toHaveAttribute('aria-current', 'page');
    const tab = page.getByRole('tab', { name: 'Context', exact: true });
    await tab.click();
    await expect(tab).toHaveAttribute('aria-selected', 'true');
    // Context saves with its own button; the settings bar only returns for unsaved settings.
    await expect(page.locator('[data-settings-save-bar]')).toBeHidden();
    const root = page.locator('#app-content #settings-panel-context');
    const base = root.getByRole('checkbox', { name: 'Built-in CLIs and Agent presets' });
    const custom = root.getByRole('checkbox', { name: 'Legacy custom environments' });
    const save = root.getByRole('button', { name: 'Save context' });
    await expect(base).toBeChecked();
    await expect(custom).not.toBeChecked();
    await expect(save).toBeDisabled();

    await base.uncheck();
    await expect(save).toBeEnabled();
    await base.check();
    await expect(save).toBeDisabled();
    await custom.check();
    await save.click();
    await expect(save).toBeDisabled();
    await base.uncheck();
    await save.click();
    await expect(save).toBeDisabled();
    expect(writes.map(item => item.changes)).toEqual([
        { createVibeStoryTrackingCustomEnvs: true },
        { createVibeStoryTracking: false }
    ]);
    expect(states['/api/v1/settings'].computerName).toBe('Fixture machine');

    // The retired Context page URL opens the tab.
    await page.goto('/?view=context', { waitUntil: 'domcontentloaded' });
    await expect(tab).toHaveAttribute('aria-selected', 'true');
    await expect(gear).toHaveAttribute('aria-current', 'page');
    await expect(base).not.toBeChecked();
    await expect(custom).toBeChecked();
    await expect(save).toBeDisabled();

    // A draft survives switching tabs, and leaving the page asks once.
    await base.check();
    await page.getByRole('tab', { name: 'General', exact: true }).click();
    await tab.click();
    await expect(base).toBeChecked();
    await nav.getByRole('button', { name: 'Board', exact: true }).click();
    const prompt = page.getByRole('alertdialog');
    await expect(prompt.getByText('You have unsaved context changes that will be lost.')).toBeVisible();
    await page.getByRole('button', { name: 'Stay', exact: true }).click();
    await expect(prompt).toHaveCount(0);
    await expect(base).toBeChecked();
    await expect(save).toBeEnabled();
});

for (const width of [390, 1280]) {
    test('a repository without boards can save Project and App context at ' + width + 'px', async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 900 });
        const { writes } = await installFixture(page);
        await page.goto('/?view=context', { waitUntil: 'domcontentloaded' });
        const editor = page.locator('[data-context-editor]');
        await expect(page.locator('[data-context-project]')).toHaveText('fixture-repo');
        await expect(editor.locator('details[open] > summary')).toHaveText(['App context']);
        await expect(editor.getByLabel('Board to edit')).toHaveText('No editable boards');
        const appMessage = editor.getByLabel('App context', { exact: true });
        const projectMessage = editor.getByLabel('Project context', { exact: true });
        await editor.locator('[data-context-level="project"] > summary').click();
        await expect(appMessage).toHaveValue('Existing app rule');
        await expect(projectMessage).toHaveValue('Existing repo rule');
        await projectMessage.fill('Run the repository tests before asking for review.');
        await appMessage.fill('Keep final answers short.');
        await editor.getByRole('button', { name: 'Save context' }).click();
        await expect(page.getByText('Context saved.', { exact: true })).toBeVisible();
        expect(writes.find(item => item.path.endsWith('/project-context')).changes).toEqual({
            message: 'Run the repository tests before asking for review.', expectedRevision: 3
        });
        expect(writes.find(item => item.path.endsWith('/app-context')).changes).toEqual({
            message: 'Keep final answers short.', expectedRevision: 2
        });
        expect(await editor.evaluate(element => element.scrollWidth <= element.clientWidth + 1)).toBe(true);
        await page.screenshot({ path: testInfo.outputPath('context-workspace.png'), animations: 'disabled' });
    });
}

test('outside a repository the Context tab offers App context', async ({ page }) => {
    await installFixture(page, { isInGit: false });
    await page.goto('/?view=settings', { waitUntil: 'domcontentloaded' });
    await page.getByRole('tab', { name: 'Context', exact: true }).click();
    const editor = page.locator('[data-context-editor]');
    await expect(page.locator('[data-context-project]')).toHaveText('No Git repository open');
    await editor.locator('[data-context-level="project"] > summary').click();
    await expect(editor.getByText('Open a Git repository to edit Project context.')).toBeVisible();
    await expect(editor.getByLabel('App context', { exact: true })).toHaveValue('Existing app rule');
    await expect(editor.getByLabel('Project context', { exact: true })).toHaveCount(0);
});
