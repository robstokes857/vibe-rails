const { test, expect } = process.env.VIBERAILS_SCRIPTS_STATIC === '1'
    ? require('@playwright/test') : require('./fixtures');

async function openScripts(page, { expandSigned = true } = {}) {
    await page.addInitScript(() => {
        sessionStorage.setItem('viberails_tab', 'scripts-fixture');
        sessionStorage.setItem('viberails_terminal_active_tab_id', 'agent');
        // A stored side-navigation preference must not bring the rail back.
        localStorage.setItem('viberails_nav_layout', 'side');
        localStorage.setItem('viberails_sidebar_collapsed', 'true');
    });
    await page.routeWebSocket('**/api/v1/events/ws*', () => {});
    await page.routeWebSocket('**/api/v1/terminal/tabs/*/ws*', socket => {
        socket.send(Buffer.from('Agent ready\r\n'));
    });
    const script = { name: 'example.py', path: 'C:/fixture/scripts/example.py',
        status: 'approved', sizeBytes: 123, modifiedUtc: '2026-10-03T01:00:00Z' };
    const scripts = Array.from({ length: 40 }, (_, i) => i ? { ...script, name: `script-${i}.py` } : script);
    const agent = { tabId: 'agent', sessionId: 'agent-session', cli: 'codex',
        hasActiveSession: true, workingDirectory: 'C:/fixture/scripts' };
    await page.route('**/api/v1/**', route => {
        const path = new URL(route.request().url()).pathname;
        const name = new URL(route.request().url()).searchParams.get('name') || script.name;
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
                scripts },
            '/api/v1/python-scripts/content': { ...script, name, version: 'one', content: `print("${name}")\n`.repeat(150) },
            '/api/v1/python-scripts/run': { exitCode: 0, durationMs: 25,
                standardOutput: Array.from({ length: 100 }, (_, i) => `Output line ${i + 1}`).join('\n'), standardError: '' },
            '/api/v1/terminal/tabs': { tabs: [agent], maxTabs: 100 },
            '/api/v1/terminal/tabs/agent/status': agent
        };
        return route.fulfill({ json: responses[path] || {} });
    });
    await page.goto('/?view=jobs');
    await expect(page.locator('.python-script-row')).toHaveCount(40);
    return scripts;
}

async function expectTerminalFits(page, minimumHeight = 100) {
    await expect.poll(() => page.evaluate(minimumHeight => {
        const terminal = document.querySelector('.xterm-screen')?.getBoundingClientRect();
        return Boolean(terminal && terminal.height > minimumHeight && terminal.bottom <= innerHeight && terminal.top >= 0);
    }, minimumHeight)).toBe(true);
    expect(await page.evaluate(() => document.documentElement.scrollHeight - innerHeight)).toBeLessThanOrEqual(1);
}

test('one selected script fills the editor and Back opens the list to choose another', async ({ page }, testInfo) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await openScripts(page);
    await page.locator('.python-script-name').first().click();
    await expect(page.locator('.monaco-editor')).toBeVisible();
    await expect(page.locator('[data-workbench-name]')).toHaveText('example.py');
    for (const width of [1440, 1000, 720]) {
        await page.setViewportSize({ width, height: 900 });
        await expect(page.locator('[data-workbench-rail], [data-workbench-open]')).toHaveCount(0);
        await expect(page.locator('[data-workbench-editor-mount]')).toHaveCount(1);
        await expect.poll(() => page.locator('[data-workbench-editor-host]').evaluate(node => {
            const editor = node.getBoundingClientRect();
            const body = node.parentElement.getBoundingClientRect();
            return Math.abs(editor.left - body.left) + Math.abs(editor.right - body.right);
        })).toBeLessThanOrEqual(1);
    }
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.screenshot({ path: testInfo.outputPath('single-script-editor.png') });
    await page.locator('[data-action="go-back"]').click();
    await expect(page.locator('.python-script-row')).toHaveCount(40);
    await page.locator('.python-script-name').filter({ hasText: /\bscript-1\.py\b/ }).click();
    await expect(page.locator('[data-workbench-name]')).toHaveText('script-1.py');
    await expect.poll(() => page.evaluate(() => window.app.pythonScriptWorkbench.editor.getValue()))
        .toContain('print("script-1.py")');
    await expect(page.locator('[data-workbench-editor-mount]')).toHaveCount(1);
});

