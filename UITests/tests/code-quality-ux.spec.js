// @ts-check

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

async function installQualityApi(page) {
    const sourceRequests = [];
    let ignoredFiles = [];
    if (process.env.VIBERAILS_QUALITY_STATIC === '1') {
        await page.addInitScript(() => sessionStorage.setItem('viberails_tab', 'quality-fixture'));
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
    await page.route('**/api/v1/code-analyzer', route => {
        const response = scanResponse();
        response.report.files = response.report.files.filter(file => !ignoredFiles.some(entry => entry.path === file.file));
        response.analyzedFileCount = response.report.files.length;
        return route.fulfill({ json: response });
    });
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
    return { sourceRequests };
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
    const report = page.locator('[data-project-health-quality-report]');
    await expect(report).toBeVisible();
    await expect(report.locator('.monaco-editor')).toBeVisible({ timeout: 20_000 });
    return report;
}

async function visibleEditorHeight(report) {
    return report.locator('[data-code-analyzer-monaco-host]').evaluate(host => {
        const bounds = host.getBoundingClientRect();
        const body = host.closest('.modal-body')?.getBoundingClientRect();
        return Math.max(0, Math.min(bounds.bottom, body?.bottom ?? innerHeight, innerHeight)
            - Math.max(bounds.top, body?.top ?? 0, 0));
    });
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
    await page.getByRole('button', { name: 'View metrics', exact: true }).click();
    const report = page.locator('[data-project-health-quality-report]');
    await expect(report.locator('.monaco-editor')).toBeVisible();
    await page.mouse.move(0, 0);
    await expectSharedButtonStyle(report.getByRole('button', { name: 'Ignore', exact: true }));
    await expectSharedButtonStyle(report.getByRole('button', { name: 'Ignore folder', exact: true }));
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
    await expect(page.locator('[data-project-health-quality-report]')).toBeVisible();
});

for (const viewport of [{ width: 1366, height: 768 }, { width: 1024, height: 768 }]) {
    test(`source is visible on opening Quality at ${viewport.width}x${viewport.height}`, async ({ page }, testInfo) => {
        await page.setViewportSize(viewport);
        await installQualityApi(page);
        const report = await openDetails(page);
        await expect(report.locator('.code-analyzer-file-title')).toHaveText('PaymentProcessor.cs');
        expect(await visibleEditorHeight(report)).toBeGreaterThanOrEqual(180);
        await page.screenshot({ path: testInfo.outputPath('quality-modal.png') });
        await expect(report.locator('.code-analyzer-file-context')).not.toHaveAttribute('open');
        const healthyGroup = report.locator('details.code-analyzer-metric-group').filter({ has: page.locator('summary', { hasText: 'Size' }) });
        await expect(healthyGroup).not.toHaveAttribute('open');
        await healthyGroup.locator('summary').click();
        await expect(healthyGroup).toHaveAttribute('open');
        await expect(healthyGroup.getByRole('button', { name: /Lines of code/ })).toBeVisible();
    });
}

test('metric navigation retargets source and filters keep the reviewed file stable', async ({ page }) => {
    await page.setViewportSize({ width: 1366, height: 768 });
    const { sourceRequests } = await installQualityApi(page);
    const report = await openDetails(page);
    const originalModel = await page.evaluate(() => window.monaco.editor.getModels()[0].uri.toString());
    const metricPicker = report.getByRole('combobox', { name: 'Inspect metric' });
    await selectNamedOption(metricPicker, 'Cyclomatic complexity');
    await expect(report.locator('.code-analyzer-editor-position')).toHaveText('Line 28');
    await expect(report.locator('.code-analyzer-code-head')).toContainText('Cyclomatic complexity');
    expect(await page.evaluate(() => window.monaco.editor.getModels()[0].uri.toString())).toBe(originalModel);
    expect(sourceRequests.filter(path => path === PAYMENT_PATH)).toHaveLength(1);

    const search = report.getByRole('searchbox', { name: 'Filter analyzed files' });
    await search.fill('HealthyHelper');
    await expect(report.locator('.code-analyzer-file-item')).toHaveCount(1);
    await expect(report.locator('.code-analyzer-file-title')).toHaveText('PaymentProcessor.cs');
    await report.locator('.code-analyzer-file-item').click();
    await expect(report.locator('.code-analyzer-file-title')).toHaveText('HealthyHelper.cs');
    await expect(report.locator('.code-analyzer-editor-position')).toHaveText('Line 6');
    await expect.poll(() => page.evaluate(() => window.monaco.editor.getModels()[0]?.getValue())).toContain(HELPER_PATH);

    await search.fill('no-such-file');
    await expect(report.locator('.code-analyzer-file-list-empty')).toBeVisible();
    await report.getByRole('button', { name: 'Clear filters' }).click();
    await expect(search).toHaveValue('');
    await expect(report.locator('.code-analyzer-file-item')).toHaveCount(2);
});

