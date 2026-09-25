// @ts-check
// The full report now uses the host-owned combined Code Atlas / Quality Lab viewer.

// The focused static config skips the machine-wide backend; the normal suite keeps
// using the authenticated backend fixture. Both modes exercise the same UI tests.
const { test, expect } = process.env.VIBERAILS_QUALITY_STATIC === '1'
    ? require('@playwright/test')
    : require('./fixtures');

const PAYMENT_PATH = 'src/Payments/PaymentProcessor.cs';
const HELPER_PATH = 'src/Utilities/HealthyHelper.cs';

function metric(name, score, line) {
    return {
        name, score, line, value: score > 54 ? 31 : 2,
        warn: 15, critical: 25, higherIsBetter: false,
        source: 'Process', snippet: `// ${name} evidence at line ${line}`
    };
}

function scanResponse() {
    const categories = [
        { name: 'Complexity', score: 88, metrics: [
            metric('cognitive_complexity', 88, 12),
            metric('cyclomatic_complexity', 68, 28),
            metric('nesting_depth', 60, 37)
        ] },
        { name: 'Size', score: 10, metrics: [
            metric('lines_of_code', 10, 1),
            metric('method_count', 5, 8),
            metric('field_count', 4, 4),
            metric('parameter_count', 3, 18)
        ] },
        { name: 'Dependencies', score: 8, metrics: [
            metric('fan_out', 8, 6),
            metric('hard_coded_dependencies', 5, 9),
            metric('ambient_dependencies', 3, 11)
        ] },
        { name: 'Maintainability', score: 10, metrics: [
            metric('maintainability_index', 10, 1),
            metric('duplication', 8, 15),
            metric('halstead_difficulty', 5, 19),
            metric('lack_of_cohesion', 3, 23)
        ] }
    ].map(category => ({ ...category, weight: 1, weightedScore: category.score }));
    return {
        success: true, healthScore: 64, rating: 'NeedsWork',
        analyzedFileCount: 2, skippedFileCount: 0, durationMs: 82,
        output: 'Fixture scan complete.',
        report: {
            score: 36, rating: 'NeedsWork', worstMetrics: [],
            overview: ['Complexity', 'Size', 'Cohesion', 'Coupling', 'Testability', 'Duplication', 'Maintainability'].map(category => ({
                category, concern: 36, worstConcern: 88, worstMetricFile: PAYMENT_PATH, worstMetricName: 'cognitive_complexity'
            })),
            files: [
                {
                    file: PAYMENT_PATH, score: 82, rating: 'AtRisk', priority: 95,
                    referencedByCount: 4, baselineScore: 72, introducedScore: 10,
                    categories
                },
                {
                    file: HELPER_PATH, score: 8, rating: 'Clean', priority: 8,
                    referencedByCount: 1, baselineScore: 8, introducedScore: 0,
                    categories: [{ name: 'Complexity', score: 8, weight: 1, weightedScore: 8,
                        metrics: [metric('cognitive_complexity', 8, 6)] }]
                }
            ]
        }
    };
}

function graphResponse() {
    return { schemaVersion: '1.0', repository: { name: 'Test repository' }, fileCount: 2, truncated: false,
        description: 'Source references from the test repository.',
        nodes: [
            { id: 'payments', name: 'Payments', kind: 'module', path: 'src/Payments' },
            { id: 'utilities', name: 'Utilities', kind: 'module', path: 'src/Utilities' },
            { id: 'payment-file', name: 'PaymentProcessor.cs', kind: 'file', path: PAYMENT_PATH, parentId: 'payments' },
            { id: 'helper-file', name: 'HealthyHelper.cs', kind: 'file', path: HELPER_PATH, parentId: 'utilities' }
        ], edges: [
            { id: 'one', source: 'payments', target: 'payment-file', kind: 'contains' },
            { id: 'two', source: 'utilities', target: 'helper-file', kind: 'contains' },
            { id: 'reference', source: 'payments', target: 'utilities', kind: 'references', evidence: 'A supplied source reference' }
        ] };
}

