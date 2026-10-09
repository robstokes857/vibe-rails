const { test, expect } = require('@playwright/test');

async function openBoards(page) {
    const state = {
        boards: [
            { id: 'alpha', name: 'Alpha', position: 0 },
            { id: 'jira', name: 'Jira · SCRUM', position: 1, isJiraBoard: true },
            { id: 'gamma', name: 'Gamma', position: 2 }
        ],
        writes: [],
        rejectSave: false
    };
    await page.addInitScript(() => {
        sessionStorage.setItem('viberails_tab', 'board-fixture');
        if (!localStorage.getItem('viberails.board.selected.v1')) {
            localStorage.setItem('viberails.board.selected.v1', 'gamma');
        }
    });
    await page.routeWebSocket('**/api/v1/events/ws*', () => {});
    await page.route('**/api/v1/**', route => {
        const url = new URL(route.request().url());
        const boardId = url.searchParams.get('boardId') || state.boards[0].id;
        if (url.pathname === '/api/v1/board/boards/order') {
            const { orderedIds } = route.request().postDataJSON();
            state.writes.push(orderedIds);
            if (state.rejectSave) return route.fulfill({ status: 400, json: { error: 'The board list changed. Reload boards and try again.' } });
            state.boards = orderedIds.map((id, position) => ({ ...state.boards.find(board => board.id === id), position }));
            return route.fulfill({ json: { boards: state.boards } });
        }
        const payloads = {
            '/api/v1/context': { isInGit: true, rootPath: 'C:/board-picker-fixture', launchDirectory: 'C:/board-picker-fixture' },
            '/api/v1/settings': {},
            '/api/v1/environments': { environments: [] },
            '/api/v1/llm-picker/preferences': { items: [] },
            '/api/v1/board/boards': { boards: state.boards },
            '/api/v1/board/columns': { columns: [{ id: `${boardId}-lane`, boardId, name: 'Backlog', position: 0, color: '#3b82f6' }] },
            '/api/v1/board/cards': { cards: [] }
        };
        return route.fulfill({ json: payloads[url.pathname] || {} });
    });
    await page.goto('/?view=board');
    await expect(page.locator('.board-picker-control')).toBeVisible();
    return state;
}

async function openManager(page) {
    await page.locator('.board-picker-control .ts-control').click();
    await page.getByRole('button', { name: 'Manage boards', exact: true }).click();
    await expect(page.locator('[data-board-order-id]').first()).toBeVisible();
    return page.getByRole('dialog', { name: 'Manage boards', exact: true });
}

