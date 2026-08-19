// @ts-check
//
// Signed Python scripts: the Automation page section and the /api/v1/python-scripts
// surface. READ-ONLY on purpose — the signing state (PIN + approvals) lives in the
// developer's real ~/.vibe_rails, so this spec never sets a PIN, approves, revokes,
// or runs a real script. Mutation paths are exercised by PythonScriptServiceTests
// against a temp directory instead.

const { test, expect } = require('./fixtures');

const API = '/api/v1/python-scripts';

test('status endpoint reports the scripts folder and signing state', async ({ context }) => {
    const response = await context.request.get(API);
    expect(response.ok()).toBeTruthy();
    const body = await response.json();
    expect(typeof body.pinConfigured).toBe('boolean');
    expect(String(body.scriptsDirectory)).toContain('.vibe_rails');
    expect(Array.isArray(body.scripts)).toBeTruthy();
    for (const script of body.scripts) {
        expect(['approved', 'modified', 'unapproved']).toContain(script.status);
    }
});

test('run refuses scripts that are not signed', async ({ context }) => {
    // A name that cannot exist in the catalog: validation rejects it before anything runs.
    const traversal = await context.request.post(`${API}/run`, { data: { name: '../nope.py' } });
    expect(traversal.status()).toBe(400);

    const ghost = await context.request.post(`${API}/run`, {
        data: { name: `vb-e2e-ghost-${Date.now()}.py` }
    });
    expect(ghost.status()).toBe(400);
});

test('approve without a valid PIN is always refused', async ({ context }) => {
    const status = await (await context.request.get(API)).json();
    // Whether or not a PIN is configured, a wrong/absent PIN must never approve.
    const response = await context.request.post(`${API}/approve`, {
        data: { name: 'anything.py', pin: 'definitely-not-the-pin' }
    });
    expect(response.status()).toBe(400);
    expect(status.pinConfigured === true || status.pinConfigured === false).toBeTruthy();
});

test('Automation page shows the Python scripts section', async ({ page, context }) => {
    const status = await (await context.request.get(API)).json();
    await page.goto('/?view=terminal-focus', { waitUntil: 'domcontentloaded' });
    await page.locator('.app-subnav-link[data-view="jobs"]:visible').click();

    const section = page.locator('[data-python-scripts-root]');
    await expect(section).toBeVisible({ timeout: 15_000 });
    await expect(section.locator('#python-scripts-title')).toContainText('Python scripts');
    await expect(section.locator('[data-python-scripts-dir]')).toContainText('.vibe_rails', { timeout: 10_000 });
    await expect(section.locator('[data-python-scripts-pin-label]'))
        .toHaveText(status.pinConfigured ? 'Change PIN' : 'Set PIN');

    if (status.scripts.length > 0) {
        await expect(section.locator('.python-script-row')).toHaveCount(status.scripts.length);
    } else {
        await expect(section.locator('.jobs-empty')).toContainText('No Python scripts yet');
    }

    // Every script surfaces in the nav Launch flyout catalog, carrying its signing status
    // (the flyout disables the unsigned ones rather than dropping them).
    const prefs = await (await context.request.get('/api/v1/automation-nav/preferences')).json();
    const scriptItems = prefs.items.filter((item) => item.kind === 'script');
    expect(scriptItems.map((item) => item.label).sort()).toEqual(
        status.scripts.map((script) => script.name).sort());
    expect(scriptItems.map((item) => [item.label, item.status]).sort()).toEqual(
        status.scripts.map((script) => [script.name, script.status]).sort());
});

test('Opening a script lands in the workbench: editor over an agent terminal in the scripts folder', async ({ page, context }) => {
    // READ-ONLY like the rest of this file: open, look, press Escape, go back. Never
    // save/sign/run/rename/delete — the scripts are the developer's real files.
    const status = await (await context.request.get(API)).json();
    test.skip(status.scripts.length === 0, 'needs at least one script in ~/.vibe_rails/scripts');

    // Same way in as the section test: the Automation page, then the first row's name.
    await page.goto('/?view=terminal-focus', { waitUntil: 'domcontentloaded' });
    await page.locator('.app-subnav-link[data-view="jobs"]:visible').click();
    const section = page.locator('[data-python-scripts-root]');
    await expect(section).toBeVisible({ timeout: 15_000 });
    const firstRow = section.locator('.python-script-row').first();
    await expect(firstRow).toBeVisible({ timeout: 10_000 });
    const scriptName = (await firstRow.getAttribute('data-python-script')) || '';
    await firstRow.locator('.python-script-name').click();

    // The workbench view: identity, Monaco with the file loaded, docked terminal.
    const workbench = page.locator('[data-python-workbench]');
    await expect(workbench).toBeVisible({ timeout: 15_000 });
    await expect(workbench.locator('[data-workbench-name]')).toHaveText(scriptName);
    const editor = workbench.locator('.monaco-editor');
    await expect(editor).toBeVisible({ timeout: 20_000 });
    await expect(workbench.locator('[data-workbench-editor-state]')).toBeHidden({ timeout: 20_000 });
    // Monaco paints its lines a frame after the text lands, so wait rather than count.
    await expect(editor.locator('.view-line').first()).toBeAttached({ timeout: 10_000 });
    // The terminal panel is pinned to the scripts directory (the header chip names the folder).
    const folder = String(status.scriptsDirectory).split(/[\\/]/).filter(Boolean).pop();
    await expect(workbench.locator('.vb-terminal-project-path')).toContainText(folder, { timeout: 10_000 });

    // Escape inside Monaco belongs to Monaco (suggest widget etc.), never "Back".
    await editor.locator('textarea').first().focus();
    await page.keyboard.press('Escape');
    await expect(workbench).toBeVisible();
    await expect(section).toHaveCount(0);

    // Back returns to the Automation page (nothing was edited, so no confirm).
    await page.locator('[data-view="python-script"] [data-action="go-back"]').click();
    await expect(page.locator('[data-python-scripts-root]')).toBeVisible({ timeout: 15_000 });
});
