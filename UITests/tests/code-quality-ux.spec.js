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

function changesResponse() {
    return { count: 3, additions: 14, deletions: 5, truncated: false, capturedUtc: '2026-10-03T12:00:00Z', head: 'abc1234', files: [
        { path: PAYMENT_PATH, status: 'modified', staged: false, unstaged: true, additions: 12, deletions: 3, binary: false },
        { path: 'docs/notes.md', status: 'untracked', staged: false, unstaged: true, additions: 2, deletions: 0, binary: false },
        { path: 'src/Utilities/Deleted.cs', status: 'deleted', staged: true, unstaged: false, additions: 0, deletions: 2, binary: false }
    ] };
}

// Pixels the field or effects canvas has painted: the renderer keeps no DOM per entity.
async function paintedPixels(map, selector) {
    return map.locator(selector).evaluate(canvas => {
        const data = canvas.getContext('2d').getImageData(0, 0, canvas.width, canvas.height).data;
        let painted = 0;
        for (let index = 3; index < data.length; index += 4) if (data[index]) painted++;
        return painted;
    });
}

// Page coordinates of an entity (by id) or of a stage-relative point, for real mouse input.
async function stagePoint(page, idOrPoint) {
    const frame = page.locator('.code-report iframe');
    const map = page.frameLocator('.code-report iframe');
    await frame.scrollIntoViewIfNeeded();
    const point = typeof idOrPoint === 'string' ? await map.locator('body').evaluate((_, id) => CodeAtlas.locate(id), idOrPoint) : idOrPoint;
    expect(point).not.toBeNull();
    const box = await frame.boundingBox();
    const stage = await map.locator('#stage').evaluate(element => { const rect = element.getBoundingClientRect(); return { x: rect.x, y: rect.y }; });
    return { x: box.x + stage.x + point.x, y: box.y + stage.y + point.y };
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
    const diffRequests = [];
    await page.route('**/api/v1/code-analyzer/changes', route => route.fulfill({ json: changesResponse() }));
    await page.route('**/api/v1/code-analyzer/changes/diff?*', route => {
        const path = new URL(route.request().url()).searchParams.get('path');
        diffRequests.push(path);
        return route.fulfill({ json: { fileName: path, language: 'csharp', status: 'modified',
            originalContent: 'class Old {}\n', modifiedContent: 'class New {}\nclass Added {}\n', binary: false, truncated: false } });
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
    return { sourceRequests, scanRequests, diffRequests };
}

async function openQuality(page) {
    await page.goto('/?view=code-quality', { waitUntil: 'domcontentloaded' });
    const quality = page.locator('.project-health-quality');
    await expect(quality.locator('.code-report')).toBeVisible({ timeout: 20_000 });
    await expect(page.locator('#loading-overlay')).toHaveClass(/\bd-none\b/);
    return quality;
}

for (const rulesView of ['dashboard', 'agents']) {
    test(`Rules and Quality run independent checks from ${rulesView}`, async ({ page }) => {
        const { scanRequests } = await installQualityApi(page);
        let validations = 0;
        await page.route('**/api/v1/hooks/preview', route => {
            validations++;
            return route.fulfill({ json: { success: true, status: 'passed', output: 'No rule violations.', violations: [] } });
        });
        await page.goto(`/?view=${rulesView}`);
        const rulesLink = page.locator('.app-subnav [data-action="navigate-home"]');
        const qualityLink = page.locator('.app-subnav [data-view="code-quality"]');
        await expect(page.getByRole('heading', { name: 'Rules', level: 1 })).toBeVisible();
        await expect(rulesLink).toHaveAttribute('aria-current', 'page');
        await expect.poll(() => validations).toBe(1);
        expect(scanRequests).toHaveLength(0);
        await expect(page.locator('.code-report')).toHaveCount(0);
        await expect(page.getByRole('button', { name: 'View/Edit Rules' })).toBeVisible();

        await qualityLink.click();
        await expect(page.locator('.code-report .qr')).toHaveAttribute('data-state', 'complete');
        await expect(qualityLink).toHaveAttribute('aria-current', 'page');
        await expect(rulesLink).not.toHaveAttribute('aria-current');
        await expect(page.locator('[data-health-card="rules"], [data-hook-health]')).toHaveCount(0);
        expect(validations).toBe(1);
        expect(scanRequests).toHaveLength(1);
        expect(await page.evaluate(() => window.app.getDuplicateTabViewName(window.app.currentView))).toBe('code-quality');

        await rulesLink.click();
        await expect.poll(() => validations).toBe(2);
        await expect(page.locator('.code-report')).toHaveCount(0);
        await qualityLink.click();
        await expect(page.locator('.code-report .qr')).toHaveAttribute('data-state', 'complete');
        expect(scanRequests).toHaveLength(1);
        expect(validations).toBe(2);
    });
}

for (const width of [1920, 1492, 1210, 1100, 1000, 768, 390]) {
    test(`split navigation and Settings cog fit at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 900 });
        await installQualityApi(page);
        await openQuality(page);
        const nav = page.locator('.app-subnav');
        await expect(nav.locator('.app-subnav-links > button').nth(2)).toHaveText('QUALITY');
        await expect(nav.locator('.app-subnav-links > button').nth(3)).toHaveText('Rules');
        const cog = nav.getByRole('button', { name: 'Settings', exact: true });
        const play = nav.getByRole('button', { name: 'Launch an automation or script', exact: true });
        await expect(cog.locator('.fa-gear')).toHaveCount(1);
        expect(await cog.innerText()).toBe('');
        expect(await nav.evaluate(element => element.scrollWidth - element.clientWidth)).toBeLessThanOrEqual(1);
        expect(await page.evaluate(() => document.documentElement.scrollWidth - innerWidth)).toBeLessThanOrEqual(1);
        if (width >= 1210) {
            const report = await page.locator('.code-report').boundingBox();
            expect(report.width).toBeGreaterThan(width - 90);
            expect(report.y).toBeLessThan(200);
        }
        if (width === 1492 || width === 390) {
            await expect(page.frameLocator('.code-report iframe').locator('body')).toHaveAttribute('data-startup', 'ready');
            await expect(page.locator('.code-report .qr')).toHaveAttribute('data-state', 'complete');
            await page.screenshot({ path: testInfo.outputPath('quality-workspace.png'), fullPage: true });
        }
        await play.focus();
        await play.press('Tab');
        await expect(cog).toBeFocused();
        await cog.press('Enter');
        await expect(page.getByRole('heading', { name: 'Application Settings', exact: true })).toBeVisible();
        await expect(cog).toHaveAttribute('aria-current', 'page');
    });
}

async function openDetails(page) {
    await openQuality(page);
    const report = page.locator('.code-report');
    await expect(report).toBeVisible();
    await expect(report.locator('.qr')).toHaveAttribute('data-state', 'complete', { timeout: 25_000 });
    // Explicit interaction keeps these manual-exploration tests out of the opening map tour.
    await report.locator('.qr-list-switch [data-list="report"]').click();
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
    await expect(pickers).toHaveCount(1);
    await expect.poll(() => pickers.evaluateAll(selects => selects.map(select => select.value)))
        .toEqual([value]);
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

for (const view of ['code-quality']) {
    test(`Quality report mounts in the live document from ${view}`, async ({ page }) => {
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
        await page.locator('[data-action="navigate"][data-view="code-quality"]:visible').click();
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
        '[data-action="run-code-analyzer"]', '[aria-label="More scan options"]'
    ].join(', '));
    await expect(actions).toHaveCount(3);
    for (const action of await actions.all()) await expectSharedButtonStyle(action);
});

test('Quality report opens inline beside compact scan controls', async ({ page }) => {
    await installQualityApi(page);
    const quality = await openQuality(page);
    const button = quality.getByRole('button', { name: 'Scan again', exact: true });
    await expect(button).toBeVisible();
    const bounds = await button.boundingBox();
    expect(bounds).not.toBeNull();
    expect(bounds.height).toBeLessThanOrEqual(52);
    await expect(quality.locator('.code-report iframe')).toBeVisible();
    await expect(quality.getByRole('button', { name: 'View metrics' })).toHaveCount(0);
});

test('the first Report file animates into focus 1.5 seconds after a delayed map is ready', async ({ page }) => {
    await page.emulateMedia({ reducedMotion: 'no-preference' });
    await installQualityApi(page);
    let release;
    const pending = new Promise(resolve => { release = resolve; });
    await page.route('**/api/v1/code-analyzer/graph', async route => {
        await pending;
        await route.fulfill({ json: graphResponse() });
    });
    await openQuality(page);
    const report = page.locator('.code-report');
    const first = report.locator('.qr-file').first();
    await expect(first).toHaveAttribute('data-path', PAYMENT_PATH);
    await expect(report.locator('[data-list="report"]')).toHaveAttribute('aria-pressed', 'true');
    await page.evaluate(() => {
        window.app.ruleController.codeReportViewer.ready.then(() => { window.mapReadyAt = performance.now(); });
        new MutationObserver(() => {
            if (!window.fileFocusedAt && document.querySelector('.qr-file[aria-pressed="true"]'))
                window.fileFocusedAt = performance.now();
        }).observe(document.querySelector('.code-report'), { subtree: true, attributes: true, attributeFilter: ['aria-pressed'] });
    });
    await page.waitForTimeout(1100);
    await expect(first).toHaveAttribute('aria-pressed', 'false');
    release();
    const map = page.frameLocator('.code-report iframe');
    await page.evaluate(() => window.app.ruleController.codeReportViewer.ready);
    await expect(first).toHaveAttribute('aria-pressed', 'false');
    await expect(map.locator('#inspector')).toBeHidden();
    await map.locator('#stage').evaluate(stage => {
        window.mapMotions = [];
        new MutationObserver(() => window.mapMotions.push(stage.dataset.motion))
            .observe(stage, { attributes: true, attributeFilter: ['data-motion'] });
    });
    await expect(first).toHaveAttribute('aria-pressed', 'true');
    await expect(map.locator('#inspector h2')).toHaveText('PaymentProcessor.cs');
    expect(await map.locator('body').evaluate(() => window.mapMotions)).toContain('focus');
    const delay = await page.evaluate(() => window.fileFocusedAt - window.mapReadyAt);
    expect(delay).toBeGreaterThanOrEqual(1450);
    expect(delay).toBeLessThan(3000);
    await expect(report.locator('.details-panel')).toBeHidden();
});

for (const interaction of ['another file', 'Git changes', 'map search']) {
    test(`opening selection yields to ${interaction}`, async ({ page }) => {
        await installQualityApi(page);
        await openQuality(page);
        const report = page.locator('.code-report');
        const map = page.frameLocator('.code-report iframe');
        await page.evaluate(() => window.app.ruleController.codeReportViewer.ready);
        if (interaction === 'another file') {
            await report.locator(`.qr-file[data-path="${HELPER_PATH}"]`).click();
        } else if (interaction === 'Git changes') {
            await report.locator('[data-list="changes"]').click();
            await report.locator('[data-list="report"]').click();
        } else {
            await map.locator('#search').fill('Healthy');
        }
        await page.waitForTimeout(1700);
        await expect(report.locator('.qr-file').first()).toHaveAttribute('aria-pressed', 'false');
        if (interaction === 'another file') await expect(map.locator('#inspector h2')).toHaveText('HealthyHelper.cs');
        else await expect(map.locator('#inspector')).toBeHidden();
    });
}

test('leaving a ready map cancels its opening selection and returning replays it', async ({ page }) => {
    const { scanRequests } = await installQualityApi(page);
    await openQuality(page);
    await page.evaluate(async () => {
        const viewer = window.app.ruleController.codeReportViewer;
        await viewer.ready;
        window.oldMapSelections = 0;
        viewer.focusFile = () => { window.oldMapSelections++; };
        window.app.navigate('environments');
    });
    await expect(page.locator('.code-report iframe')).toHaveCount(0);
    await page.waitForTimeout(1700);
    expect(await page.evaluate(() => window.oldMapSelections)).toBe(0);
    await page.locator('.app-subnav [data-view="code-quality"]').click();
    await expect(page.locator('.code-report .qr-file').first()).toHaveAttribute('aria-pressed', 'true', { timeout: 10000 });
    expect(scanRequests).toHaveLength(1);
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
    const dialog = report.locator('.details-panel');
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
    await expect(report.locator('.details-panel')).toBeVisible();
    await report.locator('.details-panel h2').press('Escape');
    await expect(report.locator('.details-panel')).not.toBeVisible();
    await expect(report).toBeVisible();
});

test('large graphs draw every entity on the canvas field and still focus report files', async ({ page }) => {
    await installQualityApi(page);
    const graph = graphResponse();
    for (let index = 0; index < 220; index++) graph.nodes.push({
        id: `extra-${index}`, name: `Extra${index}.cs`, kind: 'file', path: `src/Payments/Extra${index}.cs`, parentId: 'payments'
    });
    await page.route('**/api/v1/code-analyzer/graph', route => route.fulfill({ json: graph }));
    const report = await openDetails(page);
    const map = page.frameLocator('.code-report iframe');
    await expect(map.locator('body')).toHaveAttribute('data-startup', 'ready');
    // The overview is the whole snapshot on a canvas: no element per entity, nothing paged away.
    await expect(map.locator('#stage')).toHaveClass(/constellation/);
    await expect(map.locator('#stage')).not.toHaveClass(/dense/);
    await expect(map.locator('#nodes .node')).toHaveCount(0);
    await expect(map.getByText(/Showing \d+ of \d+ entities/)).toHaveCount(0);
    const stats = await map.locator('body').evaluate(() => CodeAtlas.fieldStats());
    expect(stats.nodes).toBe(graph.nodes.length);
    expect(stats.links).toBe(stats.linkTotal);
    const located = await map.locator('body').evaluate(() => ['payments', 'payment-file', 'extra-219'].map(id => CodeAtlas.locate(id)));
    expect(located.every(point => point && Number.isFinite(point.x) && Number.isFinite(point.y) && !point.hidden)).toBe(true);
    expect(await paintedPixels(map, '#field')).toBeGreaterThan(500);
    await report.getByRole('button', { name: new RegExp(PAYMENT_PATH) }).click();
    await expect(map.locator('#inspector h2')).toHaveText('PaymentProcessor.cs');
});

test('repository sized graphs light the hovered entity above a veil and rotate under CPU throttling', async ({ page }, testInfo) => {
    await page.emulateMedia({ reducedMotion: 'no-preference' });
    const cdp = await page.context().newCDPSession(page);
    await cdp.send('Emulation.setCPUThrottlingRate', { rate: 4 });
    await installQualityApi(page);
    const graph = graphResponse();
    for (let directory = 0; directory < 6; directory++) {
        graph.nodes.push({ id: `dir-${directory}`, name: `Area${directory}`, kind: 'module', path: `src/Area${directory}` });
        for (let index = 0; index < 450; index++) {
            const id = `large-${directory}-${index}`;
            graph.nodes.push({ id, name: `Large${index}.cs`, kind: 'file', path: `src/Area${directory}/Large${index}.cs`, parentId: `dir-${directory}` });
            graph.edges.push({ id: `c-${id}`, source: `dir-${directory}`, target: id, kind: 'contains' });
            if (index % 5 === 0) graph.edges.push({ id: `r-${id}`, source: id, target: `large-${(directory + 1) % 6}-${index}`, kind: 'references' });
        }
    }
    await page.route('**/api/v1/code-analyzer/graph', route => route.fulfill({ json: graph }));
    const report = await openDetails(page);
    const map = page.frameLocator('.code-report iframe');
    await expect(map.locator('body')).toHaveAttribute('data-startup', 'ready', { timeout: 60_000 });
    await expect(map.locator('#stage')).toHaveClass(/dense/);
    await expect(map.locator('#nodes .node')).toHaveCount(0);
    await expect(map.locator('#stage')).toHaveAttribute('data-motion', 'idle', { timeout: 15_000 });
    const stats = await map.locator('body').evaluate(() => CodeAtlas.fieldStats());
    expect(stats.nodes).toBe(graph.nodes.length);
    expect(stats.links).toBe(graph.edges.length);
    await expect(map.locator('#view-summary')).toBeHidden();
    await report.locator('iframe').screenshot({ path: testInfo.outputPath('large-field.png') });
    // Highlighting changes is on by default and dims instead of veiling; the veil belongs to a plain hover.
    await map.locator('#highlight-changes').click();
    await expect(map.locator('#highlight-changes')).toHaveAttribute('aria-pressed', 'false');
    // Hovering a module shows its tooltip, veils the field and lights every one of its links.
    const target = await stagePoint(page, 'dir-0');
    await page.mouse.move(target.x - 30, target.y - 30);
    await page.mouse.move(target.x, target.y);
    await expect(map.locator('#node-tooltip')).toContainText('Area0');
    await expect(map.locator('#stage')).toHaveClass(/focused/);
    expect(await map.locator('body').evaluate(() => CodeAtlas.fieldStats().lit)).toBe(450);
    await report.locator('iframe').screenshot({ path: testInfo.outputPath('large-hover.png') });
    const corner = await stagePoint(page, { x: 4, y: 4 });
    await page.mouse.move(corner.x, corner.y);
    await expect(map.locator('#stage')).not.toHaveClass(/focused/);
    // Rotation still works from the keyboard and settles with hover targets in place.
    await map.locator('#rotate-mode').click();
    await map.locator('#stage').press('ArrowRight');
    await expect(map.locator('#stage')).toHaveAttribute('data-yaw', /^0\.17/);
    await expect(map.locator('#stage')).toHaveAttribute('data-motion', 'idle');
    // Search reaches any entity of the snapshot and the report list still focuses the map.
    await map.locator('#search').fill('Large449');
    await map.locator('.search-result').first().click();
    await expect(map.locator('#inspector h2')).toHaveText('Large449.cs');
    await report.getByRole('button', { name: new RegExp(HELPER_PATH) }).click();
    await expect(map.locator('#inspector h2')).toHaveText('HealthyHelper.cs');
    await cdp.detach();
});

test('the field animates bounded signals and the Cards view keeps SVG curves and arrows', async ({ page }, testInfo) => {
    await page.emulateMedia({ reducedMotion: 'no-preference' });
    await installQualityApi(page);
    await page.goto('/?view=code-quality', { waitUntil: 'domcontentloaded' });
    const report = page.locator('.code-report');
    const map = page.frameLocator('.code-report iframe');
    await expect(map.locator('body')).toHaveAttribute('data-startup', 'ready');
    await expect(map.locator('#stage')).toHaveClass(/constellation/);
    // Signals ride the effects canvas within the budget, and stop under reduced motion.
    const stats = await map.locator('body').evaluate(() => CodeAtlas.fieldStats());
    expect(stats.signals).toBeGreaterThan(0);
    expect(stats.signals).toBeLessThanOrEqual(180);
    await expect.poll(() => paintedPixels(map, '#effects')).toBeGreaterThan(0);
    await report.locator('iframe').screenshot({ path: testInfo.outputPath('field-signals.png') });
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await expect.poll(() => paintedPixels(map, '#effects')).toBe(0);
    expect(await paintedPixels(map, '#field')).toBeGreaterThan(0);
    // Cards keeps its SVG relationships: curved, with arrowheads.
    await map.locator('#cards-button').click();
    await expect(map.locator('#stage')).not.toHaveClass(/constellation/);
    const edge = map.locator('.edge-group:not(.structural) .edge').first();
    await expect(edge).toHaveAttribute('marker-end', 'url(#arrow)');
    const bend = await edge.evaluate(path => {
        const values = path.getAttribute('d').match(/-?\d+(?:\.\d+)?/g).map(Number);
        const [startX, startY, controlX, controlY, endX, endY] = values;
        return Math.hypot(controlX - (startX + endX) / 2, controlY - (startY + endY) / 2);
    });
    expect(bend).toBeCloseTo(30, 2);
});

test('mouse rotation starts inertia on demand and stops when it settles', async ({ page }) => {
    await page.emulateMedia({ reducedMotion: 'no-preference' });
    await installQualityApi(page);
    await openDetails(page);
    const map = page.frameLocator('.code-report iframe');
    await expect(map.locator('body')).toHaveAttribute('data-startup', 'ready');
    await expect(map.locator('#stage')).toHaveAttribute('data-motion', 'idle', { timeout: 10_000 });
    await map.locator('#rotate-mode').click();
    const from = await stagePoint(page, { x: 25, y: 80 });
    await page.mouse.move(from.x, from.y);
    await page.mouse.down();
    await page.mouse.move(from.x + 145, from.y + 30, { steps: 6 });
    const releaseYaw = await map.locator('#stage').getAttribute('data-yaw');
    expect(releaseYaw).not.toBe('0.0000');
    await page.mouse.up();
    await expect.poll(() => map.locator('#stage').getAttribute('data-yaw')).not.toBe(releaseYaw);
    await expect(map.locator('#stage')).toHaveAttribute('data-motion', 'idle');
    const settledYaw = await map.locator('#stage').getAttribute('data-yaw');
    await page.waitForTimeout(200);
    expect(await map.locator('#stage').getAttribute('data-yaw')).toBe(settledYaw);
});

test('a fully connected field draws within its link budget and lights every link of a selection', async ({ page }) => {
    await page.emulateMedia({ reducedMotion: 'no-preference' });
    await installQualityApi(page);
    const graph = graphResponse();
    graph.nodes = Array.from({ length: 100 }, (_, index) => ({
        id: `type-${index}`, name: `Environment${index}`, kind: index % 2 ? 'interface' : 'type',
        path: `src/environment${index}.ts:1`
    }));
    graph.edges = [];
    for (let source = 0; source < 100; source++) {
        for (let target = source + 1; target < 100; target++) graph.edges.push({
            id: `${source}-${target}`, source: `type-${source}`, target: `type-${target}`, kind: 'references'
        });
    }
    await page.route('**/api/v1/code-analyzer/graph', route => route.fulfill({ json: graph }));
    await page.goto('/?view=code-quality', { waitUntil: 'domcontentloaded' });
    const map = page.frameLocator('.code-report iframe');
    await expect(map.locator('body')).toHaveAttribute('data-startup', 'ready');
    // 4,950 references exceed the ambient budget: the summary says so, the rest stay inspectable.
    const stats = await map.locator('body').evaluate(() => CodeAtlas.fieldStats());
    expect(stats.linkTotal).toBe(4950);
    expect(stats.links).toBe(4500);
    await expect(map.locator('#view-summary')).toBeHidden(); // The budget is not a notice; hover or select to see every link.
    // Atlas treats both TypeScript kinds as types instead of its generic function fallback.
    await expect(map.locator('#count-class')).toHaveText('100');
    // Selecting an entity lights all 99 of its links, including ones outside the ambient sample.
    await map.locator('body').evaluate(() => CodeAtlas.focusNode('type-23'));
    await expect(map.locator('#inspector h2')).toHaveText('Environment23');
    const selected = await map.locator('body').evaluate(() => CodeAtlas.fieldStats());
    expect(selected.lit).toBe(99);
    expect(selected.signals).toBe(99);
});

test('the wheel zooms the map from anywhere in its frame and the sidebar lists keep it; neither scrolls the page (VIBE-71)', async ({ page }) => {
    // A short viewport so the page can scroll: the bug was the page moving instead of the map zooming.
    await page.setViewportSize({ width: 1280, height: 720 });
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await installQualityApi(page);
    const report = await openDetails(page);
    const map = page.frameLocator('.code-report iframe');
    await expect(map.locator('body')).toHaveAttribute('data-startup', 'ready');
    await report.locator('iframe').evaluate(frame => frame.scrollIntoView({ block: 'start' }));
    const scrollY = () => page.evaluate(() => window.scrollY);
    const zoom = () => map.locator('body').evaluate(() => CodeAtlas.fieldStats().zoom);
    const anchored = await scrollY();
    expect(anchored).toBeGreaterThan(0);
    const centre = box => ({ x: box.x + box.width / 2, y: box.y + box.height / 2 });
    const rail = await map.locator('.canvas-controls').boundingBox();
    const stage = await map.locator('#stage').boundingBox();
    const legend = await map.locator('.graph-legend').boundingBox();
    // The camera rail, the stage beneath it (the former gutter that scrolled the page) and the legend row all zoom.
    // Wheel down (zoom out): the four-node fixture already fits at the 200% zoom cap.
    for (const [label, point] of [['rail', centre(rail)], ['gutter', { x: centre(rail).x, y: rail.y + rail.height + 30 }],
        ['stage edge', { x: stage.x + stage.width - 6, y: stage.y + stage.height - 6 }], ['legend', centre(legend)]]) {
        const before = await zoom();
        await page.mouse.move(point.x, point.y);
        await page.mouse.wheel(0, 200);
        await expect.poll(zoom, { message: `${label} zooms the map` }).toBeLessThan(before);
        await page.waitForTimeout(100);
        expect(await scrollY(), `${label} leaves the page where it was`).toBe(anchored);
    }
    expect(Math.round(stage.x + stage.width)).toBe(Math.round(rail.x + rail.width + 12)); // The stage runs under the rail to the frame's edge.
    // A short Git changes list has nothing to scroll, and still keeps the wheel inside the panel.
    await report.locator('.qr-list-switch [data-list="changes"]').click();
    const list = report.locator('.qr-changes');
    await expect(list.locator('.change-row')).toHaveCount(3);
    const before = await zoom();
    await page.mouse.move(...Object.values(centre(await list.boundingBox())));
    await page.mouse.wheel(0, 200);
    await page.waitForTimeout(150);
    expect(await scrollY()).toBe(anchored);
    expect(await zoom()).toBe(before);
    // The grade and radar are the escape hatch: the wheel there still scrolls the page (up: it is at its end).
    await page.mouse.move(...Object.values(centre(await report.locator('.qr-hero').boundingBox())));
    await page.mouse.wheel(0, -200);
    await expect.poll(scrollY).toBeLessThan(anchored);
});

test('the map fills the viewport, the page ends under the card without slack and the field is a globe (VIBE-71)', async ({ page }) => {
    await page.setViewportSize({ width: 1512, height: 940 });
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await installQualityApi(page);
    await openDetails(page);
    const map = page.frameLocator('.code-report iframe');
    await expect(map.locator('body')).toHaveAttribute('data-startup', 'ready');
    const measure = () => page.evaluate(() => {
        const layout = document.querySelector('.code-report .code-layout').getBoundingClientRect();
        const card = document.querySelector('.project-health-quality').getBoundingClientRect();
        const container = document.querySelector('.main-container').getBoundingClientRect();
        // What follows the container and is actually shown (the app's footer is hidden app-wide).
        let after = 0;
        for (let next = document.querySelector('.main-container').nextElementSibling; next; next = next.nextElementSibling) {
            const box = next.getBoundingClientRect();
            if (box.height) after += box.height + parseFloat(getComputedStyle(next).marginTop) + parseFloat(getComputedStyle(next).marginBottom);
        }
        return { layoutHeight: layout.height,
            variable: parseFloat(getComputedStyle(document.querySelector('.code-report')).getPropertyValue('--code-report-height')),
            slack: container.bottom - card.bottom, after,
            // What the viewer should have produced: the viewport less the chrome above and below the layout, 600 to 1400.
            expected: Math.min(1400, Math.max(600, window.innerHeight - (layout.top + window.scrollY) - (container.bottom - layout.bottom) - after)),
            overflow: document.documentElement.scrollHeight - window.innerHeight };
    });
    await expect.poll(async () => (await measure()).variable).toBeGreaterThanOrEqual(600);
    const check = async () => {
        const metrics = await measure();
        expect(Math.abs(metrics.layoutHeight - metrics.variable)).toBeLessThanOrEqual(1);
        expect(Math.abs(metrics.layoutHeight - metrics.expected)).toBeLessThanOrEqual(2);
        expect(metrics.after).toBe(0);
        expect(metrics.slack).toBeLessThanOrEqual(40);
        // A layout above its 600px floor was sized so the page ends with the viewport: no slack, no scroll.
        if (metrics.layoutHeight > 602) expect(Math.abs(metrics.overflow)).toBeLessThanOrEqual(2);
        return metrics;
    };
    const short = await check();
    // A taller window gives the map the room; the fixture's header and Rules card keep the floor in play below that.
    await page.setViewportSize({ width: 1512, height: 1400 });
    await expect.poll(async () => (await measure()).layoutHeight).toBeGreaterThan(short.layoutHeight + 150);
    const tall = await check();
    expect(tall.layoutHeight).toBeGreaterThan(602);
    // The field is a globe with a shell, a focal length, nine tones and a rail the camera keeps clear of; turning it deepens the view.
    const stats = await map.locator('body').evaluate(() => CodeAtlas.fieldStats());
    expect(stats.focal).toBeGreaterThan(0);
    expect(stats.radius).toBeGreaterThanOrEqual(260);
    expect(stats.turn).toBe(0);
    expect(stats.tones).toBe(9);
    expect(stats.controls.width).toBeGreaterThan(80);
    await map.locator('#rotate-mode').click();
    for (let step = 0; step < 4; step++) await map.locator('#stage').press('ArrowRight');
    await expect.poll(() => map.locator('body').evaluate(() => CodeAtlas.fieldStats().turn)).toBe(1);
    expect(await paintedPixels(map, '#field')).toBeGreaterThan(500);
});

test('a dense field hides its declarations zoomed out and reveals them on hover, zoom, search and filter', async ({ page }, testInfo) => {
    await installQualityApi(page);
    const graph = graphResponse();
    for (let directory = 0; directory < 3; directory++) {
        graph.nodes.push({ id: `area-${directory}`, name: `Area${directory}`, kind: 'module', path: `src/Area${directory}` });
        for (let index = 0; index < 150; index++) {
            const file = `f-${directory}-${index}`;
            graph.nodes.push({ id: file, name: `Unit${index}.ts`, kind: 'file', path: `src/Area${directory}/Unit${index}.ts`, parentId: `area-${directory}` });
            graph.edges.push({ id: `c-${file}`, source: `area-${directory}`, target: file, kind: 'contains' });
            graph.nodes.push({ id: `${file}-class`, name: `Unit${index}`, kind: 'class', path: `src/Area${directory}/Unit${index}.ts:1`, parentId: file });
            graph.edges.push({ id: `c-${file}-class`, source: file, target: `${file}-class`, kind: 'contains' });
            for (const name of ['load', 'save']) {
                graph.nodes.push({ id: `${file}-${name}`, name, kind: 'function', path: `src/Area${directory}/Unit${index}.ts:9`, parentId: file });
                graph.edges.push({ id: `c-${file}-${name}`, source: file, target: `${file}-${name}`, kind: 'contains' });
            }
            if (index % 3 === 0) graph.edges.push({ id: `r-${file}`, source: file, target: `f-${(directory + 1) % 3}-${index}`, kind: 'references' });
        }
    }
    await page.route('**/api/v1/code-analyzer/graph', route => route.fulfill({ json: graph }));
    const report = await openDetails(page);
    const map = page.frameLocator('.code-report iframe');
    await expect(map.locator('body')).toHaveAttribute('data-startup', 'ready', { timeout: 60_000 });
    await expect(map.locator('#stage')).toHaveClass(/dense/);
    await expect(map.locator('#stage')).toHaveAttribute('data-motion', 'idle', { timeout: 15_000 });
    const stats = () => map.locator('body').evaluate(() => CodeAtlas.fieldStats());
    const locate = id => map.locator('body').evaluate((_, id) => CodeAtlas.locate(id), id);
    // Zoomed all the way out only the structure is drawn: the layout keeps every position, the
    // 450 classes and 900 functions wait for the camera, and the summary and legend say so.
    for (let step = 0; step < 4; step++) await map.locator('#zoom-out').click();
    await expect.poll(async () => (await stats()).zoom).toBeLessThan(.4);
    await expect.poll(async () => (await stats()).hidden).toEqual({ class: 450, function: 900 });
    expect((await stats()).shown).toBe(graph.nodes.length - 1350);
    expect((await stats()).nodes).toBe(graph.nodes.length);
    expect((await stats()).fileDetail).toBe(.35); // Files are dust at the overview, never hidden.
    await expect(map.locator('#detail-notice')).toHaveText('Zoom in to show 900 functions and 450 classes and types');
    await expect(map.locator('#view-summary')).toBeHidden();
    await expect(map.locator('#count-function').locator('..')).toHaveClass(/zoomed-out/);
    await expect(map.locator('#count-class').locator('..')).toHaveClass(/zoomed-out/);
    await expect(map.locator('#count-module').locator('..')).not.toHaveClass(/zoomed-out/);
    expect(await locate('f-0-0-load')).toMatchObject({ hidden: false, shown: false, named: false });
    expect(await locate('f-0-0')).toMatchObject({ hidden: false, shown: true });
    await report.locator('iframe').screenshot({ path: testInfo.outputPath('semantic-zoom-out.png') });
    // Hovering a file lights every one of its links and raises its hidden declarations above the veil.
    const target = await stagePoint(page, 'f-0-0');
    await page.mouse.move(target.x - 30, target.y - 30);
    await page.mouse.move(target.x, target.y);
    await expect(map.locator('#node-tooltip')).toContainText('Unit0.ts');
    await expect.poll(async () => (await stats()).hidden).toEqual({ class: 449, function: 898 });
    expect(await locate('f-0-0-load')).toMatchObject({ shown: true });
    expect((await stats()).lit).toBe(graph.edges.filter(edge => edge.source === 'f-0-0' || edge.target === 'f-0-0').length);
    const corner = await stagePoint(page, { x: 4, y: 4 });
    await page.mouse.move(corner.x, corner.y);
    await expect.poll(async () => (await stats()).hidden).toEqual({ class: 450, function: 900 });
    // A search shows every match at any zoom; an explicit entity filter shows that family at any zoom.
    await map.locator('#search').fill('save');
    await expect.poll(async () => (await stats()).hidden).toEqual({ class: 0, function: 0 });
    await expect(map.locator('#detail-notice')).toBeHidden();
    await map.locator('#search').press('Escape');
    await expect.poll(async () => (await stats()).hidden).toEqual({ class: 450, function: 900 });
    await map.locator('#entity-filter').selectOption('classes');
    await expect(map.locator('#stage')).toHaveAttribute('data-motion', 'idle', { timeout: 15_000 });
    for (let step = 0; step < 4; step++) await map.locator('#zoom-out').click();
    await expect.poll(async () => (await stats()).hidden).toEqual({ class: 0, function: 0 });
    expect((await stats()).nodes).toBe(graph.nodes.length - 900 - 452);
    await map.locator('#entity-filter').selectOption('all');
    await expect(map.locator('#stage')).toHaveAttribute('data-motion', 'idle', { timeout: 15_000 });
    // Zooming in brings the declarations back and the notice goes with them.
    // Files brighten first (dust until 45%, full at 80%), then classes (70% to 100%), then functions (90% to 130%).
    for (let step = 0; step < 14 && (await stats()).zoom < 1.3; step++) await map.locator('#zoom-in').click();
    await expect.poll(async () => (await stats()).zoom).toBeGreaterThanOrEqual(1.3);
    await expect.poll(async () => (await stats()).fileDetail).toBe(1);
    await expect.poll(async () => (await stats()).hidden).toEqual({ class: 0, function: 0 });
    expect((await stats()).shown).toBe(graph.nodes.length);
    await expect(map.locator('#detail-notice')).toBeHidden();
    await expect(map.locator('#count-function').locator('..')).not.toHaveClass(/zoomed-out/);
    await report.locator('iframe').screenshot({ path: testInfo.outputPath('semantic-zoom-in.png') });
});

test('the sidebar lists Git changes beside report files and opens the shared diff viewer', async ({ page }) => {
    const { diffRequests } = await installQualityApi(page);
    await openQuality(page);
    const report = page.locator('.code-report');
    await expect(report.locator('.qr')).toHaveAttribute('data-state', 'complete', { timeout: 25_000 });
    const map = page.frameLocator('.code-report iframe');
    await expect(map.locator('body')).toHaveAttribute('data-startup', 'ready');
    // Report files opens first; the map still lights the working-tree changes.
    await expect(report.locator('[data-changes-list]')).toBeHidden();
    await expect(report.locator('.qr-files')).toBeVisible();
    await expect(map.locator('#highlight-changes')).toHaveAttribute('aria-pressed', 'true');
    const switcher = report.locator('.qr-list-switch');
    await expect(switcher.getByRole('button', { name: /Report files/ })).toContainText('2');
    await expect(switcher.getByRole('button', { name: /Git changes/ })).toContainText('3');
    await expect(map.locator('#change-summary')).toContainText('1 of 3 changed files on the map');
    await switcher.getByRole('button', { name: /Git changes/ }).click();
    const list = report.locator('[data-changes-list]');
    await expect(list).toBeVisible();
    await expect(report.locator('.qr-files')).toBeHidden();
    await expect(list.locator('.change-head')).toContainText('3 changed files against HEAD');
    await expect(list.locator('.change-head')).toContainText('+14');
    await expect(list.locator('.change-head')).toContainText('\u22125');
    const rows = list.locator('.change-row');
    await expect(rows).toHaveCount(3);
    await expect(rows.nth(0).locator('.change-status')).toHaveText('M');
    await expect(rows.nth(1).locator('.change-status')).toHaveText('?');
    await expect(rows.nth(2).locator('.change-status')).toHaveText('D');
    await expect(rows.nth(0).locator('.change-locate')).toHaveCount(1);
    await expect(rows.nth(1).locator('.change-locate')).toHaveCount(0);
    // Locate focuses the map; a row opens the shared Monaco viewer with every change in its rail.
    await rows.nth(0).locator('.change-locate').click();
    await expect(map.locator('#inspector h2')).toHaveText('PaymentProcessor.cs');
    await rows.nth(2).locator('.change-open').click();
    const modal = page.locator('.vb-diff-modal');
    await expect(modal).toBeVisible();
    await expect(modal.locator('.vb-diff-file-item')).toHaveCount(3);
    await expect(modal.locator('.vb-diff-file-item.active')).toContainText('Deleted.cs');
    await expect(modal.locator('.vb-diff-file-item .file-status.deleted')).toHaveCount(1);
    await expect(modal.locator('.monaco-diff-editor')).toBeVisible({ timeout: 30_000 });
    await expect.poll(() => diffRequests.length).toBe(1);
    expect(diffRequests[0]).toBe('src/Utilities/Deleted.cs');
    await expect(modal.locator('[data-vb-diff-stats] .added')).toHaveText('+2', { timeout: 15_000 });
    await modal.locator('.vb-diff-file-item').first().click();
    await expect.poll(() => diffRequests.length).toBe(2);
    await expect(modal.locator('[data-vb-diff-language]')).toHaveText('csharp');
    await page.keyboard.press('Escape');
    await expect(modal).toHaveCount(0);
    // Highlight changes on the map uses the git list, not only the scanned sources; it is on by default.
    await map.locator('#highlight-changes').click();
    await expect(map.locator('#highlight-changes')).toHaveAttribute('aria-pressed', 'false');
    await expect(map.locator('#change-summary')).toContainText('1 of 3 changed files on the map · highlighting off');
});

test('change rows gain their map-locate action when the graph arrives after the change list', async ({ page }) => {
    await installQualityApi(page);
    let release;
    const pending = new Promise(resolve => { release = resolve; });
    await page.route('**/api/v1/code-analyzer/graph', async route => {
        await pending;
        await route.fulfill({ json: graphResponse() }).catch(() => {});
    });
    await openQuality(page);
    const report = page.locator('.code-report');
    await report.locator('.qr-list-switch').getByRole('button', { name: /Git changes/ }).click();
    const list = report.locator('[data-changes-list]');
    await expect(list.locator('.change-row')).toHaveCount(3);
    await expect(list.locator('.change-locate')).toHaveCount(0);
    release();
    const map = page.frameLocator('.code-report iframe');
    await expect(map.locator('body')).toHaveAttribute('data-startup', 'ready');
    await expect(list.locator('.change-locate')).toHaveCount(1);
    await expect(list).toBeVisible();
    await expect(map.locator('#change-summary')).toContainText('1 of 3 changed files on the map');
    await list.locator('.change-locate').click();
    await expect(map.locator('#inspector h2')).toHaveText('PaymentProcessor.cs');
});

test('an unavailable change list leaves the report usable', async ({ page }) => {
    await installQualityApi(page);
    await page.route('**/api/v1/code-analyzer/changes', route => route.fulfill({ status: 404, json: { error: 'Not Found' } }));
    const report = await openDetails(page);
    const switcher = report.locator('.qr-list-switch');
    await expect(switcher.getByRole('button', { name: /Git changes/ })).toContainText('\u2014');
    await switcher.getByRole('button', { name: /Git changes/ }).click();
    await expect(report.locator('[data-changes-list]')).toContainText('Changes unavailable');
    await expect(report.getByRole('button', { name: new RegExp(PAYMENT_PATH) })).toHaveCount(0);
    await switcher.getByRole('button', { name: /Report files/ }).click();
    await expect(report.getByRole('button', { name: new RegExp(PAYMENT_PATH) })).toBeVisible();
});

test('map coverage explains permanent dependency exclusions and the drawing budget', async ({ page }) => {
    await installQualityApi(page);
    const requests = [];
    await page.route('**/api/v1/code-analyzer/graph', route => {
        const request = route.request().postDataJSON();
        requests.push(request);
        const graph = graphResponse();
        graph.truncated = true;
        graph.diagnostics = {
            supportedFiles: 8, excludedDependencyFiles: 3,
            excludedBuildOutputFiles: 2, includesDependencies: false,
            omissions: [{ code: 'file-size', count: 1, detail: 'files exceeded the 128 KiB source limit; their file nodes remain visible.' }]
        };
        return route.fulfill({ json: graph });
    });
    const report = await openDetails(page);
    // Coverage stays out of the way until the card menu asks for it.
    await expect(report.locator('[data-graph-options]')).toBeHidden();
    const quality = page.locator('.project-health-quality');
    await quality.getByLabel('More scan options', { exact: true }).click();
    await quality.getByRole('button', { name: 'Map coverage', exact: true }).click();
    const coverage = report.locator('[data-map-diagnostics-body]');
    await expect(coverage).toBeVisible();
    await expect(coverage).toContainText('Partial map');
    await expect(coverage).toContainText('2 source files mapped');
    await expect(coverage).toContainText('8 supported source files');
    await expect(coverage).toContainText('3 vendor/node_modules/assets files');
    await expect(coverage).toContainText('2 C# bin/obj files excluded');
    await expect(coverage).toContainText('128 KiB source limit');
    await expect(coverage).toContainText('draws every entity of this snapshot');
    await expect(coverage).toContainText('Search covers the whole snapshot');
    await expect(report.locator('[data-map-dependencies]')).toHaveCount(0);
    expect(requests).toHaveLength(1);
    expect(requests[0]).not.toHaveProperty('includeDependencies');
    await expect(report.locator('iframe')).toHaveCount(1);
});

test('graph failures leave saved report and all metrics accessible', async ({ page }) => {
    await installQualityApi(page);
    await page.route('**/api/v1/code-analyzer/graph', route => route.fulfill({ status: 503, json: { error: 'Map unavailable' } }));
    const report = await openDetails(page);
    await expect(report.locator('[data-code-map]')).toContainText('Map unavailable');
    await report.getByRole('button', { name: new RegExp(PAYMENT_PATH) }).click();
    await expect(report.locator('.details-panel')).toBeVisible();
    await report.locator('.details-panel').getByRole('button', { name: /Parameter count/ }).click();
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
    await page.emulateMedia({ reducedMotion: 'reduce' });
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
    await report.scrollIntoViewIfNeeded();
    await list.focus();
    // Focusing the list may scroll the containing page into view. Subsequent list scrolling
    // must leave the neighboring map fixed within that view.
    const mapBounds = await report.locator('iframe').boundingBox();
    await list.press('End');
    await expect.poll(() => list.evaluate(element => element.scrollTop)).toBeGreaterThan(0);
    expect(await report.locator('iframe').boundingBox()).toEqual(mapBounds);
    await page.evaluate(() => {
        const viewer = window.app.ruleController.codeReportViewer;
        viewer.showFile(viewer.response.report.files[0], 'cognitive_complexity');
    });
    const dialog = report.locator('.details-panel');
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
    await openQuality(page);
    await expect.poll(() => started).toBe(true);
    await page.locator('[data-action="navigate"][data-view="environments"]:visible').click();
    release();
    await expect(page.locator('.code-report iframe')).toHaveCount(0);
    await page.locator('[data-action="navigate"][data-view="code-quality"]:visible').click();
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
            await page.locator(scope === 'rules' ? '[data-action="navigate-home"]' : '.app-subnav [data-view="code-quality"]').click();
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
    await expect(brief).toContainText('No source files in this report.');
    const quality = page.locator('.project-health-quality');
    const fix = quality.locator('[data-action="launch-health-fix"]');
    await expect(fix).toBeDisabled();
    await expect(fix).toHaveAttribute('title', /No changed source files to fix/);
    // The scan log opens from the card menu; the transcript itself is unchanged.
    await expect(quality.locator('[data-code-analyzer-log]')).toBeHidden();
    await quality.getByLabel('More scan options', { exact: true }).click();
    await quality.getByRole('button', { name: 'Scan log', exact: true }).click();
    await expect(quality.locator('[data-code-analyzer-log]')).toBeVisible();
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
    await page.locator('[data-action="navigate"][data-view="code-quality"]:visible').click();
    const quality = page.locator('.project-health-quality');
    await expect(quality.locator('.code-report')).toContainText('No source files in this report.');
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
    const picker = page.locator('select[data-project-health-fix-agent][data-fix-scope="quality"]');
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
