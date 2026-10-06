const { test, expect } = require('@playwright/test');
const http = require('node:http');

// The real AuthRoutes header is pinned by AgentRoutesSecurityTests. This checks browser
// acceptance and transmission over the production loopback HTTP topology, without app state.
for (const host of ['localhost', '127.0.0.1', '[::1]']) {
    test(`session cookie policy preserves sign-in on http://${host}`, async ({ page, context }) => {
        const secure = host === 'localhost';
        const server = http.createServer((request, response) => {
            if (request.url === '/bootstrap') {
                response.setHeader('Set-Cookie', `viberails_session=disposable-test-value; Path=/; HttpOnly; ${secure ? 'Secure; ' : ''}SameSite=Lax`);
                response.end('<!doctype html><title>bootstrap</title>');
            } else {
                response.end(request.headers.cookie === 'viberails_session=disposable-test-value' ? 'authenticated' : 'missing');
            }
        });
        const bindHost = host === '[::1]' ? '::1' : '127.0.0.1';
        await new Promise((resolve, reject) => { server.once('error', reject); server.listen(0, bindHost, resolve); });
        try {
            const origin = `http://${host}:${server.address().port}`;
            await page.goto(`${origin}/bootstrap`);
            const cookie = (await context.cookies(origin)).find(value => value.name === 'viberails_session');
            expect(cookie).toMatchObject({ secure, httpOnly: true });
            expect(await page.evaluate(() => document.cookie)).toBe('');
            await page.goto(`${origin}/protected`);
            await expect(page.locator('body')).toHaveText('authenticated');
        } finally {
            server.closeAllConnections();
            await new Promise(resolve => server.close(resolve));
        }
    });
}