async function installQualityApi(page, { empty = false } = {}) {
    const sourceRequests = [];
    const scanRequests = [];
    let ignoredFiles = [];
    if (process.env.VIBERAILS_QUALITY_STATIC === '1') {
        await page.addInitScript(() => {
            // Atlas frames have opaque origins; only the app needs the API fixture token.
            if (window === window.top) sessionStorage.setItem('viberails_tab', 'quality-fixture');
        });
    }
    await page.routeWebSocket('**/api/v1/events/ws*', () => {});
    // Keep the UX fixture independent of machine-wide environments, preferences, and
    // repository state. The actual app, shared picker, CSS, and Monaco still run.
    await page.route('**/api/v1/**', route => {
        const path = new URL(route.request().url()).pathname;
        const payloads = {
            '/api/v1/context': { isInGit: true, rootPath: 'C:/source/vibe-rails', launchDirectory: 'C:/source/vibe-rails' },
            '/api/v1/settings': {},
            '/api/v1/environments': { environments: [{ id: 902, name: 'Review agent', cli: 'codex' }] },
            '/api/v1/agents': { agents: [] },
            '/api/v1/rules/details': { rules: [] },
            '/api/v1/sandboxes': { sandboxes: [] },
            '/api/v1/llm-picker/preferences': { items: [
                { key: 'base:claude', kind: 'base', group: 'Base CLIs', label: 'Claude (default)', cli: 'claude', enabled: true, order: 0 },
                { key: 'base:codex', kind: 'base', group: 'Base CLIs', label: 'Codex (default)', cli: 'codex', enabled: true, order: 1 },
                { key: 'base:shell', kind: 'base', group: 'Base CLIs', label: 'Terminal', cli: 'shell', enabled: true, order: 2 },
                { key: 'env:902:codex', kind: 'environment', group: 'Custom Environments', label: 'Review agent (Codex)', cli: 'codex', environmentId: 902, enabled: true, order: 0 }
            ] }
        };
        return route.fulfill({ json: payloads[path] || {} });
    });
    await page.route('**/api/v1/hooks/status', route => route.fulfill({ json: {
        inGitRepo: true, isInstalled: true, repositoryPath: 'C:/source/vibe-rails'
    } }));
    await page.route('**/api/v1/hooks/preview', route => route.fulfill({ json: {
        success: true, status: 'passed', output: 'No rule violations.', violations: []
    } }));
    await page.route(/\/api\/v1\/code-analyzer(?:\?.*)?$/, route => {
        scanRequests.push(new URL(route.request().url()).search);
        const response = scanResponse();
        response.report.files = response.report.files.filter(file => !ignoredFiles.some(entry => entry.path === file.file));
        if (empty) {
            response.report.files = [];
            response.report.score = null;
            response.report.rating = null;
            response.healthScore = null;
            response.rating = null;
            response.output = 'Scan complete. No changed source files to analyze.';
        }
        response.analyzedFileCount = response.report.files.length;
        return route.fulfill({ json: response });
    });
    await page.route('**/api/v1/code-analyzer/graph', route => route.fulfill({ json: graphResponse() }));
    await page.route('**/api/v1/code-analyzer/ignores*', route => {
        if (route.request().method() === 'POST') ignoredFiles.push(route.request().postDataJSON());
        if (route.request().method() === 'DELETE') {
            const path = new URL(route.request().url()).searchParams.get('path');
            ignoredFiles = ignoredFiles.filter(entry => entry.path !== path);
        }
        return route.fulfill({ json: { files: ignoredFiles } });
    });
    await page.route('**/api/v1/code-analyzer/source?*', route => {
        const filePath = new URL(route.request().url()).searchParams.get('path');
        sourceRequests.push(filePath);
        return route.fulfill({ json: {
            content: Array.from({ length: 80 }, (_, index) => `// ${filePath}: source line ${index + 1}`).join('\n')
        } });
    });
    return { sourceRequests, scanRequests };
}

async function openQuality(page) {
    await page.goto('/?view=agents', { waitUntil: 'domcontentloaded' });
    const brief = page.locator('[data-vca-quality-brief]');
    await expect(brief).toBeVisible({ timeout: 20_000 });
    await expect(page.locator('#loading-overlay')).toHaveClass(/\bd-none\b/);
    return brief;
}

async function openDetails(page) {
    const brief = await openQuality(page);
    await brief.getByRole('button', { name: 'View metrics' }).click();
    const report = page.locator('.code-report');
    await expect(report).toBeVisible();
    await expect(report.locator('.qr')).toHaveAttribute('data-state', 'complete', { timeout: 25_000 });
    return report;
}

async function selectNamedOption(select, label) {
    const option = select.locator('option').filter({ hasText: label });
    await expect(option).toHaveCount(1);
    await select.selectOption(await option.getAttribute('value'));
}

