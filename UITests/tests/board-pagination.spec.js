const { test, expect } = require('@playwright/test');

async function openDragBoard(page, { vscode = true, doneCount = 0 } = {}) {
    const columns = [
        { id: 'review', name: 'Review', position: 0, color: '#a855f7' },
        { id: 'done', name: 'Done', position: 1, color: '#10b981' }
    ];
    const card = { id: 'drag-card', key: 'VB-1', title: 'Move this reviewed card',
        description: '', columnId: 'review', position: 0, type: 'bug', priority: 'high', tags: [],
        activeTabId: 'tab-fixture', activeSessionId: 'session-fixture' };
    const moves = [];
    const reorders = [];
    const pageRequests = [];
    const cards = [card, ...Array.from({ length: doneCount }, (_, index) => ({
        id: `done-${index}`, key: `VB-${index + 2}`, title: `Completed task ${index + 1}`,
        description: '', columnId: 'done', position: index, type: 'task', priority: 'medium', tags: []
    }))];
    await page.setViewportSize({ width: 1200, height: 900 });
    await page.addInitScript(vscode => {
        window.__viberails_VSCODE__ = vscode;
        window.__nativeDragStarts = 0;
        document.addEventListener('dragstart', () => window.__nativeDragStarts++);
        sessionStorage.setItem('viberails_tab', 'board-fixture');
    }, vscode);
    await page.routeWebSocket('**/api/v1/events/ws*', () => {});
    await page.route('**/api/v1/**', async route => {
        const url = new URL(route.request().url());
        if (url.pathname === '/api/v1/board/cards/drag-card/move') {
            const move = route.request().postDataJSON();
            moves.push(move);
            Object.assign(card, move);
            return route.fulfill({ json: card });
        }
        if (url.pathname === '/api/v1/board/columns/order') {
            const reorder = route.request().postDataJSON();
            reorders.push(reorder);
            reorder.orderedIds.forEach((id, index) => { columns.find(column => column.id === id).position = index; });
            return route.fulfill({ json: {} });
        }
        if (url.pathname === '/api/v1/board/cards') {
            pageRequests.push(url);
            const offset = Number(url.searchParams.get('offset') || 0);
            const columnId = url.searchParams.get('columnId');
            const lanes = columns.filter(column => !columnId || column.id === columnId).map(column => {
                const total = cards.filter(item => item.columnId === column.id).length;
                const nextOffset = column.id === 'done' ? Math.min(offset + 30, total) : total;
                return { columnId: column.id, totalCount: total, filteredCount: total,
                    nextOffset, hasMore: nextOffset < total, continuationToken: `fixture-${column.id}` };
            });
            return route.fulfill({ json: { cards: lanes.flatMap(lane => cards
                .filter(item => item.columnId === lane.columnId).sort((a, b) => a.position - b.position)
                .slice(lane.columnId === 'done' ? offset : 0, lane.nextOffset)), lanes,
                totalCount: cards.length, filteredCount: cards.length, blockedCount: 0, flaggedCount: 0,
                assignees: [], tags: [] } });
        }
        const payload = {
            '/api/v1/context': { isInGit: true, rootPath: 'C:/fixture' },
            '/api/v1/board/boards': { boards: [{ id: 'main', name: 'Main', position: 0, cardCount: cards.length, columns }] },
            '/api/v1/board/columns': { columns },
            '/api/v1/board/cards/drag-card': card,
            '/api/v1/board/cards/activity': { cards: [card], activeAutomationColumnIds: [] }
        };
        return route.fulfill({ json: payload[url.pathname] || {} });
    });
    await page.goto('/?view=board', { waitUntil: 'domcontentloaded' });
    await expect(page.locator('.board-card')).toHaveCount(1 + Math.min(30, doneCount));
    await expect(page.locator('.board-view')).not.toHaveClass(/is-loading/);
    return { card, moves, reorders, pageRequests };
}

for (const vscode of [true, false]) {
    test(`${vscode ? 'VS Code pointer' : 'Browser native'} card dragging survives a lane repaint and releases the UI after dropping`, async ({ page }, testInfo) => {
        const { moves } = await openDragBoard(page, { vscode });
        const errors = [];
        page.on('pageerror', error => errors.push(error.message));
        const title = page.locator('.board-card-title');
        const start = await title.boundingBox();
        await page.mouse.move(start.x + 20, start.y + 10);
        await page.mouse.down();
        await page.mouse.move(start.x + 50, start.y + 40, { steps: 6 });
        await expect(page.locator('.board-lane-list .board-card.is-chosen')).toHaveCount(1);
        if (vscode) {
            const clone = page.locator('body > .board-card.sortable-fallback');
            await expect(clone).toBeVisible();
            await page.screenshot({ path: testInfo.outputPath('pointer-drag.png') });
            await expect(clone).toHaveCSS('display', 'grid');
        }
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
        expect(await page.evaluate(() => window.__nativeDragStarts)).toBe(vscode ? 0 : 1);
    });
}

