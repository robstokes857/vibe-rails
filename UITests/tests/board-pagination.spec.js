const { test, expect } = require('@playwright/test');

async function openDragBoard(page) {
    const columns = [
        { id: 'review', name: 'Review', position: 0, color: '#a855f7' },
        { id: 'done', name: 'Done', position: 1, color: '#10b981' }
    ];
    const card = { id: 'drag-card', key: 'VB-1', title: 'Move this reviewed card',
        description: '', columnId: 'review', position: 0, type: 'bug', priority: 'high', tags: [],
        activeTabId: 'tab-fixture', activeSessionId: 'session-fixture' };
    const moves = [];
    await page.setViewportSize({ width: 1200, height: 900 });
    await page.addInitScript(() => {
        window.__viberails_VSCODE__ = true;
        sessionStorage.setItem('viberails_tab', 'board-fixture');
    });
    await page.routeWebSocket('**/api/v1/events/ws*', () => {});
    await page.route('**/api/v1/**', async route => {
        const url = new URL(route.request().url());
        if (url.pathname === '/api/v1/board/cards/drag-card/move') {
            const move = route.request().postDataJSON();
            moves.push(move);
            Object.assign(card, move);
            return route.fulfill({ json: card });
        }
        const payload = {
            '/api/v1/context': { isInGit: true, rootPath: 'C:/fixture' },
            '/api/v1/board/boards': { boards: [{ id: 'main', name: 'Main', position: 0, cardCount: 1, columns }] },
            '/api/v1/board/columns': { columns },
            '/api/v1/board/cards': { cards: [card] },
            '/api/v1/board/cards/activity': { cards: [card], activeAutomationColumnIds: [] }
        };
        return route.fulfill({ json: payload[url.pathname] || {} });
    });
    await page.goto('/?view=board', { waitUntil: 'domcontentloaded' });
    await expect(page.locator('.board-card')).toHaveCount(1);
    await expect(page.locator('.board-view')).not.toHaveClass(/is-loading/);
    return { card, moves };
}

