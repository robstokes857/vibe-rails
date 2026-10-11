// Quick Launches are saved CLI setups without a starting message. The terminal picker's footer
// manages them; a setup with a message is an Agent, carried through the preference save but never
// listed there. Static frontend with mocked APIs: nothing on this machine is read or written.
const { test, expect } = process.env.VIBERAILS_QUALITY_STATIC === '1'
    ? require('@playwright/test')
    : require('./fixtures');

const PREFERENCES = '/api/v1/llm-picker/preferences';

function fixtureEnvironments() {
    return [
        { id: 901, name: 'Fast Opus', cli: 'claude', customArgs: '--model claude-opus-5-5', customPrompt: '', hidden: false },
        { id: 902, name: 'Nightly reviewer', cli: 'codex', customArgs: '', customPrompt: 'Review the branch.', hidden: false }
    ];
}

// The server's catalog strings are echoed back on save; the page regroups them for display only.
function catalogFor(environments) {
    return [
        { key: 'base:claude', kind: 'base', group: 'Base CLIs', label: 'Claude', cli: 'claude', enabled: true, order: 0 },
        { key: 'base:codex', kind: 'base', group: 'Base CLIs', label: 'Codex', cli: 'codex', enabled: true, order: 1 },
        { key: 'base:shell', kind: 'base', group: 'Base CLIs', label: 'Terminal', cli: 'shell', enabled: true, order: 2 },
        ...environments.map((environment, order) => ({
            key: `env:${environment.id}:${environment.cli}`, kind: 'environment', group: 'Custom Environments',
            label: `${environment.name} (${environment.cli})`, cli: environment.cli, environmentId: environment.id,
            enabled: !environment.hidden, order
        }))
    ];
}