test('a VS Code card click without dragging releases the drag guard', async ({ page }) => {
    const { moves } = await openDragBoard(page);
    const card = await page.locator('.board-card').boundingBox();
    await page.mouse.move(card.x + 8, card.y + 8);
    await page.mouse.down();
    await expect.poll(() => page.evaluate(() => Boolean(window.app.boardController._boardDragging))).toBe(true);
    // Small pointer jitter must remain a click, not start a fallback drag.
    await page.mouse.move(card.x + 10, card.y + 9);
    await expect(page.locator('.sortable-fallback')).toHaveCount(0);
    await page.mouse.up();
    await expect.poll(() => page.evaluate(() => Boolean(window.app.boardController._boardDragging))).toBe(false);
    await expect(page.locator('.is-chosen, .is-ghost, .sortable-fallback')).toHaveCount(0);
    await expect(page.locator('#board-card-title')).toHaveValue('Move this reviewed card');
    expect(moves).toEqual([]);
    expect(await page.evaluate(() => window.__nativeDragStarts)).toBe(0);
});

test('VS Code pointer mode preserves multi-select without starting a drag', async ({ page }) => {
    const { moves } = await openDragBoard(page, { doneCount: 2 });
    await page.getByRole('button', { name: 'Select cards', exact: true }).click();
    await page.locator('[data-board-card-select="drag-card"]').check();
    await page.locator('[data-board-card-select="done-0"]').check();
    await expect(page.locator('[data-board-selection-status]')).toHaveText('2 selected');
    await expect(page.locator('[data-board-card-select="done-1"]')).not.toBeChecked();
    await expect(page.locator('.sortable-fallback, .is-chosen')).toHaveCount(0);
    expect(moves).toEqual([]);
    expect(await page.evaluate(() => window.__nativeDragStarts)).toBe(0);
});

test('VS Code lanes reorder with a body-mounted pointer clone', async ({ page }, testInfo) => {
    const { reorders } = await openDragBoard(page);
    const start = await page.getByRole('button', { name: 'Reorder Review', exact: true }).boundingBox();
    const done = await page.locator('.board-lane[data-column-id="done"]').boundingBox();
    await page.mouse.move(start.x + start.width / 2, start.y + start.height / 2);
    await page.mouse.down();
    await page.mouse.move(start.x + 40, start.y + 10, { steps: 6 });
    const clone = page.locator('body > .board-lane.sortable-fallback');
    await expect(clone).toBeVisible();
    await page.screenshot({ path: testInfo.outputPath('pointer-lane-drag.png') });
    await expect(clone).toHaveCSS('display', 'flex');
    await page.mouse.move(done.x + done.width - 20, done.y + 20, { steps: 12 });
    await expect(page.locator('[data-board-lanes] > .board-lane').last()).toHaveAttribute('data-column-id', 'review');
    await page.mouse.up();
    await expect.poll(() => reorders.length).toBe(1);
    expect(reorders[0].orderedIds).toEqual(['done', 'review']);
    await expect(page.locator('.sortable-fallback, .sortable-chosen, .sortable-ghost')).toHaveCount(0);
    expect(await page.evaluate(() => window.__nativeDragStarts)).toBe(0);
});

test('VS Code pointer dragging autoscrolls Done and defers pagination until release', async ({ page }) => {
    const { moves, pageRequests } = await openDragBoard(page, { doneCount: 65 });
    const title = await page.locator('[data-card-id="drag-card"] .board-card-title').boundingBox();
    const lane = page.locator('.board-lane-list[data-column-id="done"]');
    const bounds = await lane.boundingBox();
    await page.mouse.move(title.x + 20, title.y + 10);
    await page.mouse.down();
    await page.mouse.move(title.x + 50, title.y + 40, { steps: 6 });
    await expect(page.locator('body > .sortable-fallback')).toBeVisible();
    await page.mouse.move(bounds.x + bounds.width / 2, bounds.y + bounds.height - 10, { steps: 12 });
    // Keep moving slowly at the edge until Sortable's throttled scroll listener
    // samples it, then verify scrolling continues with the pointer held still.
    let movement = 0;
    await expect.poll(async () => {
        await page.mouse.move(bounds.x + bounds.width / 2 + (++movement % 2), bounds.y + bounds.height - 10);
        return lane.evaluate(element => element.scrollTop);
    }).toBeGreaterThan(0);
    const firstScroll = await lane.evaluate(element => element.scrollTop);
    await expect.poll(() => lane.evaluate(element => element.scrollTop)).toBeGreaterThan(firstScroll);
    // Exercise the pagination boundary without waiting for 30 cards to autoscroll past.
    await lane.evaluate(element => { element.scrollTop = element.scrollHeight; });
    await expect.poll(() => lane.evaluate(element => element.scrollHeight - element.scrollTop - element.clientHeight)).toBeLessThan(240);
    await expect(page.locator('.board-lane-list .board-card')).toHaveCount(31);
    expect(pageRequests.filter(url => url.searchParams.has('offset'))).toHaveLength(0);
    await page.mouse.up();
    await expect.poll(() => moves.length).toBe(1);
    expect(moves[0].columnId).toBe('done');
    await expect(page.locator('.board-view')).not.toHaveClass(/is-loading/);
    await expect(page.locator('.sortable-fallback, .is-chosen, .is-ghost')).toHaveCount(0);
    await lane.evaluate(element => { element.scrollTop = element.scrollHeight; });
    // Restored scroll can immediately request a further page after the first.
    await expect.poll(() => lane.locator('.board-card').count()).toBeGreaterThanOrEqual(60);
    expect(pageRequests.some(url => url.searchParams.get('offset') === '30')).toBe(true);
    expect(await page.evaluate(() => window.__nativeDragStarts)).toBe(0);
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