test('capped NPath estimates explain the limit beside the source', async ({ page }, testInfo) => {
    await page.setViewportSize({ width: 1366, height: 768 });
    await installQualityApi(page);
    const response = scanResponse();
    response.report.files[0].categories[0].metrics.unshift({
        ...metric('npath_complexity', 100, 12),
        value: 1_000_000_000,
        warn: 200,
        critical: 1000,
        source: '<global>'
    });
    await page.route('**/api/v1/code-analyzer', route => route.fulfill({ json: response }));
    const report = await openDetails(page);
    const sourceHeader = report.locator('.code-analyzer-code-head');
    await expect(sourceHeader).toContainText('NPath complexity');
    await expect(sourceHeader).toContainText('Estimated paths');
    await expect(sourceHeader).toContainText('1B (cap)');
    await expect(sourceHeader).toContainText('1-billion cap. It is not an exact path count.');
    const metricRow = report.locator('.code-analyzer-metric-row').filter({ hasText: 'NPath complexity' });
    await expect(metricRow.locator('.code-analyzer-metric-raw')).toContainText('1B (cap)');
    expect(await visibleEditorHeight(report)).toBeGreaterThanOrEqual(180);
    await page.screenshot({ path: testInfo.outputPath('quality-npath-cap.png') });

    await selectNamedOption(report.getByRole('combobox', { name: 'Inspect metric' }), 'Cyclomatic complexity');
    await expect(sourceHeader).toContainText('Measured');
    await expect(sourceHeader).not.toContainText('not an exact path count');
    await expect(report.locator('.code-analyzer-editor-position')).toHaveText('Line 28');
});

test('closing Quality disposes its editor and reopening retains the selected file', async ({ page }) => {
    await installQualityApi(page);
    const report = await openDetails(page);
    await report.locator('.code-analyzer-file-item').filter({ hasText: 'HealthyHelper.cs' }).click();
    await expect.poll(() => page.evaluate(() => window.monaco.editor.getModels()[0]?.getValue())).toContain(HELPER_PATH);
    await page.locator('#modal-container [data-action="close-modal"]').click();
    await expect(report).toHaveCount(0);
    await expect.poll(() => page.evaluate(() => window.monaco.editor.getModels().length)).toBe(0);
    await page.locator('[data-vca-quality-brief]').getByRole('button', { name: 'View metrics' }).click();
    await expect(report.locator('.code-analyzer-file-title')).toHaveText('HealthyHelper.cs');
    await expect(report.locator('.monaco-editor')).toBeVisible();
    await expect.poll(() => page.evaluate(() => window.monaco.editor.getModels().length)).toBe(1);
});

