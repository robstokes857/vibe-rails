const { test, expect } = require('@playwright/test');

async function prepareResume(page) {
    await openHistory(page);
    await page.evaluate(() => {
        const app = historySidebar.app;
        app.closeModal = () => {
            app.modalCleanup?.();
            document.querySelector('#resume-fixture')?.remove();
        };
        app.showModal = (title, body, { onClose } = {}) => {
            app.closeModal();
            const modal = document.createElement('div');
            modal.id = 'resume-fixture';
            modal.style.cssText = 'position:fixed;inset:10px;background:white;color:black;padding:20px;z-index:9999;overflow:auto;';
            modal.innerHTML = body;
            document.body.append(modal);
            app.modalCleanup = onClose;
        };
        app.navigate = (view, data) => { window.resumeNavigation = { view, data }; app.closeModal(); };
        app.terminalController = { manager: {
            isDestroyed: () => false,
            startWithOptions: options => { window.resumeLaunch = options; }
        } };
        window.resumeCalls = [];
        app.apiCall = async url => {
            resumeCalls.push(url);
            return { summary: url.includes('regenerate') ? 'regenerated recap' : 'cached recap', boardCards: [
                { id: 'card-a', key: 'VB-ABCDE-1', displayId: 'VIBE-67', title: 'First <img src=x onerror=alert(1)>' },
                { id: 'card-b', key: 'VB-FGHIJ-2', title: 'Second' }
            ] };
        };
        historySidebar.activeItem = { id: 'source', cli: 'codex', workingDirectory: 'C:/source' };
    });
}

test('Send To shows all current cards separately, regenerates and launches the edited recap', async ({ page }) => {
    await prepareResume(page);
    await page.evaluate(() => historySidebar._showResumeModal({ cli: 'codex' }, 'Codex'));
    await expect(page.locator('[data-resume-card]')).toHaveCount(2);
    await expect(page.locator('[data-resume-card="card-a"]')).toContainText('VIBE-67');
    await expect(page.locator('#ch-resume-body')).toContainText('VB-ABCDE-1');
    await expect(page.locator('#ch-resume-body img')).toHaveCount(0);
    await expect(page.locator('#ch-resume-summary')).toHaveValue('cached recap');
    await page.getByRole('button', { name: 'Regenerate' }).click();
    await expect(page.locator('#ch-resume-summary')).toHaveValue('regenerated recap');
    await expect(page.locator('[data-resume-card]')).toHaveCount(2);
    await page.locator('#ch-resume-summary').fill('edited recap');
    await page.getByRole('button', { name: 'Launch Terminal' }).click();
    expect(await page.evaluate(() => resumeLaunch)).toMatchObject({ cli: 'codex', resumeSummary: 'edited recap', resumeSessionId: 'source', workingDirectory: 'C:/source' });
    await page.evaluate(() => historySidebar._showResumeModal({ cli: 'claude' }, 'Claude'));
    await page.locator('[data-resume-card="card-b"]').click();
    expect(await page.evaluate(() => resumeNavigation)).toEqual({ view: 'board', data: { openCardId: 'card-b' } });
});

test('Send To ignores a late summary from a replaced modal and handles sessions without cards', async ({ page }) => {
    await prepareResume(page);
    await page.evaluate(async () => {
        let finishOld;
        historySidebar.app.apiCall = () => new Promise(resolve => { finishOld = resolve; });
        const old = historySidebar._showResumeModal({ cli: 'codex' }, 'Codex');
        historySidebar.app.apiCall = async () => ({ summary: 'new session recap', boardCards: [] });
        historySidebar.activeItem = { id: 'new-source', cli: 'claude' };
        await historySidebar._showResumeModal({ cli: 'claude' }, 'Claude');
        finishOld({ summary: 'stale recap', boardCards: [{ id: 'stale', key: 'VB-OLD-1' }] });
        await old;
    });
    await expect(page.locator('#ch-resume-summary')).toHaveValue('new session recap');
    await expect(page.locator('[data-resume-card]')).toHaveCount(0);
    await page.locator('#ch-resume-summary').fill('x'.repeat(6001));
    await expect(page.getByRole('button', { name: 'Launch Terminal' })).toBeDisabled();
    await page.locator('#ch-resume-summary').fill('x'.repeat(6000));
    await expect(page.getByRole('button', { name: 'Launch Terminal' })).toBeEnabled();
});

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
                if (parsed.pathname.endsWith('/board/cards/link-candidates')) {
                    const card = { ...rows[44].boardCards[0], boardName: 'VibeRails', columnName: 'In Progress', isCurrentProject: true };
                    const query = (parsed.searchParams.get('q') || '').toLowerCase();
                    return { cards: !query || ['vb-64', 'history', 'filters'].some(word => query.includes(word)) ? [card] : [] };
                }
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

