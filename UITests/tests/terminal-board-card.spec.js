const { test, expect } = require('@playwright/test');

// Real frontend and xterm with read-only HTTP/WebSocket fixtures; no CLI, snapshots or database.
async function openTerminal(page) {
    const tab = { tabId: 'ordinary', sessionId: 'ordinary-session', cli: 'codex', hasActiveSession: true,
        workingDirectory: 'C:/fixture', createdUTC: '2026-10-19T06:00:00Z' };
    const tabs = new Map([[tab.tabId, tab]]);
    const connections = [];
    await page.addInitScript(() => {
        sessionStorage.setItem('viberails_tab', 'story-fixture');
        sessionStorage.setItem('viberails_terminal_active_tab_id', 'ordinary');
    });
    await page.routeWebSocket('**/api/v1/events/ws*', () => {});
    await page.routeWebSocket('**/api/v1/terminal/tabs/*/ws*', socket => {
        connections.push(new URL(socket.url()).pathname.split('/')[5]);
        socket.send(Buffer.from('Discussion output stays in this terminal.\r\n'));
    });
    await page.route('**/api/v1/**', route => {
        const path = new URL(route.request().url()).pathname;
        if (path === '/api/v1/terminal/tabs/ordinary/status')
            return route.fulfill({ json: tab });
        const responses = {
            '/api/v1/context': { isInGit: true, rootPath: 'C:/fixture', launchDirectory: 'C:/fixture' },
            '/api/v1/settings': {}, '/api/v1/environments': { environments: [] },
            '/api/v1/agents': { agents: [] }, '/api/v1/sandboxes': { sandboxes: [] },
            '/api/v1/llm-picker/preferences': { items: [] },
            '/api/v1/terminal/tabs': { tabs: [...tabs.values()], maxTabs: 100 }
        };
        return route.fulfill({ json: responses[path] || {} });
    });
    await page.goto('/');
    await expect.poll(() => page.evaluate(() =>
        window.app?.terminalController?.manager?.getActiveTab()?.instance?.hasOpenSocket()
    )).toBe(true);
    await expect.poll(() => page.evaluate(() =>
        window.app.terminalController.manager.getActiveTab().instance._initialConnectActive
    )).toBe(false);
    return { tabs, connections };
}

