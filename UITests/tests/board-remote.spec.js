const { test, expect } = require('@playwright/test');

async function setup(page) {
    const row = (id, name, isOwner = true, localCopies = []) => ({ id, name, isOwner, localCopies, lanes: ['Backlog', 'Done'], url: 'https://viberails.ai/boards/' + id });
    const state = {
        rows: [row('linked', 'Current work', true, [{ projectName: 'vibe-rails', syncEnabled: true }]),
            row('old', 'Old unused board'), row('shared', '<img src=x onerror=alert(1)>', false)],
        context: 'account-a', writes: [], offsets: [], nextOffset: null, fail: false, blockRead: null
    };
    await page.addInitScript(() => sessionStorage.setItem('viberails_tab', 'board-fixture'));
    await page.routeWebSocket('**/api/v1/events/ws*', () => {});
    await page.route('**/api/v1/**', async route => {
        const request = route.request(), url = new URL(request.url());
        if (url.pathname.startsWith('/api/v1/board/remote')) {
            if (request.method() === 'GET') {
                state.offsets.push(url.searchParams.get('offset'));
                if (state.blockRead) await state.blockRead;
                return route.fulfill({ json: { context: state.context, boards: url.searchParams.has('offset') ? [row('extra', 'Another old board')] : state.rows, nextOffset: url.searchParams.has('offset') ? null : state.nextOffset } });
            }
            const body = request.postDataJSON();
            state.writes.push({ path: url.pathname, method: request.method(), body });
            if (state.fail) return route.fulfill({ status: 400, json: { error: 'Temporary failure. Local copies are kept.' } });
            if (body.context !== state.context) return route.fulfill({ status: 400, json: { error: 'The account changed. Refresh remote boards before continuing.' } });
            const id = url.pathname.split('/')[5];
            if (url.pathname.endsWith('/delete')) state.rows = state.rows.filter(item => item.id !== id);
            else if (request.method() === 'PUT') state.rows.find(item => item.id === id).name = body.name;
            else state.rows.push(row('created', body.name));
            return route.fulfill({ json: { message: 'Remote change saved.' } });
        }
        const payloads = {
            '/api/v1/context': { isInGit: true, rootPath: 'C:/fixture', launchDirectory: 'C:/fixture' },
            '/api/v1/settings': {}, '/api/v1/environments': { environments: [] },
            '/api/v1/llm-picker/preferences': { items: [] },
            '/api/v1/board/boards': { boards: [{ id: 'local', name: 'Local', position: 0 }] },
            '/api/v1/board/columns': { columns: [{ id: 'lane', boardId: 'local', name: 'Backlog', position: 0 }] },
            '/api/v1/board/cards': { cards: [] }
        };
        return route.fulfill({ json: payloads[url.pathname] || {} });
    });
    await page.goto('/?view=board');
    await page.getByRole('button', { name: 'Remote boards', exact: true }).click();
    await expect(page.locator('[data-remote-row]')).toHaveCount(3);
    return state;
}

