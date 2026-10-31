// Production styles and Bootstrap, served statically. No application database or CLI.
const { test, expect } = require('@playwright/test');

test.beforeEach(async ({ page }) => {
    await page.routeWebSocket('**/api/v1/events/ws*', () => {});
    await page.route('**/api/v1/**', route => route.fulfill({ json: {} }));
    await page.goto('/?view=settings', { waitUntil: 'domcontentloaded' });
    await expect(page.locator('#app-content .vb-page-title')).toBeVisible();
    await page.locator('#app-content').evaluate(host => {
        host.innerHTML = `<section class="card"><div class="card-body">
            <h1 class="vb-page-title">Shared controls</h1>
            <p><button class="btn btn-primary" id="primary">Save changes</button>
            <button class="btn btn-secondary" id="secondary">Preview</button>
            <button class="btn btn-outline-secondary" id="ghost">Cancel</button>
            <button class="btn btn-danger" id="danger">Delete sample</button>
            <button class="btn btn-primary" id="disabled" disabled>Unavailable</button>
            <button class="btn btn-secondary btn-sm" id="small">Small action</button></p>
            <label class="form-label" for="name">Name</label><input class="form-control" id="name">
            <label class="form-label" for="priority">Priority</label>
            <select class="form-select" id="priority"><option>Normal</option><option>High</option></select>
            <select class="form-select" id="disabled-select" disabled aria-label="Unavailable priority"><option>Normal</option></select>
            <label><input class="form-check-input" type="checkbox" id="check"> Include notes</label>
            <div class="alert alert-success"><h2>Saved</h2><p>Your changes have been saved.</p></div>
            <div class="alert alert-warning">Needs attention</div>
            <div class="alert alert-danger">Save failed. Try again.</div>
            <span class="badge badge-cli">codex</span><span class="badge badge-warning">Pending</span>
            <div class="modal-content"><div class="modal-header"><h2 class="modal-title">Create sample</h2></div></div>
        </div></section>`;
    });
});

async function contrast(locator, foreground = 'color', background = 'backgroundColor') {
    return locator.evaluate((element, [fg, bg]) => {
        const luminance = css => {
            const channels = css.match(/[\d.]+/g).slice(0, 3).map(Number).map(v => {
                v /= 255;
                return v <= .04045 ? v / 12.92 : ((v + .055) / 1.055) ** 2.4;
            });
            return channels[0] * .2126 + channels[1] * .7152 + channels[2] * .0722;
        };
        const style = getComputedStyle(element);
        const a = luminance(style[fg]), b = luminance(style[bg]);
        return (Math.max(a, b) + .05) / (Math.min(a, b) + .05);
    }, [foreground, background]);
}

test('shared actions keep readable colors and borders through hover, press, focus and disabled', async ({ page }) => {
    const primary = page.locator('#primary');
    await expect(primary).toHaveCSS('background-color', 'rgb(124, 58, 237)');
    await expect(primary).toHaveCSS('font-size', '14px');
    await expect(primary).toHaveCSS('font-weight', '600');
    await expect(primary).toHaveCSS('line-height', '19.6px');
    await expect(primary).toHaveCSS('min-height', '40px');
    await expect(page.locator('#small')).toHaveCSS('min-height', '32px');
    await expect(page.locator('#small')).toHaveCSS('font-size', '14px');
    for (const id of ['primary', 'secondary', 'danger']) {
        const button = page.locator(`#${id}`);
        expect(await contrast(button)).toBeGreaterThanOrEqual(4.5);
        await button.hover();
        await expect(button).toHaveCSS('background-color', id === 'primary' ? 'rgb(109, 40, 217)' : id === 'danger' ? 'rgb(70, 33, 46)' : 'rgb(48, 48, 64)');
        expect(await contrast(button)).toBeGreaterThanOrEqual(4.5);
        await page.mouse.down();
        expect(await contrast(button)).toBeGreaterThanOrEqual(4.5);
        await page.mouse.up();
    }
    await page.mouse.move(0, 0);
    await page.locator('#danger').focus();
    await page.keyboard.press('Shift+Tab');
    await page.keyboard.press('Shift+Tab');
    await page.keyboard.press('Shift+Tab');
    await expect(primary).toHaveCSS('outline-color', 'rgb(20, 184, 166)');
    await expect(primary).toHaveCSS('outline-width', '2px');
    await expect(page.locator('#disabled')).toHaveCSS('background-color', 'rgb(124, 58, 237)');
    await expect(page.locator('#disabled')).toHaveCSS('border-color', 'rgb(124, 58, 237)');
    await expect(page.locator('#disabled')).toBeDisabled();
});