async function inlineAgentControl(picker) {
    await expect.poll(() => picker.evaluate(select => Boolean(select.tomselect))).toBe(true);
    const control = picker.locator('..').locator('.ts-control');
    await expect(control).toHaveCount(1);
    return control;
}

async function expectInlineAgentSelection(page, value) {
    const pickers = page.locator('select[data-project-health-fix-agent]');
    await expect(pickers).toHaveCount(2);
    await expect.poll(() => pickers.evaluateAll(selects => selects.map(select => select.value)))
        .toEqual([value, value]);
}

async function expectSharedButtonStyle(button) {
    await expect(button).toHaveClass(/\bbtn\b/);
    const styles = await button.evaluate(element => {
        // Compare against an ordinary app button in the same theme, without any
        // Quality-specific classes. This catches local overrides, not just markup.
        const reference = document.createElement(element.tagName);
        reference.className = Array.from(element.classList)
            .filter(name => name === 'btn' || name.startsWith('btn-')).join(' ');
        reference.disabled = element.disabled;
        reference.textContent = element.textContent;
        element.parentElement.append(reference);
        const properties = ['backgroundColor', 'color', 'borderTopColor', 'borderLeftColor',
            'borderTopWidth', 'borderRadius', 'paddingTop', 'paddingRight', 'paddingBottom',
            'paddingLeft', 'fontFamily', 'fontSize', 'fontWeight', 'lineHeight', 'letterSpacing',
            'minHeight', 'boxShadow'];
        const read = node => {
            const style = getComputedStyle(node);
            return Object.fromEntries(properties.map(property => [property, style[property]]));
        };
        const result = { actual: read(element), standard: read(reference) };
        reference.remove();
        return result;
    });
    expect(styles.actual).toEqual(styles.standard);
}

for (const view of ['dashboard', 'agents']) {
    test(`dashboard report mounts in the live document from ${view}`, async ({ page }) => {
        const errors = [];
        page.on('pageerror', error => errors.push(error.message));
        await page.emulateMedia({ reducedMotion: 'reduce' });
        const { scanRequests } = await installQualityApi(page);
        await page.goto(`/?view=${view}`, { waitUntil: 'domcontentloaded' });

        const report = page.locator('.code-report');
        await expect(report.locator('.qr')).toHaveAttribute('data-state', 'complete');
        expect(await page.evaluate(() => {
            const viewer = window.app.ruleController.codeReportViewer;
            return viewer.host.isConnected && viewer.document === document && viewer.window === window;
        })).toBe(true);

        // Document-level Escape must close saved details without navigating away.
        await report.getByRole('button', { name: /^Complexity:/ }).click();
        await expect(report.locator('.details-panel')).toBeVisible();
        await report.locator('.details-panel h2').press('Escape');
        await expect(report.locator('.details-panel')).toBeHidden();
        await expect(report).toBeVisible();

        await page.locator('[data-action="navigate"][data-view="environments"]:visible').click();
        await expect(report).toHaveCount(0);
        await page.locator('[data-action="navigate-home"]:visible').click();
        await expect(report.locator('.qr')).toHaveAttribute('data-state', 'complete');
        expect(scanRequests).toHaveLength(1);
        expect(errors).toEqual([]);
    });
}

test('Quality actions use the shared app button styles', async ({ page }) => {
    await installQualityApi(page);
    await openQuality(page);
    await page.addStyleTag({ content: '.btn { transition: none !important; }' });
    await page.mouse.move(0, 0);
    const actions = page.locator('.project-health-page').locator([
        '[data-action="launch-health-fix"]', '[data-action="manage-rules"]',
        '[data-action="toggle-health-details"]', '[data-action="run-hook-preview"]',
        '[data-action="run-code-analyzer"]', '[aria-label="More scan options"]',
        '.code-analyzer-brief-open'
    ].join(', '));
    await expect(actions).toHaveCount(8);
    for (const action of await actions.all()) await expectSharedButtonStyle(action);
});

test('Quality summary has a compact, usable View metrics button', async ({ page }) => {
    await installQualityApi(page);
    const brief = await openQuality(page);
    const button = brief.getByRole('button', { name: 'View metrics' });
    await expect(button).toBeVisible();
    const bounds = await button.boundingBox();
    expect(bounds).not.toBeNull();
    expect(bounds.height).toBeLessThanOrEqual(52);
    await button.click();
    await expect(page.locator('.code-report')).toBeVisible();
});

