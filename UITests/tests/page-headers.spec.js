// Real view templates and CSS with intercepted APIs; no backend or user state.
const { test, expect } = require('@playwright/test');

const VIEWS = [
    ['agents', 'Rules and code quality'],
    ['environments', 'Environment / Workers'],
    ['vibe-rails-ai', 'Vibe AI Search'],
    ['mcp', 'MCP Explorer'],
    ['settings', 'Application Settings']
];

test.beforeEach(async ({ page }) => {
    await page.addInitScript(() => sessionStorage.setItem('viberails_tab', 'headers-fixture'));
    await page.routeWebSocket('**/api/v1/events/ws*', () => {});
    await page.route('**/api/v1/**', route => {
        const path = new URL(route.request().url()).pathname;
        const payloads = {
            '/api/v1/context': { isInGit: true, rootPath: 'C:/fixture', launchDirectory: 'C:/fixture' },
            '/api/v1/settings': { mcpEnabled: true },
            '/api/v1/environments': { environments: [] },
            '/api/v1/agents': { agents: [] },
            '/api/v1/rules/details': { rules: [] },
            '/api/v1/sandboxes': { sandboxes: [] },
            '/api/v1/llm-picker/preferences': { items: [] },
            '/api/v1/hooks/status': { inGitRepo: true, isInstalled: true },
            '/api/v1/mcp/tools': [],
            '/api/v1/settings/pin/status': { isSet: false }
        };
        return route.fulfill({ json: payloads[path] || {} });
    });
});

for (const width of [1440, 768, 390]) {
    test(`page headings share typography and keep controls usable at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 900 });
        let reference;
        for (const [view, titleText] of VIEWS) {
            await page.goto(`/?view=${view}`, { waitUntil: 'domcontentloaded' });
            const header = page.locator('#app-content .vb-page-header');
            await expect(header).toBeVisible();
            await expect(page.locator('#app-content h1')).toHaveCount(1);
            await expect(header.getByRole('heading', { level: 1 })).toHaveText(titleText);
            const metrics = await header.evaluate(element => {
                const title = element.querySelector('h1');
                const style = getComputedStyle(title);
                const headerStyle = getComputedStyle(element);
                const rect = element.getBoundingClientRect();
                const titleRect = title.getBoundingClientRect();
                const shellRect = element.closest('.main-container').getBoundingClientRect();
                return {
                    typography: [style.fontFamily, style.fontSize, style.fontWeight,
                        style.lineHeight, style.letterSpacing, style.color, style.textTransform],
                    treatment: [headerStyle.paddingBottom, headerStyle.borderBottomWidth,
                        headerStyle.borderBottomColor, headerStyle.rowGap],
                    leftOffset: titleRect.left - rect.left,
                    topInset: rect.top - shellRect.top,
                    right: rect.right,
                    overflow: element.scrollWidth - element.clientWidth
                };
            });
            reference ||= metrics;
            expect(metrics.typography).toEqual(reference.typography);
            expect(metrics.treatment).toEqual(reference.treatment);
            expect(metrics.leftOffset).toBeCloseTo(0, 0);
            expect(metrics.topInset).toEqual(reference.topInset);
            expect(metrics.right).toBeLessThanOrEqual(width);
            expect(metrics.overflow).toBeLessThanOrEqual(1);

            if (view === 'agents') {
                await expect(header.getByRole('switch')).toBeEnabled();
            } else if (view === 'vibe-rails-ai') {
                const diagnostics = header.getByRole('button', { name: 'Diagnostics' });
                await diagnostics.click();
                await expect(diagnostics).toHaveAttribute('aria-expanded', 'true');
            } else if (view === 'mcp') {
                const remote = header.getByRole('button', { name: 'Remote', exact: true });
                await remote.focus();
                await remote.press('Enter');
                await expect(page.locator('[data-mcp-remote]')).toBeVisible();
                await header.getByRole('button', { name: 'Local', exact: true }).click();
                await expect(page.locator('[data-mcp-local]')).toBeVisible();
            } else if (view === 'settings') {
                await page.getByRole('tab', { name: 'Integrations', exact: true }).click();
                await expect(page.locator('#settings-panel-integrations')).toBeVisible();
            }
            await header.screenshot({ path: testInfo.outputPath(`${view}-header.png`) });
        }
    });
}

test('shared headers follow the host theme colors', async ({ page }) => {
    for (const [view] of VIEWS) {
        await page.goto(`/?view=${view}`, { waitUntil: 'domcontentloaded' });
        const title = page.locator('#app-content .vb-page-title');
        await expect(title).toBeVisible();
        await page.evaluate(() => {
            document.documentElement.style.setProperty('--color-text', 'rgb(25, 35, 45)');
            document.documentElement.style.setProperty('--color-border', 'rgb(100, 110, 120)');
        });
        await expect(title).toHaveCSS('color', 'rgb(25, 35, 45)');
        await expect(page.locator('#app-content .vb-page-header'))
            .toHaveCSS('border-bottom-color', 'rgb(100, 110, 120)');
    }
});
