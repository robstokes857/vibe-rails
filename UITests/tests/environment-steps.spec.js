// @ts-check

const { test, expect } = require('./fixtures');

const CURRENT_REPOSITORY = 'C:\\source\\fixture-repository';

/**
 * Same fake-backend shape as jobs-environment.spec.js, narrowed to what the Steps editor touches:
 * the environments list (which now carries a `steps` child collection), the per-CLI settings the
 * edit form loads, and the streaming step-test endpoint.
 */
function createFixtureState() {
    return {
        environments: [
            {
                id: 43,
                name: 'Terminal helper',
                cli: 'claude',
                path: 'C:\\test-envs\\terminal-helper',
                customArgs: '',
                customPrompt: '',
                hidden: false,
                automationWorker: false,
                workspaceMode: 0,
                steps: [
                    {
                        id: 1, phase: 0, position: 0, name: 'Pull',
                        command: 'git pull', startMinimized: false, timeoutSeconds: 600, enabled: true
                    }
                ],
                lastUsedUTC: '2026-07-21T12:00:00Z'
            }
        ],
        claudeSettings: { 'Terminal helper': {} },
        pickerItems: [
            {
                key: 'base:claude', kind: 'base', group: 'Base CLIs', label: 'Claude',
                cli: 'claude', environmentId: null, enabled: true, order: 0
            },
            {
                key: 'env:43:claude', kind: 'environment', group: 'Custom Environments',
                label: 'Terminal helper (claude)', cli: 'claude', environmentId: 43, enabled: true, order: 0
            }
        ]
    };
}

async function installStatefulApi(page) {
    const state = createFixtureState();
    const writes = { environments: [], stepTests: [] };

    await page.route('**/api/v1/**', async route => {
        const request = route.request();
        const url = new URL(request.url());
        const path = url.pathname;
        const method = request.method();
        const respond = body => route.fulfill({
            status: 200,
            contentType: 'application/json',
            body: JSON.stringify(body)
        });

        if (method === 'GET' && path === '/api/v1/context') {
            return respond({
                isInGit: true,
                rootPath: CURRENT_REPOSITORY,
                launchDirectory: CURRENT_REPOSITORY
            });
        }
        if (method === 'GET' && path === '/api/v1/environments') {
            return respond({ environments: state.environments });
        }
        if (path === '/api/v1/llm-picker/preferences') {
            if (method === 'GET') return respond({ items: state.pickerItems });
            if (method === 'PUT') return respond({ items: state.pickerItems });
            if (method === 'DELETE') return respond({ items: state.pickerItems });
        }
        if (method === 'POST' && path === '/api/v1/environments/steps/test') {
            const body = request.postDataJSON();
            writes.stepTests.push(body);
            // The real endpoint is SSE; the editor reads it with a streaming reader, and a
            // single-chunk body exercises the same parser.
            return route.fulfill({
                status: 200,
                contentType: 'text/event-stream',
                body:
                    'data: {"type":"line","line":"Already up to date.","isError":false}\n\n' +
                    'data: {"type":"done","exitCode":0,"durationMs":42}\n\n'
            });
        }
        if (method === 'PUT' && path.startsWith('/api/v1/environments/')) {
            const name = decodeURIComponent(path.slice('/api/v1/environments/'.length));
            const body = request.postDataJSON();
            const environment = state.environments.find(item => item.name === name);
            if (!environment) return respond({ error: `Unknown environment: ${name}` });
            writes.environments.push({ name, body });
            if (Array.isArray(body.steps)) {
                environment.steps = body.steps.map((step, index) => ({
                    ...step, id: index + 1, position: index
                }));
            }
            Object.assign(environment, body, {
                steps: environment.steps,
                lastUsedUTC: '2026-07-21T13:00:00Z'
            });
            return respond(environment);
        }
        if (path.startsWith('/api/v1/claude/settings/')) {
            const name = decodeURIComponent(path.slice('/api/v1/claude/settings/'.length));
            if (method === 'GET') return respond(state.claudeSettings[name] || {});
            if (method === 'PUT') {
                state.claudeSettings[name] = request.postDataJSON();
                return respond(state.claudeSettings[name]);
            }
        }

        return route.fallback();
    });

    return { state, writes };
}