test('report selection focuses the isolated map and opens saved details', async ({ page }) => {
    const { scanRequests, sourceRequests } = await installQualityApi(page);
    const report = await openDetails(page);
    const frame = report.locator('iframe');
    const sandbox = await frame.getAttribute('sandbox');
    expect(sandbox).toContain('allow-scripts');
    expect(sandbox).not.toContain('allow-same-origin');
    await report.getByRole('button', { name: new RegExp(PAYMENT_PATH) }).click();
    const map = page.frameLocator('.code-report iframe');
    await expect(map.locator('#inspector h2')).toHaveText('PaymentProcessor.cs');
    await map.getByRole('button', { name: /Open details/ }).click();
    const dialog = report.locator('dialog');
    await expect(dialog).toBeVisible();
    await expect(dialog.locator('.code-excerpt')).toContainText('cognitive_complexity evidence');
    await expect(dialog.getByRole('button', { name: /Copy context/i })).toHaveCount(0);
    await dialog.getByRole('button', { name: 'Show in code explorer' }).click();
    await expect(dialog).not.toBeVisible();
    await expect(map.locator('#inspector h2')).toHaveText('PaymentProcessor.cs');
    expect(scanRequests).toHaveLength(1);
    expect(sourceRequests).toHaveLength(0);
});

test('radar supports keyboard categories, saved detail activation and Escape', async ({ page }) => {
    await installQualityApi(page);
    const report = await openDetails(page);
    const complexity = report.getByRole('button', { name: /^Complexity:/ });
    await complexity.focus();
    await expect(report.locator('.radar-detail')).toBeVisible();
    await complexity.press('Escape');
    await expect(report.locator('.radar-detail')).not.toBeVisible();
    await expect(report).toBeVisible();
    await complexity.press('ArrowRight');
    const size = report.getByRole('button', { name: /^Size:/ });
    await expect(size).toBeFocused();
    await size.press('Enter');
    await expect(report.locator('dialog')).toBeVisible();
    await report.locator('dialog').press('Escape');
    await expect(report.locator('dialog')).not.toBeVisible();
    await expect(report).toBeVisible();
});

test('large graphs open a connected domain overview and still focus report files', async ({ page }) => {
    await installQualityApi(page);
    const graph = graphResponse();
    for (let index = 0; index < 220; index++) graph.nodes.push({
        id: `extra-${index}`, name: `Extra${index}.cs`, kind: 'file', path: `src/Payments/Extra${index}.cs`, parentId: 'payments'
    });
    await page.route('**/api/v1/code-analyzer/graph', route => route.fulfill({ json: graph }));
    const report = await openDetails(page);
    const map = page.frameLocator('.code-report iframe');
    await expect(map.getByText(/Showing 2 of 224 entities/)).toBeVisible();
    await expect(map.locator('.cross-link .edge').first()).toBeVisible();
    await report.getByRole('button', { name: new RegExp(PAYMENT_PATH) }).click();
    await expect(map.locator('#inspector h2')).toHaveText('PaymentProcessor.cs');
});

test('graph failures leave saved report and all metrics accessible', async ({ page }) => {
    await installQualityApi(page);
    await page.route('**/api/v1/code-analyzer/graph', route => route.fulfill({ status: 503, json: { error: 'Map unavailable' } }));
    const report = await openDetails(page);
    await expect(report.locator('[data-code-map]')).toContainText('Map unavailable');
    await report.getByRole('button', { name: new RegExp(PAYMENT_PATH) }).click();
    await expect(report.locator('dialog')).toBeVisible();
    await report.locator('dialog').getByRole('button', { name: /Parameter count/ }).click();
    await expect(report.locator('.code-excerpt')).toContainText('parameter_count evidence');
});

test('empty, failed and malformed reports keep their honest state beside the map', async ({ page }) => {
    await installQualityApi(page);
    const report = await openDetails(page);
    for (const response of [
        { success: false, report: { files: [] } },
        { success: true, report: { files: 'invalid' } }
    ]) {
        await page.evaluate(response => window.app.ruleController.codeReportViewer.setResponse(response), response);
        await expect(report.locator('.qr')).toHaveAttribute('data-state', 'error');
        await expect(report.locator('.qr-error')).toBeVisible();
        await expect(report.locator('iframe')).toBeVisible();
    }
    await page.evaluate(() => window.app.ruleController.codeReportViewer.setResponse({ success: true, analyzedFileCount: 0 }));
    await expect(report.locator('.qr')).toHaveAttribute('data-state', 'complete');
    await expect(report.locator('.qr-score')).toHaveText('—');
    await expect(report.locator('.qr-grade')).toHaveText('—');
    await expect(report.getByText('No source files in this report.')).toBeVisible();
});

