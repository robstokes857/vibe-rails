const { test, expect } = require('@playwright/test');

// Mount the production sidebar and stylesheet with a paged, read-only API fixture.
// This suite neither starts a CLI nor opens an application database.
async function openHistory(page) {
    await page.route('**/history-fixture', route => route.fulfill({ contentType: 'text/html', body: `
        <!doctype html><html><head>
        <link rel="stylesheet" href="/assets/bootstrap.min.css">
        <link rel="stylesheet" href="/assets/fontawesome/all.min.css">
        <link rel="stylesheet" href="/style.css"></head>
        <body style="margin:0;padding:12px;background:#121212"><main id="fixture"></main></body></html>` }));
    await page.goto('/history-fixture');
    await page.evaluate(async () => {
        const { ChatHistorySidebar } = await import('/js/modules/chat-history-sidebar.js');
        const { getCliBrand } = await import('/js/modules/utils.js');
        localStorage.setItem('viberails_history_sidebar_seen', '1');
        localStorage.setItem('viberails_history_sidebar_open', '1');
        const rows = Array.from({ length: 45 }, (_, index) => ({
            id: `session-${index}`, cli: index === 44 ? 'codex' : 'claude',
            environmentName: index === 44 ? 'Review <&>' : null,
            workingDirectory: 'C:/fixture', projectDisplayName: 'Fixture',
            startedUTC: new Date(Date.now() - index * 60000).toISOString(),
            endedUTC: index === 0 ? null : new Date(Date.now() - index * 30000).toISOString(), exitCode: index === 44 ? 2 : 0,
            inputText: index === 44 ? 'You are working on kanban card…' : `Chat ${index}`,
            sessionDisplayName: index === 44 ? 'You are working on kanban card…' : null,
            boardCards: index === 44 ? [{ id: 'card', key: 'VB-M66G2-64', title: 'History filters <img src=x onerror=alert(1)>' }] : []
        }));
        window.historyCalls = [];
        const app = {
            data: { environments: [{ id: 1, cli: 'codex', name: 'Review <&>' }, { id: 2, cli: 'claude', name: 'Review <&>' }],
                configs: { rootPath: 'C:/fixture' } },
            getCliBrand, getProjectNameFromPath: () => 'Fixture',
            showError: message => { throw new Error(message); }, showToast() {},
            async apiCall(url) {
                window.historyCalls.push(url);
                const parsed = new URL(url, location.origin);
                const page = Number(parsed.searchParams.get('page') || 1);
                const size = Number(parsed.searchParams.get('pageSize') || 20);
                return { items: rows.slice((page - 1) * size, page * size) };
            }
        };
        const root = document.querySelector('#fixture');
        root.innerHTML = ChatHistorySidebar.renderHtml();
        const element = root.querySelector('.ch-sidebar');
        element.style.cssText = 'width:min(360px,calc(100vw - 24px));height:calc(100vh - 24px);';
        window.historySidebar = new ChatHistorySidebar(app);
        window.historySidebar.mount(root, { onToggle: open => element.classList.toggle('ch-sidebar-collapsed', !open) });
    });
    await expect(page.locator('.ch-item').first()).toBeVisible();
}

test('combined filters find an older Board chat and escape metadata', async ({ page }, testInfo) => {
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.setViewportSize({ width: 1100, height: 900 });
    await openHistory(page);
    await page.getByRole('button', { name: 'Filters', exact: true }).click();
    await page.locator('[data-llm-filter="codex"]').click();
    await expect(page.locator('.ch-item')).toHaveCount(1);
    await expect(page.locator('.ch-item-name')).toContainText('VB-M66G2-64 · History filters');
    await page.getByLabel('Environment', { exact: true }).selectOption({ label: 'Review <&> (Codex)' });
    await page.getByLabel('Board', { exact: true }).selectOption('linked');
    await page.getByLabel('Card key or title').fill('VB-64 filters');
    await page.getByLabel('Status', { exact: true }).selectOption('failed');
    await page.getByRole('button', { name: 'This folder' }).click();
    await expect(page.locator('.ch-item')).toHaveCount(1);
    await expect(page.locator('.ch-item-meta')).toContainText('Exit 2');
    await expect(page.locator('.ch-filter-count-badge')).toHaveText('6');
    await expect(page.locator('#ch-filter-results')).toHaveText('1 chat');
    await expect(page.locator('.ch-item img[src="x"]')).toHaveCount(0);
    expect(await page.evaluate(() => historyCalls.some(url => url.includes('page=3')))).toBe(true);
    await page.screenshot({ path: testInfo.outputPath('history-desktop.png') });

    await page.getByLabel('Environment', { exact: true }).selectOption({ label: 'Review <&> (Claude)' });
    await expect(page.locator('.ch-empty-title')).toHaveText('No matches');
    await page.getByRole('button', { name: 'Clear all' }).click();
    await expect(page.locator('.ch-item')).toHaveCount(45);
    await expect(page.getByRole('button', { name: 'Clear all' })).toBeDisabled();
    expect(errors).toEqual([]);
});

test('narrow layout keeps filters scrollable and remount restores environment options', async ({ page }, testInfo) => {
    await page.setViewportSize({ width: 390, height: 680 });
    await openHistory(page);
    await page.getByRole('button', { name: 'Filters', exact: true }).click();
    const dimensions = await page.evaluate(() => {
        const drawer = document.querySelector('#ch-filter-drawer-content');
        const body = document.querySelector('#ch-sidebar-body');
        return { drawerHeight: drawer.clientHeight, drawerScroll: drawer.scrollHeight,
            bodyHeight: body.clientHeight, width: document.documentElement.scrollWidth };
    });
    expect(dimensions.drawerScroll).toBeGreaterThan(dimensions.drawerHeight);
    expect(dimensions.bodyHeight).toBeGreaterThan(100);
    expect(dimensions.width).toBeLessThanOrEqual(390);
    await page.getByLabel('Status', { exact: true }).selectOption('live');
    await expect(page.locator('.ch-item')).toHaveCount(1);
    await page.getByRole('button', { name: 'Clear all' }).click();
    await page.screenshot({ path: testInfo.outputPath('history-narrow.png') });
    await page.evaluate(() => {
        const root = document.querySelector('#fixture');
        historySidebar.destroy();
        historySidebar.mount(root);
    });
    await expect(page.getByLabel('Environment', { exact: true }).locator('option')).toHaveCount(5);
});
