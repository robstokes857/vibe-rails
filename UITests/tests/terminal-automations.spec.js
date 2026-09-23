const { test, expect } = require('@playwright/test');

const button = '#vb-terminal-automations-btn';
const count = '#vb-terminal-automations-count';
const menu = '#vb-terminal-automations-menu';
const normalTabs = '#vb-terminal-tab-list .vb-terminal-tab-item:visible';

async function openFixture(page, total = 35) {
    const normal = { tabId: 'ordinary', sessionId: 'ordinary-session', cli: 'codex',
        hasActiveSession: true, createdUTC: '2026-09-23T09:00:00Z', workingDirectory: 'C:/fixture' };
    const tabs = new Map([[normal.tabId, normal]]);
    for (let i = 1; i <= total; i++) {
        const tab = { tabId: `automation-${i}`, sessionId: `00000000-0000-4000-8000-${String(i).padStart(12, '0')}`,
            cli: 'shell', hasActiveSession: i % 2 === 1, createdUTC: new Date(Date.UTC(2026, 8, 23, 10, i)).toISOString(),
            workingDirectory: 'C:/fixture', jobRunId: `run-${i}`, automationName: `Nightly workflow ${i}`, statusAvailable: true };
        tabs.set(tab.tabId, tab);
    }
    const connections = [];
    const deleted = [];
    let eventSocket;
    await page.addInitScript(() => {
        sessionStorage.setItem('viberails_tab', 'automation-fixture');
        if (!sessionStorage.getItem('viberails_terminal_active_tab_id'))
            sessionStorage.setItem('viberails_terminal_active_tab_id', 'ordinary');
    });
    await page.routeWebSocket('**/api/v1/events/ws*', socket => { eventSocket = socket; });
    await page.routeWebSocket('**/api/v1/terminal/tabs/*/ws*', socket => {
        const tabId = new URL(socket.url()).pathname.split('/')[5];
        connections.push(tabId);
        socket.send(Buffer.from(`Output from ${tabId}\r\n`));
    });
    await page.route('**/api/v1/**', route => {
        const path = new URL(route.request().url()).pathname;
        const match = path.match(/^\/api\/v1\/terminal\/tabs\/([^/]+)(?:\/status)?$/);
        if (match) {
            if (route.request().method() === 'DELETE') {
                deleted.push(match[1]);
                tabs.delete(match[1]);
                return route.fulfill({ json: { success: true } });
            }
            const tab = tabs.get(match[1]);
            return route.fulfill({ status: tab ? 200 : 404, json: tab ? {
                hasActiveSession: tab.hasActiveSession, sessionId: tab.hasActiveSession ? tab.sessionId : null,
                cli: tab.cli, workingDirectory: tab.workingDirectory
            } : {} });
        }
        if (path.endsWith('/terminal-replay')) {
            return route.fulfill({ json: { initialCols: 80, initialRows: 24,
                frames: [{ data: Buffer.from('Completed workflow output\r\n').toString('base64'), delayMs: 0 }] } });
        }
        const responses = {
            '/api/v1/context': { isInGit: true, rootPath: 'C:/fixture', launchDirectory: 'C:/fixture' },
            '/api/v1/settings': {},
            '/api/v1/environments': { environments: [] },
            '/api/v1/agents': { agents: [] },
            '/api/v1/sandboxes': { sandboxes: [] },
            '/api/v1/llm-picker/preferences': { items: [] },
            '/api/v1/terminal/tabs': { tabs: [...tabs.values()], maxTabs: 100 }
        };
        return route.fulfill({ json: responses[path] || {} });
    });
    await page.goto('/');
    await expect(page.locator('#loading-overlay')).toHaveClass(/\bd-none\b/);
    await expect(page.locator(count)).toHaveText(String(total));
    await expect.poll(() => page.evaluate(() =>
        window.app?.terminalController?.manager?.getActiveTab()?.instance?.hasOpenSocket()
    )).toBe(true);
    await expect.poll(() => page.evaluate(() =>
        window.app.terminalController.manager.getActiveTab().instance._initialConnectActive
    )).toBe(false);
    // The terminal's documented initial focus retry runs180ms after connect.
    await page.waitForTimeout(220);
    return { tabs, connections, deleted, emit: (type, payload) => eventSocket.send(JSON.stringify({ type, payload })) };
}