test('history card links open each associated card with mouse or keyboard after refresh', async ({ page }, testInfo) => {
    await page.setViewportSize({ width: 390, height: 680 });
    await openHistory(page);
    await page.evaluate(() => {
        window.cardNavigations = [];
        historySidebar.app.navigate = (view, data) => cardNavigations.push({ view, data });
        historySidebar.allItems = [
            { ...historySidebar.allItems[0], sessionDisplayName: 'Linked history', boardCards: [
                { id: 'card-1', key: 'VB-ABCDE-1', displayId: 'VIBE-72', title: 'History <img src=x onerror=alert(1)>' },
                { id: 'card-2', key: 'VB-FGHIJ-2', title: 'Second card' }
            ] },
            { ...historySidebar.allItems[1], sessionDisplayName: 'Unlinked history', boardCards: [] }
        ];
        historySidebar.hasMore = false;
        historySidebar._renderItems();
        const rows = historySidebar.allItems;
        historySidebar.app.apiCall = async () => ({ items: rows });
    });
    const linked = page.locator('.ch-item').filter({ has: page.getByText('Linked history', { exact: true }) });
    const first = linked.getByRole('button', { name: 'Open Board card VIBE-72 · History', exact: false });
    const second = linked.getByRole('button', { name: 'Open Board card VB-FGHIJ-2 · Second card', exact: true });
    await expect(first).toHaveText('VIBE-72');
    await expect(linked.locator('.ch-card-meta').first()).toHaveAttribute('title', /VB-ABCDE-1/);
    await expect(linked.locator('img[src="x"]')).toHaveCount(0);
    await expect(page.locator('.ch-item').filter({ hasText: 'Unlinked history' }).locator('.ch-card-link')).toHaveCount(0);
    await linked.getByRole('button', { name: 'Actions', exact: true }).click();
    await expect(page.locator('[data-action="get-session"]')).toBeVisible();
    await first.click();
    await expect(page.locator('.ch-context-menu')).not.toHaveClass(/show/);
    await second.focus();
    await page.keyboard.press('Enter');
    // A history redraw replaces the buttons; navigation must still bind to the new elements.
    await page.evaluate(() => historySidebar._renderItems());
    await first.focus();
    await page.keyboard.press('Space');
    await page.evaluate(() => {
        historySidebar.destroy();
        historySidebar.mount(document.querySelector('#fixture'));
    });
    await second.click();
    expect(await page.evaluate(() => cardNavigations)).toEqual([
        { view: 'board', data: { openCardId: 'card-1' } },
        { view: 'board', data: { openCardId: 'card-2' } },
        { view: 'board', data: { openCardId: 'card-1' } },
        { view: 'board', data: { openCardId: 'card-2' } }
    ]);
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(390);
    await page.screenshot({ path: testInfo.outputPath('history-card-links.png') });
});

