const { test, expect } = process.env.VIBERAILS_SCRIPTS_STATIC === '1'
    ? require('@playwright/test') : require('./fixtures');

async function openScripts(page, layout = 'top') {
    await page.addInitScript(layout => {
        sessionStorage.setItem('viberails_tab', 'scripts-fixture');
        sessionStorage.setItem('viberails_terminal_active_tab_id', 'agent');
        localStorage.setItem('viberails_nav_layout', layout);
    }, layout);
    await page.routeWebSocket('**/api/v1/events/ws*', () => {});
    await page.routeWebSocket('**/api/v1/terminal/tabs/*/ws*', socket => {
        socket.send(Buffer.from('Agent ready\r\n'));
    });
    const script = { name: 'example.py', path: 'C:/fixture/scripts/example.py',
        status: 'approved', sizeBytes: 123, modifiedUtc: '2026-10-03T01:00:00Z' };
    const agent = { tabId: 'agent', sessionId: 'agent-session', cli: 'codex',
        hasActiveSession: true, workingDirectory: 'C:/fixture/scripts' };
    await page.route('**/api/v1/**', route => {
        const path = new URL(route.request().url()).pathname;
        const responses = {
            '/api/v1/context': { isInGit: true, rootPath: 'C:/fixture', launchDirectory: 'C:/fixture' },
            '/api/v1/settings': {},
            '/api/v1/environments': { environments: [] },
            '/api/v1/agents': { agents: [] },
            '/api/v1/sandboxes': { sandboxes: [] },
            '/api/v1/jobs': { jobs: [] },
            '/api/v1/jobs/runs/summary': { runs: [] },
            '/api/v1/llm-picker/preferences': { items: [] },
            '/api/v1/python-scripts': { scriptsDirectory: 'C:/fixture/scripts', pinConfigured: true,
                scripts: Array.from({ length: 40 }, (_, i) => i ? { ...script, name: `script-${i}.py` } : script) },
            '/api/v1/python-scripts/content': { ...script, version: 'one', content: 'print("Hello")\n'.repeat(150) },
            '/api/v1/terminal/tabs': { tabs: [agent], maxTabs: 100 },
            '/api/v1/terminal/tabs/agent/status': agent
        };
        return route.fulfill({ json: responses[path] || {} });
    });
    await page.goto('/?view=jobs');
    await expect(page.locator('.python-script-row')).toHaveCount(40);
}

async function expectTerminalFits(page) {
    await expect.poll(() => page.evaluate(() => {
        const terminal = document.querySelector('.xterm-screen')?.getBoundingClientRect();
        return Boolean(terminal && terminal.height > 100 && terminal.bottom <= innerHeight && terminal.top >= 0);
    })).toBe(true);
    expect(await page.evaluate(() => document.documentElement.scrollHeight - innerHeight)).toBeLessThanOrEqual(1);
}

for (const layout of ['top', 'side']) {
    test(`terminal prompt stays in the viewport with ${layout} navigation`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width: 1440, height: 900 });
        await openScripts(page, layout);
        await page.locator('.python-script-name').first().click();
        await expect(page.locator('.monaco-editor')).toBeVisible();
        await expect.poll(() => page.evaluate(() =>
            window.app.terminalController.manager.getActiveTab()?.instance?.hasOpenSocket())).toBe(true);
        // Put a CLI-style prompt on the last row of the real xterm, then resize the window.
        await page.evaluate(() => {
            const terminal = window.app.terminalController.manager.getActiveTab().instance.terminal;
            terminal.write(`\x1b[${terminal.rows};1H> Enter a request`);
        });
        await expectTerminalFits(page);
        await page.setViewportSize({ width: 1000, height: 600 });
        await expectTerminalFits(page);
        await page.locator('[data-workbench-splitter]').focus();
        await page.keyboard.press('ArrowLeft');
        await expectTerminalFits(page);
        await page.screenshot({ path: testInfo.outputPath(`workbench-${layout}.png`) });
        await page.locator('[data-action="go-back"]').click();
        await expect(page.locator('[data-python-scripts-root]')).toBeVisible();
        expect(await page.evaluate(() => document.body.classList.contains('vb-python-workbench-active'))).toBe(false);
    });
}

test('script actions align and language guidance stays readable on narrow screens', async ({ page }, testInfo) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await openScripts(page);
    const actions = page.locator('.python-scripts-heading-actions');
    const buttons = actions.locator('button');
    const boxes = await buttons.evaluateAll(nodes => nodes.map(node => {
        const rect = node.getBoundingClientRect();
        return { top: rect.top, height: rect.height };
    }));
    expect(new Set(boxes.map(box => Math.round(box.top))).size).toBe(1);
    expect(new Set(boxes.map(box => Math.round(box.height))).size).toBe(1);
    await expect(page.locator('.python-scripts-section')).toContainText('PowerShell');
    await expect(page.locator('.python-scripts-section')).toContainText('Bash');
    await page.screenshot({ path: testInfo.outputPath('scripts-desktop.png') });
    await page.setViewportSize({ width: 375, height: 812 });
    expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
    await expect(actions.getByRole('button', { name: 'Refresh the script list' })).toBeVisible();
    await actions.scrollIntoViewIfNeeded();
    await page.screenshot({ path: testInfo.outputPath('scripts-narrow.png') });
});
