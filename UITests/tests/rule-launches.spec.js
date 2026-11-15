const { test, expect } = require('@playwright/test');

async function setup(page) {
    const rule = { sourcePath: 'nested/vc.rules.md', ruleText: "File Lock('protected <file>.txt')" };
    const state = {
        preset: { id: 'agent', name: 'Respond to violations', cli: 'codex', initialPrompt: 'Inspect the change.', enabled: true, revision: 1 },
        bindings: [], requests: [], failRules: false, conflict: false,
        agents: [{ path: 'C:\\fixture\\nested\\vc.rules.md', rules: [{ text: rule.ruleText, enforcement: 'STOP' }] }]
    };
    await page.addInitScript(() => sessionStorage.setItem('viberails_tab', 'fixture'));
    await page.routeWebSocket('**/api/v1/events/ws*', () => {});
    await page.route('**/api/v1/**', async route => {
        const request = route.request(), path = new URL(request.url()).pathname, body = request.postDataJSON();
        state.requests.push({ path, body, method: request.method() });
        const ok = json => route.fulfill({ json });
        if (path === '/api/v1/context') return ok({ isInGit: true, rootPath: 'C:\\fixture', launchDirectory: 'C:\\fixture', isActiveRootBackend: true });
        if (path === '/api/v1/launch-catalog') return ok({ items: [{ ...state.preset, definitionType: 'preset', kind: 'agent', canLaunch: true, canRunAutomatically: true, supportsAttachments: true }] });
        if (path === '/api/v1/launch-projects') return ok([{ id: 'C:\\FIXTURE', path: 'C:\\fixture', name: 'Fixture', isCurrent: true }]);
        if (path === '/api/v1/llm-picker/preferences') return ok({ items: [{ key: 'base:codex', kind: 'base', group: 'Base CLIs', label: 'Codex', cli: 'codex', enabled: true, order: 0 }] });
        if (path.includes('/launch-defaults')) return ok({ model: '', effort: '', speed: '', mode: '', yolo: false });
        if (path === '/api/v1/presets/agent') {
            if (body) state.preset = { ...state.preset, ...body, revision: state.preset.revision + 1 };
            return ok(body ? { preset: state.preset, affectedBindings: [] } : state.preset);
        }
        if (path.endsWith('/attachments')) return ok([]);
        if (path.startsWith('/api/v1/trigger-bindings')) {
            if (!body) return ok(state.bindings);
            if (state.conflict) return route.fulfill({ status: 409, json: { error: 'Binding changed. Reload before saving.', code: 'revision_conflict' } });
            const value = { ...body, id: 'binding', revision: (state.bindings[0]?.revision || 0) + 1 };
            state.bindings = [value];
            return ok(value);
        }
        if (path === '/api/v1/agents') return state.failRules
            ? route.fulfill({ status: 503, json: { error: 'Rules unavailable' } }) : ok({ agents: state.agents });
        if (path === '/api/v1/runs') return ok({ runs: [], nextCursor: null });
        if (path === '/api/v1/rules/details') return ok({ rules: [] });
        if (path === '/api/v1/board/boards') return ok([]);
        if (path === '/api/v1/python-scripts') return ok({ scripts: [] });
        if (path.includes('terminal-tabs')) return ok({ tabs: [] });
        return ok({});
    });
    await page.goto('/?view=ai-agents');
    await page.getByRole('button', { name: 'Edit Respond to violations', exact: true }).click();
    return { state, rule, editor: page.locator('[data-launch-editor]') };
}

for (const width of [320, 375, 768, 1024]) test(`save and disable rule response at ${width}px`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 900 });
    const { state, rule, editor } = await setup(page);
    await editor.getByRole('button', { name: 'Create schedule or event' }).click();
    await editor.getByRole('combobox', { name: 'Trigger', exact: true }).selectOption('6');
    const select = editor.getByRole('combobox', { name: 'Repository rule', exact: true });
    await select.selectOption(JSON.stringify(rule));
    await expect(select).toHaveValue(JSON.stringify(rule));
    await editor.getByRole('button', { name: 'Save', exact: true }).click();
    await expect.poll(() => state.bindings.length).toBe(1);
    expect(state.bindings[0]).toMatchObject({ definitionId: 'agent', definitionType: 'preset', trigger: { kind: 6 }, rule, enabled: true });
    expect(state.bindings[0].trigger).toEqual({ kind: 6 });
    expect(state.requests.filter(request => request.path === '/api/v1/runs' && request.method === 'POST')).toHaveLength(0);
    await select.scrollIntoViewIfNeeded();
    await select.focus();
    await page.screenshot({ path: testInfo.outputPath(`rule-response-${width}.png`) });
    expect(await editor.evaluate(node => node.scrollWidth <= node.clientWidth)).toBe(true);
    await editor.locator('[data-binding-field="enabled"]').uncheck();
    await editor.getByRole('button', { name: 'Save', exact: true }).click();
    await expect.poll(() => state.bindings[0].enabled).toBe(false);
    await editor.getByRole('button', { name: 'Cancel', exact: true }).click();
    await page.getByRole('button', { name: 'Edit Respond to violations', exact: true }).click();
    await expect(editor.getByRole('combobox', { name: 'Repository rule', exact: true })).toHaveValue(JSON.stringify(rule));
    await expect(editor.locator('[data-binding-field="enabled"]')).not.toBeChecked();
});

test('missing rule and load failures retain selection, conflict keeps draft', async ({ page }) => {
    const { state, rule, editor } = await setup(page);
    await editor.getByRole('button', { name: 'Create schedule or event' }).click();
    await editor.getByRole('combobox', { name: 'Trigger', exact: true }).selectOption('6');
    await editor.getByRole('combobox', { name: 'Repository rule', exact: true }).selectOption(JSON.stringify(rule));
    await editor.getByRole('button', { name: 'Save', exact: true }).click();
    await expect.poll(() => state.bindings.length).toBe(1);
    state.agents = [];
    await editor.getByRole('button', { name: 'Refresh rules' }).click();
    await expect(editor.locator('[data-binding-rule] option:checked')).toContainText('not found');
    state.failRules = true;
    await editor.getByRole('button', { name: 'Refresh rules' }).click();
    await expect(editor.locator('[data-rule-load-status]')).toContainText('Could not load rules');
    state.conflict = true;
    await editor.locator('[data-binding-field="enabled"]').uncheck();
    await editor.getByRole('button', { name: 'Save', exact: true }).click();
    await expect(editor.locator('[data-launch-error]')).toContainText('Binding changed');
    await expect(editor.locator('[data-binding-field="enabled"]')).not.toBeChecked();
    await expect(editor.locator('[data-binding-rule]')).toHaveValue(JSON.stringify(rule));
});