test('verbose run output leaves the editor usable under the top navigation', async ({ page }, testInfo) => {
        await page.setViewportSize({ width: 1000, height: 500 });
        await openScripts(page);
        await page.locator('.python-script-name').first().click();
        await expect(page.locator('.monaco-editor')).toBeVisible();
        await page.locator('[data-workbench-action="run"]').click();
        await expect(page.locator('[data-run-result]')).toContainText('Output line 100');
        await page.locator('.vb-run-footer [data-run-action="close"]').click();
        const output = page.locator('[data-workbench-output]');
        await expect(output).toHaveAttribute('open', '');
        for (const height of [500, 400]) {
            await page.setViewportSize({ width: 1000, height });
            await expect.poll(() => page.locator('[data-workbench-editor-host]').evaluate(
                node => node.getBoundingClientRect().height)).toBeGreaterThanOrEqual(80);
            await expectTerminalFits(page, height === 400 ? 64 : 100);
            // All output remains available inside the drawer without scrolling the page.
            await output.scrollIntoViewIfNeeded();
            expect((await output.boundingBox()).height).toBeGreaterThanOrEqual(72);
            await output.evaluate(node => { node.scrollTop = node.scrollHeight; });
            await expect.poll(() => output.evaluate(node => node.scrollTop)).toBeGreaterThan(0);
            const summary = output.locator('summary');
            await expect(summary).toBeInViewport();
            await summary.click();
            await expect(output).not.toHaveAttribute('open', '');
            await summary.click();
            await expect(output).toHaveAttribute('open', '');
        }
        await output.scrollIntoViewIfNeeded();
        await page.screenshot({ path: testInfo.outputPath('output-top.png') });
});

test('terminal prompt stays in the viewport under the top navigation', async ({ page }, testInfo) => {
        await page.setViewportSize({ width: 1440, height: 900 });
        await openScripts(page);
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
        await page.screenshot({ path: testInfo.outputPath('workbench-top.png') });
        await page.locator('[data-action="go-back"]').click();
        await expect(page.locator('[data-python-scripts-root]')).toBeVisible();
        expect(await page.evaluate(() => document.body.classList.contains('vb-python-workbench-active'))).toBe(false);
});

