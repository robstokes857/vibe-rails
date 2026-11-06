const { test, expect } = require('@playwright/test');

async function mount(page, { shared = false } = {}) {
    const columns = [{ id: 'todo', name: 'Ready', position: 0, color: '#14b8a6' },
        { id: 'done', name: 'Done', position: 1, color: '#10b981' }];
    const state = {
        cards: Array.from({ length: 36 }, (_, index) => ({
            id: `card-${index}`, key: `VB-${index + 1}`, displayId: `VIBE-${index + 1}`, boardId: 'main',
            title: `Task ${index + 1}`, description: '', columnId: index < 2 ? 'todo' : 'done',
            position: index, type: index === 1 ? 'bug' : 'task', priority: 'medium', tags: []
        })),
        removed: [], failures: new Set(), waitForDelete: null
    };
    await page.addInitScript(() => sessionStorage.setItem('viberails_tab', 'board-fixture'));
    await page.routeWebSocket('**/api/v1/events/ws*', () => {});
    await page.route('**/api/v1/**', async route => {
        const url = new URL(route.request().url());
        if (route.request().method() === 'DELETE') {
            const id = url.pathname.split('/').pop();
            state.removed.push(id);
            if (state.waitForDelete) await state.waitForDelete;
            if (state.failures.has(id)) return route.fulfill({ status: 409, json: { error: 'Card is unavailable. Try again.' } });
            state.cards = state.cards.filter(card => card.id !== id);
            return route.fulfill({ json: { message: 'Card deleted' } });
        }
        if (url.pathname === '/api/v1/board/cards') {
            const type = url.searchParams.get('type');
            const filtered = state.cards.filter(card => !type || card.type === type);
            const laneId = url.searchParams.get('columnId');
            const offset = Number(url.searchParams.get('offset') || 0);
            const lanes = columns.filter(lane => !laneId || lane.id === laneId).map(lane => {
                const cards = filtered.filter(card => card.columnId === lane.id);
                const take = lane.id === 'done' ? 30 : cards.length;
                return { columnId: lane.id, totalCount: state.cards.filter(card => card.columnId === lane.id).length,
                    filteredCount: cards.length, nextOffset: Math.min(offset + take, cards.length),
                    hasMore: offset + take < cards.length, continuationToken: 'fixture' };
            });
            return route.fulfill({ json: {
                cards: lanes.flatMap(lane => filtered.filter(card => card.columnId === lane.columnId).slice(offset, lane.nextOffset)),
                lanes, totalCount: state.cards.length, filteredCount: filtered.length, flaggedCount: 0, blockedCount: 0, assignees: []
            } });
        }
        const payloads = {
            '/api/v1/context': { isInGit: true, rootPath: 'C:/fixture' },
            '/api/v1/board/boards': { boards: [{ id: 'main', name: 'Main', position: 0, shared, columns },
                { id: 'other', name: 'Other', position: 1, columns: [] }] },
            '/api/v1/board/columns': { columns },
            '/api/v1/board/cards/search': { cards: [] }
        };
        return route.fulfill({ json: payloads[url.pathname] || {} });
    });
    await page.goto('/?view=board', { waitUntil: 'domcontentloaded' });
    await expect(page.locator('.board-card')).toHaveCount(32);
    await expect(page.locator('.board-view')).not.toHaveClass(/is-loading/);
    return state;
}

const checkbox = (page, id) => page.locator(`[data-board-card-select="card-${id}"]`);
const selected = page => page.locator('[data-board-selection-status]');
const deleteSelected = page => page.getByRole('button', { name: 'Delete selected', exact: true });

