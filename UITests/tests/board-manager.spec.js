const { test, expect } = process.env.VIBERAILS_BOARD_STATIC === '1'
    ? require('@playwright/test')
    : require('./fixtures');

// The AI board manager (VIBE-204) against mocked Board routes: hidden while Settings > AI board
// manager is off; with it on, the header chip's states, Start and Stop, the Manager tab's save,
// the tile of a board-managed card, and the Settings switch itself.

const COLUMNS = [
    { id: 'col_ready', name: 'Ready', position: 0, color: '#3b82f6', boardId: 'brd_main' },
    { id: 'col_review', name: 'Review', position: 1, color: '#a855f7', boardId: 'brd_main' }
];

async function openBoard(page, { enabled = true, manager = {} } = {}) {
    if (process.env.VIBERAILS_BOARD_STATIC === '1') {
        await page.addInitScript(() => sessionStorage.setItem('viberails_tab', 'board-manager-fixture'));
    }
    const state = {
        enabled,
        manager: {
            selection: 'base:codex', baseLlmOptions: null, revision: 1, active: null,
            managedCards: 1, ownManagerCards: 0, maxWorkers: 1, instructions: 'Bugs before features.',
            runningWorkers: 0, flaggedCards: 0, ...manager
        }
    };
    const card = {
        id: 'card_managed', key: 'VB-1', displayId: 'VB-1', boardId: 'brd_main', columnId: 'col_ready', position: 0,
        title: 'Board managed card', description: '', type: 'feature', priority: 'high', assignee: 'base:claude',
        points: null, tags: [], blocked: false, flagged: false, commentCount: 0, workflowMode: 'agent', managerSelection: null,
        createdAt: '2026-11-02T06:00:00Z', updatedAt: '2026-11-02T06:00:00Z',
        attachments: [], comments: [], commits: [], sessions: []
    };
    const requests = [];
    const managerResponse = () => state.enabled ? { enabled: true, ...state.manager } : {
        enabled: false, selection: null, baseLlmOptions: null, revision: 0, active: null, managedCards: 0, ownManagerCards: 0
    };
    await page.routeWebSocket('**/api/v1/events/ws*', () => {});
    await page.route('**/api/v1/**', async route => {
        const request = route.request();
        const url = new URL(request.url());
        const path = url.pathname;
        const method = request.method();
        requests.push({ path, method, body: request.postDataJSON() });
        if (path === '/api/v1/board/ai-board-manager') {
            if (method === 'PUT') state.enabled = request.postDataJSON().enabled === true;
            return route.fulfill({ json: { enabled: state.enabled } });
        }
        if (path === '/api/v1/board/boards/brd_main/manager') {
            if (method === 'PUT') {
                const body = request.postDataJSON();
                expect(body.expectedRevision).toBe(state.manager.revision);
                state.manager = { ...state.manager, selection: body.selection || null, baseLlmOptions: body.baseLlmOptions,
                    instructions: body.instructions ?? state.manager.instructions, maxWorkers: body.maxWorkers ?? state.manager.maxWorkers,
                    revision: body.selection ? state.manager.revision + 1 : 0 };
            }
            return route.fulfill({ json: managerResponse() });
        }
        if (path === '/api/v1/board/boards/brd_main/manager/launch') {
            state.manager = { ...state.manager, active: { sessionId: 'mgr-session', tabId: 'mgr-tab', cli: 'codex', selection: 'base:codex', startedAt: '2026-11-02T07:00:00Z' } };
            return route.fulfill({ json: { tabId: 'mgr-tab', sessionId: 'mgr-session', cli: 'codex', boardId: 'brd_main', selection: 'base:codex', reused: false } });
        }
        if (path === '/api/v1/board/boards/brd_main/manager/stop') {
            const stopped = Boolean(state.manager.active);
            state.manager = { ...state.manager, active: null };
            return route.fulfill({ json: { stopped, sessionId: stopped ? 'mgr-session' : null } });
        }
        if (path === '/api/v1/board/cards/activity') {
            return route.fulfill({ json: { cards: [{ id: card.id, activeSessionId: null, activeTabId: null, hasActiveAutomation: false, hasActiveDesktopAgent: false }] } });
        }
        if (path === `/api/v1/board/cards/${card.id}`) return route.fulfill({ json: { ...card, linkedCards: [] } });
        if (path.endsWith('/context')) return route.fulfill({ json: { revision: 1, context: { defaultMessage: '', typeOverrides: [] } } });
        if (path.endsWith('/sync')) return route.fulfill({ json: { boardId: 'brd_main', enabled: false, configured: true } });
        const payloads = {
            '/api/v1/context': { isInGit: true, rootPath: 'C:/board-fixture', launchDirectory: 'C:/board-fixture' },
            '/api/v1/settings': {},
            '/api/v1/environments': { environments: [] },
            '/api/v1/llm-picker/preferences': { items: [
                { key: 'base:codex', kind: 'base', group: 'Base CLIs', label: 'Codex', cli: 'codex', enabled: true, order: 0 },
                { key: 'base:claude', kind: 'base', group: 'Base CLIs', label: 'Claude', cli: 'claude', enabled: true, order: 1 }
            ] },
            '/api/v1/board/boards': { boards: [{ id: 'brd_main', name: 'Main', position: 0, cardCount: 1, columns: COLUMNS }] },
            '/api/v1/board/columns': { columns: COLUMNS },
            '/api/v1/board/cards': { cards: [card] }
        };
        return route.fulfill({ json: payloads[path] || {} });
    });
    await page.goto('/?view=board', { waitUntil: 'domcontentloaded' });
    await expect(page.locator('#app-content [data-view="board"]')).toBeVisible();
    return { state, requests };
}

