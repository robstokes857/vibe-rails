const { test, expect } = require('@playwright/test');
const button = '#vb-terminal-automations-btn';
const takeoverToast = '.notAToasttoast-container > .notAToastvr-dark';

for (const width of [1280, 390]) {
    test(`remote takeover uses the shared toast until clicked away at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 850 });
        const fixture = await openFixture(page);
        const notice = page.locator(takeoverToast);
        const screen = () => page.evaluate(() =>
            window.app.terminalController.manager.getActiveTab().instance.vibeTerminal.getPlainText()
                .replace(/\s+/g, ' ').trim());
        await expect.poll(screen).toMatch(/Output from\s+ordinary/);
        const before = await screen();

        fixture.sockets.get('ordinary').close({ code: 1000, reason: 'Session taken over by remote viewer' });
        await expect(notice).toContainText('This terminal was taken over by a remote host. Select Connect to take it back.');
        await expect(notice).toContainText('Terminal taken over: My terminal');
        await expect(page.locator('#terminal-stop-btn')).toBeVisible();
        await page.waitForTimeout(6500); // Beyond both the ordinary warning duration and first reconnect delay.
        await expect(notice).toBeVisible();
        expect(fixture.connections).toEqual(['ordinary']);
        expect(await screen()).toBe(before);
        expect(await notice.evaluate(node => node.scrollWidth <= node.clientWidth)).toBe(true);
        expect(await notice.evaluate(node => node.getBoundingClientRect().right)).toBeLessThanOrEqual(width);
        await page.screenshot({ path: testInfo.outputPath(`remote-takeover-${width}.png`) });

        // The shared toast survives changing tabs and reconnecting until explicitly dismissed.
        await page.locator(button).click();
        await page.locator('[data-automation-open="automation-1"]').click();
        await expect(notice).toBeVisible();
        await page.locator('[data-tab-id="ordinary"] .vb-terminal-tab-button').click();
        await expect(notice).toContainText('a remote host');
        expect(fixture.connections.filter(id => id === 'ordinary')).toHaveLength(1);

        await page.locator('#terminal-stop-btn').click();
        await expect(page.locator('#terminal-stop-btn')).toBeHidden();
        await expect(notice).toBeVisible();
        expect(fixture.connections.filter(id => id === 'ordinary')).toHaveLength(2);

        await notice.locator('.notAToastclsBtn').click();
        await expect(notice).toHaveCount(0);
        await page.evaluate(() => window.app.terminalController.manager.updateUi());
        await expect(notice).toHaveCount(0);

        // Dismissing one event must not suppress a later takeover.
        fixture.sockets.get('ordinary').close({ code: 1000, reason: 'Session taken over by remote viewer' });
        await expect(notice).toContainText('a remote host');
        await notice.locator('.notAToastclsBtn').click();
        await expect(notice).toHaveCount(0);
    });
}

test('other viewer takeovers are identified without calling them remote, and ordinary drops have no takeover notice', async ({ page }) => {
    const fixture = await openFixture(page);
    const notice = page.locator(takeoverToast);
    for (const reason of ['Session taken over', 'Session taken over by local viewer']) {
        fixture.sockets.get('ordinary').close({ code: 1000, reason });
        await expect(notice).toContainText('taken over by another viewer');
        await notice.locator('.notAToastclsBtn').click();
        await expect(notice).toHaveCount(0);
        await page.locator('#terminal-stop-btn').click();
        await expect(page.locator('#terminal-stop-btn')).toBeHidden();
    }
    fixture.sockets.get('ordinary').close({ code: 1000, reason: 'Peer disconnected' });
    await expect(page.locator('#terminal-stop-btn')).toBeVisible();
    await expect(notice).toHaveCount(0);
});

// Real terminal UI and xterm with local socket/API fixtures; no CLI or database is opened.
async function openFixture(page) {
    const tabs = [
        { tabId: 'ordinary', sessionId: 'ordinary-session', cli: 'codex', hasActiveSession: true,
            createdUTC: '2026-10-20T09:00:00Z', workingDirectory: 'C:/fixture' },
        { tabId: 'automation-1', sessionId: 'automation-session', cli: 'shell', hasActiveSession: true,
            createdUTC: '2026-10-20T10:00:00Z', workingDirectory: 'C:/fixture',
            jobRunId: 'run-1', automationName: 'Other terminal', statusAvailable: true }
    ];
    const sockets = new Map();
    const connections = [];
    await page.addInitScript(() => {
        sessionStorage.setItem('viberails_tab', 'takeover-fixture');
        sessionStorage.setItem('viberails_terminal_active_tab_id', 'ordinary');
        sessionStorage.setItem('viberails_terminal_tab_meta_ordinary', JSON.stringify({
            label: 'My terminal', customLabel: true
        }));
    });
    await page.routeWebSocket('**/api/v1/events/ws*', () => {});
    await page.routeWebSocket('**/api/v1/terminal/tabs/*/ws*', socket => {
        const id = new URL(socket.url()).pathname.split('/')[5];
        sockets.set(id, socket);
        connections.push(id);
        socket.send(Buffer.from(`Output from ${id}\r\n`));
    });
    await page.route('**/api/v1/**', route => {
        const path = new URL(route.request().url()).pathname;
        const status = path.match(/^\/api\/v1\/terminal\/tabs\/([^/]+)\/status$/);
        if (status) {
            const tab = tabs.find(tab => tab.tabId === status[1]);
            return route.fulfill({ status: tab ? 200 : 404, json: tab || {} });
        }
        const responses = {
            '/api/v1/context': { isInGit: true, rootPath: 'C:/fixture', launchDirectory: 'C:/fixture' },
            '/api/v1/settings': {},
            '/api/v1/environments': { environments: [] },
            '/api/v1/agents': { agents: [] },
            '/api/v1/sandboxes': { sandboxes: [] },
            '/api/v1/llm-picker/preferences': { items: [] },
            '/api/v1/terminal/tabs': { tabs, maxTabs: 100 }
        };
        return route.fulfill({ json: responses[path] || {} });
    });
    await page.goto('/');
    await expect(page.locator('#loading-overlay')).toHaveClass(/\bd-none\b/);
    await expect.poll(() => page.evaluate(() =>
        window.app?.terminalController?.manager?.getActiveTab()?.instance?._initialConnectActive
    )).toBe(false);
    // Wait for the documented initial focus retry before testing tab switches and geometry.
    await page.waitForTimeout(220);
    return { sockets, connections };
}