test('long file lists scroll independently and saved excerpts remain inert text', async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
    await installQualityApi(page);
    const report = await openDetails(page);
    const response = scanResponse();
    response.startedUtc = '2026-09-21T12:00:00Z';
    response.report.files[0].categories[0].metrics[0].snippet = '<img src=x onerror="window.reportInjected=true">';
    response.report.files.push(...Array.from({length: 60}, (_, index) => ({
        ...response.report.files[1], file: `src/Extra/File${index}.cs`
    })));
    await page.evaluate(response => window.app.ruleController.codeReportViewer.setResponse(response), response);
    await expect(report.locator('.qr')).toHaveAttribute('data-state', 'complete');
    const list = report.locator('.qr-files');
    const mapBounds = await report.locator('iframe').boundingBox();
    await list.focus();
    await list.press('End');
    await expect.poll(() => list.evaluate(element => element.scrollTop)).toBeGreaterThan(0);
    expect(await report.locator('iframe').boundingBox()).toEqual(mapBounds);
    await page.evaluate(() => {
        const viewer = window.app.ruleController.codeReportViewer;
        viewer.showFile(viewer.response.report.files[0], 'cognitive_complexity');
    });
    const dialog = report.locator('dialog');
    await expect(dialog.locator('.code-excerpt')).toContainText('<img src=x');
    await expect(dialog.locator('img')).toHaveCount(0);
    await expect(dialog).toContainText('Report captured');
    await expect(dialog).toContainText('2026');
    expect(await page.evaluate(() => window.reportInjected)).toBeUndefined();
});

test('navigation aborts a late graph and route re-entry uses the cached report', async ({ page }) => {
    const { scanRequests } = await installQualityApi(page);
    let release;
    const pending = new Promise(resolve => { release = resolve; });
    let started = false;
    await page.route('**/api/v1/code-analyzer/graph', async route => {
        started = true;
        await pending;
        await route.fulfill({ json: graphResponse() }).catch(() => {});
    });
    const brief = await openQuality(page);
    await brief.getByRole('button', { name: 'View metrics' }).click();
    await expect.poll(() => started).toBe(true);
    await page.locator('[data-action="navigate"][data-view="environments"]:visible').click();
    release();
    await expect(page.locator('.code-report iframe')).toHaveCount(0);
    await page.locator('[data-action="navigate-home"]:visible').click();
    await page.getByRole('button', { name: 'View metrics' }).click();
    await expect(page.locator('.code-report .qr')).toHaveAttribute('data-state', 'complete', { timeout: 25_000 });
    expect(scanRequests).toHaveLength(1);
});

test('report respects reduced motion and host theme without remounting the map', async ({ page }) => {
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await installQualityApi(page);
    const report = await openDetails(page);
    await report.getByRole('button', { name: new RegExp(PAYMENT_PATH) }).click();
    await page.evaluate(() => {
        window.__reportFrame = document.querySelector('.code-report iframe');
        document.documentElement.style.setProperty('--color-bg-surface', '#fafafa');
        document.documentElement.style.setProperty('--color-text', '#111111');
    });
    await expect.poll(() => report.locator('.qr-panel').evaluate(el => getComputedStyle(el).backgroundColor)).toBe('rgb(250, 250, 250)');
    expect(await page.evaluate(() => window.__reportFrame === document.querySelector('.code-report iframe'))).toBe(true);
    await expect(page.frameLocator('.code-report iframe').locator('#inspector h2')).toHaveText('PaymentProcessor.cs');
});

for (const viewport of [{ width: 1440, height: 900 }, { width: 620, height: 800 }, { width: 390, height: 844 }]) {
    test(`combined report fits ${viewport.width}x${viewport.height}`, async ({ page }, testInfo) => {
        await page.setViewportSize(viewport);
        await installQualityApi(page);
        const report = await openDetails(page);
        await expect(report.locator('.qr-grade')).toHaveText('D');
        await expect(report.locator('.qr-score')).toHaveText('64');
        expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
        await report.screenshot({ path: testInfo.outputPath('combined-report.png') });
    });
}