async function openEnvironments(page) {
    // The SPA keeps long-lived status/WebSocket traffic open, so networkidle is never a valid
    // readiness signal here.
    await page.goto('/?view=environments', { waitUntil: 'domcontentloaded' });
    await expect(page.locator('.view[data-view="environments"]')).toBeVisible({ timeout: 15_000 });
    await expect(page.locator('[data-environments-table]')).toContainText('Terminal helper');
}

async function openStepsEditor(page) {
    await page.locator('[data-action="edit-environment"][data-env-name="Terminal helper"]').click();
    await expect(page.locator('#env-form')).toBeVisible();
    await page.locator('[data-env-steps-open]').click();
    await expect(page.locator('.env-steps-modal')).toBeVisible();
}

async function saveSteps(page) {
    await page.locator('[data-env-steps-action="save"]').click();
    await expect(page.locator('.env-steps-modal')).toHaveCount(0);
}

test.describe('Environment Steps editor', () => {
    test('the summary button reports the saved steps and opens a nested layer', async ({ page }) => {
        await installStatefulApi(page);
        await openEnvironments(page);

        await page.locator('[data-action="edit-environment"][data-env-name="Terminal helper"]').click();
        await expect(page.locator('#env-form')).toBeVisible();
        await expect(page.locator('[data-env-steps-summary]')).toHaveText('1 before');

        await page.locator('[data-env-steps-open]').click();

        // A nested layer, not a second app.showModal: the environment form underneath must survive.
        await expect(page.locator('.env-steps-modal')).toBeVisible();
        await expect(page.locator('#env-form')).toHaveCount(1);
        await expect(page.locator('[data-env-steps-section="0"] .env-step-row')).toHaveCount(1);
        await expect(page.locator('[data-env-steps-section="1"] .env-steps-empty')).toBeVisible();
        await expect(page.locator('[data-env-steps-section="1"]'))
            .toContainText('for a tab it is when you close the tab');
    });

    test('adding, reordering, and saving steps sends them in run order', async ({ page }) => {
        const { writes } = await installStatefulApi(page);
        await openEnvironments(page);
        await openStepsEditor(page);

        // Add a second pre-launch step and one post-exit step.
        await page.locator('[data-env-steps-add="0"]').click();
        await page.locator('[data-env-steps-section="0"] .env-step-row').nth(1)
            .locator('[data-env-step-field="name"]').fill('Install');
        await page.locator('[data-env-steps-section="0"] .env-step-row').nth(1)
            .locator('[data-env-step-field="command"]').fill('npm ci');

        await page.locator('[data-env-steps-add="1"]').click();
        const postRow = page.locator('[data-env-steps-section="1"] .env-step-row').first();
        await postRow.locator('[data-env-step-field="name"]').fill('Push');
        await postRow.locator('[data-env-step-field="command"]').fill('git push');
        await postRow.locator('[data-env-step-field="startMinimized"]').check();
        await postRow.locator('[data-env-step-field="timeoutSeconds"]').fill('120');

        // Move "Install" above "Pull" with the explicit button (the keyboard-and-mouse
        // equivalent of the drag handle).
        await page.locator('[data-env-steps-section="0"] .env-step-row').nth(1)
            .locator('[data-env-step-move="up"]').click();
        await expect(page.locator('[data-env-steps-section="0"] .env-step-row').first()
            .locator('[data-env-step-field="name"]')).toHaveValue('Install');

        await saveSteps(page);
        await expect(page.locator('[data-env-steps-summary]')).toHaveText('2 before · 1 after');

        await page.locator('#env-form button[type="submit"]').click();
        await expect(page.locator('#env-form')).toHaveCount(0);

        await expect.poll(() => writes.environments.length).toBeGreaterThan(0);
        const saved = writes.environments.at(-1).body.steps;
        expect(saved.map(step => [step.phase, step.name, step.command])).toEqual([
            [0, 'Install', 'npm ci'],
            [0, 'Pull', 'git pull'],
            [1, 'Push', 'git push']
        ]);
        // Position is implied by array order and never sent.
        expect(Object.keys(saved[0])).not.toContain('position');
        expect(saved[2].startMinimized).toBe(true);
        expect(saved[2].timeoutSeconds).toBe(120);
    });

    test('saving the form without opening the editor omits steps entirely', async ({ page }) => {
        const { writes } = await installStatefulApi(page);
        await openEnvironments(page);

        await page.locator('[data-action="edit-environment"][data-env-name="Terminal helper"]').click();
        await expect(page.locator('#env-form')).toBeVisible();
        await page.locator('#env-form button[type="submit"]').click();
        await expect(page.locator('#env-form')).toHaveCount(0);

        await expect.poll(() => writes.environments.length).toBeGreaterThan(0);
        // `null`/absent means "leave them untouched" — a form saved without the steps modal must
        // not wipe a configured setup chain.
        expect(writes.environments.at(-1).body).not.toHaveProperty('steps');
    });

    test('Test runs the command as typed and renders its output and exit code', async ({ page }) => {
        const { writes } = await installStatefulApi(page);
        await openEnvironments(page);
        await openStepsEditor(page);

        const row = page.locator('[data-env-steps-section="0"] .env-step-row').first();
        await row.locator('[data-env-step-action="test"]').click();

        const console = row.locator('.env-step-console');
        await expect(console).toBeVisible();
        await expect(console.locator('[data-vca-console-output]')).toContainText('Already up to date.');
        await expect(console.locator('[data-vca-console-state]')).toHaveText('Passed');
        await expect(console.locator('[data-vca-console-meta]')).toContainText('Exit code 0');

        // The unsaved command is what gets tested, so an edit can be checked before committing.
        expect(writes.stepTests.at(-1).command).toBe('git pull');
    });

    test('deleting a step confirms in-app and only then removes the row', async ({ page }) => {
        await installStatefulApi(page);
        await openEnvironments(page);
        await openStepsEditor(page);

        await page.locator('[data-env-steps-section="0"] .env-step-row').first()
            .locator('[data-env-step-action="delete"]').click();

        // window.confirm is a silent no-op in the VS Code webview, so this must be the in-app
        // overlay from utils.js.
        await expect(page.locator('.vb-confirm')).toBeVisible();
        await expect(page.locator('[data-env-steps-section="0"] .env-step-row')).toHaveCount(1);

        await page.locator('.vb-confirm .btn-danger').click();
        await expect(page.locator('[data-env-steps-section="0"] .env-step-row')).toHaveCount(0);
        await expect(page.locator('.env-steps-modal')).toBeVisible();
    });

    test('a long step list scrolls inside the modal body instead of running off screen', async ({ page }) => {
        await installStatefulApi(page);
        await page.setViewportSize({ width: 1280, height: 700 });
        await openEnvironments(page);
        await openStepsEditor(page);

        // Enough rows to overflow any reasonable viewport.
        for (let index = 0; index < 5; index++) {
            await page.locator('[data-env-steps-add="0"]').click();
        }
        await expect(page.locator('[data-env-steps-section="0"] .env-step-row')).toHaveCount(6);

        const body = page.locator('.env-steps-modal .modal-body');
        const metrics = await body.evaluate(element => ({
            scrollHeight: element.scrollHeight,
            clientHeight: element.clientHeight,
            overflowY: getComputedStyle(element).overflowY
        }));

        // The <form> wrapping body + footer is a flex item with min-height: auto; without the
        // flex-chain rule it refuses to shrink and .modal-content clips the list with no
        // scrollbar anywhere.
        expect(metrics.overflowY).toBe('auto');
        expect(metrics.scrollHeight).toBeGreaterThan(metrics.clientHeight);

        await body.evaluate(element => { element.scrollTop = element.scrollHeight; });
        expect(await body.evaluate(element => element.scrollTop)).toBeGreaterThan(0);

        // And the dialog itself stays inside the viewport, so the footer buttons remain reachable.
        const dialog = await page.locator('.env-steps-modal .modal-content').boundingBox();
        expect(dialog.y + dialog.height).toBeLessThanOrEqual(700);
        await expect(page.locator('[data-env-steps-action="save"]')).toBeInViewport();
    });

    test('a step with no command blocks Done instead of saving a broken list', async ({ page }) => {
        await installStatefulApi(page);
        await openEnvironments(page);
        await openStepsEditor(page);

        await page.locator('[data-env-steps-add="0"]').click();
        await page.locator('[data-env-steps-action="save"]').click();

        await expect(page.locator('[data-env-steps-error]')).toContainText('Every step needs a command');
        await expect(page.locator('.env-steps-modal')).toBeVisible();
    });
});
