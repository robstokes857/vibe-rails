// @ts-check

const { test, expect } = process.env.VIBERAILS_AUTOMATION_STATIC === '1' ? require('@playwright/test') : require('./fixtures');

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
        ['glm-5.3', 'GLM 5.3'],
        ['deepseek-v4-pro', 'DeepSeek V4 Pro'],
        ['kimi-k3', 'Kimi K3'],
        ['grok', 'Grok'],
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
    await page.addInitScript(() => { sessionStorage.setItem('viberails_tab', 'automation-fixture'); });
    await page.routeWebSocket('**/api/v1/events/ws*', () => {});
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
                launchDirectory: CURRENT_REPOSITORY,
                isActiveRootBackend: true
            });
        }
        if (method === 'GET' && path === '/api/v1/environments') {
            return respond({ environments: state.environments });
        }
        if (method === 'POST' && path === '/api/v1/environments') {
            const body = request.postDataJSON();
            // Mirrors EnvironmentRoutes: a name LlmParser resolves to a built-in CLI is rejected.
            if (/^(\d+|codex|claude|antigravity|copilot|shell|opencode|grok|kimi-k3|deepseek-v4-pro)$/i.test(String(body.name).trim())) {
                return route.fulfill({ status: 400, json: { error: `'${body.name}' is the name of a built-in CLI.` } });
            }
            const environment = {
                id: state.nextEnvironmentId++,
                name: body.name,
                cli: body.cli,
                path: `C:\\test-envs\\${body.name}`,
                customArgs: body.customArgs || '',
                customPrompt: body.customPrompt || '',
                purpose: body.purpose,
                reviewerRouting: body.reviewerRouting,
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
        if (/^\/api\/v1\/(codex|claude)\/settings\//.test(path)) {
            const name = decodeURIComponent(path.slice(path.indexOf('/settings/') + '/settings/'.length));
            if (method === 'GET') {
                return respond(state.codexSettings[name] || {});
            }
            if (method === 'PUT') {
                const body = request.postDataJSON();
                if (state.failSettings) { state.failSettings--; return route.fulfill({ status: 503, json: { error: 'Settings temporarily unavailable' } }); }
                state.codexSettings[name] = { ...body };
                writes.settings.push({ name, body });
                return respond(body);
            }
        }
        if (method === 'GET' && path === '/api/v1/jobs') {
            return respond({ jobs: state.jobs });
        }
        if (method === 'POST' && path === '/api/v1/jobs') {
            if (state.failJob) { state.failJob--; return route.fulfill({ status: 503, json: { error: 'Automation temporarily unavailable' } }); }
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
        // The Python scripts section reads the developer's real ~/.vibe_rails/scripts
        // through the fallback; keep this spec deterministic (and read-only) instead.
        if (method === 'GET' && path === '/api/v1/python-scripts') {
            return respond({
                pinConfigured: false,
                scriptsDirectory: 'C:\Users\test\.vibe_rails\scripts',
                scripts: []
            });
        }

        if (process.env.VIBERAILS_AUTOMATION_STATIC === '1') return respond({ agents: [], sandboxes: [], runs: [], tabs: [] });
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

async function openJobs(page, { expandDisabled = true } = {}) {
    if (!await page.locator('.jobs-view[data-view="jobs"]').isVisible()) {
        await page.locator('.app-subnav-link[data-view="jobs"]:visible').click();
    }
    await expect(page.locator('.jobs-view[data-view="jobs"]')).toBeVisible({ timeout: 10_000 });
    await expect(page.locator('[data-jobs-list]')).toContainText('Environment review');
    if (expandDisabled && !await page.locator('[data-job-action="edit"][data-job-id="17"]').isVisible()) {
        await page.locator('[data-disabled-jobs] > summary').click();
    }
}

async function openWorkers(page) {
    await page.locator('.app-subnav-link[data-view="environments"]:visible').click();
    // Scope to the view container: [data-view="environments"] now also matches the top subnav link
    // and the sidebar link, and an unscoped locator is a Playwright strict-mode violation.
    await expect(page.locator('.view[data-view="environments"]')).toBeVisible({ timeout: 10_000 });
    await expect(page.locator('[data-environments-table]')).toContainText('Nightly Codex');
}

async function openAutomationEditorForExistingJob(page) {
    await expect(page.locator('[data-job-action="edit"][data-job-id="17"]')).toBeAttached();
    if (!await page.locator('[data-job-action="edit"][data-job-id="17"]').isVisible()) {
        await page.locator('[data-disabled-jobs] > summary').click();
    }
    await page.locator('[data-job-action="edit"][data-job-id="17"]').click();
    await expect(page.locator('[data-job-form]')).toBeVisible();
    await expect(page.locator('#env-initial-message')).toBeVisible();
}

async function environmentControlSnapshot(page) {
    return page.locator('.environment-editor [id]').evaluateAll(elements => Object.fromEntries(
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
    for (const cli of ['claude', 'copilot', 'antigravity', 'grok', 'opencode', 'glm-5.2', 'glm-5.3', 'deepseek-v4-pro', 'kimi-k3']) {
        test(`Automation modal saves the shared ${cli} controls`, async ({ page }) => {
            const { writes } = await installStatefulApi(page);
            await openApp(page);
            await page.locator('[data-job-action="new"]').click();
            await page.locator('#job-name').fill(`${cli} task`);
            await page.locator('#env-initial-message').fill('Keep these instructions.');
            await page.locator('#env-cli').evaluate((el, value) => el.tomselect.setValue(value), cli);
            const model = page.locator('[data-primary-options] [id$="-model"]');
            await expect(model).toBeVisible();
            const selectedModel = await model.evaluate(el => {
                if (el.tagName === 'SELECT') {
                    el.value = [...el.options].find(option => option.value)?.value || '';
                    el.dispatchEvent(new Event('change', { bubbles: true }));
                }
                return el.value;
            });
            const permissions = page.locator('[data-primary-options] input[type="checkbox"]');
            if (await permissions.count()) await permissions.check();
            await page.locator('[data-job-form] button[type="submit"]').click();
            await expect(page.locator('[data-job-form]')).toHaveCount(0);
            expect(writes.environmentCreates).toHaveLength(1);
            expect(writes.environmentCreates[0].body).toMatchObject({ cli, customPrompt: 'Keep these instructions.', automationWorker: true });
            expect(writes.environmentCreates[0].body.customArgs).toContain(selectedModel);
            expect(writes.jobs[0].body.environmentId).toBe(60);
        });
    }

    test('steps modal restores form focus and invalid workflows create nothing', async ({ page }) => {
        const { writes } = await installStatefulApi(page);
        await openApp(page);
        await page.locator('[data-job-action="new"]').click();
        await page.locator('#job-name').fill('Validate first');
        await page.locator('#env-initial-message').fill('Review changes.');
        await page.locator('#job-name').fill('   ');
        await page.locator('[data-job-form] button[type="submit"]').click();
        await expect(page.locator('#job-name')).toBeFocused();
        expect(writes.environmentCreates).toHaveLength(0);
        await page.locator('#job-name').fill('Validate first');
        await page.locator('[data-env-steps-open]').click();
        await expect(page.locator('.env-steps-modal')).toBeVisible();
        expect(await page.locator('[data-job-form]').evaluate(el => Boolean(el.closest('[inert]')))).toBe(true);
        await page.keyboard.press('Escape');
        await expect(page.locator('.env-steps-modal-layer')).toHaveCount(0);
        await expect(page.locator('[data-env-steps-open]')).toBeFocused();
        expect(await page.locator('[data-job-form]').evaluate(el => Boolean(el.closest('[inert]')))).toBe(false);
        await page.locator('#job-trigger-schedule').check();
        await page.locator('#job-schedule-kind').selectOption('2');
        await page.locator('[data-job-form] button[type="submit"]').click();
        await expect(page.locator('[data-job-form]')).toBeVisible();
        expect(writes.environmentCreates).toHaveLength(0);
        expect(writes.settings).toHaveLength(0);
    });

    test('regular Environment creation still trims its name and allows an empty prompt', async ({ page }) => {
        const { writes } = await installStatefulApi(page);
        await openApp(page);
        await openWorkers(page);
        await page.locator('[data-action="create-environment"]').click();
        await page.locator('#env-name').fill('  My environment  ');
        await expect(page.locator('[data-primary-options] #codex-model')).toBeVisible();
        await page.locator('#env-form button[type="submit"]').click();
        await expect(page.locator('#env-form')).toHaveCount(0);
        expect(writes.environmentCreates[0].body.name).toBe('My environment');
        expect(writes.environmentCreates[0].body.automationWorker).toBeUndefined();
        expect(writes.environmentCreates[0].body.steps).toBeUndefined();
    });


    test('Codex Speed saves and reopens Ultrafast, Fast and Default, restricted to Astra', async ({ page }) => {
        const { writes } = await installStatefulApi(page);
        await openApp(page);
        await openWorkers(page);
        await page.locator('[data-action="create-environment"]').click();
        await page.locator('#env-name').fill('Astra speed');
        const speed = page.getByLabel('Speed', { exact: true });
        const model = page.locator('#codex-model');
        const ultrafast = speed.locator('option[value="ultrafast"]');
        await expect(ultrafast).toBeDisabled();
        await model.selectOption('gpt-6-astra');
        await expect(ultrafast).toBeEnabled();
        await speed.selectOption('ultrafast');
        await model.selectOption('gpt-6.1-sol');
        await expect(speed).toHaveValue('');
        await expect(ultrafast).toBeDisabled();
        await model.selectOption('gpt-6-astra');

        for (const tier of ['ultrafast', 'fast', '']) {
            await speed.selectOption(tier);
            await page.locator('#env-form button[type="submit"]').click();
            await expect(page.locator('#env-form')).toHaveCount(0);
            const write = tier === 'ultrafast' ? writes.environmentCreates.at(-1) : writes.environments.at(-1);
            expect(write.body.customArgs).toBe('--model gpt-6-astra'
                + (tier ? ` -c service_tier=${tier} --enable fast_mode` : ''));
            expect(writes.settings.at(-1).body).toMatchObject({
                model: 'gpt-6-astra', fastMode: tier === 'fast', ultrafastMode: tier === 'ultrafast'
            });
            await page.locator('[data-action="edit-environment"][data-env-name="Astra speed"]').click();
            await expect(speed).toHaveValue(tier);
        }
    });

    test('changing work type after a partial save keeps the created provider', async ({ page }) => {
        const { state, writes } = await installStatefulApi(page);
        state.failSettings = 1;
        await openApp(page);
        await page.locator('[data-job-action="new"]').click();
        await page.locator('#job-name').fill('Claude review');
        await page.locator('#env-cli').evaluate(el => el.tomselect.setValue('claude'));
        await page.locator('#env-initial-message').fill('Review changes.');
        const submit = page.locator('[data-job-form] button[type="submit"]');
        await submit.click();
        await expect(submit).toBeEnabled();
        await page.locator('#env-purpose').selectOption('code_review');
        await expect(page.locator('#claude-model')).toBeVisible();
        await expect(page.locator('#env-cli')).toHaveValue('claude');
        await submit.click();
        await expect(page.locator('[data-job-form]')).toHaveCount(0);
        expect(writes.environmentCreates).toHaveLength(1);
        expect(writes.environments[0].body.purpose).toBe('code_review');
        expect(writes.jobs).toHaveLength(1);
    });

    test('one form uses the shared provider controls and saves one name', async ({ page }) => {
        const { writes } = await installStatefulApi(page);
        await openApp(page);
        await page.locator('[data-job-action="new"]').click();
        await page.locator('#job-name').fill('Post-merge auditor');
        await expect(page.locator('#env-name')).toHaveCount(0);
        await expect(page.locator('#env-form')).toHaveCount(0);
        await expect(page.locator('[data-primary-options] #codex-model')).toBeVisible();
        await expect(page.locator('[data-primary-options] #codex-effort')).toBeVisible();
        await expect(page.locator('[data-primary-options] #codex-yolo')).toBeVisible();
        await page.locator('#env-initial-message').fill('Audit every merge.');
        await page.locator('#codex-model').selectOption('gpt-6.1-sol');
        await page.locator('#codex-effort').selectOption('high');
        await page.locator('#codex-yolo').check();
        await page.locator('#env-workspace-mode').selectOption('2');
        await page.locator('[data-job-form] button[type="submit"]').click();
        await expect(page.locator('[data-job-form]')).toHaveCount(0);
        expect(writes.environmentCreates).toHaveLength(1);
        expect(writes.jobs).toHaveLength(1);
        expect(writes.environmentCreates[0].body).toMatchObject({ name: 'Post-merge auditor', automationWorker: true, workspaceMode: 2, customPrompt: 'Audit every merge.' });
        expect(writes.environmentCreates[0].body.customArgs).toContain('--model gpt-6.1-sol');
        expect(writes.environmentCreates[0].body.customArgs).toContain('--dangerously-bypass-approvals-and-sandbox');
        expect(writes.jobs[0].body.environmentId).toBe(60);
        expect(writes.jobs[0].body.name).toBe('Post-merge auditor');
        expect(writes.settings[0].body).toMatchObject({ model: 'gpt-6.1-sol', effort: 'high', yolo: true });
    });

    for (const failure of ['failSettings', 'failJob']) test(`retry after ${failure} reuses the created Worker`, async ({ page }) => {
        const { state, writes } = await installStatefulApi(page);
        state[failure] = 1;
        await openApp(page);
        await page.locator('[data-job-action="new"]').click();
        await page.locator('#job-name').fill('Retry safely');
        await page.locator('#env-initial-message').fill('Review changes.');
        const submit = page.locator('[data-job-form] button[type="submit"]');
        await submit.click();
        await expect(submit).toBeEnabled();
        await expect(page.locator('#env-initial-message')).toHaveValue('Review changes.');
        expect(writes.environmentCreates).toHaveLength(1);
        await page.locator('#env-initial-message').fill('Review all changes.');
        await submit.click();
        await expect(page.locator('[data-job-form]')).toHaveCount(0);
        expect(writes.environmentCreates).toHaveLength(1);
        expect(writes.jobs).toHaveLength(1);
        expect(writes.jobs[0].body.environmentId).toBe(60);
        expect(writes.jobs[0].body.prompt).toBe('Review all changes.');
    });

    test('draft survives provider switching and workflow reorder; cancelling writes nothing', async ({ page }) => {
        const { writes } = await installStatefulApi(page);
        await openApp(page);
        await page.locator('[data-job-action="new"]').click();
        await page.locator('#job-name').fill('Draft');
        await page.locator('#env-initial-message').fill('Keep these instructions.');
        await page.locator('#codex-model').selectOption('gpt-6.1-sol');
        await page.locator('#codex-effort').selectOption('high');
        await page.locator('#env-cli').evaluate(el => el.tomselect.setValue('claude'));
        await page.locator('#claude-model').selectOption('claude-opus-5-5[1m]');
        await page.locator('#env-purpose').selectOption('code_review');
        await expect(page.locator('#env-cli')).toHaveValue('claude');
        await expect(page.locator('#env-initial-message')).toHaveValue('Keep these instructions.');
        await page.locator('#env-cli').evaluate(el => el.tomselect.setValue('codex'));
        await expect(page.locator('#codex-model')).toHaveValue('gpt-6.1-sol');
        await expect(page.locator('#codex-effort')).toHaveValue('high');
        await page.locator('[data-job-action="add-script-action"]').click();
        await page.locator('[data-kind="script"] [data-workflow-action="move-up"]').click();
        await expect(page.locator('#env-initial-message')).toHaveValue('Keep these instructions.');
        await expect(page.locator('#codex-model')).toHaveValue('gpt-6.1-sol');
        await page.locator('[data-job-action="cancel-editor"]').first().click();
        expect(writes.environmentCreates).toHaveLength(0);
        expect(writes.jobs).toHaveLength(0);
    });

    test('shared editor remains usable at narrow widths', async ({ page }) => {
        await installStatefulApi(page);
        await page.setViewportSize({ width: 375, height: 850 });
        await openApp(page);
        await page.locator('[data-job-action="new"]').click();
        expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
        for (const selector of ['#env-cli', '#codex-model', '#codex-effort', '#codex-yolo', '#env-initial-message']) {
            await expect(page.locator(selector)).toBeVisible();
            const box = await page.locator(selector).boundingBox();
            expect(box.x).toBeGreaterThanOrEqual(0);
            expect(box.x + box.width).toBeLessThanOrEqual(375);
        }
    });

    test('editing the automation and Environments uses the same settings and one save', async ({ page }) => {
        const { state, writes } = await installStatefulApi(page);
        await openApp(page);
        await openAutomationEditorForExistingJob(page);
        await expect(page.locator('#env-initial-message')).toHaveValue('Use the nightly security-review profile.');
        await expect(page.locator('#env-name')).toHaveCount(0);
        await expect(page.locator('#env-form')).toHaveCount(0);
        await expect(page.locator('#codex-model')).toHaveValue('gpt-5.6-sol');
        await page.locator('#env-initial-message').fill('Security review configured from Automation.');
        await page.locator('#codex-model').selectOption('gpt-5.6-luna');
        await page.locator('[data-job-form] button[type="submit"]').click();
        await expect(page.locator('[data-job-form]')).toHaveCount(0);
        expect(writes.jobs[0].body.prompt).toBe('Security review configured from Automation.');
        expect(writes.environmentCreates).toHaveLength(0);
        await openWorkers(page);
        await page.locator('[data-action="edit-environment"][data-env-name="Nightly Codex"]').click();
        await expect(page.locator('#env-initial-message')).toHaveValue('Security review configured from Automation.');
        await expect(page.locator('#codex-model')).toHaveValue('gpt-5.6-luna');
        await page.locator('#env-initial-message').fill('Updated from Environments.');
        await page.locator('#env-form button[type="submit"]').click();
        await expect(page.locator('#env-form')).toHaveCount(0);
        expect(state.environments[0].customPrompt).toBe('Updated from Environments.');
        expect(writes.environments).toHaveLength(2);
        expect(writes.settings).toHaveLength(2);
        await openJobs(page);
        await openAutomationEditorForExistingJob(page);
        await expect(page.locator('#env-initial-message')).toHaveValue('Updated from Environments.');
    });

    // VIBE-58 / VB-8WE2S-129 F1: renaming an Automation used to PUT a Worker rebuilt from its
    // controls, dropping --sandbox read-only and --search for every user of that Worker.
    test('a rename leaves the Worker untouched and Worker edits keep unrepresented arguments', async ({ page }) => {
        const { state, writes } = await installStatefulApi(page);
        const stored = '--model gpt-5.6-sol -c model_reasoning_effort=high --sandbox read-only --search';
        state.environments[0].customArgs = stored;
        const submit = page.locator('[data-job-form] button[type="submit"]');
        await openApp(page);
        await openAutomationEditorForExistingJob(page);
        await expect(page.locator('#codex-additional-args')).toHaveValue('--sandbox read-only --search');
        await page.locator('#job-name').fill('Rename only');
        await submit.click();
        await expect(page.locator('[data-job-form]')).toHaveCount(0);
        expect(writes.environments).toHaveLength(0);
        expect(writes.settings).toHaveLength(0);
        expect(writes.jobs).toHaveLength(1);
        expect(writes.jobs[0].body).toMatchObject({ name: 'Rename only', environmentId: 41, prompt: 'Use the nightly security-review profile.' });
        expect(state.environments[0].customArgs).toBe(stored);

        await openAutomationEditorForExistingJob(page);
        await expect(page.locator('#codex-additional-args')).toHaveValue('--sandbox read-only --search');
        await page.locator('#codex-model').selectOption('gpt-5.6-luna');
        await submit.click();
        await expect(page.locator('[data-job-form]')).toHaveCount(0);
        expect(writes.environments).toHaveLength(1);
        expect(writes.environments[0].body.customArgs).toBe('--model gpt-5.6-luna -c model_reasoning_effort=high --sandbox read-only --search');
        expect(state.environments[0].customArgs).toBe('--model gpt-5.6-luna -c model_reasoning_effort=high --sandbox read-only --search');

        // The Environments page mounts the same editor: saving it unchanged writes nothing.
        await openWorkers(page);
        await page.locator('[data-action="edit-environment"][data-env-name="Nightly Codex"]').click();
        await expect(page.locator('#codex-additional-args')).toHaveValue('--sandbox read-only --search');
        await page.locator('#env-form button[type="submit"]').click();
        await expect(page.locator('#env-form')).toHaveCount(0);
        expect(writes.environments).toHaveLength(1);
        expect(writes.settings).toHaveLength(1);
    });

    // F2: the derived Worker identifier must not be a name EnvironmentRoutes rejects.
    test('an Automation named after a built-in CLI keeps its name and gets a valid Worker', async ({ page }) => {
        const { writes } = await installStatefulApi(page);
        await openApp(page);
        await page.locator('[data-job-action="new"]').click();
        await page.locator('#job-name').fill('Codex');
        await page.locator('#env-initial-message').fill('Review with Codex.');
        await page.locator('[data-job-form] button[type="submit"]').click();
        await expect(page.locator('[data-job-form]')).toHaveCount(0);
        expect(writes.environmentCreates).toHaveLength(1);
        expect(writes.environmentCreates[0].body.name).toBe('Codex Worker');
        expect(writes.jobs[0].body).toMatchObject({ name: 'Codex', environmentId: 60 });
    });

    // F3: lane checks and other script/check-only workflows must stay editable as they are.
    test('a check-only Automation saves unchanged and converts only when instructions are added', async ({ page }) => {
        const { state, writes } = await installStatefulApi(page);
        const checks = Array.from({ length: 20 }, (_, index) => ({ id: `check-${index}`, kind: index % 2 ? 2 : 3, arguments: ['working-tree'] }));
        state.jobs.push({
            id: 19, name: 'Lane checks', projectPath: CURRENT_REPOSITORY, llm: 0, environmentId: null, environmentName: null,
            prompt: '', timeoutMinutes: null, enabled: true, triggers: [], actions: checks
        });
        const submit = page.locator('[data-job-form] button[type="submit"]');
        const addInstructions = page.locator('[data-job-action="add-worker-action"]');
        await openApp(page);
        await page.locator('[data-job-action="edit"][data-job-id="19"]').click();
        await expect(page.locator('[data-job-form]')).toBeVisible();
        await expect(page.locator('[data-kind="worker"]')).toHaveCount(0);
        await expect(page.locator('#env-initial-message')).toHaveCount(0);
        await expect(addInstructions).toBeVisible();
        await submit.click();
        await expect(page.locator('[data-job-form]')).toHaveCount(0);
        expect(writes.environmentCreates).toHaveLength(0);
        expect(writes.jobs).toHaveLength(1);
        expect(writes.jobs[0].body).toMatchObject({ name: 'Lane checks', environmentId: null, prompt: '' });
        expect(writes.jobs[0].body.actions).toEqual(checks);

        // Conversion is explicit: drop one check to make room, add instructions, and the added
        // Worker can be removed again (its draft comes back with it) before anything is saved.
        await page.locator('[data-job-action="edit"][data-job-id="19"]').click();
        await page.locator('[data-job-action-id="check-19"] [data-workflow-action="remove"]').click();
        await addInstructions.click();
        await expect(addInstructions).toBeHidden();
        await page.locator('#env-initial-message').fill('Review the check results.');
        await page.locator('[data-kind="worker"] [data-workflow-action="remove"]').click();
        await expect(page.locator('[data-kind="worker"]')).toHaveCount(0);
        await addInstructions.click();
        await expect(page.locator('#env-initial-message')).toHaveValue('Review the check results.');
        await submit.click();
        await expect(page.locator('[data-job-form]')).toHaveCount(0);
        expect(writes.environmentCreates).toHaveLength(1);
        expect(writes.environmentCreates[0].body).toMatchObject({ name: 'Lane checks', automationWorker: true, customPrompt: 'Review the check results.' });
        const converted = writes.jobs[1].body;
        expect(converted).toMatchObject({ environmentId: 60, prompt: 'Review the check results.' });
        expect(converted.actions).toHaveLength(20);
        expect(converted.actions.at(-1)).toMatchObject({ kind: 0, environmentId: 60 });

        // A new Automation still always includes its required Worker.
        await page.locator('[data-job-action="new"]').click();
        await expect(page.locator('[data-kind="worker"]')).toHaveCount(1);
        await expect(page.locator('[data-kind="worker"] [data-workflow-action="remove"]')).toHaveCount(0);
        await expect(addInstructions).toBeHidden();
    });

    test('new automation includes its own configuration and commit triggers', async ({ page }) => {
        const { writes } = await installStatefulApi(page);
        await openApp(page);
        await page.locator('[data-job-action="new"]').click();
        expect(await page.locator('[data-job-form]').innerText()).not.toMatch(/Worker|Configure new agent/);
        await expect(page.locator('#job-llm-selection')).toHaveCount(0);
        await expect(page.locator('[data-workflow-action="remove"]')).toHaveCount(0);
        await page.locator('#env-initial-message').fill('Review this commit.');
        await page.locator('#job-name').fill('Security review after commit');
        await page.locator('#job-trigger-precommit').check();
        await page.locator('#job-trigger-commit').check();
        await expect(page.locator('#job-trigger-precommit')).not.toBeChecked();
        await page.locator('#job-trigger-precommit').check();
        await expect(page.locator('#job-trigger-commit')).not.toBeChecked();
        await page.locator('#job-trigger-commit').check();
        await page.locator('[data-job-form] button[type="submit"]').click();

        await expect.poll(() => writes.jobs.filter(write => write.method === 'POST').length)
            .toBe(1);
        const create = writes.jobs.find(write => write.method === 'POST');
        expect(create.body.projectPath).toBe(CURRENT_REPOSITORY);
        expect(create.body.environmentId).toBe(60);
        expect(create.body.llm).toBe(1);
        expect(create.body.prompt).toBe('Review this commit.');
        expect(writes.environmentCreates[0].body.name).toBe(create.body.name);
        expect(create.body.triggers).toEqual([{ kind: 2 }]);
    });

    test('prompt tokens insert at the cursor, replace selection, and work from the keyboard', async ({ page }) => {
        await installStatefulApi(page);
        await openApp(page);
        await page.locator('[data-job-action="new"]').click();
        const input = page.locator('#env-initial-message');
        await input.fill('Review changes now.');
        await input.evaluate(el => el.setSelectionRange(7, 14));
        await page.locator('[data-prompt-variable="{{git_branch}}"]') .click();
        await expect(input).toHaveValue('Review {{git_branch}} now.');
        await expect(input).toBeFocused();
        const tokens = await page.locator('[data-prompt-variable]').evaluateAll(elements => elements.map(el => el.dataset.promptVariable));
        for (const token of tokens) {
            await input.fill('');
            const button = page.locator('[data-prompt-variable]').filter({ has: page.locator('code', { hasText: token }) });
            await button.focus();
            await page.keyboard.press('Enter');
            await expect(input).toHaveValue(token);
            await expect(input).toBeFocused();
        }
    });

    test('modal cancel, Escape, focus trap and reopening leave no stale draft', async ({ page }) => {
        const { writes } = await installStatefulApi(page);
        await openApp(page);
        const opener = page.locator('[data-job-action="new"]');
        await opener.click();
        await page.locator('#job-name').fill('Unsaved draft');
        await page.locator('#env-initial-message').fill('Unsaved instructions.');
        await page.locator('[data-job-form] button[type="submit"]').focus();
        await page.keyboard.press('Tab');
        await expect(page.locator('.job-creation-modal [data-action="close-modal"]')).toBeFocused();
        await page.keyboard.press('Escape');
        await expect(page.locator('[data-job-form]')).toHaveCount(0);
        await expect(opener).toBeFocused();
        await opener.click();
        await expect(page.locator('#job-name')).toHaveValue('');
        await expect(page.locator('#env-initial-message')).toHaveValue('');
        await page.locator('[data-job-action="cancel-editor"]').click();
        await expect(page.locator('[data-job-form]')).toHaveCount(0);
        expect(writes.environmentCreates).toHaveLength(0);
        expect(writes.settings).toHaveLength(0);
        expect(writes.jobs).toHaveLength(0);
    });

    test('the enable toggle is a real switch that reflects the job state', async ({ page }) => {
        const { state } = await installStatefulApi(page);
        await openApp(page);
        await openJobs(page, { expandDisabled: false });

        // Fixture job 17 is disabled → switch off.
        const toggle = page.locator('[data-job-action="toggle"][data-job-id="17"]');
        const group = page.locator('[data-disabled-jobs]');
        await expect(toggle).toBeHidden();
        await expect(group.locator(':scope > summary')).toHaveText('Disabled automations1');
        await group.locator(':scope > summary').focus();
        await page.keyboard.press('Enter');
        await expect(toggle).toBeVisible();
        state.jobs[0].prompt = 'Updated while the disabled group is open.';
        await page.evaluate(() => window.app.jobController.refreshJobs({ quiet: true }));
        await expect(group).toHaveJSProperty('open', true);
        await expect(toggle).toHaveClass(/job-switch/);
        await expect(toggle).toHaveAttribute('role', 'switch');
        await expect(toggle).toHaveAttribute('aria-checked', 'false');
        await expect(toggle.locator('.job-switch-text')).toHaveText('Disabled');

        await toggle.click();
        const enabledToggle = page.locator('[data-job-action="toggle"][data-job-id="17"]');
        await expect(enabledToggle).toBeVisible();
        await expect(group).toHaveCount(0);
        await expect(enabledToggle).toHaveAttribute('aria-checked', 'true');
        await expect(enabledToggle.locator('.job-switch-text')).toHaveText('Enabled');
    });

    for (const width of [1440, 390]) {
        test(`saved Switch reviewer offers every other LLM and persists across cards at ${width}px`, async ({ page }, testInfo) => {
            await page.setViewportSize({ width, height: 1100 });
            const { state, writes } = await installStatefulApi(page);
            state.environments[0].purpose = 'code_review';
            state.environments[0].reviewerRouting = { mode: 'switch', fallback: { selection: 'base:codex' }, mappings: [
                { sourceProvider: 'claude', reviewer: { selection: 'base:codex' } },
                { sourceProvider: 'codex', reviewer: { selection: 'base:claude' } }
            ] };
            // Even a globally hidden provider remains configurable for this saved Board policy.
            state.pickerItems.find(item => item.cli === 'grok').enabled = false;
            state.pickerItems.push({ key: 'env:99:codex', kind: 'environment', cli: 'codex', label: 'Other Codex', enabled: true });
            await openApp(page);
            await openAutomationEditorForExistingJob(page);
            const form = page.locator('[data-job-form]');
            const defaultReviewer = form.locator('[data-reviewer-fallback] [data-reviewer-target]');
            const alternate = form.locator('[data-reviewer-alternate] [data-reviewer-target]');
            await expect(form.locator('[data-primary-options]')).toBeHidden();
            await expect(alternate).toHaveValue('base:claude');
            const options = await alternate.evaluate(select => Object.keys(select.tomselect.options));
            for (const cli of ['claude', 'grok', 'copilot', 'opencode', 'antigravity', 'glm-5.2', 'glm-5.3', 'deepseek-v4-pro', 'kimi-k3']) {
                expect(options).toContain(`base:${cli}`);
            }
            expect(options).not.toContain('base:codex');
            expect(options).not.toContain('env:99:codex');
            await alternate.evaluate(select => select.tomselect.setValue('base:grok'));
            await page.screenshot({ path: testInfo.outputPath(`switch-reviewer-settings-${width}.png`) });
            await form.locator('button[type="submit"]').click();
            await expect(form).toHaveCount(0);
            expect(writes.environments[0].body.reviewerRouting).toMatchObject({ fallback: { selection: 'base:codex' },
                mappings: [{ sourceProvider: 'codex', reviewer: { selection: 'base:grok' } },
                    { sourceProvider: 'claude', reviewer: { selection: 'base:codex' } }] });
            await openAutomationEditorForExistingJob(page);
            await expect(alternate).toHaveValue('base:grok');
            // Changing the default removes it from the alternate's choices without selecting a substitute.
            await defaultReviewer.evaluate(select => select.tomselect.setValue('base:grok'));
            await expect(alternate).toHaveValue('');
            expect(await alternate.evaluate(select => Object.keys(select.tomselect.options))).not.toContain('base:grok');
            await alternate.evaluate(select => select.tomselect.setValue('base:copilot'));
            await form.locator('button[type="submit"]').click();
            await expect(form).toHaveCount(0);
            await openAutomationEditorForExistingJob(page);
            await expect(defaultReviewer).toHaveValue('base:grok');
            await expect(alternate).toHaveValue('base:copilot');
        });
    }

    test('Automation opens a real modal with ordinary visible form settings', async ({ page }) => {
        await installStatefulApi(page);
        await openApp(page);
        await openJobs(page);

        const disabledRow = page.locator('.job-card[data-enabled="false"]');
        await expect(disabledRow).toHaveCSS('opacity', '1');
        expect(await disabledRow.evaluate(element => getComputedStyle(element).backgroundColor))
            .not.toBe('rgba(0, 0, 0, 0)');

        await page.locator('[data-job-action="edit"][data-job-id="17"]').click();
        await expect(page.locator('[data-job-form]')).toBeVisible();
        await expect(page.getByRole('dialog')).toHaveAttribute('aria-modal', 'true');
        expect(await page.locator('[data-jobs-list]').evaluate(el => Boolean(el.closest('[inert]')))).toBe(true);
        await expect(page.locator('[data-job-form]')).not.toContainText('Advanced');
        await expect(page.locator('[data-job-form] details')).toHaveCount(0);
        await expect(page.locator('#env-purpose')).toBeVisible();
        await expect(page.locator('.env-prompt-variables')).toBeVisible();
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
        await expect(page.locator('.job-creation-modal .modal-title')).toContainText(longName);
        expect(await page.evaluate(() => document.documentElement.scrollWidth))
            .toBeLessThanOrEqual(320);
    });
});
