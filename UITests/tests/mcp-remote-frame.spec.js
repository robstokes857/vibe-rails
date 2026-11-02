// The real MCP Explorer and its sandboxed remote frame, with intercepted APIs; no backend.
const { test, expect } = require('@playwright/test');

test('remote MCP data reaches the sandboxed frame only through its private port', async ({ page }) => {
    const calls = [];
    // Runs in every frame. The sandboxed frame records each window message it receives
    // before its own listener sees it.
    await page.addInitScript(() => {
        if (window === window.top) {
            sessionStorage.setItem('viberails_tab', 'mcp-remote-fixture');
            return;
        }
        window.__windowMessages = [];
        window.addEventListener('message', event => window.__windowMessages.push(event.data));
    });
    await page.routeWebSocket('**/api/v1/events/ws*', () => {});
    await page.route('**/api/v1/**', route => {
        const request = route.request();
        const path = new URL(request.url()).pathname;
        if (path === '/api/v1/mcp/inspect') {
            return route.fulfill({ json: {
                endpoint: 'https://mcp.example/mcp',
                message: 'Connected',
                tools: [{ name: 'echo', description: 'Echo the text back.', inputSchema: { type: 'object' } }]
            } });
        }
        if (path === '/api/v1/mcp/tools/echo') {
            calls.push(request.postDataJSON());
            return route.fulfill({ json: { success: true, result: 'pong' } });
        }
        const payloads = {
            '/api/v1/context': { isInGit: true, rootPath: 'C:/fixture', launchDirectory: 'C:/fixture' },
            '/api/v1/settings': { mcpEnabled: true },
            '/api/v1/environments': { environments: [] },
            '/api/v1/mcp/tools': []
        };
        return route.fulfill({ json: payloads[path] || {} });
    });

    await page.goto('/?view=mcp', { waitUntil: 'domcontentloaded' });
    await page.locator('#app-content').getByRole('button', { name: 'Remote', exact: true }).click();
    await page.locator('[data-mcp-endpoint]').fill('https://mcp.example/mcp');
    await page.locator('.mcp-header-box summary').click();
    await page.locator('[data-mcp-headers]').fill('{"Authorization":"Bearer fixture"}');
    await page.locator('[data-mcp-connect]').click();

    const frame = page.frameLocator('[data-mcp-remote-frame]');
    await expect(frame.locator('[data-status]')).toHaveText('1 tool(s)');
    await expect(frame.locator('[data-tool="echo"]')).toBeVisible();
    await frame.locator('[data-call]').click();
    await expect(frame.locator('[data-result]')).toHaveText('pong');
    await expect(frame.locator('[data-banner]')).toHaveText('Call succeeded.');
    expect(calls).toEqual([{
        endpoint: 'https://mcp.example/mcp',
        headers: { Authorization: 'Bearer fixture' },
        arguments: {}
    }]);

    const remoteFrame = page.frames().find(candidate => candidate.url().endsWith('/mcp-remote.html'));
    expect(await remoteFrame.evaluate(() => window.__windowMessages)).toEqual([{ type: 'mcp-remote-port' }]);
});
