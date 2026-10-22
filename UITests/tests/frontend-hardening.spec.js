const { test, expect } = require('./fixtures');

test('dashboard entry points serve fresh matching CSP nonces', async ({ page, context }) => {
    test.setTimeout(60000);
    const nonces = new Set();
    for (const path of ['/', '/index.html', '/git-guard', '/mcp-remote.html']) {
        const response = await context.request.get(path);
        expect(response.ok(), `${path}: HTTP ${response.status()}`).toBe(true);
        const policy = response.headers()['content-security-policy'];
        const nonce = policy.match(/script-src 'self' 'nonce-([^']+)'/)[1];
        expect(policy).toContain("connect-src 'self';");
        const html = await response.text();
        expect(html).not.toContain('{{VIBERAILS_CSP_NONCE}}');
        expect([...html.matchAll(/<script nonce="([^"]+)"/g)].map(match => match[1])).toEqual(
            path === '/mcp-remote.html' ? [nonce] : [nonce, nonce]);
        expect(nonces.has(nonce)).toBe(false);
        nonces.add(nonce);
    }
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.addInitScript(() => {
        window.cspViolations = [];
        document.addEventListener('securitypolicyviolation', event => window.cspViolations.push(event.effectiveDirective));
    });
    await page.goto('/');
    await page.waitForFunction(() => window.app?.data?.configs?.rootPath && document.querySelector('#app-content [data-view]'));
    await page.evaluate(async () => {
        await document.fonts.ready;
        await document.fonts.load('16px Lato');
    });
    expect(await page.evaluate(() => document.fonts.check('16px Lato'))).toBe(true);
    expect(await page.evaluate(() => window.cspViolations)).toEqual([]);
    expect(errors).toEqual([]);
    // The opaque-origin MCP frame must still initialize under the HTTP policy.
    await page.evaluate(() => {
        window.remoteFrameReady = false;
        const frame = document.createElement('iframe');
        frame.sandbox = 'allow-scripts';
        window.addEventListener('message', event => {
            if (event.source === frame.contentWindow && event.data?.type === 'mcp-remote-ready') window.remoteFrameReady = true;
        });
        frame.src = '/mcp-remote.html';
        document.body.append(frame);
    });
    await page.waitForFunction(() => window.remoteFrameReady);
    expect(await page.evaluate(async () => {
        const { isParentMessage } = await import('/js/modules/parent-message.js');
        return new Promise(resolve => {
            const frame = document.createElement('iframe');
            frame.sandbox = 'allow-scripts';
            window.addEventListener('message', function receive(event) {
                if (event.source !== frame.contentWindow || event.data !== 'forged-parent') return;
                window.removeEventListener('message', receive);
                resolve(isParentMessage(event));
                frame.remove();
            });
            frame.srcdoc = '<script nonce="' + window.__viberails_NONCE__ + '">window.length = 1; window[0] = window.parent; window.parent.postMessage("forged-parent", "*");<\/script>';
            document.body.append(frame);
        });
    })).toBe(false);
    await page.evaluate(() => {
        const script = document.createElement('script');
        script.textContent = 'window.unexpectedInlineScript = true';
        document.body.append(script);
        void fetch('https://example.invalid/blocked').catch(() => {});
    });
    await expect.poll(() => page.evaluate(() => window.cspViolations)).toContain('script-src-elem');
    await expect.poll(() => page.evaluate(() => window.cspViolations)).toContain('connect-src');
    expect(await page.evaluate(() => window.unexpectedInlineScript)).toBeUndefined();
});
