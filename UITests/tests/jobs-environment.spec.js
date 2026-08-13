// @ts-check

const { test, expect } = require('./fixtures');

const CURRENT_REPOSITORY = 'C:\\source\\fixture-repository';

function createFixtureState() {
    const state = {
        environments: [
            {
                id: 41,
                name: 'Nightly Codex',
                cli: 'codex',
                path: 'C:\\test-envs\\nightly-codex',
                customArgs: '--model gpt-5.6-sol -c model_reasoning_effort=high',
                customPrompt: 'Use the nightly security-review profile.',
                hidden: false,
                automationWorker: true,
                lastUsedUTC: '2026-07-21T12:00:00Z'
            },
            {
                id: 42,
                name: 'Copilot reviewer',
                cli: 'copilot',
                path: 'C:\\test-envs\\copilot-reviewer',
                customArgs: '--model gpt-5.5',
                customPrompt: 'Review this commit with Copilot.',
                hidden: true,
                automationWorker: true,
                lastUsedUTC: '2026-07-21T12:00:00Z'
            },
            {
                id: 43,
                name: 'Terminal helper',
                cli: 'claude',
                path: 'C:\\test-envs\\terminal-helper',
                customArgs: '',
                customPrompt: '',
                hidden: false,
                automationWorker: false,
                lastUsedUTC: '2026-07-21T12:00:00Z'
            }
        ],
        codexSettings: {
            'Nightly Codex': {
                model: 'gpt-5.6-sol',
                effort: 'high',
                prompt: 'Use the nightly security-review profile.',
                fastMode: false,
                yolo: false,
                noAltScreen: false
            }
        },
        jobs: [{
            id: 17,
            name: 'Environment review',
            projectPath: CURRENT_REPOSITORY,
            llm: 1,
            environmentId: 41,
            environmentName: 'Nightly Codex',
            prompt: 'Use the nightly security-review profile.',
            executionMode: 0,
            timeoutMinutes: 30,
            enabled: false,
            triggers: []
        }],
        nextJobId: 18,
        nextEnvironmentId: 60
    };
    const base = [
        ['codex', 'Codex'],
        ['claude', 'Claude'],
        ['opencode', 'OpenCode'],
        ['glm-5.2', 'GLM 5.2'],
        ['antigravity', 'Antigravity'],
        ['copilot', 'Copilot'],
        ['shell', 'Terminal']
    ];
    // Mirrors the server: Automation Workers are excluded from the preferences
    // catalog, so only the regular custom environment (43) appears here.
    state.pickerItems = [
        ...base.map(([cli, label], order) => ({
            key: `base:${cli}`, kind: 'base', group: 'Base CLIs', label, cli,
            environmentId: null, enabled: true, order
        })),
        ...state.environments
            .filter(environment => !environment.automationWorker)
            .map((environment, order) => ({
                key: `env:${environment.id}:${environment.cli}`,
                kind: 'environment',
                group: 'Custom Environments',
                label: `${environment.name} (${environment.cli})`,
                cli: environment.cli,
                environmentId: environment.id,
                enabled: !environment.hidden,
                order
            }))
    ];
    return state;
}