for (const width of [1440, 390]) {
    test(`Board Tom Select searches, manages order and loads the top board at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 900 });
        const state = await openBoards(page);
        const select = page.locator('[data-board-select]');
        await expect(select).toHaveValue('alpha');
        await expect(page.locator('.board-picker-control .item img')).toHaveAttribute('src', 'assets/img/logo.png');
        expect((await page.locator('.board-picker-control').boundingBox()).width).toBeGreaterThanOrEqual(130);
        await page.locator('.board-picker-control .ts-control').click();
        await expect(page.locator('.board-picker-dropdown .option')).toHaveText(['Alpha', 'Jira · SCRUM', 'Gamma']);
        await expect(page.locator('.board-picker-dropdown [data-value="jira"] img')).toHaveAttribute('src', 'assets/img/jira.svg');
        await expect(page.locator('.board-picker-dropdown [data-value="alpha"] img')).toHaveAttribute('alt', 'VibeRails board');
        await expect.poll(() => page.locator('.board-picker-dropdown img').evaluateAll(images => images.every(image => image.complete && image.naturalWidth > 0))).toBe(true);
        const search = page.getByPlaceholder('Search boards...', { exact: true });
        await search.fill('SCRUM');
        await page.evaluate(() => window.app.boardController.refreshBoardSnapshot());
        await expect(search).toHaveValue('SCRUM');
        await expect(page.locator('.board-picker-dropdown .option')).toHaveText(['Jira · SCRUM']);
        await page.locator('.board-picker-dropdown .option[data-value="jira"]').click();
        await expect(select).toHaveValue('jira');
        await expect(page.locator('.board-picker-control .item img')).toHaveAttribute('alt', 'Jira board');

        const manager = await openManager(page);
        await manager.getByRole('button', { name: 'Move Gamma up', exact: true }).click();
        await manager.getByRole('button', { name: 'Move Gamma up', exact: true }).click();
        await expect(manager.locator('[data-board-order-id]').first()).toContainText('Gamma');
        await expect(manager.locator('[data-board-order-id]').first()).toContainText('Default');
        await expect(manager.getByRole('button', { name: 'Move Gamma up', exact: true })).toBeDisabled();
        await page.evaluate(() => window.app.boardController.refreshBoardSnapshot());
        await expect(manager.locator('[data-board-order-id]').first()).toContainText('Gamma');
        expect(await manager.evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true);
        await page.screenshot({ path: testInfo.outputPath(`manage-boards-${width}.png`) });
        await manager.getByRole('button', { name: 'Save order', exact: true }).click();
        await expect(manager).toHaveCount(0);
        expect(state.writes).toEqual([['gamma', 'alpha', 'jira']]);
        await expect(select).toHaveValue('jira');

        await page.reload();
        await page.getByRole('navigation').getByRole('button', { name: /^Board(?: —.*)?$/ }).click();
        await expect(select).toHaveValue('gamma');
        await page.locator('.board-picker-control .ts-control').click();
        await expect(page.locator('.board-picker-dropdown .option')).toHaveText(['Gamma', 'Alpha', 'Jira · SCRUM']);
        await page.screenshot({ path: testInfo.outputPath(`board-picker-${width}.png`) });
        await page.evaluate(() => window.app.navigate('settings'));
        await expect(page.locator('.board-picker-dropdown')).toHaveCount(0);
        await page.evaluate(() => window.app.navigate('board'));
        await expect(page.locator('.board-picker-control')).toHaveCount(1);
        await expect(select).toHaveValue('gamma');
    });
}

test('Board logo refresh follows connection changes while preserving an open search', async ({ page }) => {
    const state = await openBoards(page);
    state.boards[0].name = '<img src=x onerror=alert(1)> Jira';
    await page.evaluate(() => window.app.boardController.refreshBoardSnapshot());
    await expect(page.locator('.board-picker-control .item img')).toHaveCount(1);
    await expect(page.locator('.board-picker-control .item')).toHaveText(state.boards[0].name);
    await page.locator('.board-picker-control .ts-control').click();
    const search = () => page.getByPlaceholder('Search boards...', { exact: true });
    await search().fill('SCRUM');
    state.boards[1].isJiraBoard = false;
    await page.evaluate(() => window.app.boardController.refreshBoardSnapshot());
    await expect(search()).toBeVisible();
    await expect(search()).toHaveValue('SCRUM');
    await expect(page.locator('.board-picker-dropdown .option img')).toHaveAttribute('src', 'assets/img/logo.png');
    await page.locator('.board-picker-dropdown .option').click();
    await expect(page.locator('.board-picker-control .item img')).toHaveAttribute('alt', 'VibeRails board');
    state.boards[1].isJiraBoard = true;
    await page.evaluate(() => window.app.boardController.refreshBoardSnapshot());
    await expect(page.locator('.board-picker-control .item img')).toHaveAttribute('src', 'assets/img/jira.svg');
});

test('Board order errors preserve edits and reload recovers a changed catalog', async ({ page }) => {
    const state = await openBoards(page);
    const manager = await openManager(page);
    await manager.getByRole('button', { name: 'Move Gamma up', exact: true }).click();
    state.rejectSave = true;
    await manager.getByRole('button', { name: 'Save order', exact: true }).click();
    await expect(manager.locator('[data-board-order-status]')).toContainText('Reload boards');
    expect(await manager.locator('[data-board-order-id]').evaluateAll(rows => rows.map(row => row.dataset.boardOrderId)))
        .toEqual(['alpha', 'gamma', 'jira']);
    state.boards.push({ id: 'delta', name: '<img src=x onerror=alert(1)>', position: 3 });
    state.rejectSave = false;
    await manager.getByRole('button', { name: 'Reload boards', exact: true }).click();
    await expect(manager.locator('[data-board-order-id]')).toHaveCount(4);
    await expect(manager.locator('[data-board-order-id="delta"] img')).toHaveCount(0);
    await manager.getByRole('button', { name: 'Save order', exact: true }).click();
    await expect(manager).toHaveCount(0);
    expect(state.writes.at(-1)).toEqual(['alpha', 'jira', 'gamma', 'delta']);
});

test('Board picker keyboard can reach Manage and cancel without changing the order', async ({ page }) => {
    const state = await openBoards(page);
    await page.locator('.board-picker-control .ts-control').click();
    const search = page.getByPlaceholder('Search boards...', { exact: true });
    await search.focus();
    await page.keyboard.press('Tab');
    await expect(page.getByRole('button', { name: 'Manage boards', exact: true })).toBeFocused();
    await page.keyboard.press('Enter');
    const manager = page.getByRole('dialog', { name: 'Manage boards', exact: true });
    await manager.getByRole('button', { name: 'Move Gamma up', exact: true }).click();
    await page.keyboard.press('Escape');
    await expect(manager).toHaveCount(0);
    expect(state.writes).toEqual([]);
    await expect(page.locator('[data-board-select]')).toHaveValue('alpha');
});
