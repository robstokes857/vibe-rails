// @ts-check

const { test, expect } = require('./fixtures');

const VIEWPORTS = [
    { width: 1492, height: 900 },
    { width: 1210, height: 800 },
    { width: 1100, height: 800 },
    { width: 1000, height: 800 },
    { width: 768, height: 800 }
];

test.describe('responsive top navigation', () => {
    for (const viewport of VIEWPORTS) {
        test(`keeps the top navigation inside the viewport at ${viewport.width}px`, async ({ page }) => {
            await page.setViewportSize(viewport);
            await page.addInitScript(() => {
                localStorage.setItem('viberails_nav_layout', 'side');
            });

            await page.goto('/');

            const nav = page.locator('.app-subnav');
            await expect(nav).toBeVisible();
            await expect(nav).toHaveCSS('opacity', '1');
            await expect(page.locator('.app-sidebar, [data-action="switch-nav-layout"]')).toHaveCount(0);
            await expect(page.locator('.app-subnav [data-action="navigate"][data-view="board"]')).toBeVisible();

            const navOverflow = await nav.evaluate(element =>
                element.scrollWidth - element.clientWidth);
            expect(navOverflow).toBeLessThanOrEqual(1);

            const main = page.locator('.app-main');
            const mainBox = await main.boundingBox();
            expect(mainBox.x).toBeLessThanOrEqual(1);
            expect(mainBox.width).toBeGreaterThanOrEqual(viewport.width - 1);
        });
    }

    test('does not wrap the brand before the compact breakpoint', async ({ page }) => {
        await page.setViewportSize({ width: 1492, height: 900 });
        await page.addInitScript(() => {
            localStorage.setItem('viberails_nav_layout', 'side');
        });

        await page.goto('/');

        const brand = page.locator('.app-subnav-brand .brand-text-sm');
        await expect(brand).toBeVisible();
        const brandHeight = await brand.evaluate(element => element.getBoundingClientRect().height);

        expect(brandHeight).toBeLessThan(30);
    });
});