async function installStatefulApi(page) {
    const state = createFixtureState();
    const writes = {
        environments: [],
        environmentCreates: [],
        settings: [],
        jobs: []
    };

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
        if (method === 'POST' && path === '/api/v1/environments') {
            const body = request.postDataJSON();
            const environment = {
                id: state.nextEnvironmentId++,
                name: body.name,
                cli: body.cli,
                path: `C:\\test-envs\\${body.name}`,
                customArgs: body.customArgs || '',
                customPrompt: body.customPrompt || '',
                hidden: Boolean(body.hidden),
                automationWorker: Boolean(body.automationWorker),
                lastUsedUTC: '2026-07-21T13:00:00Z'
            };
            state.environments.push(environment);
            writes.environmentCreates.push({ body });
            return respond(environment);
        }
        if (path === '/api/v1/llm-picker/preferences') {
            if (method === 'GET') return respond({ items: state.pickerItems });
            if (method === 'PUT') {
                state.pickerItems = request.postDataJSON().items;
                return respond({ items: state.pickerItems });
            }
            if (method === 'DELETE') return respond({ items: state.pickerItems });
        }
        if (method === 'PUT' && path.startsWith('/api/v1/environments/')) {
            const name = decodeURIComponent(path.slice('/api/v1/environments/'.length));
            const body = request.postDataJSON();
            const environment = state.environments.find(item => item.name === name);
            if (!environment) return respond({ error: `Unknown environment: ${name}` });
            Object.assign(environment, body, { lastUsedUTC: '2026-07-21T13:00:00Z' });
            writes.environments.push({ name, body });
            return respond(environment);
        }
        if (path.startsWith('/api/v1/codex/settings/')) {
            const name = decodeURIComponent(path.slice('/api/v1/codex/settings/'.length));
            if (method === 'GET') {
                return respond(state.codexSettings[name] || {});
            }
            if (method === 'PUT') {
                const body = request.postDataJSON();
                state.codexSettings[name] = { ...body };
                writes.settings.push({ name, body });
                return respond(body);
            }
        }
        if (method === 'GET' && path === '/api/v1/jobs') {
            return respond({ jobs: state.jobs });
        }
        if (method === 'POST' && path === '/api/v1/jobs') {
            const body = request.postDataJSON();
            const environment = state.environments.find(item => Number(item.id) === Number(body.environmentId));
            const job = {
                id: state.nextJobId++,
                ...body,
                environmentName: environment?.name || null
            };
            state.jobs.push(job);
            writes.jobs.push({ method, path, body });
            return respond(job);
        }
        if (method === 'PUT' && /^\/api\/v1\/jobs\/\d+$/.test(path)) {
            const id = Number(path.split('/').pop());
            const body = request.postDataJSON();
            const index = state.jobs.findIndex(item => Number(item.id) === id);
            const environment = state.environments.find(item => Number(item.id) === Number(body.environmentId));
            const job = {
                ...(state.jobs[index] || { id }),
                ...body,
                environmentName: environment?.name || null
            };
            if (index >= 0) state.jobs[index] = job;
            writes.jobs.push({ method, path, body });
            return respond(job);
        }
        if (method === 'GET' && path === '/api/v1/jobs/runs') {
            return respond({ runs: [] });
        }
        if (method === 'GET' && path === '/api/v1/jobs/worker') {
            return respond({
                state: 'running',
                installed: true,
                running: true,
                needsRepair: false,
                message: 'Jobs worker is running.'
            });
        }

        return route.fallback();
    });

    return { state, writes };
}

async function openApp(page) {
    // The SPA keeps long-lived status/WebSocket traffic open, so networkidle is not a valid
    // readiness signal. Start directly on Jobs so the asynchronous terminal-focus bootstrap
    // cannot race a test navigation and replace the Jobs DOM after it has rendered.
    await page.goto('/?view=jobs', { waitUntil: 'domcontentloaded' });
    await expect(page.locator('.app-subnav-link[data-view="jobs"]:visible')).toBeVisible({ timeout: 15_000 });
    await expect(page.locator('.jobs-view[data-view="jobs"]')).toBeVisible({ timeout: 10_000 });
}

async function openJobs(page) {
    if (!await page.locator('.jobs-view[data-view="jobs"]').isVisible()) {
        await page.locator('.app-subnav-link[data-view="jobs"]:visible').click();
    }
    await expect(page.locator('.jobs-view[data-view="jobs"]')).toBeVisible({ timeout: 10_000 });
    await expect(page.locator('[data-jobs-list]')).toContainText('Environment review');
}

async function openWorkers(page) {
    await page.locator('.app-subnav-link[data-view="environments"]:visible').click();
    // Scope to the view container: [data-view="environments"] now also matches the top subnav link
    // and the sidebar link, and an unscoped locator is a Playwright strict-mode violation.
    await expect(page.locator('.view[data-view="environments"]')).toBeVisible({ timeout: 10_000 });
    await expect(page.locator('[data-environments-table]')).toContainText('Nightly Codex');
}

async function openAutomationEditorForExistingJob(page) {
    await page.locator('[data-job-action="edit"][data-job-id="17"]').click();
    await expect(page.locator('[data-job-form]')).toBeVisible();
    await expect.poll(() => page.locator('#job-llm-selection').evaluate(
        element => Boolean(element.tomselect))).toBe(true);
}

async function environmentControlSnapshot(page) {
    return page.locator('#env-form [id]').evaluateAll(elements => Object.fromEntries(
        elements.map(element => [
            element.id,
            element.type === 'checkbox' || element.type === 'radio' ? element.checked : element.value
        ])
    ));
}