test('combined filters find an older Board chat and escape metadata', async ({ page }, testInfo) => {
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.setViewportSize({ width: 1100, height: 900 });
    await openHistory(page);
    await page.getByRole('button', { name: 'Filters', exact: true }).click();
    await page.locator('[data-llm-filter="codex"]').click();
    await expect(page.locator('.ch-item')).toHaveCount(1);
    await expect(page.locator('.ch-item-name')).toContainText('VB-M66G2-64 · History filters');
    await expect(page.locator('#ch-environment-filter, #ch-board-filter, #ch-status-filter, #ch-card-filter')).toHaveCount(0);
    await page.getByLabel('Find a card').fill('VB-64');
    await expect(page.locator('[data-board-link-card]')).toHaveCount(1);
    await expect(page.locator('#ch-card-results')).toContainText('VibeRails · In Progress');
    await expect(page.locator('#ch-card-results img')).toHaveCount(0);
    await page.getByLabel('Find a card').press('ArrowDown');
    await page.keyboard.press('Enter');
    await expect(page.locator('[data-history-card-selection]')).toContainText('VB-M66G2-64');
    await expect(page.locator('#ch-card-results')).toBeHidden();
    await page.getByRole('button', { name: 'This folder' }).click();
    await expect(page.locator('.ch-item')).toHaveCount(1);
    await expect(page.locator('.ch-item-meta')).toContainText('Exit 2');
    await expect(page.locator('.ch-filter-count-badge')).toHaveText('3');
    await expect(page.locator('#ch-filter-results')).toHaveText('1 chat');
    await expect(page.locator('.ch-item img[src="x"]')).toHaveCount(0);
    expect(await page.evaluate(() => historyCalls.some(url => url.includes('page=3')))).toBe(true);
    await page.screenshot({ path: testInfo.outputPath('history-desktop.png') });

    await page.locator('[data-llm-filter="codex"]').click();
    await page.locator('[data-llm-filter="claude"]').click();
    await expect(page.locator('.ch-empty-title')).toHaveText('No matches');
    await page.getByRole('button', { name: 'Clear all' }).click();
    await expect(page.locator('.ch-item')).toHaveCount(45);
    await expect(page.getByRole('button', { name: 'Clear all' })).toBeDisabled();
    expect(errors).toEqual([]);
});

test('narrow layout keeps card results scrollable and remount preserves the selected card', async ({ page }, testInfo) => {
    await page.setViewportSize({ width: 390, height: 680 });
    await openHistory(page);
    await page.getByRole('button', { name: 'Filters', exact: true }).click();
    await page.getByLabel('Find a card').fill('history filters');
    await expect(page.locator('[data-board-link-card]')).toHaveCount(1);
    const dimensions = await page.evaluate(() => {
        const drawer = document.querySelector('#ch-filter-drawer-content');
        const body = document.querySelector('#ch-sidebar-body');
        return { drawerHeight: drawer.clientHeight, drawerScroll: drawer.scrollHeight,
            bodyHeight: body.clientHeight, width: document.documentElement.scrollWidth };
    });
    expect(dimensions.drawerScroll).toBeGreaterThanOrEqual(dimensions.drawerHeight);
    expect(dimensions.bodyHeight).toBeGreaterThan(100);
    expect(dimensions.width).toBeLessThanOrEqual(390);
    await page.locator('[data-board-link-card]').click();
    await expect(page.locator('.ch-item')).toHaveCount(1);
    await page.screenshot({ path: testInfo.outputPath('history-narrow.png') });
    await page.evaluate(() => {
        const root = document.querySelector('#fixture');
        historySidebar.destroy();
        historySidebar.mount(root);
    });
    await expect(page.locator('[data-history-card-selection]')).toContainText('VB-M66G2-64');
    await expect(page.locator('.ch-item')).toHaveCount(1);
    await page.getByRole('button', { name: 'Clear card filter' }).click();
    await expect(page.locator('.ch-item')).toHaveCount(45);
    await expect(page.locator('[data-history-card-selection]')).toBeHidden();
});