test('scan exclusions remain available on Project health', async ({ page }) => {
    const { scanRequests } = await installQualityApi(page);
    await openQuality(page);
    await page.getByLabel('More scan options', { exact: true }).click();
    await page.getByRole('button', { name: 'Manage scan exclusions' }).click();
    const modal = page.locator('#modal-container');
    await modal.getByRole('button', { name: 'Exclude file', exact: true }).first().click();
    await modal.getByRole('button', { name: 'Ignore file', exact: true }).click();
    await expect.poll(() => scanRequests.length).toBe(2);
    await expect(modal.getByRole('button', { name: 'Restore', exact: true })).toBeVisible();
    await modal.getByRole('button', { name: 'Restore', exact: true }).click();
    await expect.poll(() => scanRequests.length).toBe(3);
    await expect(modal).toContainText('No exclusions.');
});

for (const choice of [
    { value: 'base:codex', cli: 'codex', environmentName: null },
    { value: 'env:902:codex', cli: 'codex', environmentName: 'Review agent' }
]) {
    test(`Fix code quality directly launches inline selection ${choice.value} and remembers it`, async ({ page }) => {
        await installQualityApi(page);
        await openQuality(page);
        await page.evaluate(() => {
            window.__qualityLaunches = [];
            window.app.terminalController.launchInFocus = async options => {
                window.__qualityLaunches.push(options);
                return true;
            };
        });
        const picker = page.locator('select[data-project-health-fix-agent][data-fix-scope="quality"]');
        const control = await inlineAgentControl(picker);
        await expect(control).toBeVisible();
        await expect(picker.locator('option[value="base:shell"]')).toHaveCount(0);
        await control.click();
        const dropdown = page.locator('.ts-dropdown:visible');
        await expect(dropdown.locator('.llm-picker-customize-button')).toBeVisible();
        await dropdown.locator(`[data-value="${choice.value}"]`).click();
        await expectInlineAgentSelection(page, choice.value);
        await expect.poll(() => page.evaluate(() => window.__qualityLaunches.length)).toBe(0);
        await expect(page.locator('#modal-container [role="dialog"]')).toHaveCount(0);
        await page.locator('[data-action="launch-health-fix"][data-fix-scope="quality"]').click();
        await expect.poll(() => page.evaluate(() => window.__qualityLaunches.length)).toBe(1);
        await expect(page.locator('#modal-container [role="dialog"]')).toHaveCount(0);
        const [launch] = await page.evaluate(() => window.__qualityLaunches);
        expect(launch.cli).toBe(choice.cli);
        expect(launch.environmentName || null).toBe(choice.environmentName);
        expect(launch.workingDirectory).toBe('C:/source/vibe-rails');
        expect(launch.initialPrompt).toContain('CODE QUALITY');
        expect(launch.initialPrompt).toContain(PAYMENT_PATH);
        expect(launch.forceNewTab).toBe(true);
        // The startup route is consumed from the URL, so explicitly request Quality
        // in a fresh document to check persistence rather than the default terminal.
        await openQuality(page);
        await expectInlineAgentSelection(page, choice.value);
    });
}

for (const viewport of [{ width: 1366, height: 768 }, { width: 390, height: 844 }]) {
    test(`inline Fix controls fit the Quality page at ${viewport.width}x${viewport.height}`, async ({ page }, testInfo) => {
        await page.setViewportSize(viewport);
        await installQualityApi(page);
        await openQuality(page);
        for (const scope of ['rules', 'quality']) {
            const picker = page.locator(`select[data-project-health-fix-agent][data-fix-scope="${scope}"]`);
            const control = await inlineAgentControl(picker);
            const button = page.locator(`[data-action="launch-health-fix"][data-fix-scope="${scope}"]`);
            const group = button.locator('..');
            await expect(group).toHaveAttribute('role', 'group');
            await expect(button).toContainText(scope === 'rules' ? 'Fix rules with:' : 'Fix code quality with:');
            await expect(button.locator('.fa-screwdriver-wrench')).toHaveCount(1);
            expect(await group.evaluate(element => element.firstElementChild.tagName)).toBe('BUTTON');
            expect(await group.evaluate(element => getComputedStyle(element).borderTopWidth)).toBe('1px');
            await expect(control).toBeVisible();
            await expect(button).toBeVisible();
            const controlBounds = await control.boundingBox();
            const buttonBounds = await button.boundingBox();
            expect(controlBounds.width).toBeGreaterThanOrEqual(150);
            expect(controlBounds.x).toBeGreaterThanOrEqual(0);
            expect(controlBounds.x + controlBounds.width).toBeLessThanOrEqual(viewport.width);
            expect(buttonBounds.x + buttonBounds.width).toBeLessThanOrEqual(viewport.width);
            expect(buttonBounds.height).toBeLessThanOrEqual(52);
            if (viewport.width > 520) {
                expect(buttonBounds.x + buttonBounds.width).toBeLessThanOrEqual(controlBounds.x);
            } else {
                expect(buttonBounds.y + buttonBounds.height).toBeLessThanOrEqual(controlBounds.y + 1);
            }
        }
        expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
        await page.screenshot({ path: testInfo.outputPath('quality-inline-fix.png'), fullPage: true });
    });
}