test('many Automations stay in a scrollable robot menu and attach only when selected', async ({ page }, testInfo) => {
    const fixture = await openFixture(page);
    await expect(page.locator(normalTabs)).toHaveCount(1);
    await expect(page.locator('.xterm-helper-textarea')).toHaveCount(1);
    expect(fixture.connections).toEqual(['ordinary']);
    await expect(page.locator(`${button} .fa-robot`)).toBeVisible();
    await page.locator(button).click();
    await expect(page.locator(menu)).toBeVisible();
    await expect(page.locator(`${menu} .vb-terminal-automation-row`)).toHaveCount(35);
    expect(await page.locator('.vb-terminal-automations-list').evaluate(list => list.scrollHeight > list.clientHeight)).toBe(true);
    await page.screenshot({ path: testInfo.outputPath('automation-menu.png') });
    await page.locator('[data-automation-open="automation-1"]').click();
    await expect.poll(() => page.evaluate(() => window.app.terminalController.manager.activeTabId)).toBe('automation-1');
    await expect.poll(() => fixture.connections.includes('automation-1')).toBe(true);
    await expect(page.locator('#vb-terminal-window-title')).toContainText('Nightly workflow 1');
    await expect(page.locator(normalTabs)).toHaveCount(1);
    expect(fixture.connections.filter(id => id !== 'ordinary')).toEqual(['automation-1']);
});

test('completed Automations replay without connecting a terminal host', async ({ page }) => {
    const fixture = await openFixture(page, 2);
    await page.locator(button).click();
    await expect(page.locator('[data-automation-open="automation-2"]')).toContainText('Replay');
    await page.locator('[data-automation-open="automation-2"]').click();
    await expect(page.getByText(`Session Replay — ${fixture.tabs.get('automation-2').sessionId}`, { exact: true })).toBeVisible();
    await expect(page.getByRole('combobox', { name: 'Replay speed' })).toBeVisible();
    expect(fixture.connections).toEqual(['ordinary']);
});

test('Automation events update the count without stealing focus and survive a view remount', async ({ page }) => {
    const fixture = await openFixture(page, 2);
    const added = { ...fixture.tabs.get('automation-1'), tabId: 'automation-new', jobRunId: 'run-new', automationName: 'New workflow' };
    fixture.tabs.set(added.tabId, added);
    fixture.emit('automation_terminal_started', { ...added, jobName: added.automationName });
    await expect(page.locator(count)).toHaveText('3');
    expect(await page.evaluate(() => window.app.terminalController.manager.activeTabId)).toBe('ordinary');
    expect(fixture.connections).toEqual(['ordinary']);
    await page.evaluate(() => window.app.navigate('settings'));
    await page.evaluate(() => window.app.navigate('terminal-focus'));
    await expect(page.locator(count)).toHaveText('3');
    await expect(page.locator(normalTabs)).toHaveCount(1);
    await page.locator(button).click();
    await expect(page.locator('[data-automation-open="automation-new"]')).toContainText('New workflow');
});