test('card search leaves history unchanged until selection and handles empty, failed and foreign results', async ({ page }, testInfo) => {
    await page.setViewportSize({ width: 390, height: 680 });
    await openHistory(page);
    await page.evaluate(() => {
        const api = historySidebar.app.apiCall;
        historySidebar.app.apiCall = async (url, ...args) => {
            if (!url.includes('/board/cards/link-candidates')) return api(url, ...args);
            const query = new URL(url, location.origin).searchParams.get('q');
            if (query === 'offline') throw new Error('Search unavailable. Try again.');
            if (query === 'missing') return { cards: [] };
            return { cards: Array.from({ length: 50 }, (_, i) => ({ id: `foreign-${i}`, key: `VB-OTHER-${i}`,
                displayId: `VIBE-${i}`, title: 'Other project card <img src=x>', boardName: 'Other board',
                columnName: 'Done', isCurrentProject: false, projectPath: 'C:/source/another-project' })) };
        };
    });
    await page.getByRole('button', { name: 'Filters', exact: true }).click();
    await page.locator('[data-llm-filter="codex"]').click();
    await expect(page.locator('.ch-item')).toHaveCount(1);
    await page.getByLabel('Find a card').fill('missing');
    await expect(page.locator('[data-board-link-status]')).toHaveText('No matching cards.');
    await expect(page.locator('.ch-item')).toHaveCount(1);
    await page.getByLabel('Find a card').fill('offline');
    await expect(page.locator('[data-board-link-status]')).toContainText('Search unavailable');
    await page.getByLabel('Find a card').fill('other words');
    await expect(page.locator('[data-board-link-card]')).toHaveCount(50);
    await expect(page.locator('[data-board-link-status]')).toContainText('Refine your search');
    await expect(page.locator('#ch-card-results')).toContainText('Another project');
    await expect(page.locator('#ch-card-results img')).toHaveCount(0);
    const dimensions = await page.locator('#ch-card-results').evaluate(el => ({ height: el.clientHeight, scroll: el.scrollHeight }));
    expect(dimensions.scroll).toBeGreaterThan(dimensions.height);
    const drawer = await page.locator('#ch-filter-drawer-content').boundingBox();
    const firstResult = await page.locator('[data-board-link-card]').first().boundingBox();
    expect(firstResult.y + firstResult.height).toBeLessThanOrEqual(drawer.y + drawer.height);
    expect((await page.getByLabel('Find a card').boundingBox()).y).toBeGreaterThanOrEqual(drawer.y);
    expect(await page.locator('#ch-sidebar-body').evaluate(el => el.clientHeight)).toBeGreaterThan(100);
    expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(390);
    await page.screenshot({ path: testInfo.outputPath('history-card-dropdown.png') });
    await page.locator('[data-board-link-card="foreign-0"]').click();
    await expect(page.locator('.ch-empty-title')).toHaveText('No matches');
    await page.getByRole('button', { name: 'Clear card filter' }).click();
    await expect(page.locator('.ch-item')).toHaveCount(1);
    await expect(page.locator('[data-llm-filter="codex"]')).toHaveAttribute('aria-pressed', 'true');
    await page.getByLabel('Find a card').press('Escape');
    await expect(page.locator('#ch-card-results')).toBeHidden();
});

test('late card searches cannot reopen the dropdown or reach a replacement sidebar', async ({ page }) => {
    await openHistory(page);
    await page.evaluate(() => {
        window.pendingCards = [];
        const api = historySidebar.app.apiCall;
        historySidebar.app.apiCall = (url, ...args) => {
            if (!url.includes('/board/cards/link-candidates')) return api(url, ...args);
            return new Promise(resolve => pendingCards.push({ resolve, signal: args[2].signal }));
        };
    });
    await page.getByRole('button', { name: 'Filters', exact: true }).click();
    await page.getByLabel('Find a card').focus();
    await expect.poll(() => page.evaluate(() => pendingCards.length)).toBe(1);
    await page.getByLabel('Find a card').press('Escape');
    await page.evaluate(() => pendingCards[0].resolve({ cards: [{ id: 'late', key: 'VIBE-1', title: 'Late' }] }));
    await expect(page.locator('#ch-card-results')).toBeHidden();
    await expect(page.locator('[data-board-link-card]')).toHaveCount(0);
    await page.getByLabel('Find a card').fill('next query');
    await expect.poll(() => page.evaluate(() => pendingCards.length)).toBe(2);
    await page.evaluate(() => {
        const root = document.querySelector('#fixture');
        historySidebar.destroy();
        root.innerHTML = historySidebar.constructor.renderHtml();
        historySidebar.mount(root);
        pendingCards[1].resolve({ cards: [{ id: 'stale', key: 'VIBE-2', title: 'Stale' }] });
    });
    expect(await page.evaluate(() => pendingCards.every(call => call.signal.aborted))).toBe(true);
    await expect(page.locator('[data-board-link-card]')).toHaveCount(0);
    await expect(page.locator('#ch-card-results')).toBeHidden();
});