test('an empty code scan completes its transcript and cannot launch a repair agent', async ({ page }) => {
    await installQualityApi(page, { empty: true });
    const brief = await openQuality(page);
    await expect(brief).toContainText('No changed code');
    const quality = page.locator('.project-health-quality');
    const fix = quality.locator('[data-action="launch-health-fix"]');
    await expect(fix).toBeDisabled();
    await expect(fix).toHaveAttribute('title', /No changed source files to fix/);
    await quality.getByText('Technical details', { exact: true }).click();
    await expect(quality.locator('[data-vca-console-meta]')).toHaveText('Scan complete · No changed source files');
    await expect(quality.locator('[data-vca-console-output]')).toContainText('No changed source files to analyze');
    await expect(quality).toHaveAttribute('aria-busy', 'false');
    await expect(brief.getByRole('button', { name: 'View metrics' })).toHaveCount(0);

    await page.evaluate(() => {
        window.__qualityLaunches = [];
        window.app.terminalController.launchInFocus = async options => {
            window.__qualityLaunches.push(options);
            return true;
        };
    });
    // A programmatic click also exercises the controller's guard, rather than
    // depending solely on the browser suppressing disabled button clicks.
    await fix.dispatchEvent('click');
    await expect.poll(() => page.evaluate(() => window.__qualityLaunches.length)).toBe(0);
    await expect(fix).toBeDisabled();
});

test('returning to QUALITY restores the completed empty transcript without rescanning', async ({ page }) => {
    const { scanRequests } = await installQualityApi(page, { empty: true });
    await openQuality(page);
    await expect.poll(() => scanRequests.length).toBe(1);
    await page.locator('[data-action="navigate"][data-view="environments"]:visible').click();
    await expect(page.locator('#app-content [data-view="environments"]')).toBeVisible();
    await page.locator('[data-action="navigate-home"]:visible').click();
    const quality = page.locator('.project-health-quality');
    await expect(quality.locator('[data-vca-quality-brief]')).toContainText('No changed code');
    await expect(quality.locator('[data-vca-console-meta]')).toHaveText('Scan complete · No changed source files');
    await expect(quality.locator('[data-action="launch-health-fix"]')).toBeDisabled();
    expect(scanRequests).toHaveLength(1);
});

test('QUALITY scan again and unpushed controls request a new scan from the overview', async ({ page }) => {
    const { scanRequests } = await installQualityApi(page);
    await openQuality(page);
    const quality = page.locator('.project-health-quality');
    await quality.getByRole('button', { name: 'Scan again', exact: true }).click();
    await expect.poll(() => scanRequests.length).toBe(2);
    await expect(quality.locator('[data-vca-console-output]')).toHaveText('Fixture scan complete.');
    await quality.getByLabel('More scan options', { exact: true }).click();
    await quality.getByRole('button', { name: 'Scan unpushed commits', exact: true }).click();
    await expect.poll(() => scanRequests).toEqual(['', '', '?scope=unpushed']);
    await expect(quality).toHaveAttribute('aria-busy', 'false');
    await expect(quality.locator('[data-action="launch-health-fix"]')).toBeEnabled();
});

async function openLongViewportPicker(page, top) {
    await installQualityApi(page);
    await openQuality(page);
    const picker = page.locator('select[data-project-health-fix-agent][data-fix-scope="rules"]');
    await inlineAgentControl(picker);
    await picker.evaluate((select, top) => {
        const ts = select.tomselect;
        // Exercise the shared picker at the same viewport anchors used by cards,
        // sticky toolbars, and modals, with a catalog longer than either side fits.
        document.body.appendChild(ts.wrapper);
        Object.assign(ts.wrapper.style, { position: 'fixed', top: `${top}px`, right: '12px', width: '180px', zIndex: '1099' });
        for (let index = 0; index < 24; index++) {
            ts.addOption({ value: `fixture:${index}`, text: `Review environment ${index}`, cli: 'codex' });
        }
        ts.refreshOptions(false);
    }, top);
    await picker.evaluate(select => select.tomselect.open());
    const dropdown = page.locator('.ts-dropdown:visible');
    await expect(dropdown).toBeVisible();
    return { picker, dropdown };
}