for (const width of [1440, 390]) {
    test(`Remote manager filters, escapes metadata and supports CRUD at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 900 });
        const state = await setup(page);
        const manager = page.getByRole('dialog', { name: 'Remote boards', exact: true });
        const shared = manager.locator('[data-remote-row="shared"]');
        await expect(shared.locator('img')).toHaveCount(0);
        await expect(shared.getByRole('button')).toHaveCount(0);
        await expect(shared.getByRole('link', { name: 'Open board' })).toHaveAttribute('rel', 'noopener noreferrer');
        await manager.getByLabel('Filter remote boards').selectOption('unlinked');
        await expect(manager.locator('[data-remote-row]')).toHaveCount(2);
        await manager.getByLabel('Search loaded remote boards').fill('unused');
        await expect(manager.locator('[data-remote-row]')).toHaveCount(1);
        await manager.getByLabel('Search loaded remote boards').fill('');
        await manager.getByLabel('Filter remote boards').selectOption('all');
        expect(await manager.evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true);
        await page.screenshot({ path: testInfo.outputPath(`remote-boards-${width}.png`) });

        await manager.locator('[data-remote-row="old"]').getByRole('button', { name: 'Rename', exact: true }).click();
        await manager.getByLabel('Rename remote board').fill('Renamed archive');
        await manager.getByRole('button', { name: 'Save', exact: true }).click();
        await expect(manager.locator('[data-remote-row="old"]')).toContainText('Renamed archive');
        await manager.getByRole('button', { name: 'New remote board', exact: true }).click();
        await manager.getByLabel('New remote board name').fill('New project');
        await manager.getByRole('button', { name: 'Save', exact: true }).click();
        await expect(manager.locator('[data-remote-row="created"]')).toContainText('New project');
        await manager.locator('[data-remote-row="old"]').getByRole('button', { name: 'Delete', exact: true }).click();
        await expect(page.getByRole('alertdialog')).toContainText('Local copies on this computer are kept');
        await page.getByRole('alertdialog').getByRole('button', { name: 'Cancel' }).click();
        expect(state.writes).toHaveLength(2);
        await manager.locator('[data-remote-row="old"]').getByRole('button', { name: 'Delete', exact: true }).click();
        await page.getByRole('alertdialog').getByRole('button', { name: 'Delete remote board', exact: true }).click();
        await expect(manager.locator('[data-remote-row="old"]')).toHaveCount(0);
        expect(state.writes.map(w => w.method)).toEqual(['PUT', 'POST', 'POST']);
    });
}

test('Create retries keep the draft and request identity; account changes reject stale drafts', async ({ page }) => {
    const state = await setup(page);
    const manager = page.getByRole('dialog', { name: 'Remote boards', exact: true });
    await manager.getByRole('button', { name: 'New remote board', exact: true }).click();
    await manager.getByLabel('New remote board name').fill('Retained draft');
    state.fail = true;
    await manager.getByRole('button', { name: 'Save', exact: true }).click();
    await expect(manager.locator('[data-remote-status]')).toContainText('Temporary failure');
    await expect(manager.getByLabel('New remote board name')).toHaveValue('Retained draft');
    await expect(manager.getByRole('button', { name: 'New remote board', exact: true })).toBeDisabled();
    state.fail = false;
    state.context = 'account-b';
    await manager.getByRole('button', { name: 'Refresh', exact: true }).click();
    await expect(manager.locator('[data-remote-status]')).toContainText('3 remote boards loaded');
    await manager.getByRole('button', { name: 'Save', exact: true }).click();
    await expect(manager.locator('[data-remote-status]')).toContainText('account changed');
    expect(state.writes[0].body.requestId).toMatch(/^[a-f0-9]{32}$/);
    expect(state.writes[1].body).toEqual(state.writes[0].body);
    await manager.getByRole('button', { name: 'Cancel', exact: true }).click();
    await expect(manager.locator('[data-remote-editor]')).toBeHidden();
});

test('Pagination adds older boards and delete failures leave them visible', async ({ page }) => {
    const state = await setup(page);
    const manager = page.getByRole('dialog', { name: 'Remote boards', exact: true });
    state.nextOffset = 100;
    await manager.getByRole('button', { name: 'Refresh', exact: true }).click();
    await manager.getByRole('button', { name: 'Load more boards' }).click();
    await expect(manager.locator('[data-remote-row]')).toHaveCount(4);
    expect(state.offsets).toContain('100');
    await expect(manager.getByRole('button', { name: 'Load more boards' })).toBeHidden();
    state.fail = true;
    await manager.locator('[data-remote-row="old"]').getByRole('button', { name: 'Delete', exact: true }).click();
    await page.getByRole('alertdialog').getByRole('button', { name: 'Delete remote board', exact: true }).click();
    await expect(manager.locator('[data-remote-status]')).toContainText('Temporary failure');
    await expect(manager.locator('[data-remote-row="old"]')).toBeVisible();
});

test('Closing a manager cancels stale reads without reopening the modal', async ({ page }) => {
    const state = await setup(page);
    let release;
    state.blockRead = new Promise(resolve => { release = resolve; });
    const manager = page.getByRole('dialog', { name: 'Remote boards', exact: true });
    await manager.getByRole('button', { name: 'Refresh', exact: true }).click();
    await expect(manager.getByRole('button', { name: 'Refresh', exact: true })).toBeDisabled();
    await page.keyboard.press('Escape');
    release();
    await expect(manager).toHaveCount(0);
    state.blockRead = null;
    await page.getByRole('button', { name: 'Remote boards', exact: true }).click();
    await expect(page.locator('[data-remote-row]')).toHaveCount(3);
});