function withoutWorkspaceControls(controls) {
    return Object.fromEntries(Object.entries(controls)
        .filter(([id]) => !id.startsWith('env-workspace')));
}

async function closeEnvironmentModal(page) {
    await page.locator('#modal-container [data-action="close-modal"]').click();
    await expect(page.locator('#env-form')).toHaveCount(0);
}

test.describe('Jobs Worker / Environments integration', () => {
    test('Add Worker opens the shared modal with the automation name as an editable prefill', async ({ page }) => {
        await installStatefulApi(page);
        await openApp(page);
        await openJobs(page);

        await page.locator('[data-job-action="new"]').click();
        await expect(page.locator('[data-job-form]')).toBeVisible();
        await page.locator('#job-name').fill('Post-merge auditor');
        await page.locator('[data-job-action="add-environment-from-editor"]').click();
        await expect(page.getByRole('heading', { name: 'Create Worker', exact: true }))
            .toBeVisible();
        await expect(page.locator('#env-form')).toHaveClass(/env-worker-form/);
        await expect(page.locator('#env-form')).not.toContainText('Advanced');
        await expect(page.locator('#env-form')).toContainText('Workspace');
        await expect(page.locator('#env-workspace-mode')).toHaveCount(0);
        await expect(page.locator('input[name="env-workspace-mode"]')).toHaveCount(2);
        await expect(page.getByRole('radio', { name: /Project directory/ })).toBeChecked();
        await expect(page.getByRole('radio', { name: /Clean Git checkout every run/ })).not.toBeChecked();
        await expect(page.locator('#env-form')).not.toContainText('Its own clone');
        await expect(page.locator('#env-form .form-switch .form-check-input').first())
            .toHaveCSS('appearance', 'none');
        const jobsControls = await environmentControlSnapshot(page);
        await closeEnvironmentModal(page);

        await openWorkers(page);
        await page.locator('[data-action="create-environment"]').click();
        await expect(page.getByRole('heading', { name: 'Create Environment / Worker', exact: true }))
            .toBeVisible();
        const workersControls = await environmentControlSnapshot(page);
        await closeEnvironmentModal(page);

        // One shared modal: the Worker variant names the Worker in its own (prefilled,
        // editable) field, drops the launch-picker visibility switch, and replaces the
        // general three-mode Workspace select with its two automation-safe choices.
        // Provider-specific settings remain identical.
        expect(jobsControls['env-name']).toBe('Post-merge auditor');
        expect(Object.keys(jobsControls)).not.toContain('env-hidden');
        expect(Object.keys(withoutWorkspaceControls(workersControls)).sort()).toEqual(
            [...Object.keys(withoutWorkspaceControls(jobsControls)), 'env-hidden'].sort());
    });

    test('Add Worker works nameless: the Worker is named in the modal and the automation adopts it', async ({ page }) => {
        const { writes } = await installStatefulApi(page);
        await openApp(page);
        await openJobs(page);

        await page.locator('[data-job-action="new"]').click();
        await expect(page.locator('[data-job-form]')).toBeVisible();

        // No automation name yet — the modal still opens, with a blank name field
        // to fill in there.
        await page.locator('[data-job-action="add-environment-from-editor"]').click();
        await expect(page.locator('#env-form')).toBeVisible({ timeout: 5_000 });
        await expect(page.locator('#env-name')).toHaveValue('');

        await page.locator('#env-name').fill('Post-merge auditor');
        await page.locator('#env-initial-message').fill('Audit every merge.');
        const cleanCheckout = page.getByRole('radio', { name: /Clean Git checkout every run/ });
        await page.locator('label.env-workspace-choice', { has: cleanCheckout }).click();
        await expect(cleanCheckout).toBeChecked();
        await page.locator('#env-form button[type="submit"]').click();
        await expect(page.locator('#env-form')).toHaveCount(0);

        expect(writes.environmentCreates).toHaveLength(1);
        const created = writes.environmentCreates[0].body;
        expect(created.name).toBe('Post-merge auditor');
        expect(created.automationWorker).toBe(true);
        expect(created.hidden).toBeUndefined();
        expect(created.workspaceMode).toBe(2);

        // The freshly created worker is auto-selected in the Worker picker, and the
        // empty automation name adopts the Worker's name as its editable default.
        await expect.poll(() => page.locator('#job-llm-selection').evaluate(
            element => element.tomselect?.getValue?.() || element.value)).toBe('env:60:codex');
        await expect(page.locator('#job-name')).toHaveValue('Post-merge auditor');
    });

    test('editing a Worker from either screen stays synchronized', async ({ page }) => {
        const { state, writes } = await installStatefulApi(page);
        await openApp(page);
        await openJobs(page);

        await openAutomationEditorForExistingJob(page);
        await expect(page.locator('[data-job-environment-preview]'))
            .toContainText('Use the nightly security-review profile.');
        await page.locator('[data-job-action="edit-selected-environment"]').click();
        await expect(page.getByRole('heading', { name: 'Edit Worker: Nightly Codex', exact: true }))
            .toBeVisible();
        const jobsControls = await environmentControlSnapshot(page);
        expect(jobsControls['env-initial-message']).toBe('Use the nightly security-review profile.');
        expect(jobsControls['codex-model']).toBe('gpt-5.6-sol');
        expect(Object.keys(jobsControls)).not.toContain('env-name');
        expect(Object.keys(jobsControls)).not.toContain('env-hidden');

        await page.locator('#env-initial-message').fill('Security review configured from Jobs.');
        await page.locator('#codex-model').selectOption('gpt-5.6-luna');
        await page.locator('#env-form button[type="submit"]').click();
        await expect(page.locator('#env-form')).toHaveCount(0);
        await expect(page.locator('[data-job-environment-preview]'))
            .toContainText('Security review configured from Jobs.');

        await openWorkers(page);
        await expect(page.locator('[data-environments-table]'))
            .toContainText('Security review configured from Jobs.');
        // Workers carry the robot badge in the Environments table.
        await expect(page.locator('[data-environments-table] tr', { hasText: 'Nightly Codex' })
            .locator('.env-worker-badge')).toBeVisible();
        await page.locator('[data-environments-table] [data-action="edit-environment"][data-env-name="Nightly Codex"]').click();
        await expect(page.getByRole('heading', { name: 'Edit Worker: Nightly Codex', exact: true }))
            .toBeVisible();
        const workersControls = await environmentControlSnapshot(page);
        expect(workersControls['env-initial-message']).toBe('Security review configured from Jobs.');
        expect(workersControls['codex-model']).toBe('gpt-5.6-luna');

        await page.locator('#env-initial-message').fill('Security review updated from Workers.');
        await page.locator('#env-form button[type="submit"]').click();
        await expect(page.locator('#env-form')).toHaveCount(0);

        expect(state.environments[0].customPrompt).toBe('Security review updated from Workers.');
        expect(writes.environments).toHaveLength(2);
        expect(writes.settings).toHaveLength(2);
    });

    test('new automation lists only Workers and posts the denormalized prompt', async ({ page }) => {
        const { writes } = await installStatefulApi(page);
        await openApp(page);
        await openJobs(page);

        await page.locator('[data-job-action="new"]').click();
        await expect(page.locator('[data-job-form]')).toBeVisible();
        await expect(page.locator('#job-project')).toHaveCount(0);
        await expect(page.locator('#job-prompt')).toHaveCount(0);
        await expect(page.locator('[data-job-form]')).toContainText('Runs in');
        await expect(page.locator('[data-job-form]')).toContainText(CURRENT_REPOSITORY);
        await expect(page.locator('[data-job-form]')).toContainText('After each commit');

        const picker = page.locator('#job-llm-selection');
        await expect.poll(() => picker.evaluate(element => Boolean(element.tomselect)))
            .toBe(true);
        const optionValues = await picker.locator('option').evaluateAll(options =>
            options.map(option => option.value).filter(Boolean));
        // Workers only — the regular custom environment (43) and base CLIs are for
        // launch pickers. The hidden worker (42) still appears: `hidden` has no
        // power over the Worker picker.
        expect(optionValues.sort()).toEqual(['env:41:codex', 'env:42:copilot']);

        // The Worker picker is not a launch picker: no customization footer.
        expect(await picker.evaluate(element =>
            Boolean(element.tomselect.dropdown.querySelector('.llm-picker-customize-button')))).toBe(false);

        await picker.evaluate(element => element.tomselect.setValue('env:41:codex'));
        await page.locator('#job-name').fill('Security review after commit');
        await page.locator('#job-trigger-commit').check();
        await page.locator('[data-job-form] button[type="submit"]').click();

        await expect.poll(() => writes.jobs.filter(write => write.method === 'POST').length)
            .toBe(1);
        const create = writes.jobs.find(write => write.method === 'POST');
        expect(create.body.projectPath).toBe(CURRENT_REPOSITORY);
        expect(create.body.environmentId).toBe(41);
        expect(create.body.llm).toBe(1);
        expect(create.body.prompt).toBe('Use the nightly security-review profile.');
        expect(create.body.triggers).toEqual([{ kind: 2 }]);
    });

    test('the enable toggle is state-colored green/red', async ({ page }) => {
        await installStatefulApi(page);
        await openApp(page);
        await openJobs(page);

        // Fixture job 17 is disabled → red with the off icon.
        const toggle = page.locator('[data-job-action="toggle"][data-job-id="17"]');
        await expect(toggle).toHaveClass(/btn-outline-danger/);
        await expect(toggle.locator('.fa-toggle-off')).toBeVisible();

        await toggle.click();
        const enabledToggle = page.locator('[data-job-action="toggle"][data-job-id="17"]');
        await expect(enabledToggle).toHaveClass(/btn-outline-success/);
        await expect(enabledToggle.locator('.fa-toggle-on')).toBeVisible();
    });

    test('Automation uses opaque rows and a focused form without an Advanced disclosure', async ({ page }) => {
        await installStatefulApi(page);
        await openApp(page);
        await openJobs(page);

        const disabledRow = page.locator('.job-card[data-enabled="false"]');
        await expect(disabledRow).toHaveCSS('opacity', '1');
        expect(await disabledRow.evaluate(element => getComputedStyle(element).backgroundColor))
            .not.toBe('rgba(0, 0, 0, 0)');

        await page.locator('[data-job-action="edit"][data-job-id="17"]').click();
        await expect(page.locator('[data-job-form]')).toBeVisible();
        await expect(page.locator('[data-jobs-list]')).toBeHidden();
        await expect(page.locator('.jobs-history-panel')).toBeHidden();
        await expect(page.locator('[data-job-form]')).not.toContainText('Advanced');
        await expect(page.locator('#job-timeout')).toBeVisible();
        await expect(page.locator('#job-timeout-enabled')).toHaveCount(0);
        await expect(page.locator('#job-trigger-schedule')).toHaveCSS('appearance', 'none');
    });

    test('Automation More actions stay inside a narrow viewport', async ({ page }) => {
        await page.setViewportSize({ width: 320, height: 800 });
        const { state } = await installStatefulApi(page);
        const longName = 'AutomationWithAnIntentionallyLongUnbrokenNameThatMustWrapWithoutMakingThePageScrollSideways';
        const longWorkerName = 'WorkerWithAnIntentionallyLongUnbrokenNameThatMustAlsoStayInsideTheAutomationRow';
        state.environments.push({
            id: 44,
            name: longWorkerName,
            cli: 'codex',
            path: 'C:\\test-envs\\long-name-worker',
            customArgs: '',
            customPrompt: 'Check narrow viewport wrapping.',
            hidden: false,
            automationWorker: true,
            lastUsedUTC: '2026-07-21T12:00:00Z'
        });
        state.jobs.push({
            id: 18,
            name: longName,
            projectPath: CURRENT_REPOSITORY,
            llm: 1,
            environmentId: 44,
            environmentName: longWorkerName,
            prompt: 'Check narrow viewport wrapping.',
            executionMode: 0,
            timeoutMinutes: null,
            enabled: true,
            triggers: []
        });
        await openApp(page);
        await openJobs(page);

        const row = page.locator('.job-card').filter({ hasText: 'Environment review' });
        await row.locator('.job-more > summary').click();
        const menu = row.locator('.job-more-menu');
        await expect(menu).toBeVisible();
        const bounds = await menu.boundingBox();
        expect(bounds.x).toBeGreaterThanOrEqual(0);
        expect(bounds.x + bounds.width).toBeLessThanOrEqual(320);

        await expect(page.locator('.job-card').filter({ hasText: longName })).toBeVisible();
        expect(await page.evaluate(() => document.documentElement.scrollWidth))
            .toBeLessThanOrEqual(320);

        await page.locator('[data-job-action="edit"][data-job-id="18"]').click();
        await expect(page.locator('.job-inline-form-header h3')).toContainText(longName);
        expect(await page.evaluate(() => document.documentElement.scrollWidth))
            .toBeLessThanOrEqual(320);
    });
});