async function expectDropdownInsideViewport(page, dropdown) {
    const bounds = await dropdown.boundingBox();
    const viewport = page.viewportSize();
    expect(bounds.x).toBeGreaterThanOrEqual(7);
    expect(bounds.y).toBeGreaterThanOrEqual(7);
    expect(bounds.x + bounds.width).toBeLessThanOrEqual(viewport.width - 7);
    expect(bounds.y + bounds.height).toBeLessThanOrEqual(viewport.height - 7);
    await expect(dropdown.locator('.dropdown-input')).toBeInViewport({ ratio: 1 });
    await expect(dropdown.locator('.llm-picker-customize-button')).toBeInViewport({ ratio: 1 });
    // The host's sidebar/main width transition takes 250 ms at the mobile breakpoint.
    await expect.poll(() => page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
}

for (const anchor of [{ label: 'top', top: 16 }, { label: 'middle', top: 282 }, { label: 'bottom', top: 492 }]) {
    test(`long shared agent picker near viewport ${anchor.label} keeps search, footer, and every option reachable`, async ({ page }) => {
        await page.setViewportSize({ width: 1295, height: 560 });
        const { dropdown } = await openLongViewportPicker(page, anchor.top);
        await expectDropdownInsideViewport(page, dropdown);
        const content = dropdown.locator('.ts-dropdown-content');
        expect(await content.evaluate(node => node.scrollHeight > node.clientHeight)).toBe(true);
        const last = dropdown.locator('[data-selectable]').last();
        await last.scrollIntoViewIfNeeded();
        await expect(last).toBeInViewport({ ratio: 1 });
        const first = dropdown.locator('[data-selectable]').first();
        await first.scrollIntoViewIfNeeded();
        await expect(first).toBeInViewport({ ratio: 1 });
        await expectDropdownInsideViewport(page, dropdown);
    });
}

test('open shared agent picker refits after resize and filtering without clipping the footer', async ({ page }) => {
    await page.setViewportSize({ width: 1295, height: 560 });
    const { picker, dropdown } = await openLongViewportPicker(page, 282);
    await page.setViewportSize({ width: 390, height: 320 });
    await expect.poll(async () => (await dropdown.boundingBox()).x + (await dropdown.boundingBox()).width).toBeLessThanOrEqual(383);
    await expectDropdownInsideViewport(page, dropdown);
    await dropdown.locator('.dropdown-input').fill('Review environment 23');
    await expect(dropdown.locator('[data-selectable]')).toHaveCount(1);
    await expectDropdownInsideViewport(page, dropdown);
    await dropdown.locator('[data-selectable]').click();
    await expect(picker).toHaveValue('fixture:23');
    await expect(dropdown).toBeHidden();
});

test('shared agent picker follows its control when an inner panel scrolls', async ({ page }) => {
    await page.setViewportSize({ width: 1295, height: 560 });
    const { picker, dropdown } = await openLongViewportPicker(page, 166);
    await picker.evaluate(select => {
        const ts = select.tomselect;
        ts.close();
        const panel = document.createElement('div');
        panel.dataset.pickerScrollPanel = '';
        Object.assign(panel.style, { position: 'fixed', top: '16px', left: '100px', width: '220px', height: '240px', overflow: 'auto' });
        const before = document.createElement('div');
        before.style.height = '150px';
        const after = document.createElement('div');
        after.style.height = '300px';
        Object.assign(ts.wrapper.style, { position: 'relative', top: '', right: '', width: '180px' });
        panel.append(before, ts.wrapper, after);
        document.body.appendChild(panel);
        ts.open();
    });
    const initialTop = (await dropdown.boundingBox()).y;
    await page.locator('[data-picker-scroll-panel]').evaluate(panel => { panel.scrollTop = 80; });
    await expect.poll(async () => Math.round((await dropdown.boundingBox()).y - initialTop)).toBe(-80);
    await expectDropdownInsideViewport(page, dropdown);
});
