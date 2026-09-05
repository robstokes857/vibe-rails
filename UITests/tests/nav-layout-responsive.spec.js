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
        test(`keeps the side-navigation switch intact at ${viewport.width}px`, async ({ page }) => {
            await page.setViewportSize(viewport);
            await page.addInitScript(() => {
                localStorage.setItem('viberails_nav_layout', 'top');
            });

            await page.goto('/');

            const nav = page.locator('.app-subnav');
            const switchButton = nav.locator('[aria-label="Switch to side navigation"]');
            await expect(nav).toBeVisible();
            await expect(nav).toHaveCSS('opacity', '1');
            await expect(switchButton).toBeVisible();

            const dimensions = await switchButton.evaluate(element => {
                const rect = element.getBoundingClientRect();
                return { width: rect.width, height: rect.height };
            });

            expect(dimensions.width).toBeGreaterThanOrEqual(25);
            expect(dimensions.height).toBeGreaterThanOrEqual(25);
            expect(Math.abs(dimensions.width - dimensions.height)).toBeLessThanOrEqual(1);

            const navOverflow = await nav.evaluate(element =>
                element.scrollWidth - element.clientWidth);
            expect(navOverflow).toBeLessThanOrEqual(1);
        });
    }

    test('does not wrap the brand before the compact breakpoint', async ({ page }) => {
        await page.setViewportSize({ width: 1492, height: 900 });
        await page.addInitScript(() => {
            localStorage.setItem('viberails_nav_layout', 'top');
        });

        await page.goto('/');

        const brand = page.locator('.app-subnav-brand .brand-text-sm');
        await expect(brand).toBeVisible();
        const brandHeight = await brand.evaluate(element => element.getBoundingClientRect().height);

        expect(brandHeight).toBeLessThan(30);
    });
});