test('VS Code card dragging survives a lane repaint and releases the UI after dropping', async ({ page }) => {
    const { moves } = await openDragBoard(page);
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    const title = page.locator('.board-card-title');
    const start = await title.boundingBox();
    await page.mouse.move(start.x + 20, start.y + 10);
    await page.mouse.down();
    await page.mouse.move(start.x + 50, start.y + 40, { steps: 6 });
    await expect(page.locator('.board-card.is-chosen')).toHaveCount(1);
    await page.evaluate(() => {
        window.__draggedCard = document.querySelector('[data-card-id="drag-card"].is-chosen');
        window.app.boardController.renderLanes();
    });
    expect(await page.evaluate(() => window.__draggedCard?.isConnected)).toBe(true);
    const done = await page.locator('.board-lane-list[data-column-id="done"]').boundingBox();
    await page.mouse.move(done.x + done.width / 2, done.y + 60, { steps: 12 });
    await page.mouse.up();
    await expect.poll(() => moves.length).toBe(1);
    expect(moves[0]).toEqual({ columnId: 'done', position: 0 });
    await expect(page.locator('.board-lane-list[data-column-id="done"] .board-card')).toHaveCount(1);
    await expect(page.locator('.is-ghost, .is-chosen, .sortable-fallback')).toHaveCount(0);
    await expect.poll(() => page.evaluate(() => Boolean(window.app.boardController._boardDragging))).toBe(false);
    await page.getByRole('button', { name: 'Hide Done lane', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Show Done lane', exact: true })).toBeVisible();
    expect(errors).toEqual([]);
});

test('a VS Code card click without dragging releases the drag guard', async ({ page }) => {
    const { moves } = await openDragBoard(page);
    const card = await page.locator('.board-card').boundingBox();
    await page.mouse.move(card.x + 8, card.y + 8);
    await page.mouse.down();
    await expect.poll(() => page.evaluate(() => Boolean(window.app.boardController._boardDragging))).toBe(true);
    await page.mouse.up();
    await expect.poll(() => page.evaluate(() => Boolean(window.app.boardController._boardDragging))).toBe(false);
    await expect(page.locator('.is-chosen, .is-ghost, .sortable-fallback')).toHaveCount(0);
    expect(moves).toEqual([]);
});

test('Done loads 30 cards, scrolls for more, and searches unloaded history', async ({ page }) => {
    const cards = Array.from({ length: 95 }, (_, index) => ({
        id: `done-${index}`, key: `VB-${index + 1}`, title: `Completed task ${index + 1}`,
        description: '', columnId: 'done', position: index, type: 'task', priority: 'medium', tags: []
    }));
    const requests = [];
    const columns = [{ id: 'done', name: 'Done', position: 0, color: '#10b981' }];
    await page.addInitScript(() => sessionStorage.setItem('viberails_tab', 'board-fixture'));
    await page.routeWebSocket('**/api/v1/events/ws*', () => {});
    await page.route('**/api/v1/**', route => {
        const url = new URL(route.request().url());
        const payload = {
            '/api/v1/context': { isInGit: true, rootPath: 'C:/fixture' },
            '/api/v1/board/boards': { boards: [{ id: 'main', name: 'Main', position: 0, cardCount: 95, columns }] },
            '/api/v1/board/columns': { columns }
        };
        if (url.pathname === '/api/v1/board/cards') {
            requests.push(url);
            const query = url.searchParams.get('q') || '';
            const filtered = cards.filter(card => card.title.toLowerCase().includes(query.toLowerCase()));
            const offset = Number(url.searchParams.get('offset') || 0);
            return route.fulfill({ json: {
                cards: filtered.slice(offset, offset + 30),
                lanes: [{ columnId: 'done', totalCount: 95, filteredCount: filtered.length,
                    nextOffset: offset + 30, hasMore: offset + 30 < filtered.length }],
                totalCount: 95, filteredCount: filtered.length, blockedCount: 0, remainingPoints: 0,
                assignees: [], tags: []
            } });
        }
        if (url.pathname === '/api/v1/board/cards/search') {
            const query = (url.searchParams.get('q') || '').toLowerCase();
            return route.fulfill({ json: { cards: cards.filter(card => card.title.toLowerCase().includes(query))
                .map(card => ({ ...card, boardId: 'main', boardName: 'Main', columnName: 'Done', isCurrentProject: true })) } });
        }
        return route.fulfill({ json: payload[url.pathname] || {} });
    });
    await page.goto('/?view=board', { waitUntil: 'domcontentloaded' });
    await expect(page.locator('.board-card')).toHaveCount(30);
    await expect(page.locator('.board-lane-head .board-card-count')).toHaveText('95');
    const lane = page.locator('.board-lane-list');
    await lane.evaluate(element => { element.scrollTop = element.scrollHeight; });
    await expect(page.locator('.board-card')).toHaveCount(60);
    await expect(page.locator('[data-board-action="load-more"]')).toContainText('60 of 95');
    await lane.evaluate(element => { element.scrollTop = 400; });
    const scrollTop = await lane.evaluate(element => element.scrollTop);
    cards[0].title = 'Updated by another window';
    await page.evaluate(() => window.dispatchEvent(new Event('focus')));
    await expect(page.locator('.board-card-title').first()).toHaveText('Updated by another window');
    await expect(page.locator('.board-card')).toHaveCount(60);
    expect(await lane.evaluate(element => element.scrollTop)).toBe(scrollTop);
    await page.locator('[data-board-search]').fill('Completed task 95');
    await expect(page.locator('[data-board-search-card]')).toHaveCount(1);
    await expect(page.locator('[data-board-search-card]')).toContainText('Completed task 95');
    await expect(page.locator('[data-board-canvas]')).toBeHidden();
    await page.locator('[data-board-search]').fill('');
    await expect(page.locator('[data-board-canvas]')).toBeVisible();
    await expect(page.locator('.board-lane-head .board-card-count')).toHaveText('95');
    expect(requests.every(url => url.searchParams.get('pageSize') === '30')).toBeTruthy();
    expect(requests.some(url => url.searchParams.get('offset') === '30')).toBeTruthy();
});