for (const width of [1280, 390]) {
    test(`terminal story overlay preserves the terminal and links open chats in the footer at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 850 });
        const fixture = await openTerminal(page);
        const column = { id: 'ready', boardId: 'board', name: 'Ready', position: 0 };
        const card = { id: 'story', key: 'VB-ABCDE-164', displayId: 'VIBE-164', boardId: 'board', columnId: 'ready',
            title: 'Performance and memory review', description: 'Discuss the optimization list.',
            type: 'task', assignee: 'base:codex', workflowMode: 'manual', attachments: [], comments: [], commits: [],
            sessions: [
                { id: 'ordinary-session', tabId: 'ordinary', cli: 'codex', origin: 'chat', active: true,
                    selection: 'base:codex', displayName: 'Performance discussion', createdAt: '2026-10-19T06:00:00Z' },
                { id: 'other-session', tabId: 'other', cli: 'claude', origin: 'chat', active: true,
                    displayName: 'Second discussion', createdAt: '2026-10-19T06:00:00Z' },
                { id: 'automation-session', tabId: 'automation-1', cli: 'shell', isAutomation: true, active: true,
                    displayName: 'Lane automation', createdAt: '2026-10-19T06:00:00Z' },
                { id: 'ended-session', cli: 'codex', active: false, displayName: 'Previous discussion' },
                { id: 'no-tab', cli: 'codex', origin: 'chat', active: true, displayName: 'Another root' }
            ] };
        fixture.tabs.get('ordinary').boardCard = card;
        const writes = [];
        await page.route('**/api/v1/board/**', route => {
            const path = new URL(route.request().url()).pathname;
            if (path === '/api/v1/board/boards') return route.fulfill({ json: { boards: [
                { id: 'default-board', name: 'Default board', position: 0, columns: [] },
                { id: 'board', name: 'VibeRails', position: 1, columns: [column] }
            ] } });
            if (path === '/api/v1/board/columns') return route.fulfill({ json: { columns: [column] } });
            if (path === '/api/v1/board/cards') return route.fulfill({ json: { cards: [card] } });
            if (path === '/api/v1/board/cards/story') {
                if (route.request().method() === 'PUT') {
                    const patch = route.request().postDataJSON();
                    writes.push(patch);
                    Object.assign(card, patch);
                }
                return route.fulfill({ json: card });
            }
            return route.fulfill({ json: {} });
        });
        await page.evaluate(async () => {
            window.overlayTerminal = window.app.terminalController.manager;
            await overlayTerminal.refreshAutomationTabs();
        });
        const storyLink = page.locator('#terminal-card-link-btn');
        await expect(storyLink).toHaveText('VIBE-164');
        await storyLink.click();
        const editor = page.locator('[data-board-card-editor]');
        await expect(editor).toBeVisible();
        await expect(editor).toHaveCSS('display', 'flex');
        const boardLink = page.getByRole('button', { name: 'Open on Board', exact: true });
        await expect(boardLink).toBeVisible();
        await expect(editor.locator('#board-card-lane')).toHaveValue('ready');
        await expect(page.locator('#app-content [data-view="board"]')).toHaveCount(0);
        await expect(editor.locator('[data-board-start-work]')).toHaveText('Start work');
        const footer = editor.locator('[data-board-open-terminals]');
        await expect(footer.locator('button')).toHaveCount(2);
        await expect(footer).toContainText('Codex');
        await expect(footer).toContainText('Claude');
        await expect(footer.locator('[data-board-focus-terminal="automation-session"]')).toHaveCount(0);
        await page.getByRole('button', { name: 'Close dialog', exact: true }).click();
        await expect(editor).toHaveCount(0);
        expect(await page.evaluate(() => window.app.terminalController.manager === overlayTerminal)).toBe(true);
        await storyLink.click();
        await expect(editor).toBeVisible();
        await editor.locator('#board-card-title').fill('My draft title');
        card.sessions[1].active = false;
        await page.evaluate(() => window.app.boardController.refreshSessionActivity());
        await expect(footer.locator('button')).toHaveCount(1);
        await expect(editor.locator('#board-card-title')).toHaveValue('My draft title');
        await boardLink.click();
        await expect(editor.locator('#board-card-title')).toHaveValue('My draft title');
        await expect(page.getByText('Save your changes and post or clear any draft comment before opening the Board.', { exact: true })).toBeVisible();
        await page.locator('.notAToastclsBtn').click();
        await expect(page.locator('.notAToastclsBtn')).toHaveCount(0);
        expect(await page.evaluate(() => window.app.currentView)).toBe('terminal-focus');
        expect(writes).toEqual([]);
        await editor.locator('[data-board-composer="comment"] [data-board-composer-input]').fill('Unposted comment');
        await editor.locator('#board-card-title').fill(card.title);
        await boardLink.click();
        await expect(editor.locator('[data-board-composer="comment"] [data-board-composer-input]')).toHaveValue('Unposted comment');
        expect(await page.evaluate(() => window.app.currentView)).toBe('terminal-focus');
        await page.locator('.notAToastclsBtn').click();
        await expect(page.locator('.notAToastclsBtn')).toHaveCount(0);
        await editor.locator('[data-board-composer="comment"] [data-board-composer-input]').fill('');
        await editor.locator('#board-card-title').fill('My draft title');
        const actions = await editor.locator('.board-editor-actions').boundingBox();
        expect(actions.y + actions.height).toBeLessThanOrEqual(850);
        expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(width);
        await page.screenshot({ path: testInfo.outputPath(`terminal-story-overlay-${width}.png`) });
        await editor.locator('[data-board-save-card]').click();
        await expect(editor).toHaveCount(0);
        expect(writes).toEqual([{ title: 'My draft title' }]);
        await storyLink.click();
        await footer.getByRole('button', { name: 'Open terminal Performance discussion', exact: true }).focus();
        await page.keyboard.press('Enter');
        await expect(editor).toHaveCount(0);
        expect(await page.evaluate(() => ({ same: window.app.terminalController.manager === overlayTerminal,
            view: window.app.currentView, tab: overlayTerminal.activeTabId,
            listeners: window.app.boardController._activityDisposers.length })))
            .toEqual({ same: true, view: 'terminal-focus', tab: 'ordinary', listeners: 0 });
        expect(fixture.connections).toEqual(['ordinary']);

        await storyLink.click();
        await boardLink.focus();
        await page.keyboard.press('Enter');
        await expect(editor).toHaveCount(0);
        await expect(page.locator('#app-content [data-view="board"]')).toBeVisible();
        await expect(page.locator('[data-board-select]')).toHaveValue('board');
        await expect(page.locator('.board-card[data-card-id="story"]')).toBeVisible();
        expect(await page.evaluate(() => window.app.navigationStack.at(-1).data)).toEqual({});
        await page.locator('.board-card[data-card-id="story"]').click();
        await expect(editor).toBeVisible();
        await expect(boardLink).toHaveCount(0);
    });
}