test('fields, feedback and dialog chrome use readable shared surfaces', async ({ page }) => {
    const input = page.locator('#name');
    await expect(input).toHaveCSS('font-size', '16px');
    await expect(input).toHaveCSS('border-radius', '4px');
    expect(await contrast(input, 'borderTopColor')).toBeGreaterThanOrEqual(3);
    await input.fill('Draft name');
    await input.evaluate(element => element.setAttribute('aria-invalid', 'true'));
    await expect(input).toHaveCSS('border-color', 'rgb(252, 165, 165)');
    await expect(input).toHaveValue('Draft name');
    await page.locator('#priority').focus();
    await page.keyboard.press('ArrowDown');
    await expect(page.locator('#priority')).toHaveValue('High');
    await expect(page.locator('#disabled-select')).toHaveCSS('background-color', 'rgb(10, 10, 15)');
    await page.locator('#check').check();
    await expect(page.locator('#check')).toBeChecked();
    for (const tone of ['success', 'warning', 'danger']) {
        expect(await contrast(page.locator(`#app-content .alert-${tone}`))).toBeGreaterThanOrEqual(4.5);
    }
    await expect(page.locator('.alert-success p')).toHaveCSS('color', 'rgb(110, 231, 183)');
    await expect(page.locator('.badge-warning')).toHaveCSS('color', 'rgb(246, 98, 16)');
    await expect(page.locator('.modal-content')).toHaveCSS('border-radius', '12px');
    await expect(page.locator('.modal-title')).toHaveCSS('font-size', '21px');
});

test('small screens retain touch targets, readable fields and reduced motion', async ({ page }) => {
    await page.setViewportSize({ width: 320, height: 900 });
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await expect(page.locator('#primary')).toHaveCSS('min-height', '44px');
    await expect(page.locator('#name')).toHaveCSS('font-size', '16px');
    await expect(page.locator('#primary')).toHaveCSS('transition-duration', '0s');
    expect(await page.locator('#app-content').evaluate(el => el.scrollWidth - el.clientWidth)).toBeLessThanOrEqual(1);
});

test('opt-in VS Code colors and light fields survive the shared component layer', async ({ page }) => {
    await page.evaluate(() => {
        const root = document.documentElement;
        root.dataset.viberailsHost = 'vscode';
        root.dataset.viberailsVscodeTheme = 'enabled';
        root.style.setProperty('--vscode-editor-background', '#ffffff');
        root.style.setProperty('--vscode-editor-foreground', '#202020');
        root.style.setProperty('--vscode-button-background', '#246abc');
        root.style.setProperty('--vscode-button-foreground', '#ffffff');
        root.style.setProperty('--vscode-focusBorder', '#005fb8');
        document.body.classList.add('vscode-light');
    });
    await expect(page.locator('#primary')).toHaveCSS('background-color', 'rgb(36, 106, 188)');
    await expect(page.locator('#name')).toHaveCSS('background-color', 'rgb(255, 255, 255)');
    await expect(page.locator('#name')).toHaveCSS('color-scheme', 'light');
    await page.locator('#name').focus();
    await expect(page.locator('#name')).toHaveCSS('outline-color', 'rgb(0, 95, 184)');
});
