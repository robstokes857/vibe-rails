const { test, expect } = require('@playwright/test');

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
        return route.fulfill({ json: payload[url.pathname] || {} });
    });
    await page.goto('/?view=board', { waitUntil: 'domcontentloaded' });
    await expect(page.locator('.board-card')).toHaveCount(30);
    await expect(page.locator('.board-card-count')).toHaveText('95');
    const lane = page.locator('.board-lane-list');
    await lane.evaluate(element => { element.scrollTop = element.scrollHeight; });
    await expect(page.locator('.board-card')).toHaveCount(60);
    await expect(page.locator('[data-board-action="load-more"]')).toContainText('60 of 95');
    await page.locator('[data-board-search]').fill('Completed task 95');
    await expect(page.locator('.board-card')).toHaveCount(1);
    await expect(page.locator('.board-card')).toContainText('Completed task 95');
    await expect(page.locator('.board-card-count')).toHaveText('95');
    expect(requests.every(url => url.searchParams.get('pageSize') === '30')).toBeTruthy();
    expect(requests.some(url => url.searchParams.get('offset') === '30')).toBeTruthy();
});
