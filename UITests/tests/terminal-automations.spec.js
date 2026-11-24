const { test, expect } = require('@playwright/test');
const snapshotFixtures = require('./terminal-snapshot-fixtures.cjs');

let completedSnapshot;
test.beforeAll(() => {
    test.setTimeout(120000);
    completedSnapshot = snapshotFixtures().find(value => value.completed && value.rows === 24);
});

const button = '#vb-terminal-automations-btn';
const count = '#vb-terminal-automations-count';
const menu = '#vb-terminal-automations-menu';
const normalTabs = '#vb-terminal-tab-list .vb-terminal-tab-item:visible';

for (const width of [1280, 390]) {
    test(`session sharing captures the recording and safely shows a copyable link at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 850 });
        await openFixture(page, 1);
        const requests = [];
        const shareUrl = 'https://viberails.ai/shared/session?key=' + 'a'.repeat(64);
        await page.route('**/sharing-links', async route => {
            const body = route.request().postDataJSON();
            requests.push({ url: route.request().url(), body, headers: route.request().headers() });
            // The desktop service only reports success once the server confirmed the requested audience.
            await route.fulfill({ json: { success: true, status: 'pending_upload', url: shareUrl, access: body.access, recipients: body.emails,
                expiresUtc: '2026-11-06T20:00:00Z', message: 'Available after the session ends and uploads.' } });
        });
        await page.evaluate(() => {
            const manager = window.app.terminalController.manager;
            const tab = manager.tabs.get('ordinary');
            tab.state.sessionId = 'd88b7a85-0c2d-4203-acf1-6b9f22227f57';
            tab.state.title = '<img src=x onerror=alert(1)> Demo';
            manager.syncTabActionAvailability(tab);
            Object.defineProperty(navigator, 'clipboard', { configurable: true, value: {
                writeText: async value => { window.copiedShare = value; }
            } });
        });
        const tab = page.locator(normalTabs).first();
        await tab.hover();
        const share = tab.getByRole('button', { name: 'Share session', exact: true });
        await expect(share).toBeEnabled();
        await share.click();
        const modal = page.locator('#terminal-session-share');
        await expect(modal).toBeVisible();
        await expect(modal.locator('[data-share-emails]')).toBeHidden();
        await modal.locator('#session-share-access-email').check();
        await expect(modal.locator('[data-share-emails]')).toBeVisible();
        await modal.locator('#session-share-emails').fill('reviewer@example.test\nreviewer@example.test, lead@example.test');
        await modal.getByRole('button', { name: 'Create link', exact: true }).click();
        await expect(modal.locator('#session-share-url')).toHaveValue(shareUrl);
        await expect(modal.locator('[data-share-audience]')).toContainText('reviewer@example.test, lead@example.test');
        await expect(modal.locator('[data-share-name]')).toHaveText('<img src=x onerror=alert(1)> Demo');
        await expect(modal.locator('img')).toHaveCount(0);
        await expect(modal).toContainText('Available after the session ends and uploads.');
        await expect(modal).toContainText('Links expire after one month');
        await modal.getByRole('button', { name: 'Copy link' }).click();
        expect(await page.evaluate(() => window.copiedShare)).toBe(shareUrl);
        await expect(modal.getByRole('button', { name: 'Copied' })).toBeVisible();
        // Repeated Share while this modal is open must not create duplicate links, even
        // when the underlying tab has moved on to a different recording.
        await page.evaluate(() => {
            const manager = window.app.terminalController.manager;
            manager.tabs.get('ordinary').state.sessionId = 'replacement-session';
            void manager.shareTab('ordinary');
        });
        expect(requests).toHaveLength(1);
        expect(requests[0].url).toContain('/sessions/d88b7a85-0c2d-4203-acf1-6b9f22227f57/sharing-links');
        expect(requests[0].body.displayName).toBe('<img src=x onerror=alert(1)> Demo');
        expect(requests[0].body.access).toBe('email');
        expect(requests[0].body.emails).toEqual(['reviewer@example.test', 'lead@example.test']);
        expect(requests[0].headers.viberails_tab).toBe('automation-fixture');
        expect(await modal.evaluate(node => node.scrollWidth <= node.clientWidth)).toBe(true);
        await page.screenshot({ path: testInfo.outputPath(`session-sharing-${width}.png`) });
        await page.keyboard.press('Escape');
        await expect(modal).toHaveCount(0);
    });
}

test('session sharing handles sign-in errors, ready sessions and late replies after modal replacement', async ({ page }) => {
    await openFixture(page, 1);
    let count = 0;
    let release;
    const held = new Promise(resolve => { release = resolve; });
    await page.route('**/sharing-links', async route => {
        count++;
        if (count === 1) return route.fulfill({ json: { success: false, status: 'no_api_key', message: 'Sign in before sharing.' } });
        await held;
        return route.fulfill({ json: { success: true, status: 'ready', message: 'Your replay is ready to share.',
            url: 'https://viberails.ai/shared/session?key=' + 'b'.repeat(64), expiresUtc: '2026-11-06T20:00:00Z' } });
    });
    await page.evaluate(() => { void window.app.terminalController.manager.shareTab('ordinary'); });
    await page.locator('#terminal-session-share').getByRole('button', { name: 'Create link', exact: true }).click();
    await expect(page.locator('#terminal-session-share')).toContainText('Sign in before sharing.');
    await page.getByRole('button', { name: 'Try again', exact: true }).click();
    await expect.poll(() => count).toBe(2);
    await page.evaluate(() => window.app.showModal('Replacement', '<p id="replacement-share-dialog">Unchanged</p>'));
    release();
    await expect(page.locator('#replacement-share-dialog')).toHaveText('Unchanged');
    await expect(page.locator('#terminal-session-share')).toHaveCount(0);
    await page.keyboard.press('Escape');
    await page.evaluate(() => { void window.app.terminalController.manager.shareTab('ordinary'); });
    await page.locator('#terminal-session-share').getByRole('button', { name: 'Create link', exact: true }).click();
    await expect(page.locator('#terminal-session-share')).toContainText('Your replay is ready to share.');
    expect(count).toBe(3);
});

test('session sharing shows an actionable migration error on a phone', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 850 });
    await openFixture(page, 1);
    const message = 'The sharing server needs a database update. Ask the server administrator to apply the pending migrations.';
    await page.route('**/sharing-links', route => route.fulfill({ json: {
        success: false, status: 'schema_update_required', httpStatus: 503, message
    } }));
    await page.evaluate(() => { void window.app.terminalController.manager.shareTab('ordinary'); });
    const modal = page.locator('#terminal-session-share');
    await modal.getByRole('button', { name: 'Create link', exact: true }).click();
    await expect(modal).toContainText(message);
    await expect(modal.getByRole('button', { name: 'Try again', exact: true })).toBeVisible();
    await expect(modal.locator('[data-share-result]')).toBeHidden();
    expect(await modal.evaluate(node => node.scrollWidth <= node.clientWidth)).toBe(true);
});

test('card attention colors the originating terminal and Automation menu until cleared', async ({ page }, testInfo) => {
    const fixture = await openFixture(page, 1);
    fixture.tabs.get('ordinary').needsAttention = true;
    fixture.tabs.get('automation-1').needsAttention = true;
    await page.evaluate(() => window.app.terminalController.manager.refreshAutomationTabs());
    const tab = page.locator(normalTabs).first();
    await expect(tab).toHaveClass(/tab-needs-attention/);
    await expect(tab).toHaveCSS('background-color', 'rgb(73, 27, 37)');
    await expect(tab.locator('.vb-tab-attention')).toBeVisible();
    await expect(page.locator(button)).toHaveClass(/needs-attention/);
    await page.locator(button).click();
    await expect(page.locator('.vb-terminal-automation-row.needs-attention')).toContainText('Needs your attention');
    await page.screenshot({ path: testInfo.outputPath('attention-terminals.png') });
    await page.keyboard.press('Escape');
    await page.reload();
    await expect(page.locator(normalTabs).first()).toHaveClass(/tab-needs-attention/);
    fixture.tabs.get('ordinary').needsAttention = false;
    fixture.tabs.get('automation-1').needsAttention = false;
    await page.evaluate(() => window.app.terminalController.manager.refreshAutomationTabs());
    await expect(page.locator(normalTabs).first()).not.toHaveClass(/tab-needs-attention/);
    await expect(page.locator(button)).not.toHaveClass(/needs-attention/);
});

async function openFixture(page, total = 35, presets = []) {
    const normal = { tabId: 'ordinary', sessionId: 'ordinary-session', cli: 'codex',
        hasActiveSession: true, statusAvailable: true, createdUTC: '2026-09-23T09:00:00Z', workingDirectory: 'C:/fixture' };
    const tabs = new Map([[normal.tabId, normal]]);
    for (let i = 1; i <= total; i++) {
        const tab = { tabId: `automation-${i}`, sessionId: `00000000-0000-4000-8000-${String(i).padStart(12, '0')}`,
            cli: 'shell', hasActiveSession: i % 2 === 1, createdUTC: new Date(Date.UTC(2026, 8, 23, 10, i)).toISOString(),
            workingDirectory: 'C:/fixture', jobRunId: `run-${i}`, automationName: `Nightly workflow ${i}`, statusAvailable: true };
        tabs.set(tab.tabId, tab);
    }
    const connections = [];
    const deleted = [];
    const launches = [];
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
        if (path.startsWith('/api/v1/presets/'))
            return route.fulfill({ json: { ...presets.find(p => p.id === path.split('/').at(-1)), cli: 1 } });
        if (path === '/api/v1/runs' && route.request().method() === 'POST') {
            const body = route.request().postDataJSON();
            launches.push(body);
            // Every launch is its own run and tab. The first keeps the bare names; later ones are numbered.
            const suffix = launches.length === 1 ? '' : `-${launches.length}`;
            // The run records its session before the tab host publishes it (BeforeSpawnAsync), so
            // the host first reports no session, as the real child does while the CLI spawns.
            const tab = { tabId: `picker-agent${suffix}`, sessionId: `picker-session${suffix}`, cli: 'codex',
                hasActiveSession: false, startingReads: 1, createdUTC: new Date().toISOString(), workingDirectory: 'C:/fixture' };
            tabs.set(tab.tabId, tab);
            // The root opens a picker tab before it answers, so the response already names it.
            return route.fulfill({ json: { id: `picker-run${suffix}`, status: 'running',
                tabId: tab.tabId, sessionId: tab.sessionId } });
        }
        if (path.startsWith('/api/v1/runs/picker-run')) {
            const suffix = path.slice('/api/v1/runs/picker-run'.length);
            return route.fulfill({ json: { id: `picker-run${suffix}`, status: 'running',
                tabId: `picker-agent${suffix}`, sessionId: `picker-session${suffix}` } });
        }
        if (path === '/api/v1/terminal/tabs' && route.request().method() === 'POST') {
            // "+" opens a backend host with no session: the blank "Select a LLM" placeholder.
            const tab = { tabId: `placeholder-${tabs.size}`, sessionId: null, cli: null,
                hasActiveSession: false, statusAvailable: true, createdUTC: new Date().toISOString(), workingDirectory: 'C:/fixture' };
            tabs.set(tab.tabId, tab);
            return route.fulfill({ json: tab });
        }
        const prune = path.match(/^\/api\/v1\/terminal\/tabs\/([^/]+)\/prune$/);
        if (prune) {
            const tab = tabs.get(prune[1]);
            if (!tab || tab.statusAvailable !== true || tab.hasActiveSession || tab.sessionId || tab.wasStarted)
                return route.fulfill({ status: 409, json: { error: 'The terminal tab is not confirmed unused.' } });
            deleted.push(prune[1]);
            tabs.delete(prune[1]);
            return route.fulfill({ status: 204 });
        }
        const snapshotMatch = path.match(/^\/api\/v1\/agent-tools\/terminal\/([^/]+)\/snapshot$/);
        if (snapshotMatch) {
            const tab = tabs.get(snapshotMatch[1]);
            return route.fulfill({ json: { sessionId: tab.sessionId,
                cols: completedSnapshot.cols, rows: completedSnapshot.rows,
                xterm_ui_bytes: { base64: completedSnapshot.base64, includes_scrollback: true } } });
        }
        const match = path.match(/^\/api\/v1\/terminal\/tabs\/([^/]+)(?:\/status)?$/);
        if (match) {
            if (route.request().method() === 'DELETE') {
                deleted.push(match[1]);
                tabs.delete(match[1]);
                return route.fulfill({ json: { success: true } });
            }
            const tab = tabs.get(match[1]);
            const json = tab ? {
                hasActiveSession: tab.hasActiveSession, sessionId: tab.hasActiveSession ? tab.sessionId : null,
                cli: tab.cli, workingDirectory: tab.workingDirectory
            } : {};
            if (tab?.startingReads > 0 && --tab.startingReads === 0) tab.hasActiveSession = true;
            return route.fulfill({ status: tab ? 200 : 404, json });
        }
        if (path.startsWith('/api/v1/session-replay/sessions/')) {
            if (path.endsWith('/frames')) return route.fulfill({json:{items:[{id:1,at:0,cols:80,rows:24,data:Buffer.from('Completed workflow output\r\n').toString('base64')}],next:1,done:true}});
            return route.fulfill({json:{session:{id:path.split('/').at(-1),cli:'shell',title:'Completed workflow',started:0,ended:1000,directory:''},
                cards:[],prompts:[],changes:[],geometry:[],frameSource:'enriched',frameCount:1,frameMaxId:1,frameBytes:27,proxyMaxId:0,end:1000,notes:[]}});
        }
        const responses = {
            '/api/v1/context': { isInGit: true, rootPath: 'C:/fixture', launchDirectory: 'C:/fixture' },
            '/api/v1/settings': {},
            '/api/v1/environments': { environments: [] },
            '/api/v1/agents': { agents: [] },
            '/api/v1/sandboxes': { sandboxes: [] },
            '/api/v1/llm-picker/preferences': { items: presets.map((p, order) => ({
                key: `preset:${p.id}:${p.cli}`, kind: p.initialPrompt ? 'agent' : 'quick-launch',
                group: 'AI Agents', label: p.name, cli: p.cli, enabled: true, inProject: true, order
            })) },
            '/api/v1/terminal/tabs': { tabs: [...tabs.values()], maxTabs: 100 }
        };
        return route.fulfill({ json: responses[path] || {} });
    });
    await page.goto('/');
    await expect(page.locator('#loading-overlay')).toHaveClass(/\bd-none\b/);
    await expect(page.locator(count)).toHaveText(String(Math.ceil(total / 2)));
    await expect.poll(() => page.evaluate(() =>
        window.app?.terminalController?.manager?.getActiveTab()?.instance?.hasOpenSocket()
    )).toBe(true);
    await expect.poll(() => page.evaluate(() =>
        window.app.terminalController.manager.getActiveTab().instance._initialConnectActive
    )).toBe(false);
    // The terminal's documented initial focus retry runs180ms after connect.
    await page.waitForTimeout(220);
    return { tabs, connections, deleted, launches, emit: (type, payload) => eventSocket.send(JSON.stringify({ type, payload })) };
}

for (const width of [1280, 390]) test(`terminal picker opens a regular Agent tab with controls and restores it at ${width}px`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 850 });
    const fixture = await openFixture(page, 1, [{ id: 'reviewer', name: 'Saved reviewer', cli: 'codex',
        initialPrompt: 'Review the changes.', revision: 4 }]);
    await page.locator('#terminal-header-select-ts-control').click();
    await page.locator('.ts-dropdown .option[data-value="preset:reviewer:codex"]').click();
    await page.locator('#terminal-start-btn').click();
    await expect(page.locator(normalTabs)).toHaveCount(2);
    await expect.poll(() => page.evaluate(() => window.app.terminalController.manager.activeTabId)).toBe('picker-agent');
    const tab = page.locator(normalTabs).filter({ hasText: 'Saved reviewer' });
    await expect(tab).toBeVisible();
    await expect(page.locator('.board-card-editor')).toHaveCount(0);
    expect(fixture.launches).toEqual([expect.objectContaining({
        definitionId: 'reviewer', terminalTab: true, nativeTerminal: false,
        initialPrompt: 'Review the changes.', expectedRevision: 4
    })]);
    await expect(page.locator(count)).toHaveText('1');
    expect(fixture.connections).toContain('picker-agent');
    await tab.hover();
    await expect(tab.getByRole('button', { name: 'Share session', exact: true })).toBeEnabled();
    await page.screenshot({ path: testInfo.outputPath(`terminal-picker-agent-${width}.png`) });
    await page.reload();
    await expect(page.locator(normalTabs)).toHaveCount(2);
    await expect(page.locator(normalTabs).filter({ hasText: 'Saved reviewer' })).toBeVisible();
    await expect.poll(() => page.evaluate(() => window.app.terminalController.manager.activeTabId)).toBe('picker-agent');
    await page.locator(button).click();
    await expect(page.locator('[data-automation-open="automation-1"]')).toBeVisible();
    await expect(page.locator('[data-automation-open="picker-agent"]')).toHaveCount(0);
    await page.keyboard.press('Escape');
    await page.evaluate(() => window.app.terminalController.manager.closeTab('picker-agent'));
    await expect(page.locator(normalTabs)).toHaveCount(1);
    expect(fixture.deleted).not.toContain('picker-agent'); // Normal tabs retain the close/undo grace period.
});

test('terminal picker collects user_message before opening an Agent tab', async ({ page }) => {
    const fixture = await openFixture(page, 1, [{ id: 'reviewer', name: 'Saved reviewer', cli: 'codex',
        initialPrompt: 'Review {{user_message}}', revision: 4 }]);
    await page.locator('#terminal-header-select-ts-control').click();
    await page.locator('.ts-dropdown .option[data-value="preset:reviewer:codex"]').click();
    await page.locator('#terminal-start-btn').click();
    const form = page.locator('[data-prompt-template-modal]');
    await expect(form.getByLabel('User message', { exact: true })).toBeFocused();
    expect(fixture.launches).toHaveLength(0);
    await form.getByRole('button', { name: 'Cancel', exact: true }).click();
    await expect(page.locator('#terminal-start-btn')).toBeEnabled();
    await page.locator('#terminal-start-btn').click();
    await form.getByLabel('User message', { exact: true }).fill('the {{worktree}} example');
    await form.getByRole('button', { name: 'Start Terminal', exact: true }).click();
    await expect(page.locator(normalTabs)).toHaveCount(2);
    expect(fixture.launches).toEqual([expect.objectContaining({
        definitionId: 'reviewer', terminalTab: true, nativeTerminal: false,
        initialPrompt: 'Review {{user_message}}', userMessage: 'the {{worktree}} example', expectedRevision: 4
    })]);
});

test('terminal picker starts the same saved launch again as a second tab', async ({ page }) => {
    // One active run of a definition per project was the rule for every launch, and a picker
    // tab's run stays active while the tab is open: the second Start waited 30 seconds and gave up.
    const fixture = await openFixture(page, 1, [{ id: 'yolo', name: 'codex_yolo', cli: 'codex', initialPrompt: '', revision: 2 }]);
    await page.locator('#terminal-header-select-ts-control').click();
    await page.locator('.ts-dropdown .option[data-value="preset:yolo:codex"]').click();
    await page.locator('#terminal-start-btn').click();
    await expect.poll(() => page.evaluate(() => window.app.terminalController.manager.activeTabId)).toBe('picker-agent');
    // The picker keeps the saved launch selected for the next Start.
    await page.locator('#terminal-start-btn').click();
    await expect.poll(() => fixture.launches.length).toBe(2);
    await expect.poll(() => page.evaluate(() => window.app.terminalController.manager.activeTabId)).toBe('picker-agent-2');
    await expect(page.locator(normalTabs).filter({ hasText: 'codex_yolo' })).toHaveCount(2);
    await expect(page.locator(normalTabs)).toHaveCount(3);
    expect(fixture.connections).toEqual(expect.arrayContaining(['picker-agent', 'picker-agent-2']));
    await expect(page.getByText(/did not start|waiting to start|unusually long/)).toHaveCount(0);
});

for (const width of [1280, 390]) test(`terminal picker shows a steady spinner beside Start while opening at ${width}px`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 850 });
    const fixture = await openFixture(page, 1, [{ id: 'reviewer', name: 'Saved reviewer', cli: 'codex',
        initialPrompt: 'Review the changes.', revision: 4 }]);
    let release;
    const pending = new Promise(resolve => { release = resolve; });
    let requests = 0;
    await page.route('**/api/v1/runs', async route => {
        requests++;
        await pending;
        await route.fallback();
    });
    await page.locator('#terminal-header-select-ts-control').click();
    await page.locator('.ts-dropdown .option[data-value="preset:reviewer:codex"]').click();
    const start = page.locator('#terminal-start-btn');
    const progress = page.locator('#terminal-start-progress');
    const before = await start.boundingBox();
    const panelBefore = await page.locator('.vb-terminal-window-shell').boundingBox();
    await start.click();
    try {
        await expect(start).toBeDisabled();
        await expect(start).toHaveAttribute('aria-busy', 'true');
        await expect(progress).toBeVisible();
        await expect(progress).toHaveText('Opening terminal…');
        expect(await start.boundingBox()).toEqual(before);
        expect(await page.locator('.vb-terminal-window-shell').boundingBox()).toEqual(panelBefore);
        // A routine control refresh and a second programmatic Start cannot clear the guard.
        await page.evaluate(() => {
            const manager = window.app.terminalController.manager;
            manager.updateAddButtonState();
            void manager.startActiveTab();
        });
        await expect(start).toBeDisabled();
        expect(requests).toBe(1);
        await page.screenshot({ path: testInfo.outputPath(`terminal-start-loading-${width}.png`) });
    } finally { release(); }
    await expect.poll(() => fixture.launches.length).toBe(1);
    await expect(start).toBeEnabled();
    await expect(start).toHaveAttribute('aria-busy', 'false');
    await expect(progress).toBeHidden();
    await expect.poll(() => page.evaluate(() => window.app.terminalController.manager.activeTabId)).toBe('picker-agent');
});

test('terminal picker clears its spinner after a failed start and tracks the selected launch', async ({ page }) => {
    await openFixture(page, 1, [{ id: 'reviewer', name: 'Saved reviewer', cli: 'codex',
        initialPrompt: 'Review the changes.', revision: 4 },
        { id: 'writer', name: 'Writer', cli: 'claude', initialPrompt: '', revision: 1 }]);
    let release;
    const pending = new Promise(resolve => { release = resolve; });
    await page.route('**/api/v1/runs', async route => {
        await pending;
        await route.fulfill({ status: 400, json: { error: 'Launch unavailable for this project.' } });
    });
    const choose = async value => {
        await page.locator('#terminal-header-select-ts-control').click();
        await page.locator(`.ts-dropdown .option[data-value="${value}"]`).click();
    };
    await choose('preset:reviewer:codex');
    const start = page.locator('#terminal-start-btn');
    const progress = page.locator('#terminal-start-progress');
    await start.click();
    try {
        await expect(start).toBeDisabled();
        await expect(progress).toBeVisible();
        await choose('preset:writer:claude');
        await expect(start).toBeEnabled();
        await expect(progress).toBeHidden();
        await choose('preset:reviewer:codex');
        await expect(start).toBeDisabled();
        await expect(progress).toBeVisible();
    } finally { release(); }
    await expect(start).toBeEnabled();
    await expect(progress).toBeHidden();
    await expect(page.getByText('Launch unavailable for this project.', { exact: true })).toBeVisible();
});

test('terminal picker launched from a blank placeholder tab shows the new Agent tab', async ({ page }) => {
    const fixture = await openFixture(page, 1, [{ id: 'reviewer', name: 'Code review', cli: 'codex',
        initialPrompt: 'Review the changes.', revision: 4 }]);
    await page.locator('#vb-terminal-tab-add-btn').click();
    await expect(page.locator(normalTabs)).toHaveCount(2);
    await expect(page.getByText('Select a LLM to continue')).toBeVisible();
    await page.locator('#terminal-header-select-ts-control').click();
    await page.locator('.ts-dropdown .option[data-value="preset:reviewer:codex"]').click();
    // The blank tab now holds the pick. It reads as the launch, not as a generic "Saved launch".
    await expect(page.locator(normalTabs).filter({ hasText: 'Code review' })).toHaveCount(1);
    await expect(page.locator(normalTabs).filter({ hasText: 'Saved launch' })).toHaveCount(0);
    await page.locator('#terminal-start-btn').click();
    await expect.poll(() => fixture.launches.length).toBe(1);
    await expect.poll(() => page.evaluate(() => window.app.terminalController.manager.activeTabId)).toBe('picker-agent');
    // The run opened its own tab, so the blank one it was picked in has no job left. It used
    // to stay behind, empty, beside the tab that was running.
    await expect(page.locator(normalTabs)).toHaveCount(2);
    await expect(page.locator(normalTabs).filter({ hasText: 'Code review' })).toHaveCount(1);
    await expect(page.getByText('Select a LLM to continue')).toBeHidden();
    expect(fixture.connections).toContain('picker-agent');
    await expect.poll(() => fixture.deleted.filter(id => id.startsWith('placeholder-')).length).toBe(1);
});

for (const statusAvailable of [false, true]) test(`terminal cleanup preserves a saved-launch tab with ${statusAvailable ? 'stale inactive' : 'unavailable'} status`, async ({ page }) => {
    const fixture = await openFixture(page, 1, [{ id: 'reviewer', name: 'Saved reviewer', cli: 'codex', initialPrompt: '', revision: 1 }]);
    fixture.tabs.set('saved', { tabId: 'saved', hasActiveSession: false, sessionId: null,
        statusAvailable, wasStarted: true, cli: null, createdUTC: new Date().toISOString() });
    await page.evaluate(() => {
        sessionStorage.setItem('viberails_terminal_tab_selection_saved', 'preset:reviewer:codex');
        sessionStorage.setItem('viberails_terminal_tab_meta_saved', JSON.stringify({
            label: 'Generated discussion title', customLabel: false, labelSource: null, taskKey: 'board-card:story'
        }));
    });
    const unsafeDeletes = [];
    page.on('request', request => {
        if (request.method() === 'DELETE' && request.url().includes('/terminal/tabs/')) unsafeDeletes.push(request.url());
    });
    await page.reload();
    await expect.poll(() => page.evaluate(() =>
        window.app?.terminalController?.manager?.getActiveTab()?.instance?.hasOpenSocket())).toBe(true);
    await expect(page.locator('#vb-terminal-tab-list [data-tab-id="saved"]')).toBeVisible();
    expect(fixture.deleted).not.toContain('saved');
    expect(unsafeDeletes).toEqual([]);
    expect(fixture.tabs.has('saved')).toBe(true);
});

for (const renderer of ['webgl', 'canvas']) test(`${renderer} renderer fonts survive the strict canvas font parser in VS Code webviews`, async ({ page }) => {
    // Chromium 156 (Electron 45, VS Code's webview) ignores a canvas font with surrounding
    // whitespace and keeps the 10px sans-serif default. The vendored xterm addons built
    // " normal 26px Menlo, …" for upright text, so every glyph was drawn tiny inside a
    // full-size cell (xterm.js #6297). Playwright's Chromium still accepts the space, so
    // emulate the strict parser and fail on any font it would have dropped.
    await page.addInitScript((preferWebgl) => {
        localStorage.setItem('viberails_terminal_webgl', String(preferWebgl));
        window.__canvasFonts = [];
        window.__droppedCanvasFonts = [];
        for (const proto of [globalThis.CanvasRenderingContext2D?.prototype,
            globalThis.OffscreenCanvasRenderingContext2D?.prototype]) {
            const font = proto && Object.getOwnPropertyDescriptor(proto, 'font');
            if (!font?.set) continue;
            Object.defineProperty(proto, 'font', { ...font, set(value) {
                const text = String(value);
                window.__canvasFonts.push(text);
                if (text !== text.trim()) window.__droppedCanvasFonts.push(text);
                else font.set.call(this, value);
            } });
        }
    }, renderer === 'webgl');
    await openFixture(page, 1);
    await page.evaluate(() => window.app.terminalController.manager.getActiveTab().instance.vibeTerminal.terminal
        .write('plain \x1b[1mbold\x1b[0m \x1b[3mitalic\x1b[0m \x1b[1;3mboth\x1b[0m\r\n'));
    await expect.poll(() => page.evaluate(() =>
        window.app.terminalController.manager.getActiveTab().instance.vibeTerminal.getActiveRenderer())).toBe(renderer);
    // Glyphs rasterize on the next frames; wait until the renderer drew upright and italic text.
    await expect.poll(() => page.evaluate(() => {
        const drawn = window.__canvasFonts.filter(font => /\dpx /.test(font) && /Menlo/.test(font));
        return [drawn.some(font => !/italic/.test(font)), drawn.some(font => /italic/.test(font))];
    })).toEqual([true, true]);
    expect(await page.evaluate(() => window.__droppedCanvasFonts)).toEqual([]);
});

test('finished workflows leave the robot menu even with an active wrapper shell', async ({ page }) => {
    const fixture = await openFixture(page);
    for (const tab of fixture.tabs.values()) if (tab.jobRunId) tab.automationCompleted = true;
    await page.evaluate(() => window.app.terminalController.manager.refreshAutomationTabs());
    await expect(page.locator('#vb-terminal-automations-wrapper')).toBeHidden();
    await expect(page.locator(normalTabs)).toHaveCount(1);
    expect(fixture.connections).toEqual(['ordinary']);
    expect(fixture.deleted).toEqual([]); // The server's guarded cleanup owns host teardown.
});

test('many Automations stay in a scrollable robot menu and attach only when selected', async ({ page }, testInfo) => {
    const fixture = await openFixture(page);
    await expect(page.locator(normalTabs)).toHaveCount(1);
    await expect(page.locator('.xterm-helper-textarea')).toHaveCount(1);
    expect(fixture.connections).toEqual(['ordinary']);
    await expect(page.locator(`${button} .fa-robot`)).toBeVisible();
    await page.locator(button).click();
    await expect(page.locator(menu)).toBeVisible();
    await expect(page.locator(`${menu} .vb-terminal-automation-row`)).toHaveCount(18);
    await expect(page.locator('[data-automation-open="automation-2"]')).toHaveCount(0);
    expect(await page.locator('.vb-terminal-automations-list').evaluate(list => list.scrollHeight > list.clientHeight)).toBe(true);
    await page.screenshot({ path: testInfo.outputPath('automation-menu.png') });
    await page.locator('[data-automation-open="automation-1"]').click();
    await expect.poll(() => page.evaluate(() => window.app.terminalController.manager.activeTabId)).toBe('automation-1');
    await expect.poll(() => fixture.connections.includes('automation-1')).toBe(true);
    await expect(page.locator('#vb-terminal-window-title')).toContainText('Nightly workflow 1');
    await expect(page.locator(normalTabs)).toHaveCount(1);
    expect(fixture.connections.filter(id => id !== 'ordinary')).toEqual(['automation-1']);
});

test('completed Automations leave the group but Board links still open retained output through switching and reload', async ({ page }, testInfo) => {
    const fixture = await openFixture(page, 2);
    await page.locator(button).click();
    await expect(page.locator('[data-automation-open="automation-2"]')).toHaveCount(0);
    await page.keyboard.press('Escape');
    await page.evaluate(() => window.app.terminalController.adoptLaunchedTab('automation-2', { focus: true }));
    const readOutput = () => page.evaluate(() => window.app?.terminalController?.manager?.getActiveTab()?.instance?.vibeTerminal?.getPlainText());
    await expect.poll(readOutput).toContain('Review finding 100');
    expect((await readOutput()).split('\n').map(line => line.trimEnd()).filter(Boolean))
        .toEqual(completedSnapshot.expected);
    await expect(page.locator('#vb-terminal-window-title')).toContainText('Nightly workflow 2');
    expect(await page.evaluate(() => window.app.terminalController.manager.getActiveTab().instance.terminal.options.disableStdin)).toBe(true);
    const viewport = page.locator('.vb-terminal-tab-panel:visible .xterm');
    // Use the real wheel handler: mouse reporting must not swallow scrolling after exit.
    const before = await page.evaluate(() => window.app.terminalController.manager.getActiveTab().instance.terminal.buffer.active.viewportY);
    expect(before).toBeGreaterThan(0);
    await viewport.hover();
    await page.mouse.wheel(0, -1200);
    await expect.poll(() => page.evaluate(() => window.app.terminalController.manager.getActiveTab().instance.terminal.buffer.active.viewportY)).toBeLessThan(before);
    await page.screenshot({ path: testInfo.outputPath('completed-output.png') });
    await page.locator('.vb-terminal-tab-item[data-tab-id="ordinary"] .vb-terminal-tab-button').click();
    await page.evaluate(() => window.app.terminalController.adoptLaunchedTab('automation-2', { focus: true }));
    await expect.poll(readOutput).toContain('Review finding 1');
    await page.reload();
    await expect.poll(readOutput).toContain('Review finding 100');
    await expect(page.locator('iframe[data-session-replay]')).toHaveCount(0);
    expect(fixture.connections).toEqual(['ordinary']);
});

test('switching away and back during a completed write opens a usable replacement viewer', async ({ page }) => {
    const fixture = await openFixture(page, 2);
    await page.evaluate(async () => {
        const { VibeTerminal } = await import('/js/modules/vibe-terminal.js');
        const writeAsync = VibeTerminal.prototype.writeAsync;
        let held = false;
        VibeTerminal.prototype.writeAsync = function (bytes) {
            const write = writeAsync.call(this, bytes);
            if (held) return write;
            held = true;
            return write.then(() => new Promise(resolve => { window.releaseCompletedWrite = resolve; }));
        };
        window.firstCompletedOpen = window.app.terminalController.manager.openAutomationTab('automation-2');
    });
    await expect.poll(() => page.evaluate(() => typeof window.releaseCompletedWrite)).toBe('function');
    await page.locator('.vb-terminal-tab-item[data-tab-id="ordinary"] .vb-terminal-tab-button').click();
    await page.evaluate(() => window.app.terminalController.adoptLaunchedTab('automation-2', { focus: true }));
    await expect.poll(() => page.evaluate(() =>
        window.app.terminalController.manager.getActiveTab().instance._completedOutputSessionId
    )).toBe(fixture.tabs.get('automation-2').sessionId);
    await page.evaluate(async () => {
        window.releaseCompletedWrite();
        await window.firstCompletedOpen;
    });
    const output = await page.evaluate(() =>
        window.app.terminalController.manager.getActiveTab().instance.vibeTerminal.getPlainText());
    expect(output.split('\n').map(line => line.trimEnd()).filter(Boolean)).toEqual(completedSnapshot.expected);
    expect(fixture.connections).toEqual(['ordinary']);
});

test('Automation events update the count without stealing focus and survive a view remount', async ({ page }) => {
    const fixture = await openFixture(page, 2);
    const added = { ...fixture.tabs.get('automation-1'), tabId: 'automation-new', jobRunId: 'run-new', automationName: 'New workflow' };
    fixture.tabs.set(added.tabId, added);
    fixture.emit('automation_terminal_started', { ...added, jobName: added.automationName });
    await expect(page.locator(count)).toHaveText('2');
    expect(await page.evaluate(() => window.app.terminalController.manager.activeTabId)).toBe('ordinary');
    expect(fixture.connections).toEqual(['ordinary']);
    await page.evaluate(() => window.app.navigate('settings'));
    await page.evaluate(() => window.app.navigate('terminal-focus'));
    await expect(page.locator(count)).toHaveText('2');
    await expect(page.locator(normalTabs)).toHaveCount(1);
    await page.locator(button).click();
    await expect(page.locator('[data-automation-open="automation-new"]')).toContainText('New workflow');
});

test('closing an Automation leaves the ordinary undo-close menu independent', async ({ page }) => {
    const fixture = await openFixture(page, 3);
    await page.locator('.vb-terminal-tab-item[data-tab-id="ordinary"]').hover();
    await page.locator('[data-tab-id="ordinary"] .vb-terminal-tab-close').click();
    await expect(page.locator('#vb-terminal-tab-undo-count')).toHaveText('1');
    await page.locator(button).click();
    await page.locator('[data-automation-close="automation-1"]').click();
    await expect(page.locator(count)).toHaveText('1');
    expect(fixture.deleted).toEqual(['automation-1']);
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

test('completion retains the selected Automation output and an unavailable host stays unavailable', async ({ page }) => {
    const fixture = await openFixture(page, 2);
    await page.locator(button).click();
    await page.locator('[data-automation-open="automation-1"]').click();
    await expect.poll(() => fixture.connections.includes('automation-1')).toBe(true);
    const finished = fixture.tabs.get('automation-1');
    finished.hasActiveSession = false;
    fixture.emit('session_completed', { tabId: finished.tabId, sessionId: finished.sessionId, exitCode: 0 });
    fixture.tabs.get('automation-2').statusAvailable = false;
    await expect.poll(() => page.evaluate(() => window.app.terminalController.manager.getActiveTab().instance.vibeTerminal?.getPlainText())).toContain('Review finding 100');
    expect(await page.evaluate(() => window.app.terminalController.manager.activeTabId)).toBe('automation-1');
    await page.locator(button).click();
    await expect(page.locator('[data-automation-open="automation-1"]')).toHaveCount(0);
    await expect(page.locator('[data-automation-open="automation-2"]')).toContainText('Unavailable');
    await page.locator('[data-automation-open="automation-2"]').click();
    await expect(page.getByText('This Automation terminal is temporarily unavailable. Try again shortly.', { exact: true })).toBeVisible();
    await expect(page.getByRole('combobox', { name: 'Replay speed' })).toHaveCount(0);
    expect(fixture.connections).toEqual(['ordinary', 'automation-1']);
});

test('confirmed Automation closure removes its selected viewer and returns to the ordinary terminal', async ({ page }) => {
    const fixture = await openFixture(page, 2);
    await page.locator(button).click();
    await page.locator('[data-automation-open="automation-1"]').click();
    await expect.poll(() => page.evaluate(() => window.app.terminalController.manager.activeTabId)).toBe('automation-1');
    fixture.tabs.delete('automation-1');
    fixture.emit('automation_terminal_closed', { tabId: 'automation-1' });
    await expect.poll(() => page.evaluate(() => window.app.terminalController.manager.activeTabId)).toBe('ordinary');
    await expect.poll(() => page.evaluate(() => window.app.terminalController.manager.tabs.has('automation-1'))).toBe(false);
    await expect(page.locator(normalTabs)).toHaveCount(1);
    await expect(page.locator(button)).toBeHidden();
    await expect(page.locator(count)).toHaveText('0');
    await expect(page.locator('[data-automation-open="automation-1"]')).toHaveCount(0);
    expect(fixture.deleted).toEqual([]); // The server already removed the host; no second DELETE.
});

test('the last finished agent closes the group, preserves recordings and stays absent after reload', async ({ page }) => {
    const fixture = await openFixture(page, 2);
    await page.locator(button).click();
    await page.locator('[data-automation-open="automation-1"]').focus();
    const finished = fixture.tabs.get('automation-1');
    finished.hasActiveSession = false;
    fixture.emit('session_completed', { tabId: finished.tabId, sessionId: finished.sessionId, exitCode: 0 });
    await expect(page.locator(button)).toBeHidden();
    await expect(page.locator(menu)).toBeHidden();
    await expect(page.locator(count)).toHaveText('0');
    await expect.poll(() => page.evaluate(() => document.activeElement === window.app.terminalController.manager.tabAdd)).toBe(true);
    expect(fixture.deleted).toEqual([]);
    expect(fixture.tabs.size).toBe(3);
    expect(await page.evaluate(() => window.app.terminalController.manager.automationTabs.size)).toBe(2);
    await page.reload();
    await expect(page.locator('#loading-overlay')).toHaveClass(/\bd-none\b/);
    await expect(page.locator(button)).toBeHidden();
    await expect(page.locator(normalTabs)).toHaveCount(1);
    await page.evaluate(() => window.app.terminalController.adoptLaunchedTab('automation-1', { focus: true }));
    await expect.poll(() => page.evaluate(() => window.app.terminalController.manager.getActiveTab()?.instance?.vibeTerminal?.getPlainText()))
        .toContain('Review finding 100');
    expect(fixture.connections.every(id => id === 'ordinary')).toBe(true);
});

test('finishing a focused menu row moves keyboard focus to the next running agent', async ({ page }) => {
    const fixture = await openFixture(page, 3);
    await page.locator(button).click();
    await page.locator('[data-automation-close="automation-1"]').focus();
    fixture.tabs.get('automation-1').hasActiveSession = false;
    // The fallback status refresh also catches a missed completion event.
    await page.evaluate(() => window.app.terminalController.manager.refreshAutomationTabs());
    await expect(page.locator(count)).toHaveText('1');
    await expect(page.locator('[data-automation-open="automation-1"]')).toHaveCount(0);
    await expect(page.locator('[data-automation-open="automation-3"]')).toBeFocused();
    expect(fixture.deleted).toEqual([]);
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