test('keyboard selection, counted confirmation, Escape/cancel, and successful deletion of only selected cards', async ({ page }) => {
    const state = await mount(page);
    await page.getByRole('button', { name: 'Select cards', exact: true }).click();
    await expect(checkbox(page, 0)).toBeFocused();
    await page.keyboard.press('Space');
    await checkbox(page, 2).check();
    await expect(selected(page)).toHaveText('2 selected');
    await deleteSelected(page).click();
    const dialog = page.getByRole('alertdialog');
    await expect(dialog).toContainText('Delete the 2 selected cards?');
    await expect(dialog.getByRole('button', { name: 'Cancel', exact: true })).toBeFocused();
    await page.keyboard.press('Escape');
    await expect(dialog).toHaveCount(0);
    await expect(deleteSelected(page)).toBeFocused();
    expect(state.removed).toEqual([]);
    await expect(checkbox(page, 0)).toBeChecked();
    await deleteSelected(page).click();
    await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
    expect(state.removed).toEqual([]);
    await deleteSelected(page).click();
    await dialog.getByRole('button', { name: 'Delete 2 cards', exact: true }).click();
    await expect(selected(page)).toContainText('2 of 2 cards deleted.');
    await expect(page.locator('[data-card-id="card-0"]')).toHaveCount(0);
    await expect(page.locator('[data-card-id="card-2"]')).toHaveCount(0);
    await expect(page.locator('.board-lane[data-column-id="todo"] .board-lane-head .board-card-count')).toHaveText('1');
    await expect(page.locator('[data-board-stats]')).toContainText('34 cards');
    expect(state.removed).toEqual(['card-0', 'card-2']);
    await expect(deleteSelected(page)).toBeDisabled();
});

test('partial failures retain selected IDs beyond the first page and retry never resends successful deletes', async ({ page }) => {
    const state = await mount(page);
    state.failures.add('card-35');
    await page.getByRole('button', { name: 'Select cards', exact: true }).click();
    await checkbox(page, 0).check();
    await page.getByRole('button', { name: /Load more/ }).click();
    await expect(page.locator('[data-board-card-select]')).toHaveCount(36);
    await expect(selected(page)).toHaveText('1 selected');
    await checkbox(page, 35).check();
    await deleteSelected(page).click();
    await page.getByRole('button', { name: 'Delete 2 cards', exact: true }).click();
    await expect(selected(page)).toContainText('1 could not be deleted and remain selected');
    await expect(page.locator('[data-board-selection-list]')).toContainText('VIBE-36');
    await expect(page.locator('[data-board-selection-list]')).toContainText('Card is unavailable. Try again.');
    await expect(page.locator('[data-board-selection-action="remove"]')).toHaveCount(1);
    expect(state.cards.some(card => card.id === 'card-35')).toBeTruthy();
    state.failures.clear();
    await deleteSelected(page).click();
    await page.getByRole('alertdialog').getByRole('button', { name: 'Delete card', exact: true }).click();
    await expect(selected(page)).toContainText('1 of 1 cards deleted.');
    expect(state.removed).toEqual(['card-0', 'card-35', 'card-35']);
});

test('filters and global search clear selection; hidden lanes retain only explicit, listed selections', async ({ page }) => {
    const state = await mount(page);
    await page.getByRole('button', { name: 'Select cards', exact: true }).click();
    await checkbox(page, 0).check();
    await page.getByRole('button', { name: 'Hide Ready lane', exact: true }).click();
    await expect(selected(page)).toContainText('1 outside the visible lanes');
    await expect(page.locator('[data-board-selection-list]')).toContainText('VIBE-1');
    await page.locator('[data-board-filter-type]').selectOption('bug');
    await expect(selected(page)).toContainText('Selection cleared');
    await expect(page.locator('.board-view')).not.toHaveClass(/is-loading/);
    await page.getByRole('button', { name: 'Show Ready lane', exact: true }).click();
    await page.getByRole('button', { name: 'Select cards', exact: true }).click();
    await expect(selected(page)).toHaveText('0 selected');
    await checkbox(page, 1).check();
    await page.locator('[data-board-search]').fill('Task');
    await expect(page.locator('[data-board-selection-action="start"]')).toBeHidden();
    await expect(page.locator('[data-board-selection-controls]')).toBeHidden();
    expect(state.removed).toEqual([]);
});

