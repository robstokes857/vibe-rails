const { test, expect } = process.env.VIBERAILS_BOARD_STATIC === '1'
    ? require('@playwright/test')
    : require('./fixtures');

const IMAGE = 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a5XcAAAAASUVORK5CYII=';
const DESCRIPTION = 'Repro screenshot\n![Screenshot.png](attachment:att_image)\n<img src=x onerror="window.__injected=true">';

async function openBoard(page, { active = false } = {}) {
    if (process.env.VIBERAILS_BOARD_STATIC === '1') {
        await page.addInitScript(() => sessionStorage.setItem('viberails_tab', 'board-fixture'));
    }
    let card = {
        id: 'card_test', key: 'VB-1', columnId: 'col_ready', position: 0,
        title: 'Description images', description: DESCRIPTION, priority: 'high',
        assignee: null, points: null, tags: [], blocked: false, commentCount: 1,
        activeSessionId: active ? 'session_test' : null,
        createdAt: '2026-09-11T06:00:00Z', updatedAt: '2026-09-11T06:00:00Z',
        attachments: [{ id: 'att_image', name: 'Screenshot.png', url: IMAGE }],
        comments: [{ id: 'comment_1', author: { kind: 'user', label: 'You' },
            body: '![Screenshot.png](attachment:att_image)', createdAt: '2026-09-11T06:00:00Z' }],
        commits: [], sessions: [{ id: 'session_test', displayName: 'Codex session', cli: 'codex',
            active, createdAt: '2026-09-11T06:00:00Z' }]
    };
    await page.routeWebSocket('**/api/v1/events/ws*', () => {});
    await page.route('**/api/v1/**', async route => {
        const path = new URL(route.request().url()).pathname;
        if (path === '/api/v1/board/cards/card_test') {
            if (route.request().method() === 'PUT') card = { ...card, ...route.request().postDataJSON() };
            return route.fulfill({ json: card });
        }
        if (path === '/api/v1/board/cards/card_test/attachments') {
            const upload = route.request().postDataJSON();
            const attachment = { id: 'att_uploaded', name: upload.name, url: upload.dataUrl };
            card.attachments.push(attachment);
            return route.fulfill({ json: attachment });
        }
        const payloads = {
            '/api/v1/context': { isInGit: true, rootPath: 'C:/board-fixture', launchDirectory: 'C:/board-fixture' },
            '/api/v1/settings': {},
            '/api/v1/environments': { environments: [] },
            '/api/v1/llm-picker/preferences': { items: [] },
            '/api/v1/board/columns': { columns: [{ id: 'col_ready', name: 'Ready', position: 0, color: '#3b82f6' }] },
            '/api/v1/board/cards': { cards: [card] }
        };
        return route.fulfill({ json: payloads[path] || {} });
    });
    await page.goto('/?view=board', { waitUntil: 'domcontentloaded' });
    await expect(page.locator('#app-content [data-view="board"]')).toBeVisible();
}