test('with the AI board manager off, the board shows no manager anywhere', async ({ page }) => {
    const { requests } = await openBoard(page, { enabled: false });
    await expect(page.locator('.board-card')).toHaveCount(1);
    await expect.poll(() => requests.some(request => request.path === '/api/v1/board/boards/brd_main/manager')).toBe(true);
    await expect(page.locator('[data-board-ai-manager]')).toBeHidden();
    await expect(page.locator('.board-card .is-board-manager')).toHaveCount(0);
    await page.getByRole('button', { name: 'Board settings', exact: true }).click();
    await expect(page.getByRole('tab')).toHaveText(['General', 'Jira Cloud', 'Azure DevOps', 'Context', 'History']);
    expect(requests.filter(request => request.path.includes('/manager') && request.method !== 'GET')).toEqual([]);
});

for (const width of [1440, 390]) {
    test(`the manager chip starts and stops the board manager at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 900 });
        const { requests } = await openBoard(page);
        const chip = page.locator('[data-board-ai-manager]');
        await expect(chip).toBeVisible();
        await expect(chip).toContainText('Codex');
        await expect(chip).toContainText('Stopped');
        // The board-managed card names the board manager in its foot.
        await expect(page.locator('.board-card .board-manager-chip.is-board-manager')).toHaveText('Board manager · Codex');

        await chip.getByRole('button', { name: 'Start manager', exact: true }).click();
        // A manager the chip just started reads Thinking, as its tab does (VIBE-262).
        await expect(chip.locator('.board-ai-manager-word')).toHaveText('Thinking');
        await expect(chip.getByRole('button', { name: 'Go to manager', exact: true })).toBeVisible();
        await expect(page.locator('.board-card [data-board-start-work-label]')).toHaveText('Go to manager');
        expect(requests.filter(request => request.method === 'POST' && request.path.endsWith('/manager/launch'))).toHaveLength(1);
        await page.screenshot({ path: testInfo.outputPath(`manager-running-${width}.png`) });

        // Stop asks first; Cancel keeps it running.
        await chip.getByRole('button', { name: 'Stop board manager', exact: true }).click();
        const dialog = page.getByRole('alertdialog');
        await expect(dialog).toContainText('Its terminal closes');
        await dialog.getByRole('button', { name: 'Cancel' }).click();
        await expect(chip.getByRole('button', { name: 'Go to manager', exact: true })).toBeVisible();
        expect(requests.filter(request => request.path.endsWith('/manager/stop'))).toEqual([]);
        await chip.getByRole('button', { name: 'Stop board manager', exact: true }).click();
        await page.getByRole('alertdialog').getByRole('button', { name: 'Stop manager', exact: true }).click();
        await expect(chip).toContainText('Stopped');
        await expect(chip.getByRole('button', { name: 'Start manager', exact: true })).toBeVisible();
        expect(requests.filter(request => request.method === 'POST' && request.path.endsWith('/manager/stop'))).toHaveLength(1);
    });

    test(`"needs you" opens the flagged Agent cards inside the window at ${width}px (VIBE-262)`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 900 });
        await openBoard(page, { manager: {
            active: { sessionId: 'mgr-session', tabId: 'mgr-tab', cli: 'codex', selection: 'base:codex', startedAt: '2026-11-02T07:00:00Z' },
            flaggedCards: 1, flagged: [{ id: 'card_managed', key: 'VB-1', displayId: 'VB-1', title: 'Board managed card', laneName: 'Ready' }]
        } });
        const chip = page.locator('[data-board-ai-manager]');
        const needs = chip.getByRole('button', { name: '1 needs you' });
        await expect(needs).toHaveAttribute('aria-expanded', 'false');
        await needs.click();
        const menu = chip.locator('#board-ai-manager-flagged-menu');
        await expect(menu).toBeVisible();
        await expect(menu).toContainText('Board managed card');
        await expect(menu).toContainText('Ready');
        const box = await menu.boundingBox();
        expect(box.x).toBeGreaterThanOrEqual(0);
        expect(box.x + box.width).toBeLessThanOrEqual(width);
        await page.screenshot({ path: testInfo.outputPath(`manager-flagged-${width}.png`) });

        // Escape closes it and returns focus to the button.
        await page.keyboard.press('Escape');
        await expect(menu).toBeHidden();
        await expect(chip.getByRole('button', { name: '1 needs you' })).toBeFocused();

        // Picking a card opens its editor.
        await chip.getByRole('button', { name: '1 needs you' }).click();
        await menu.getByRole('button', { name: /Board managed card/ }).click();
        await expect(menu).toBeHidden();
        await expect(page.locator('.modal-dialog')).toContainText('Board managed card');
    });
}

test('a manager running in another VibeRails window shows there, with nothing to start or stop (VIBE-262)', async ({ page }) => {
    await openBoard(page, { manager: { elsewhere: { sessionId: 'other-session', cli: 'Claude', selection: 'base:codex', startedAt: '2026-11-02T07:00:00Z' } } });
    const chip = page.locator('[data-board-ai-manager]');
    await expect(chip).toContainText('Running in another window');
    await expect(chip.locator('.is-elsewhere')).toHaveCount(1);
    await expect(chip.getByRole('button')).toHaveCount(0);
});

test('the Manager tab saves the worker limit and standing orders with the revision', async ({ page }) => {
    const { state, requests } = await openBoard(page);
    await page.getByRole('button', { name: 'Board settings', exact: true }).click();
    await expect(page.getByRole('tab')).toHaveText(['General', 'Jira Cloud', 'Azure DevOps', 'Manager', 'Context', 'History']);
    await page.getByRole('tab', { name: 'Manager', exact: true }).click();
    const panel = page.locator('[data-board-manager-settings]');
    await expect(panel.locator('[data-board-manager-status]')).toHaveText('Not running · manages 1 Agent card.');
    const instructions = panel.getByLabel('Manager instructions');
    await expect(instructions).toHaveValue('Bugs before features.');
    await instructions.fill('Bugs before features. Run the tests before Review.');
    await panel.getByLabel('Workers at a time').selectOption('2');
    await panel.getByRole('button', { name: 'Save manager', exact: true }).click();
    await expect(page.locator('.vr-toast-body').filter({ hasText: 'Board manager saved.' })).toBeVisible();
    const put = requests.filter(request => request.method === 'PUT' && request.path === '/api/v1/board/boards/brd_main/manager').at(-1);
    expect(put.body).toMatchObject({ selection: 'base:codex', instructions: 'Bugs before features. Run the tests before Review.', maxWorkers: 2, expectedRevision: 1 });
    expect(state.manager.revision).toBe(2);
    await expect(panel.getByLabel('Workers at a time')).toHaveValue('2');
});

test('saved empty board-manager options never re-inherit a YOLO-on default', async ({ page }) => {
    const { state } = await openBoard(page, { manager: { baseLlmOptions: { yolo: true } } });
    let defaultReads = 0;
    await page.route('**/api/v1/llm-picker/defaults/*', route => {
        defaultReads++;
        return route.fulfill({ json: { yolo: true } });
    });
    await page.route('**/api/v1/board/boards/brd_main/manager', route => {
        if (route.request().method() === 'PUT') {
            const body = route.request().postDataJSON();
            expect(body.baseLlmOptions).toEqual({ model: '', effort: '', mode: '', yolo: false, speed: '' });
            state.manager = { ...state.manager, ...body, baseLlmOptions: null, revision: state.manager.revision + 1 };
        }
        return route.fulfill({ json: { enabled: true, ...state.manager } });
    });
    const open = async () => {
        await page.getByRole('button', { name: 'Board settings', exact: true }).click();
        await page.getByRole('tab', { name: 'Manager', exact: true }).click();
    };
    await open();
    const yolo = page.locator('[data-board-manager-settings] [data-board-launch-yolo]');
    await yolo.uncheck();
    await page.getByRole('button', { name: 'Save manager', exact: true }).click();
    await expect.poll(() => state.manager.baseLlmOptions).toBeNull();
    await expect(yolo).not.toBeChecked();
    await page.evaluate(() => window.app.closeModal());
    await open();
    await expect(yolo).not.toBeChecked();
    expect(defaultReads).toBe(0);
});

test('the chip offers set up when the board has no manager, and opens the Manager tab', async ({ page }) => {
    await openBoard(page, { manager: { selection: null, revision: 0, instructions: null } });
    const chip = page.locator('[data-board-ai-manager]');
    await chip.getByRole('button', { name: 'Set up board manager' }).click();
    await expect(page.getByRole('tab', { name: 'Manager', exact: true })).toHaveAttribute('aria-selected', 'true');
    await expect(page.locator('[data-board-manager-status]')).toContainText('No board manager yet');
    await expect(page.getByRole('button', { name: 'Remove manager', exact: true })).toBeDisabled();
});

test('Settings > AI board manager saves at once and the board follows', async ({ page }) => {
    const { state, requests } = await openBoard(page, { enabled: false });
    // Signed out, Settings is the text link in the top navigation.
    await page.locator('[data-settings-nav]').click();
    await page.getByRole('tab', { name: 'AI board manager', exact: true }).click();
    const toggle = page.getByRole('switch', { name: 'AI board manager' });
    await expect(toggle).toBeEnabled();
    await expect(toggle).not.toBeChecked();
    await expect(page.locator('[data-ai-board-manager-status]')).toHaveText('The AI board manager is off.');
    await toggle.check();
    await expect(page.locator('[data-ai-board-manager-status]')).toHaveText(/^Saved: The AI board manager is on/);
    expect(state.enabled).toBe(true);
    expect(requests.filter(request => request.method === 'PUT' && request.path === '/api/v1/board/ai-board-manager').map(request => request.body)).toEqual([{ enabled: true }]);
    await page.getByRole('navigation').getByRole('button', { name: /^Board(?: —.*)?$/ }).click();
    await expect(page.locator('[data-board-ai-manager]')).toContainText('Codex');
});