async function openManageModal(page) {
    const environments = fixtureEnvironments();
    const writes = { preferences: [], environments: [], deletes: [] };
    const server = { refuseDelete: false, nextId: 903 };
    await page.addInitScript(() => sessionStorage.setItem('viberails_tab', 'quick-launch-fixture'));
    await page.routeWebSocket('**/api/v1/events/ws*', () => {});
    await page.route('**/api/v1/**', route => {
        const request = route.request();
        const path = new URL(request.url()).pathname;
        const method = request.method();
        if (path === PREFERENCES && method === 'PUT') {
            const body = request.postDataJSON();
            writes.preferences.push(body);
            return route.fulfill({ json: body });
        }
        if (path === '/api/v1/environments' && method === 'POST') {
            const body = request.postDataJSON();
            writes.environments.push(body);
            const created = { id: server.nextId++, hidden: false, ...body };
            environments.push(created);
            return route.fulfill({ json: created });
        }
        if (path.startsWith('/api/v1/environments/') && method === 'DELETE') {
            const name = decodeURIComponent(path.slice('/api/v1/environments/'.length));
            writes.deletes.push(name);
            if (server.refuseDelete) {
                return route.fulfill({ status: 409, json: { error: `'${name}' is used by an Automation. Delete that first.` } });
            }
            const index = environments.findIndex(environment => environment.name === name);
            if (index >= 0) environments.splice(index, 1);
            return route.fulfill({ json: { success: true } });
        }
        if (/^\/api\/v1\/[a-z0-9.-]+\/settings\//.test(path)) return route.fulfill({ json: {} });
        const payloads = {
            '/api/v1/context': { isInGit: true, rootPath: 'C:/source/vibe-rails', launchDirectory: 'C:/source/vibe-rails' },
            '/api/v1/environments': { environments },
            '/api/v1/agents': { agents: [] },
            '/api/v1/rules/details': { rules: [] },
            [PREFERENCES]: { items: catalogFor(environments) }
        };
        return route.fulfill({ json: payloads[path] || {} });
    });
    await page.goto('/?view=agents', { waitUntil: 'domcontentloaded' });
    await expect(page.getByRole('heading', { name: 'Rules', level: 1, exact: true })).toBeVisible({ timeout: 15_000 });
    await expect(page.locator('#loading-overlay')).toHaveClass(/\bd-none\b/);
    await page.waitForFunction(() => Boolean(window.app?.llmPickerController?.catalog));
    await page.evaluate(async () => {
        const { mountLlmPicker } = await import('/js/modules/pickers/llm-picker.js');
        document.getElementById('app-content').innerHTML = '<select id="quick-launch-test-picker"></select>';
        const select = document.getElementById('quick-launch-test-picker');
        mountLlmPicker(window.app, select, { context: 'terminal' });
        select.tomselect.open();
    });
    const picker = page.locator('#quick-launch-test-picker');
    await page.getByRole('button', { name: 'Manage quick launches…', exact: true }).click();
    const modal = page.getByRole('dialog', { name: 'Manage quick launches', exact: true });
    await expect(modal).toBeVisible();
    return { modal, picker, writes, server };
}

test('the terminal picker offers built-in CLIs and Quick Launches; the dialog hides Agents but saves every item', async ({ page }) => {
    const { modal, picker, writes } = await openManageModal(page);

    // The picker: the setup without a message is a Quick Launch, the one with a message is not offered.
    await expect(picker.locator('option[value="env:901:claude"]')).toHaveCount(1);
    await expect(picker.locator('option[value="env:902:codex"]')).toHaveCount(0);
    await expect(picker.locator('option[value="base:shell"]')).toHaveCount(1);

    // The dialog lists the same two groups under the user's names and never shows the Agent.
    await expect(modal.getByText('Built-in CLIs', { exact: true })).toBeVisible();
    await expect(modal.getByText('Quick Launches', { exact: true })).toBeVisible();
    await expect(modal.locator('[data-llm-picker-key="env:901:claude"]')).toBeVisible();
    await expect(modal.locator('[data-llm-picker-key="env:902:codex"]')).toHaveCount(0);
    await expect(modal.locator('[data-llm-picker-key="env:901:claude"] [data-llm-picker-edit]')).toBeVisible();
    await expect(modal.locator('[data-llm-picker-key="base:claude"] [data-llm-picker-edit]')).toHaveCount(0);

    // Hiding a Quick Launch saves the whole catalog with the server's own group names, Agent included.
    await modal.locator('[data-llm-picker-key="env:901:claude"] [data-llm-picker-enabled]').uncheck();
    await modal.locator('[data-llm-picker-action="save"]').click();
    await expect(modal).toHaveCount(0);
    expect(writes.preferences).toHaveLength(1);
    const saved = writes.preferences[0].items;
    expect(saved.map(item => item.key).sort()).toEqual(['base:claude', 'base:codex', 'base:shell', 'env:901:claude', 'env:902:codex']);
    expect(saved.find(item => item.key === 'env:901:claude')).toMatchObject({ enabled: false, group: 'Custom Environments' });
    expect(saved.find(item => item.key === 'env:902:codex')).toMatchObject({ enabled: true, group: 'Custom Environments' });
    expect(saved.find(item => item.key === 'base:shell')).toMatchObject({ group: 'Base CLIs' });
    await expect(picker.locator('option[value="env:901:claude"]')).toHaveCount(0);
});

test('New quick launch opens the setup form without a message field and saves an empty prompt', async ({ page }) => {
    const { modal, writes } = await openManageModal(page);
    await modal.locator('[data-llm-picker-action="create"]').click();

    const form = page.locator('#env-form');
    await expect(form).toBeVisible();
    await expect(page.locator('#modal-container')).toContainText('New quick launch');
    await expect(page.locator('#env-initial-message')).toHaveCount(0);
    await expect(page.locator('#env-hidden')).toHaveCount(0);
    await expect(page.locator('[data-env-steps-open]')).toHaveCount(0);
    await expect(form.locator('button[type="submit"]')).toHaveText(/Create quick launch/);

    await page.locator('#env-name').fill('Terse Codex');
    // The form starts on the first built-in CLI; pick Codex so the saved setup and its settings are Codex's.
    await page.locator('#env-cli').selectOption('codex');
    await expect(page.locator('[data-primary-options] #codex-model')).toBeVisible();
    await form.locator('button[type="submit"]').click();
    await expect(form).toHaveCount(0);
    expect(writes.environments).toHaveLength(1);
    expect(writes.environments[0]).toMatchObject({ name: 'Terse Codex', cli: 'codex', customPrompt: '' });
    expect(writes.environments[0].automationWorker).toBeUndefined();

    // The dialog comes back with the new Quick Launch listed.
    const reopened = page.getByRole('dialog', { name: 'Manage quick launches', exact: true });
    await expect(reopened).toBeVisible();
    await expect(reopened.locator('[data-llm-picker-key="env:903:codex"]')).toBeVisible();
});

test('Delete asks first, shows the server refusal inline, and removes the row once allowed', async ({ page }) => {
    const { modal, writes, server } = await openManageModal(page);
    const row = modal.locator('[data-llm-picker-key="env:901:claude"]');

    await row.locator('[data-llm-picker-delete]').click();
    const confirm = page.locator('.vb-confirm');
    await expect(confirm).toContainText('Delete quick launch “Fast Opus”?');
    await confirm.getByRole('button', { name: 'Cancel', exact: true }).click();
    await expect(confirm).toHaveCount(0);
    expect(writes.deletes).toEqual([]);

    server.refuseDelete = true;
    await row.locator('[data-llm-picker-delete]').click();
    await page.locator('.vb-confirm .btn-danger').click();
    await expect(modal.locator('[data-llm-picker-error]')).toContainText('used by an Automation');
    await expect(row).toBeVisible();
    expect(writes.deletes).toEqual(['Fast Opus']);

    server.refuseDelete = false;
    await row.locator('[data-llm-picker-delete]').click();
    await page.locator('.vb-confirm .btn-danger').click();
    await expect(row).toHaveCount(0);
    expect(writes.deletes).toEqual(['Fast Opus', 'Fast Opus']);
    expect(writes.preferences).toEqual([]);
});