test('closing an Automation leaves the ordinary undo-close menu independent', async ({ page }) => {
    const fixture = await openFixture(page, 2);
    await page.locator('.vb-terminal-tab-item[data-tab-id="ordinary"]').hover();
    await page.locator('[data-tab-id="ordinary"] .vb-terminal-tab-close').click();
    await expect(page.locator('#vb-terminal-tab-undo-count')).toHaveText('1');
    await page.locator(button).click();
    await page.locator('[data-automation-close="automation-2"]').click();
    await expect(page.locator(count)).toHaveText('1');
    expect(fixture.deleted).toEqual(['automation-2']);
    await expect(page.locator('#vb-terminal-tab-undo-count')).toHaveText('1');
    await page.locator('#vb-terminal-tab-undo-btn').click();
    await expect(page.locator('.vb-undo-dropdown')).toBeVisible();
    await expect(page.locator('.vb-undo-dropdown')).toContainText('2 minutes');
});

test('opening a Board Automation session link retains its menu classification', async ({ page }) => {
    const fixture = await openFixture(page, 2);
    await page.evaluate(() => window.app.terminalController.adoptLaunchedTab('automation-1', { focus: true }));
    await expect.poll(() => fixture.connections.includes('automation-1')).toBe(true);
    await expect(page.locator(normalTabs)).toHaveCount(1);
    await expect(page.locator('#vb-terminal-window-title')).toContainText('Nightly workflow 1');
    await page.locator('.vb-terminal-tab-item[data-tab-id="ordinary"] .vb-terminal-tab-button').click();
    await expect.poll(() => page.evaluate(() =>
        Boolean(window.app.terminalController.manager.tabs.get('automation-1')?.instance?.hasOpenSocket())
    )).toBe(false);
});

test('completion switches a live Automation to replay and an unavailable host stays unavailable', async ({ page }) => {
    const fixture = await openFixture(page, 2);
    const finished = fixture.tabs.get('automation-1');
    finished.hasActiveSession = false;
    fixture.emit('session_completed', { tabId: finished.tabId, sessionId: finished.sessionId, exitCode: 0 });
    fixture.tabs.get('automation-2').statusAvailable = false;
    await page.locator(button).click();
    await expect(page.locator('[data-automation-open="automation-1"]')).toContainText('Finished');
    await expect(page.locator('[data-automation-open="automation-2"]')).toContainText('Unavailable');
    await page.locator('[data-automation-open="automation-2"]').click();
    await expect(page.getByText('This Automation terminal is temporarily unavailable. Try again shortly.', { exact: true })).toBeVisible();
    await expect(page.getByRole('combobox', { name: 'Replay speed' })).toHaveCount(0);
    expect(fixture.connections).toEqual(['ordinary']);
});

test('reloading a selected Automation still lets ordinary terminals reconnect on selection', async ({ page }) => {
    await openFixture(page, 2);
    await page.locator(button).click();
    await page.locator('[data-automation-open="automation-1"]').click();
    await expect.poll(() => page.evaluate(() => window.app.terminalController.manager.activeTabId)).toBe('automation-1');
    await page.reload();
    await expect.poll(() => page.evaluate(() => window.app?.terminalController?.manager?.activeTabId)).toBe('automation-1');
    await page.locator('.vb-terminal-tab-item[data-tab-id="ordinary"] .vb-terminal-tab-button').click();
    await expect.poll(() => page.evaluate(() =>
        window.app.terminalController.manager.getActiveTab()?.instance?.hasOpenSocket()
    )).toBe(true);
    await expect(page.locator(normalTabs)).toHaveCount(1);
});

test('robot menu stays inside a narrow viewport and supports keyboard dismissal', async ({ page }) => {
    await page.setViewportSize({ width: 460, height: 720 });
    await openFixture(page);
    await page.locator(button).focus();
    await page.keyboard.press('Enter');
    await expect(page.locator(menu)).toBeVisible();
    const bounds = await page.locator(menu).boundingBox();
    expect(bounds.x).toBeGreaterThanOrEqual(0);
    expect(bounds.x + bounds.width).toBeLessThanOrEqual(460);
    expect(bounds.y + bounds.height).toBeLessThanOrEqual(720);
    await page.keyboard.press('Escape');
    await expect(page.locator(menu)).toBeHidden();
    await expect(page.locator(button)).toBeFocused();
});
