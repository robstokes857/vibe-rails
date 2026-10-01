const { test, expect } = require('@playwright/test');

// Exercise the real shared pickers and routing editor without a live CLI or user database.
async function mount(page) {
    await page.goto('/');
    await page.evaluate(async () => {
        const { cardReviewsSection, bindCardReviews } = await import('/js/modules/board-card-reviews.js');
        const { BoardApi } = await import('/js/modules/board-api.js');
        const { LlmPickerController } = await import('/js/modules/llm-picker-controller.js');
        window.reviewSettings = { sourceKind: 'unknown', description: '', scope: 'working-tree' };
        window.reviewLaunches = []; window.reviewerUnavailable = true;
        const app = window.app;
        const originalApi = app.apiCall.bind(app);
        app.apiCall = async (url, method, body, options) => {
            if (url.endsWith('/reviews/settings')) {
                if (method === 'PUT') window.reviewSettings = body;
                return window.reviewSettings;
            }
            if (url.endsWith('/reviews/preview')) {
                const source = window.reviewSettings.sourceKind;
                const provider = body.override?.selection?.split(':').at(-1) || 'codex';
                return { source: { kind: source, provider: source === 'session' ? 'claude' : null }, provider, reviewer: provider,
                    usedFallback: source !== 'session' && !body.override, overridden: Boolean(body.override),
                    workspace: 'C:/project', scopeDescription: 'Project checkout; working-tree',
                    problem: window.reviewerUnavailable && !body.override ? 'Chosen CLI unavailable. Install it or choose an override.' : null };
            }
            if (url.includes('/reviews?')) return { reviews: [], hasMore: false };
            if (url.endsWith('/launch')) { window.reviewLaunches.push(body); return { tabId: 'review-tab' }; }
            return originalApi(url, method, body, options);
        };
        const card = { id: 'card_test', sessions: [
            { id: 'coding', displayName: 'Claude coding', cli: 'claude', origin: 'launch' },
            { id: 'chat', displayName: 'Discussion', cli: 'codex', origin: 'chat' },
            { id: 'review', displayName: 'Old review', cli: 'codex', origin: 'code_review' }
        ] };
        const host = document.createElement('div'); host.id = 'routing-test'; host.className = 'p-3';
        host.innerHTML = cardReviewsSection(card); document.body.replaceChildren(host);
        BoardApi.attach(app); window.reviewPanel = bindCardReviews(host, card, app);
    });
}

for (const width of [1440, 390]) {
    test(`Switch reviewer explicit source, override and prerequisite recovery at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 1100 });
        // The Board fixture supplies provider catalogs and avoids any backend startup.
        await page.route('**/api/**', route => {
            const url = new URL(route.request().url());
            if (url.pathname.includes('llm-picker')) return route.fulfill({ json: { items: [
                { key: 'base:codex', kind: 'base', cli: 'codex', label: 'Codex', enabled: true, order: 0 },
                { key: 'base:claude', kind: 'base', cli: 'claude', label: 'Claude', enabled: true, order: 1 }
            ] } });
            return route.fulfill({ json: {} });
        });
        await mount(page);
        const panel = page.locator('[data-board-reviews]');
        await expect(panel.locator('[data-review-resolution]')).toContainText('configured fallback');
        await expect(panel.locator('[data-review-run]')).toBeDisabled();
        await panel.locator('[data-review-settings] > summary').click();
        await expect(panel.locator('[data-review-source] option[value="session:chat"]')).toHaveCount(0);
        await panel.locator('[data-review-source]').selectOption('session:coding');
        await panel.locator('[data-review-description]').fill('Implemented the changes');
        await page.evaluate(() => { window.reviewerUnavailable = false; });
        await panel.locator('[data-review-save]').click();
        await expect(panel.locator('[data-review-resolution]')).toContainText('claude coded this → codex will review');
        await panel.locator('[data-review-settings] > summary').click();
        await panel.locator('[data-review-override]').check();
        await panel.locator('[data-review-picker]').evaluate(select => select.tomselect.setValue('base:claude'));
        await expect(panel.locator('[data-review-resolution]')).toContainText('claude will review (override)');
        await panel.locator('[data-review-run]').click();
        await expect.poll(() => page.evaluate(() => window.reviewLaunches.length)).toBe(1);
        expect(await page.evaluate(() => window.reviewLaunches[0])).toMatchObject({ intent: 'code_review', review: { override: { selection: 'base:claude' } } });
        await page.screenshot({ path: testInfo.outputPath(`switch-reviewer-${width}.png`) });
        await page.evaluate(() => window.reviewPanel.dispose());
    });
}