test('duplicate submissions are blocked while requests are pending', async ({ page }) => {
    const state = await mount(page);
    let release;
    state.waitForDelete = new Promise(resolve => { release = resolve; });
    await page.getByRole('button', { name: 'Select cards', exact: true }).click();
    await checkbox(page, 0).check();
    await checkbox(page, 1).check();
    await page.locator('[data-board-selection-action="delete"]').evaluate(button => { button.click(); button.click(); });
    await expect(page.getByRole('alertdialog')).toHaveCount(1);
    await page.getByRole('button', { name: 'Delete 2 cards', exact: true }).click();
    await expect.poll(() => state.removed.length).toBe(1);
    await expect(page.locator('[data-board-selection-action="delete"]')).toBeDisabled();
    await expect(checkbox(page, 0)).toBeDisabled();
    release();
    await expect(selected(page)).toContainText('2 of 2 cards deleted.');
    expect(state.removed).toEqual(['card-0', 'card-1']);
});

test('shared boards do not offer deletion selection', async ({ page }) => {
    await mount(page, { shared: true });
    await expect(page.getByRole('button', { name: 'Select cards', exact: true })).toBeHidden();
    await expect(page.locator('[data-board-card-select]')).toHaveCount(0);
});

test('background refresh preserves a focused checkbox and its explicit selection', async ({ page }) => {
    const state = await mount(page);
    await page.getByRole('button', { name: 'Select cards', exact: true }).click();
    await checkbox(page, 0).check();
    await checkbox(page, 0).focus();
    state.cards[0].title = 'Updated task';
    await page.evaluate(() => window.dispatchEvent(new Event('focus')));
    await expect(page.locator('[data-card-id="card-0"] .board-card-title')).toHaveText('Updated task');
    await expect(checkbox(page, 0)).toBeFocused();
    await expect(checkbox(page, 0)).toBeChecked();
    await expect(selected(page)).toHaveText('1 selected');
});

test('switching boards clears selection and collapsed lanes do not receive keyboard focus', async ({ page }) => {
    const state = await mount(page);
    await page.getByRole('button', { name: 'Hide Ready lane', exact: true }).click();
    await page.getByRole('button', { name: 'Select cards', exact: true }).click();
    await expect(checkbox(page, 2)).toBeFocused();
    await page.keyboard.press('Space');
    await page.locator('.board-picker-control .ts-control').click();
    await page.locator('.board-picker-dropdown .option[data-value="other"]').click();
    await expect(page.locator('.board-view')).not.toHaveClass(/is-loading/);
    await expect(page.locator('[data-board-selection]')).toBeHidden();
    await expect(page.locator('[data-board-card-select]')).toHaveCount(0);
    await page.getByRole('button', { name: 'Select cards', exact: true }).click();
    await expect(selected(page)).toHaveText('0 selected');
    expect(state.removed).toEqual([]);
});

test('selection controls remain usable on narrow screens and with reduced motion', async ({ page }, testInfo) => {
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await mount(page);
    await page.getByRole('button', { name: 'Select cards', exact: true }).click();
    await checkbox(page, 0).check();
    for (const width of [320, 375, 768, 1024]) {
        await page.setViewportSize({ width, height: 900 });
        await expect(deleteSelected(page)).toBeInViewport();
        const bounds = await deleteSelected(page).boundingBox();
        expect(bounds.x).toBeGreaterThanOrEqual(0);
        expect(bounds.x + bounds.width).toBeLessThanOrEqual(width);
        expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBeTruthy();
        if (width === 320 || width === 1024)
            await page.screenshot({ path: testInfo.outputPath(`selection-${width}.png`), fullPage: true });
    }
    await page.getByRole('button', { name: 'Cancel selection', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Select cards', exact: true })).toBeFocused();
    await expect(page.locator('[data-board-card-select]')).toHaveCount(0);
});
