const { test, expect } = process.env.VIBERAILS_BOARD_STATIC === '1'
    ? require('@playwright/test')
    : require('./fixtures');

const IMAGE = 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a5XcAAAAASUVORK5CYII=';
const DESCRIPTION = 'Repro screenshot\n![Screenshot.png](attachment:att_image)\n<img src=x onerror="window.__injected=true">';

async function openBoard(page, { active = false, assignee = null } = {}) {
    if (process.env.VIBERAILS_BOARD_STATIC === '1') {
        await page.addInitScript(() => sessionStorage.setItem('viberails_tab', 'board-fixture'));
    }
    let card = {
        id: 'card_test', key: 'VB-1', columnId: 'col_ready', position: 0,
        title: 'Description images', description: DESCRIPTION, type: 'feature', priority: 'high',
        assignee, points: null, tags: [], blocked: false, commentCount: 1, descriptionRevision: 1,
        activeSessionId: active ? 'session_test' : null,
        createdAt: '2026-09-11T06:00:00Z', updatedAt: '2026-09-11T06:00:00Z',
        attachments: [{ id: 'att_image', name: 'Screenshot.png', url: IMAGE }],
        comments: [{ id: 'comment_1', author: { kind: 'user', label: 'You' },
            body: '![Screenshot.png](attachment:att_image)', createdAt: '2026-09-11T06:00:00Z' }],
        commits: [], sessions: [{ id: 'session_test', displayName: 'Codex session', cli: 'codex',
            active, createdAt: '2026-09-11T06:00:00Z' }]
    };
    const requests = [];
    const contents = new Map();
    await page.routeWebSocket('**/api/v1/events/ws*', () => {});
    await page.route('**/api/v1/**', async route => {
        const path = new URL(route.request().url()).pathname;
        requests.push({ path, method: route.request().method(), body: route.request().postDataJSON() });
        if (path === '/api/v1/board/cards' && route.request().method() === 'POST') {
            card = { ...card, ...route.request().postDataJSON(), id: 'card_created', key: 'VB-2', attachments: [], comments: [], sessions: [], descriptionRevision: 1 };
            return route.fulfill({ json: card });
        }
        if (path === `/api/v1/board/cards/${card.id}`) {
            if (route.request().method() === 'PUT') {
                const patch = route.request().postDataJSON();
                const changed = patch.description !== undefined && patch.description !== card.description;
                card = { ...card, ...patch, descriptionChanged: changed, descriptionRevision: card.descriptionRevision + (changed ? 1 : 0) };
            }
            return route.fulfill({ json: card });
        }
        if (path === `/api/v1/board/cards/${card.id}/attachments`) {
            const upload = route.request().postDataJSON();
            const attachment = { id: 'att_uploaded', name: upload.name, url: upload.mimeType?.startsWith('image/') ? upload.dataUrl : '', mimeType: upload.mimeType, bytes: upload.bytes };
            contents.set(attachment.id, Buffer.from(upload.dataUrl.split(',')[1], 'base64'));
            card.attachments.push(attachment);
            return route.fulfill({ json: attachment });
        }
        if (path.endsWith('/attachments/att_uploaded/content')) return route.fulfill({ contentType: 'application/octet-stream', body: contents.get('att_uploaded') });
        if (path.endsWith('/launch')) return route.fulfill({ json: { tabId: 'board_background', cardKey: card.key, selection: card.assignee } });
        if (path.endsWith('/history')) return route.fulfill({ json: { currentRevision: card.descriptionRevision, revisions: [{ revision: 1, description: DESCRIPTION, createdAt: card.createdAt, author: { label: 'You' }, sessions: [{ sessionId: 'session_test', kind: 'launch', status: 'recorded', createdAt: card.createdAt }] }] } });
        const payloads = {
            '/api/v1/context': { isInGit: true, rootPath: 'C:/board-fixture', launchDirectory: 'C:/board-fixture' },
            '/api/v1/settings': {},
            '/api/v1/environments': { environments: [] },
            '/api/v1/llm-picker/preferences': { items: [
                { key: 'base:codex', kind: 'base', group: 'Base CLIs', label: 'Codex', cli: 'codex', enabled: true, order: 0 },
                { key: 'base:claude', kind: 'base', group: 'Base CLIs', label: 'Claude', cli: 'claude', enabled: true, order: 1 }
            ] },
            '/api/v1/board/columns': { columns: [{ id: 'col_ready', name: 'Ready', position: 0, color: '#3b82f6' }] },
            '/api/v1/board/cards': { cards: [card] }
        };
        return route.fulfill({ json: payloads[path] || {} });
    });
    await page.goto('/?view=board', { waitUntil: 'domcontentloaded' });
    await expect(page.locator('#app-content [data-view="board"]')).toBeVisible();
    return requests;
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

test('card type renders, filters, edits and is sent on save', async ({ page }) => {
    const requests = await openBoard(page);
    await expect(page.locator('.board-card .board-type-chip')).toHaveText('Feature');

    await page.locator('[data-board-filter-type]').selectOption('bug');
    await expect(page.getByText('Description images', { exact: true })).toHaveCount(0);
    await page.locator('[data-board-filter-type]').selectOption('feature');
    await page.getByText('Description images', { exact: true }).click();
    await expect(page.locator('#board-card-type')).toHaveValue('feature');
    await page.locator('#board-card-type').selectOption('bug');
    await page.locator('[data-board-save-card]').click();

    const update = requests.find(request => request.method === 'PUT' && request.path.endsWith('/card_test'));
    expect(update.body.type).toBe('bug');
    await page.locator('[data-board-filter-type]').selectOption('');
    await expect(page.locator('.board-card .board-type-chip')).toHaveText('Bug');
});

test('long new-card descriptions grow inside the composer instead of painting over attachments', async ({ page }) => {
    await page.setViewportSize({ width: 960, height: 640 });
    await openBoard(page);
    await page.getByRole('button', { name: 'New card', exact: true }).click();
    const input = page.locator('[data-board-composer="description"] textarea');
    await input.fill(Array.from({ length: 24 }, (_, index) => `description line ${index + 1}`).join('\n'));

    const layout = await input.evaluate(element => {
        const composer = element.closest('[data-board-composer]');
        const attachments = element.closest('.board-editor-main').querySelectorAll('.board-block')[1];
        return {
            clientHeight: element.clientHeight,
            scrollHeight: element.scrollHeight,
            inputBottom: element.getBoundingClientRect().bottom,
            composerBottom: composer.getBoundingClientRect().bottom,
            attachmentsTop: attachments.getBoundingClientRect().top
        };
    });
    expect(layout.clientHeight).toBeGreaterThanOrEqual(layout.scrollHeight - 1);
    expect(layout.composerBottom).toBeGreaterThanOrEqual(layout.inputBottom);
    expect(layout.attachmentsTop).toBeGreaterThanOrEqual(layout.composerBottom);
});

test('a running agent disables Start work while keeping Save and the session available', async ({ page }) => {
    await openBoard(page, { active: true });
    await page.getByText('Description images', { exact: true }).click();
    await expect(page.getByRole('button', { name: 'Agent running', exact: true })).toBeDisabled();
    await expect(page.locator('[data-board-save-card]')).toBeEnabled();
    await expect(page.locator('[data-board-open-session="session_test"]')).toBeEnabled();
});

test('Start work preserves the board and stores model and effort', async ({ page }) => {
    const requests = await openBoard(page, { assignee: 'base:codex' });
    await page.getByText('Description images', { exact: true }).click();
    await expect(page.locator('.board-card-modal-dialog .modal-title')).toHaveText('VB-1 · Description images');
    await page.locator('[data-board-launch-model]').selectOption('gpt-6-astra');
    await page.locator('[data-board-launch-effort]').selectOption('high');
    // Codex exposes no Start mode: its /plan is a TUI command, and nothing types into a TUI.
    await expect(page.locator('[data-board-launch-mode]')).toHaveCount(0);
    await page.locator('[data-board-start-work]').click();
    await expect(page.locator('[data-board-card-editor]')).toHaveCount(0);
    await expect(page.locator('#app-content [data-view="board"]')).toBeVisible();
    expect(requests.find(request => request.method === 'PUT' && request.path.endsWith('/card_test')).body.baseLlmOptions)
        .toEqual({ model: 'gpt-6-astra', effort: 'high', mode: '' });
    expect(requests.filter(request => request.path.endsWith('/launch'))).toHaveLength(1);
    await page.getByText('Description images', { exact: true }).click();
    await expect(page.locator('[data-board-launch-effort]')).toHaveValue('high');
});

test('a description edit saves without touching the running agent, and history identifies the session', async ({ page }) => {
    const requests = await openBoard(page, { active: true });
    await page.getByText('Description images', { exact: true }).click();
    await page.getByRole('button', { name: 'Edit description' }).click();
    await page.locator('[data-board-composer="description"] textarea').fill('New scope');
    await page.locator('[data-board-save-card]').click();
    // Saving closes the editor and asks nothing: with a live session on the card there is still
    // no prompt and no terminal input, because the board has no way to type into an agent.
    await expect(page.locator('[data-board-card-editor]')).toHaveCount(0);
    await expect(page.getByRole('alertdialog')).toHaveCount(0);
    expect(requests.filter(request => request.path.endsWith('/notify'))).toHaveLength(0);

    await page.getByText('Description images', { exact: true }).click();
    await page.locator('[data-board-history-details] > summary').click();
    await page.locator('.board-history-revision > summary').click();
    await expect(page.locator('[data-board-history]')).toContainText('Codex session · launch');
    await expect(page.locator('[data-board-history]')).toContainText('Repro screenshot');
});

test('new cards queue files until Save and do not launch', async ({ page }) => {
    const requests = await openBoard(page);
    await page.getByRole('button', { name: 'New card', exact: true }).click();
    await page.locator('#board-card-title').fill('File first');
    await page.locator('[data-board-files]').setInputFiles({ name: 'notes.zip', mimeType: 'application/zip', buffer: Buffer.from('archive') });
    await expect(page.locator('[data-board-attachments]')).toContainText('Uploads when you save');
    expect(requests.filter(request => request.path.endsWith('/attachments'))).toHaveLength(0);
    await page.locator('[data-board-save-card]').click();
    await expect(page.locator('[data-board-card-editor]')).toHaveCount(0);
    expect(requests.filter(request => request.path.endsWith('/attachments'))).toHaveLength(1);
    expect(requests.filter(request => request.path.endsWith('/launch'))).toHaveLength(0);
    await expect(page.locator('#app-content [data-view="board"]')).toBeVisible();
});

test('Markdown previews as literal source without HTML execution or external images', async ({ page }) => {
    await openBoard(page);
    const external = [];
    await page.route('https://attacker.invalid/**', route => { external.push(route.request().url()); return route.abort(); });
    const source = '# CLI Options\n\n**Model settings**\n\n<img src=x onerror="window.__boardXss=1">\n\n'
        + '![remote](https://attacker.invalid/track)\n\n[bad](javascript:alert(1))';
    await page.getByText('Description images', { exact: true }).click();
    await page.locator('[data-board-files]').setInputFiles({ name: 'CLI_OPTIONS.MD', mimeType: 'text/markdown', buffer: Buffer.from(source) });
    await page.locator('[data-board-view-attachment="att_uploaded"]').click();
    const viewer = page.getByRole('dialog', { name: 'Attachment preview' });
    // Markdown shows its own source, so the syntax survives instead of becoming elements.
    // Nothing parses it, which is why the embedded HTML is inert rather than sanitized.
    await expect(viewer.locator('.vb-board-attachment-text')).toContainText('# CLI Options');
    await expect(viewer.locator('.vb-board-attachment-text')).toContainText('**Model settings**');
    await expect(viewer.locator('.vb-board-attachment-text')).toContainText('<img src=x onerror=');
    await expect(viewer.locator('[data-attachment-body]')
        .locator('h1,strong,img,iframe,script,a[href^="javascript:"]')).toHaveCount(0);
    expect(await page.evaluate(() => window.__boardXss)).toBeUndefined();
    expect(external).toEqual([]);
    await viewer.getByRole('button', { name: 'Close attachment' }).click();
    await expect(page.locator('[data-board-card-editor]')).toBeVisible();
});

function pdfFixture() {
    const stream = 'BT /F1 18 Tf 20 100 Td (Board attachment) Tj ET';
    const objects = [
        '<< /Type /Catalog /Pages 2 0 R /OpenAction << /S /JavaScript /JS (app.alert("unsafe")) >> >>',
        '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
        '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 180] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>',
        '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>',
        `<< /Length ${stream.length} >>\nstream\n${stream}\nendstream`
    ];
    let output = '%PDF-1.4\n';
    const offsets = [0];
    objects.forEach((object, index) => { offsets.push(Buffer.byteLength(output)); output += `${index + 1} 0 obj\n${object}\nendobj\n`; });
    const xref = Buffer.byteLength(output);
    output += `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n`;
    offsets.slice(1).forEach(offset => { output += `${String(offset).padStart(10, '0')} 00000 n \n`; });
    output += `trailer\n<< /Size ${objects.length + 1} /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`;
    return Buffer.from(output);
}

test('PDF previews render onto a canvas and do not execute document actions', async ({ page }) => {
    await openBoard(page);
    let dialogs = 0;
    page.on('dialog', dialog => { dialogs++; void dialog.dismiss(); });
    await page.getByText('Description images', { exact: true }).click();
    await page.locator('[data-board-files]').setInputFiles({ name: 'scope.PDF', mimeType: 'application/pdf', buffer: pdfFixture() });
    await page.locator('[data-board-view-attachment="att_uploaded"]').click();
    const viewer = page.getByRole('dialog', { name: 'Attachment preview' });
    await expect(viewer.locator('canvas')).toBeVisible();
    await expect(viewer.locator('[data-pdf-position]')).toHaveText('Page 1 of 1');
    await expect(viewer.locator('iframe,object,embed')).toHaveCount(0);
    expect(dialogs).toBe(0);
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