test('cancelling Ignore returns to the selected file in the Quality report', async ({ page }) => {
    await installQualityApi(page);
    const report = await openDetails(page);
    await report.locator('.code-analyzer-file-item').filter({ hasText: 'HealthyHelper.cs' }).click();
    await report.getByRole('button', { name: 'Ignore', exact: true }).click();
    const confirmation = page.locator('.analyzer-ignore-modal');
    await expect(confirmation).toContainText(HELPER_PATH);
    await expect(report).toHaveCount(0);
    await expect.poll(() => page.evaluate(() => window.monaco.editor.getModels().length)).toBe(0);
    await confirmation.getByRole('button', { name: 'Cancel', exact: true }).click();
    await expect(report.locator('.code-analyzer-file-title')).toHaveText('HealthyHelper.cs');
    await expect(report.locator('.monaco-editor')).toBeVisible();
});

test('ignoring and restoring a file keeps the Quality report open', async ({ page }) => {
    await installQualityApi(page);
    const report = await openDetails(page);
    await report.getByRole('button', { name: 'Ignore', exact: true }).click();
    await page.locator('[data-analyzer-ignore-confirm]').click();
    await expect(report.locator('.code-analyzer-file-title')).toHaveText('HealthyHelper.cs');
    const ignored = report.locator('.code-analyzer-ignored-box');
    await ignored.locator('summary').click();
    await expect(ignored).toContainText(PAYMENT_PATH);
    await page.mouse.move(0, 0);
    await expectSharedButtonStyle(ignored.getByRole('button', { name: 'Restore', exact: true }));
    await ignored.getByRole('button', { name: 'Restore', exact: true }).click();
    await expect(report.locator('.code-analyzer-file-item')).toHaveCount(2);
    await expect(report.locator('.code-analyzer-file-title')).toHaveText('HealthyHelper.cs');
    await expect(report.locator('.monaco-editor')).toBeVisible();
});

test('healthy metric details can be reached and expanded with the keyboard', async ({ page }) => {
    await installQualityApi(page);
    const report = await openDetails(page);
    const group = report.locator('details.code-analyzer-metric-group').filter({ has: page.locator('summary', { hasText: 'Size' }) });
    const summary = group.locator('summary');
    await page.locator('#modal-container [data-action="close-modal"]').focus();
    for (let step = 0; step < 40; step++) {
        await page.keyboard.press('Tab');
        if (await summary.evaluate(element => element === document.activeElement)) break;
    }
    await expect(summary).toBeFocused();
    await page.keyboard.press('Enter');
    await expect(group).toHaveAttribute('open');
});

for (const viewport of [{ width: 600, height: 768 }, { width: 390, height: 844 }]) {
    test(`narrow Quality at ${viewport.width}x${viewport.height} offers file selection before the source`, async ({ page }, testInfo) => {
        await page.setViewportSize(viewport);
        await installQualityApi(page);
        const report = await openDetails(page);
        const filePicker = report.getByRole('combobox', { name: 'Inspect file' });
        await expect(filePicker).toBeVisible();
        expect(await visibleEditorHeight(report)).toBeGreaterThanOrEqual(140);
        expect(await page.locator('#modal-container .modal-body').evaluate(body => body.scrollWidth - body.clientWidth)).toBeLessThanOrEqual(1);
        await page.screenshot({ path: testInfo.outputPath('quality-modal-narrow.png') });
        await selectNamedOption(filePicker, 'HealthyHelper.cs');
        await expect(report.locator('.code-analyzer-file-title')).toHaveText('HealthyHelper.cs');
        await expect(report.locator('.code-analyzer-editor-position')).toHaveText('Line 6');
    });
}

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
            await expect(control).toBeVisible();
            await expect(button).toBeVisible();
            const controlBounds = await control.boundingBox();
            const buttonBounds = await button.boundingBox();
            expect(controlBounds.width).toBeGreaterThanOrEqual(150);
            expect(controlBounds.x).toBeGreaterThanOrEqual(0);
            expect(controlBounds.x + controlBounds.width).toBeLessThanOrEqual(viewport.width);
            expect(buttonBounds.x + buttonBounds.width).toBeLessThanOrEqual(viewport.width);
            expect(buttonBounds.height).toBeLessThanOrEqual(52);
        }
        expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
        await page.screenshot({ path: testInfo.outputPath('quality-inline-fix.png'), fullPage: true });
    });
}