test('script actions align and language guidance stays readable on narrow screens', async ({ page }, testInfo) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    const scripts = await openScripts(page, { expandSigned: false });
    await expect(page.locator('.python-script-row').first()).toBeVisible();
    scripts[0].status = 'modified';
    await page.locator('[data-python-scripts-action="refresh"]').click();
    const group = page.locator('[data-unsigned-scripts]');
    await expect(group.locator(':scope > summary')).toHaveText('Unsigned scripts1');
    await expect(page.locator('[data-python-script="example.py"]')).toBeHidden();
    await group.locator(':scope > summary').focus();
    await page.keyboard.press('Space');
    await expect(page.locator('[data-python-script="example.py"]')).toBeVisible();
    await page.locator('[data-python-scripts-action="refresh"]').click();
    await expect(group).toHaveJSProperty('open', true);
    await group.locator(':scope > summary').click();
    await expect(page.locator('[data-python-script="example.py"]')).toBeHidden();
    await expect(page.locator('[data-python-script="script-1.py"]')).toBeVisible();
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

test('New script says pwsh, bash or python, keeps the extension in step and opens with shell highlighting', async ({ page }, testInfo) => {
    await page.setViewportSize({ width: 1280, height: 860 });
    await openScripts(page);
    const created = [];
    const deploy = { id: 'unique-deploy.sh', name: 'deploy.sh', displayName: 'Start my app', scope: 'repo',
        path: 'C:/fixture/repo/deploy.sh', status: 'unapproved',
        sizeBytes: 64, modifiedUtc: '2026-10-03T01:00:00Z' };
    const list = () => ({ scriptsDirectory: 'C:/fixture/scripts', pinConfigured: true, scripts: created.length ? [deploy] : [] });
    // Registered after openScripts, so these win over its catch-all fixture.
    await page.route('**/api/v1/python-scripts/create', route => {
        created.push(route.request().postDataJSON());
        return route.fulfill({ json: list() });
    });
    await page.route('**/api/v1/python-scripts', route => route.fulfill({ json: list() }));
    await page.route('**/api/v1/python-scripts/content*', route =>
        route.fulfill({ json: { ...deploy, version: 'one', content: created[0]?.content || '' } }));

    await page.locator('[data-python-scripts-action="new"]').first().click();
    const modal = page.locator('.python-scripts-pin-modal');
    await expect(modal.locator('.modal-title')).toHaveText('New script');
    await expect(modal).toContainText('Choose pwsh, bash, or python');
    const runtime = modal.locator('select[data-pin-field="runtime"]');
    const name = modal.locator('input[data-pin-field="name"]');
    await expect(runtime.locator('option')).toHaveText(['python — Python (.py)', 'pwsh — PowerShell (.ps1)', 'bash — Bash (.sh)']);
    await expect(name).toBeFocused();
    await expect(name).toHaveValue('script.py');
    const displayName = modal.locator('[data-pin-field="displayName"]');
    await expect(displayName).toHaveValue('script.py');

    await runtime.selectOption('bash');
    await expect(name).toHaveValue('script.sh');
    await expect(displayName).toHaveValue('script.sh');
    await displayName.fill('Start my app');
    await name.fill('deploy.ps1');
    await expect(runtime).toHaveValue('pwsh');
    await page.screenshot({ path: testInfo.outputPath('new-script-dialog.png') });
    await name.fill('deploy');
    await runtime.selectOption('bash');
    await expect(name).toHaveValue('deploy.sh');
    await expect(displayName).toHaveValue('Start my app');
    await page.evaluate(() => {
        window.app.pickFileSystemEntry = async options => {
            window.scriptPickerOptions = options;
            return { path: 'C:/fixture/repo' };
        };
    });
    await modal.getByRole('button', { name: 'Browse…' }).click();
    await expect(modal.locator('[data-pin-field="directory"]')).toHaveValue('C:/fixture/repo');
    await modal.locator('[data-pin-field="directory"]').fill('C:/fixture/repo/');
    await modal.locator('[data-pin-field="requirePinEachRun"]').selectOption('true');
    await modal.getByRole('button', { name: 'Create and edit' }).click();

    await expect.poll(() => created.length).toBe(1);
    expect(created[0].name).toBe('deploy.sh');
    expect(created[0]).toMatchObject({ displayName: 'Start my app', directory: 'C:/fixture/repo/', scope: 'repo', requirePinEachRun: true });
    expect(await page.evaluate(() => window.scriptPickerOptions.mode)).toBe('directory');
    expect(created[0].content.startsWith('#!/usr/bin/env bash\n')).toBe(true);
    await expect(page.locator('.monaco-editor')).toBeVisible();
    await expect(page.locator('[data-workbench-name]')).toHaveText('Start my app');
    await expect.poll(() => page.evaluate(() => window.monaco?.editor.getModels().map(model => model.getLanguageId())))
        .toContain('shell');
    await expect(page.locator('.python-workbench-identity > i')).toHaveClass(/fa-terminal/);
    await expect(page.locator('[data-workbench-meta]')).toContainText('runs with bash');
    await page.screenshot({ path: testInfo.outputPath('new-script-workbench.png') });
});

test('Add from disk keeps the selected path and exposes display, scope and run PIN settings', async ({ page }, testInfo) => {
    await page.setViewportSize({ width: 1100, height: 800 });
    await openScripts(page);
    let registered = null;
    let importBody = null;
    let settingsBody = null;
    const list = () => ({ scriptsDirectory: 'C:/fixture/scripts/UserScripts', pinConfigured: true,
        scripts: registered ? [registered] : [] });
    await page.route('**/api/v1/python-scripts', route => route.fulfill({ json: list() }));
    await page.route('**/api/v1/python-scripts/import', route => {
        importBody = route.request().postDataJSON();
        registered = { id: 'external-launch.ps1', name: 'launch.ps1', path: importBody.sourcePath,
            displayName: importBody.displayName, scope: importBody.scope, requirePinEachRun: importBody.requirePinEachRun,
            status: 'unapproved', sizeBytes: 20 };
        return route.fulfill({ json: list() });
    });
    await page.route('**/api/v1/python-scripts/settings', route => {
        settingsBody = route.request().postDataJSON();
        registered = { ...registered, displayName: settingsBody.displayName };
        return route.fulfill({ json: list() });
    });
    await page.route('**/api/v1/python-scripts/content*', route => route.fulfill({
        json: { name: 'external-launch.ps1', content: 'Write-Output 1', version: 'one', status: 'unapproved' }
    }));
    await page.evaluate(() => {
        window.app.pickFileSystemEntry = async () => ({ path: 'C:/fixture/repo/launch.ps1', name: 'launch.ps1' });
    });
    await page.getByRole('button', { name: 'Add from disk' }).first().click();
    const modal = page.locator('.python-scripts-pin-modal');
    await expect(modal.locator('[data-pin-field="displayName"]')).toHaveValue('launch.ps1');
    await modal.locator('[data-pin-field="displayName"]').fill('Launch Front');
    await modal.locator('[data-pin-field="scope"]').selectOption('global');
    await modal.locator('[data-pin-field="requirePinEachRun"]').selectOption('true');
    await modal.getByRole('button', { name: 'Add script', exact: true }).click();
    await expect(page.locator('[data-workbench-name]')).toHaveText('Launch Front');
    expect(importBody).toEqual({ sourcePath: 'C:/fixture/repo/launch.ps1', displayName: 'Launch Front',
        scope: 'global', requirePinEachRun: true });
    await expect(page.locator('[data-workbench-meta]')).toContainText('C:/fixture/repo/launch.ps1');
    await page.locator('[data-action="go-back"]').click();
    await expect(page.locator('.python-script-row')).toBeHidden();
    await page.locator('[data-unsigned-scripts] > summary').click();
    await page.locator('[data-python-scripts-action="menu"]').click();
    await page.getByRole('menuitem', { name: 'Script settings…' }).click();
    await expect(modal.locator('[data-pin-field="requirePinEachRun"]')).toHaveValue('true');
    await modal.locator('[data-pin-field="displayName"]').fill('Start Front');
    await modal.getByRole('button', { name: 'Save', exact: true }).click();
    await expect(page.locator('.python-script-name')).toHaveText('Start Front');
    expect(settingsBody).toEqual({ name: 'external-launch.ps1', displayName: 'Start Front', scope: 'global', requirePinEachRun: true });
    await page.screenshot({ path: testInfo.outputPath('registered-external-script.png') });
});
