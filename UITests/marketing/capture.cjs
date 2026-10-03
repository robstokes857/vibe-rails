// Capture the production frontend with an isolated browser and hand-authored data.
// Every request is fulfilled locally or blocked. No server, account, CLI or database is used.
const { chromium, expect } = require('@playwright/test');
const fs = require('node:fs/promises');
const path = require('node:path');
const { createHash } = require('node:crypto');
const demo = require('./demo-data.cjs');
const root = path.resolve(__dirname, '../../VibeRails/wwwroot');
const output = path.resolve(__dirname, '../../vscode-viberails/media/screenshots');
const origin = 'http://viberails.demo';
const mime = { '.html': 'text/html', '.js': 'application/javascript', '.mjs': 'application/javascript', '.css': 'text/css', '.json': 'application/json', '.svg': 'image/svg+xml', '.png': 'image/png', '.woff': 'font/woff', '.woff2': 'font/woff2', '.ttf': 'font/ttf', '.ico': 'image/x-icon' };

async function main() {
    await fs.mkdir(output, { recursive: true });
    const browser = await chromium.launch({ headless: true });
    const context = await browser.newContext({ viewport: { width: 1600, height: 900 }, deviceScaleFactor: 2, colorScheme: 'dark', reducedMotion: 'reduce', locale: 'en-US', timezoneId: 'America/Chicago', serviceWorkers: 'block' });
    const unknown = new Set(), blocked = new Set(), errors = [], shots = [];
    let terminalSocket, terminalTranscript;
    try {
        const installRoutes = async context => {
        await context.addInitScript(() => {
            if (window === window.top) {
                sessionStorage.setItem('viberails_tab', 'fictional-demo-token');
                sessionStorage.setItem('viberails_terminal_active_tab_id', 'demo_terminal');
                sessionStorage.setItem('viberails_terminal_tab_selection_demo_terminal', 'base:codex');
                sessionStorage.setItem('viberails_terminal_tab_selection_demo_claude', 'base:claude');
            }
        });
        // A routed socket never connects to a server unless connectToServer is called.
        await context.routeWebSocket('**/*', socket => {
            if (socket.url().includes('/terminal/tabs/')) {
                if (socket.url().includes('/demo_terminal/ws')) terminalSocket = socket;
                terminalTranscript = Buffer.from([
                '\x1b[36mTrailhead / demo workspace\x1b[0m',
                '\x1b[90mSample agent session · fictional content\x1b[0m', '',
                '\x1b[1m> Add search to the command palette. Include keyboard navigation.\x1b[0m', '',
                'I will check the existing commands, add matching, and verify keyboard behavior.', '',
                '\x1b[36m• Inspecting the workspace\x1b[0m',
                '  Read src/Search/CommandPalette.cs',
                '  Read src/Search/SearchService.cs',
                '  Read tests/SearchTests.cs', '',
                '\x1b[36m• Updating command search\x1b[0m',
                '  Match command names and categories',
                '  Keep selection visible while using the arrow keys',
                '  Return focus when the palette closes', '',
                '\x1b[32m✓ Search filters commands as you type\x1b[0m',
                '\x1b[32m✓ Arrow keys, Enter, and Escape are covered\x1b[0m',
                '\x1b[32m✓ Existing shortcuts keep working\x1b[0m', '',
                'Ready for review. The change is linked to TRAIL-45.', '',
                '\x1b[90m/demo/trailhead  •  feature/command-search\x1b[0m', '',
                '\x1b[36m›\x1b[0m '
                ].join('\r\n'));
                socket.send(Buffer.from('\r\n'));
            }
        });
        await context.route('**/*', async route => {
            const url = new URL(route.request().url());
            if (url.origin !== origin) { blocked.add(url.hostname); return route.abort(); }
            if (url.pathname.startsWith('/api/')) {
                const json = demo.api(url.pathname, route.request().method());
                if (json === undefined) unknown.add(`${route.request().method()} ${url.pathname}`);
                return route.fulfill({ json: json ?? {} });
            }
            const file = path.resolve(root, '.' + decodeURIComponent(url.pathname === '/' ? '/index.html' : url.pathname));
            if (!file.startsWith(root + path.sep)) return route.abort();
            try { return await route.fulfill({ body: await fs.readFile(file), contentType: mime[path.extname(file)] || 'application/octet-stream' }); }
            catch { unknown.add(`asset ${url.pathname}`); return route.fulfill({ status: 404, body: '' }); }
        });
        };
        await installRoutes(context);
        let page = await context.newPage();
        page.setDefaultTimeout(12000);
        page.on('pageerror', error => errors.push(error.message));
        const open = async view => {
            await page.goto(`${origin}/?view=${view}`, { waitUntil: 'networkidle' });
            await expect(page.locator('#loading-overlay')).toHaveClass(/d-none/, { timeout: 20000 });
            await page.evaluate(() => document.fonts.ready);
        };
        const capture = async (name, title, locator) => {
            await page.mouse.move(1, 1);
            const texts = await Promise.all(page.frames().map(frame => frame.evaluate(() => document.body.innerText + '\n' + Array.from(document.querySelectorAll('input, textarea')).map(el => el.value).join('\n')).catch(() => '')));
            const text = texts.join('\n');
            if (/robst|OneDrive|C:\\Users|vibe-books|sk-[a-zA-Z0-9]{12}|ghp_[a-zA-Z0-9]+|@(?:gmail|outlook)\.com/i.test(text)) throw new Error(`Privacy check failed: ${name}`);
            if (/Notifications Offline|\bundefined\b|Invalid Date/.test(text)) throw new Error(`Incomplete fixture visible: ${name}`);
            await (locator || page).screenshot({ path: path.join(output, `${name}.png`), animations: 'disabled' });
            const bytes = await fs.readFile(path.join(output, `${name}.png`));
            shots.push({ name, title, width: bytes.readUInt32BE(16), height: bytes.readUInt32BE(20), sha256: createHash('sha256').update(bytes).digest('hex') });
            console.log(`Captured ${name}`);
        };
        await open('board');
        await expect(page.locator('.board-card')).toHaveCount(12);
        await capture('01-board', 'Plan work across agents');
        await capture('02-board-closeup', 'A shared Board for every coding agent', page.locator('#app-content [data-view="board"]'));
        await page.getByRole('button', { name: 'Agents on entry to Review', exact: true }).click();
        await expect(page.locator('.board-lane-agents-panel')).toBeVisible();
        await capture('03-lane-agents', 'Run reviews and checks when a card enters a lane');
        await page.getByRole('button', { name: 'Close lane agents' }).click();
        await page.getByText('Improve notification preferences', { exact: true }).click();
        await expect(page.locator('#board-card-title')).toBeVisible();
        await capture('04-card-details', 'Keep requirements and agent discussion together');
        await page.locator('[data-board-comments]').scrollIntoViewIfNeeded();
        await capture('12-card-discussion', 'Follow the handoff from implementation to review');
        await open('agents');
        await expect(page.locator('.code-report .qr')).toHaveAttribute('data-state', 'complete', { timeout: 25000 });
        await capture('05-project-health', 'Rules and code quality in one workspace');
        await capture('06-code-graph', 'Explore source relationships and code quality', page.locator('.project-health-quality'));
        const map = page.frameLocator('.code-report iframe');
        await map.getByRole('button', { name: 'Cards', exact: true }).click();
        await capture('07-code-cards', 'Explore the repository as connected cards', page.locator('.project-health-quality'));
        await page.locator('.code-report').getByRole('button', { name: /src\/Notifications\/PreferenceService.cs/ }).click();
        await expect(map.locator('#inspector h2')).toHaveText('PreferenceService.cs');
        await capture('08-code-inspector', 'Follow a quality finding into the source map', page.locator('.project-health-quality'));
        await map.getByRole('button', { name: /Open details/ }).click();
        await expect(page.locator('.details-panel')).toBeVisible();
        await capture('09-quality-details', 'Inspect saved measurements and code excerpts', page.locator('.project-health-quality'));
        await open('environments');
        await expect(page.getByText('Feature builder', { exact: true }).first()).toBeVisible();
        await capture('10-environments', 'Reusable environments and Automation Workers');
        await page.locator('[data-action="edit-environment"][data-env-name="Careful refactor"]').click();
        await capture('13-environment-settings', 'Configure a repeatable coding workflow');
        await open('jobs');
        await capture('11-automations', 'Automate repeatable project workflows');
        await open('board');
        await page.getByRole('button', { name: 'Switch to top navigation', exact: true }).click();
        await capture('15-board-wide', 'A wide Board with top navigation');
        await page.close();
        // The terminal's canvas is captured in a fresh context at native pixel density.
        const terminalContext = await browser.newContext({ viewport: { width: 1600, height: 900 }, deviceScaleFactor: 1, colorScheme: 'dark', reducedMotion: 'reduce', locale: 'en-US', timezoneId: 'America/Chicago', serviceWorkers: 'block' });
        await installRoutes(terminalContext);
        page = await terminalContext.newPage();
        page.setDefaultTimeout(12000);
        page.on('pageerror', error => errors.push(error.message));
        await open('terminal-focus');
        await expect.poll(() => page.evaluate(() => window.app?.terminalController?.manager?.getActiveTab()?.instance?._initialConnectActive)).toBe(false);
        terminalSocket.send(terminalTranscript);
        await expect.poll(() => page.evaluate(() => window.app?.terminalController?.manager?.getActiveTab()?.instance?.vibeTerminal?.getPlainText())).toContain('Ready for review');
        await page.evaluate(() => window.app.terminalController.manager.getActiveTab().instance.terminal.scrollToTop());
        await page.waitForTimeout(300);
        await capture('14-terminal', 'Real terminal UI with a fictional agent transcript');
        shots.sort((a, b) => a.name.localeCompare(b.name));
        await fs.writeFile(path.join(output, 'capture-manifest.json'), JSON.stringify({
            content: 'Fictional Trailhead demo data; illustrative quality metrics and activity.',
            renderer: 'Unmodified VibeRails frontend; Playwright Chromium; 2x pixel density (terminal: 1x).',
            privacy: 'Fresh browser context. All HTTP and WebSocket requests intercepted without forwarding. No backend or database opened. No real account, terminal history, filesystem explorer, or credentials loaded.',
            shots, blockedHosts: [...blocked], unhandledFixtures: [...unknown], pageErrors: errors
        }, null, 2) + '\n');
        const gallery = (entries, compact = false) => `<!doctype html><html lang="en"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>VibeRails screenshot gallery</title><style>
            *{box-sizing:border-box}body{margin:0;padding:40px;background:#101114;color:#f4f4f5;font:16px 'Segoe UI',sans-serif}header{max-width:1440px;margin:0 auto 30px}small{color:#4dd7e7;letter-spacing:2px;font-weight:650}h1{font-size:38px;letter-spacing:-1px;margin:8px 0 12px}p{color:#a5adb9;line-height:1.6;max-width:1050px}main{display:grid;grid-template-columns:repeat(${compact ? 3 : 2},minmax(0,1fr));gap:24px;max-width:1440px;margin:auto}a{display:block;color:inherit;text-decoration:none;background:#1b1e25;border:1px solid #333946;border-radius:12px;overflow:hidden}a:hover{border-color:#4dd7e7}img{display:block;width:100%;height:${compact ? 270 : 330}px;object-fit:contain;background:#0c0d10}figcaption{padding:18px}b{display:block;font-size:17px}span{display:block;margin-top:7px;color:#939ead;font:12px Consolas,monospace}@media(max-width:850px){main{grid-template-columns:1fr}body{padding:22px}}</style>
            <header><small>VIBE RAILS / PRODUCT GALLERY</small><h1>From an idea to reviewed code.</h1><p>${compact ? 'A selection from 15 screenshots for documentation and demos.' : '15 screenshots of the real VibeRails interface. Click any image for its full-resolution PNG.'} All project names, cards, activity, code metrics, and terminal output use fictional demo data.</p></header><main>${entries.map(shot => `<a href="${shot.name}.png"><img src="${shot.name}.png" alt="${shot.title}"><figcaption><b>${shot.title}</b><span>${shot.name}.png · ${shot.width} × ${shot.height}</span></figcaption></a>`).join('')}</main></html>`;
        await fs.writeFile(path.join(output, 'index.html'), gallery(shots));
        const selected = ['15-board-wide', '06-code-graph', '07-code-cards', '03-lane-agents', '11-automations', '10-environments'].map(name => shots.find(shot => shot.name === name));
        let preview = gallery(selected, true);
        for (const shot of selected) preview = preview.replace(`src="${shot.name}.png"`, `src="data:image/png;base64,${(await fs.readFile(path.join(output, `${shot.name}.png`))).toString('base64')}"`);
        const overview = await browser.newPage({ viewport: { width: 1600, height: 1040 }, deviceScaleFactor: 1 });
        await overview.setContent(preview, { waitUntil: 'load' });
        await overview.screenshot({ path: path.join(output, 'preview.png'), fullPage: true });
        await overview.close();
        if (errors.length || unknown.size) throw new Error('Capture requires review: see capture-manifest.json.');
        console.log(JSON.stringify({ shots: shots.length, unknown: [...unknown], errors }, null, 2));
    } finally { await browser.close(); }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
