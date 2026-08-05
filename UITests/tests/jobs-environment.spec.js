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
                lastUsedUTC: '2026-07-21T12:00:00Z'
            },
            {
                id: 42,
                name: 'Copilot reviewer',
                cli: 'copilot',
                path: 'C:\\test-envs\\copilot-reviewer',
                customArgs: '--model gpt-5.5',
                customPrompt: 'Review this commit with Copilot.',
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
        nextJobId: 18
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
    state.pickerItems = [
        ...base.map(([cli, label], order) => ({
            key: `base:${cli}`, kind: 'base', group: 'Base CLIs', label, cli,
            environmentId: null, enabled: true, order
        })),
        ...state.environments.map((environment, order) => ({
            key: `env:${environment.id}:${environment.cli}`,
            kind: 'environment',
            group: 'Custom Environments',
            label: `${environment.name} (${environment.cli})`,
            cli: environment.cli,
            environmentId: environment.id,
            enabled: true,
            order
        }))
    ];
    return state;
}

async function installStatefulApi(page) {
    const state = createFixtureState();
    const writes = {
        environments: [],
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
    await expect(page.getByRole('heading', { name: 'Environment / Workers', exact: true })).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Automation rules', exact: true })).toBeVisible();
    await expect(page.locator('[data-job-environments-table]')).toContainText('Nightly Codex');
}

async function openWorkers(page) {
    await page.locator('.app-subnav-link[data-view="environments"]:visible').click();
    // Scope to the view container: [data-view="environments"] now also matches the top subnav link
    // and the sidebar link, and an unscoped locator is a Playwright strict-mode violation.
    await expect(page.locator('.view[data-view="environments"]')).toBeVisible({ timeout: 10_000 });
    await expect(page.locator('[data-environments-table]')).toContainText('Nightly Codex');
}

async function openNightlyWorkerFrom(page, tableSelector) {
    await page.locator(
        `${tableSelector} [data-action="edit-environment"][data-env-name="Nightly Codex"]`
    ).click();
    await expect(page.locator('#env-form')).toBeVisible({ timeout: 5_000 });
    await expect(page.getByRole('heading', {
        name: 'Edit Environment / Worker: Nightly Codex',
        exact: true
    })).toBeVisible();
}

async function environmentControlSnapshot(page) {
    return page.locator('#env-form [id]').evaluateAll(elements => Object.fromEntries(
        elements.map(element => [
            element.id,
            element.type === 'checkbox' ? element.checked : element.value
        ])
    ));
}

async function closeEnvironmentModal(page) {
    await page.locator('#modal-container [data-action="close-modal"]').click();
    await expect(page.locator('#env-form')).toHaveCount(0);
}

test.describe('Jobs Environment / Workers integration', () => {
    test('Jobs and Workers launch the very same full Add Worker modal', async ({ page }) => {
        await installStatefulApi(page);
        await openApp(page);
        await openJobs(page);

        await page.locator('[data-job-action="new-environment"]').click();
        await expect(page.getByRole('heading', { name: 'Create Environment / Worker', exact: true }))
            .toBeVisible();
        const jobsControls = await environmentControlSnapshot(page);
        await closeEnvironmentModal(page);

        await openWorkers(page);
        await page.locator('[data-action="create-environment"]').click();
        await expect(page.getByRole('heading', { name: 'Create Environment / Worker', exact: true }))
            .toBeVisible();
        const workersControls = await environmentControlSnapshot(page);

        expect(workersControls).toEqual(jobsControls);
        expect(Object.keys(workersControls).sort()).toEqual([
            'codex-effort',
            'codex-fast-mode',
            'codex-model',
            'codex-no-alt-screen',
            'codex-prompt',
            'codex-yolo',
            'env-cli',
            'env-cli-ts-control',
            'env-custom-args',
            'env-name'
        ]);
    });

    test('editing Worker settings on either screen stays synchronized on the other', async ({ page }) => {
        const { state, writes } = await installStatefulApi(page);
        await openApp(page);
        await openJobs(page);

        await openNightlyWorkerFrom(page, '[data-job-environments-table]');
        const originalJobsControls = await environmentControlSnapshot(page);
        expect(originalJobsControls['codex-prompt']).toBe('Use the nightly security-review profile.');
        expect(originalJobsControls['codex-model']).toBe('gpt-5.6-sol');
        expect(originalJobsControls['codex-effort']).toBe('high');
        await page.locator('#codex-prompt').fill('Security review configured from Jobs.');
        await page.locator('#codex-model').selectOption('gpt-5.6-luna');
        await page.locator('#codex-effort').selectOption('max');
        await page.locator('#codex-fast-mode').check();
        await page.locator('#env-form button[type="submit"]').click();
        await expect(page.locator('#env-form')).toHaveCount(0);
        await expect(page.locator('[data-job-environments-table]'))
            .toContainText('Security review configured from Jobs.');

        await openWorkers(page);
        await expect(page.locator('[data-environments-table]'))
            .toContainText('Security review configured from Jobs.');
        await openNightlyWorkerFrom(page, '[data-environments-table]');
        const workersControls = await environmentControlSnapshot(page);
        expect(workersControls['codex-prompt']).toBe('Security review configured from Jobs.');
        expect(workersControls['codex-model']).toBe('gpt-5.6-luna');
        expect(workersControls['codex-effort']).toBe('max');
        expect(workersControls['codex-fast-mode']).toBe(true);

        await page.locator('#codex-prompt').fill('Security review updated from Workers.');
        await page.locator('#codex-model').selectOption('gpt-5.6-terra');
        await page.locator('#codex-effort').selectOption('ultra');
        await page.locator('#env-form button[type="submit"]').click();
        await expect(page.locator('#env-form')).toHaveCount(0);

        await openJobs(page);
        await expect(page.locator('[data-job-environments-table]'))
            .toContainText('Security review updated from Workers.');
        await expect(page.locator('[data-jobs-list]'))
            .toContainText('Security review updated from Workers.');
        await openNightlyWorkerFrom(page, '[data-job-environments-table]');
        await expect(page.locator('#codex-prompt')).toHaveValue('Security review updated from Workers.');
        await expect(page.locator('#codex-model')).toHaveValue('gpt-5.6-terra');
        await expect(page.locator('#codex-effort')).toHaveValue('ultra');

        expect(state.environments[0].customPrompt).toBe('Security review updated from Workers.');
        expect(writes.environments).toHaveLength(2);
        expect(writes.settings).toHaveLength(2);
    });

    test('new automation uses a custom Worker and current repository without duplicate editors', async ({ page }) => {
        const { writes } = await installStatefulApi(page);
        await openApp(page);
        await openJobs(page);

        await page.locator('[data-job-action="new"]').click();
        await expect(page.locator('[data-job-form]')).toBeVisible();
        await expect(page.locator('#job-project')).toHaveCount(0);
        await expect(page.locator('#job-prompt')).toHaveCount(0);
        await expect(page.locator('[data-job-form]')).toContainText('Runs in the current VibeRails repository');
        await expect(page.locator('[data-job-form]')).toContainText(CURRENT_REPOSITORY);
        await expect(page.locator('[data-job-form]')).toContainText('After every successful commit');

        const picker = page.locator('#job-llm-selection');
        await expect.poll(() => picker.evaluate(element => Boolean(element.tomselect)))
            .toBe(true);
        const optionValues = await picker.locator('option').evaluateAll(options =>
            options.map(option => option.value).filter(Boolean));
        expect(optionValues.sort()).toEqual(['env:41:codex', 'env:42:copilot']);
        expect(optionValues.some(value => value.startsWith('base:'))).toBe(false);

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
});