test('board heading matches Settings and description images survive editing and save', async ({ page }) => {
    await openBoard(page);
    const heading = page.getByRole('heading', { name: 'Vibe Board', exact: true });
    await expect(heading).toBeVisible();
    const styles = await heading.evaluate(element => {
        const settings = document.getElementById('settings-template').content.cloneNode(true);
        const reference = settings.querySelector('h4');
        document.body.append(settings);
        const read = node => {
            const style = getComputedStyle(node);
            return [style.fontSize, style.fontWeight, style.letterSpacing, style.textTransform, style.textAlign];
        };
        const result = { actual: read(element), settings: read(reference) };
        reference.closest('[data-view="settings"]').remove();
        return result;
    });
    expect(styles.actual).toEqual(styles.settings);
    await page.getByText('Description images', { exact: true }).click();
    await expect(page.locator('[data-board-open-session="session_test"]')).toBeVisible();
    await expect(page.locator('[data-board-dump-session]')).toHaveCount(0);
    const description = page.locator('[data-board-composer="description"]');
    const preview = description.locator('[data-board-composer-preview]');
    const input = description.locator('textarea');
    await expect(preview.locator('img.board-image')).toBeVisible();
    await expect.poll(() => preview.locator('img').evaluate(image => image.complete && image.naturalWidth > 0)).toBe(true);
    await expect(page.locator('[data-board-comments] img.board-image')).toBeVisible();
    await expect(input).toBeHidden();
    await expect(preview).toContainText('<img src=x onerror=');
    expect(await page.evaluate(() => window.__injected)).toBeUndefined();

    await description.getByRole('button', { name: 'Edit description' }).click();
    await expect(input).toHaveValue(DESCRIPTION);
    await input.fill(`${DESCRIPTION}\nEdited context`);
    await description.getByRole('button', { name: 'Preview description' }).click();
    await expect(preview).toContainText('Edited context');
    await expect(preview.locator('img.board-image')).toBeVisible();
    await page.locator('[data-board-save-card]').click();
    await expect(page.locator('[data-board-card-editor]')).toHaveCount(0);
    await page.getByText('Description images', { exact: true }).click();
    await expect(preview).toContainText('Edited context');
    await expect(preview.locator('img.board-image')).toBeVisible();
});

test('description previews newly uploaded images and new cards start in edit mode', async ({ page }) => {
    await openBoard(page);
    await page.getByText('Description images', { exact: true }).click();
    const description = page.locator('[data-board-composer="description"]');
    await description.getByRole('button', { name: 'Edit description' }).click();
    await description.locator('input[type="file"]').setInputFiles({
        name: 'pasted.png', mimeType: 'image/png', buffer: Buffer.from(IMAGE.split(',')[1], 'base64')
    });
    await expect(description.locator('textarea')).toHaveValue(/attachment:att_uploaded/);
    await description.getByRole('button', { name: 'Preview description' }).click();
    await expect(description.locator('[data-board-composer-preview] img')).toHaveCount(2);
    await expect(description.locator('[data-board-image="att_uploaded"]')).toBeVisible();
    await page.locator('[data-board-save-card]').click();
    await page.getByRole('button', { name: 'New card', exact: true }).click();
    await expect(description.locator('textarea')).toBeVisible();
    await expect(description.locator('textarea')).toHaveValue('');
    await expect(description.locator('[data-board-composer-preview]')).toBeHidden();
});

test('a running agent disables Start work while keeping Save and the session available', async ({ page }) => {
    await openBoard(page, { active: true });
    await page.getByText('Description images', { exact: true }).click();
    await expect(page.getByRole('button', { name: 'Agent running', exact: true })).toBeDisabled();
    await expect(page.locator('[data-board-save-card]')).toBeEnabled();
    await expect(page.locator('[data-board-open-session="session_test"]')).toBeEnabled();
});

for (const width of [1440, 520]) {
    test(`card images fit the editor at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 900 });
        await openBoard(page);
        await page.screenshot({ path: testInfo.outputPath('board.png') });
        await page.getByText('Description images', { exact: true }).click();
        const editor = page.locator('[data-board-card-editor]');
        await expect(editor.locator('[data-board-composer-preview] img')).toBeVisible();
        const layout = await editor.evaluate(element => ({
            width: element.clientWidth, scrollWidth: element.scrollWidth,
            right: element.getBoundingClientRect().right, viewport: innerWidth
        }));
        expect(layout.scrollWidth).toBeLessThanOrEqual(layout.width + 1);
        expect(layout.right).toBeLessThanOrEqual(layout.viewport);
        if (width < 860) {
            const stack = await editor.evaluate(element => ({
                composerBottom: element.querySelector('[data-board-composer="comment"]').getBoundingClientRect().bottom,
                fieldsTop: element.querySelector('.board-editor-side').getBoundingClientRect().top
            }));
            expect(stack.fieldsTop).toBeGreaterThanOrEqual(stack.composerBottom);
        }
        await expect(page.locator('[data-board-save-card]')).toBeInViewport();
        await page.screenshot({ path: testInfo.outputPath('card.png') });
    });
}
