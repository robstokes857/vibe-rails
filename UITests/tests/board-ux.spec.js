const { test, expect } = process.env.VIBERAILS_BOARD_STATIC === '1'
    ? require('@playwright/test')
    : require('./fixtures');

const IMAGE = 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a5XcAAAAASUVORK5CYII=';
const DESCRIPTION = 'Repro screenshot\n![Screenshot.png](attachment:att_image)\n<img src=x onerror="window.__injected=true">';

async function openBoard(page, { active = false, assignee = null, relatedCards = false,
    columns = [{ id: 'col_ready', name: 'Ready', position: 0, color: '#3b82f6', boardId: 'brd_main' }] } = {}) {
    if (process.env.VIBERAILS_BOARD_STATIC === '1') {
        await page.addInitScript(() => sessionStorage.setItem('viberails_tab', 'board-fixture'));
    }
    let card = {
        id: 'card_test', key: 'VB-1', columnId: 'col_ready', position: 0,
        title: 'Description images', description: DESCRIPTION, type: 'feature', priority: 'high',
        assignee, points: null, tags: [], blocked: false, flagged: false, commentCount: 1,
        activeSessionId: active ? 'session_test' : null,
        createdAt: '2026-09-11T06:00:00Z', updatedAt: '2026-09-11T06:00:00Z',
        attachments: [{ id: 'att_image', name: 'Screenshot.png', url: IMAGE, mimeType: 'image/png', bytes: 68 }],
        comments: [{ id: 'comment_1', author: { kind: 'user', label: 'You' },
            body: '![Screenshot.png](attachment:att_image)', createdAt: '2026-09-11T06:00:00Z' }],
        commits: [], sessions: [{ id: 'session_test', tabId: active ? 'agent_tab' : null, displayName: 'Codex session', cli: 'codex',
            active, createdAt: '2026-09-11T06:00:00Z' }]
    };
    const requests = [];
    const contents = new Map();
    const related = relatedCards ? [{
        ...card, id: 'card_related', key: 'VB-2', title: 'Related work', boardId: 'brd_sprint', columnId: 'col_build',
        boardName: 'Sprint 2', columnName: 'Build', comments: [], attachments: [], sessions: [], linkedCards: []
    }, {
        ...card, id: 'card_markup', key: 'VB-3', title: '<img src=x onerror="window.__linkXss=1">',
        boardId: 'brd_sprint', columnId: 'col_build', boardName: 'Sprint 2', columnName: 'Build', linkedCards: []
    }] : [];
    const linkedIds = new Set();
    const linkSummary = item => ({ id: item.id, key: item.key, title: item.title,
        boardId: item.boardId || 'brd_main', boardName: item.boardName || 'Main',
        columnId: item.columnId, columnName: item.columnName || 'Ready' });
    await page.routeWebSocket('**/api/v1/events/ws*', () => {});
    await page.route('**/api/v1/**', async route => {
        const url = new URL(route.request().url());
        const path = url.pathname;
        requests.push({ path, method: route.request().method(), body: route.request().postDataJSON() });
        const linkPath = path.match(/^\/api\/v1\/board\/cards\/([^/]+)\/links(?:\/(.*))?$/);
        if (linkPath && relatedCards) {
            const [, source, action] = linkPath;
            if (action === 'candidates') {
                const query = (url.searchParams.get('q') || '').toLowerCase();
                const candidates = source === card.id ? related.filter(item => !linkedIds.has(item.id))
                    : (linkedIds.has(source) ? [] : [card]);
                return route.fulfill({ json: { cards: candidates.filter(item => `${item.key} ${item.title}`.toLowerCase().includes(query)).map(linkSummary) } });
            }
            if (route.request().method() === 'DELETE') {
                linkedIds.delete(source === card.id ? action : source);
                return route.fulfill({ json: { ok: true } });
            }
            const targetId = route.request().postDataJSON().card;
            linkedIds.add(source === card.id ? targetId : source);
            return route.fulfill({ json: linkSummary(targetId === card.id ? card : related.find(item => item.id === targetId)) });
        }
        const relatedCard = related.find(item => path === `/api/v1/board/cards/${item.id}`);
        if (relatedCard) {
            if (route.request().method() === 'PUT') Object.assign(relatedCard, route.request().postDataJSON());
            return route.fulfill({ json: { ...relatedCard, linkedCards: linkedIds.has(relatedCard.id) ? [linkSummary(card)] : [] } });
        }
        if (path === '/api/v1/board/cards' && route.request().method() === 'POST') {
            card = { ...card, ...route.request().postDataJSON(), id: 'card_created', key: 'VB-2', attachments: [], comments: [], sessions: [] };
            return route.fulfill({ json: { ...card, linkedCards: related.filter(item => linkedIds.has(item.id)).map(linkSummary) } });
        }
        if (path === `/api/v1/board/cards/${card.id}`) {
            if (route.request().method() === 'PUT') {
                const patch = route.request().postDataJSON();
                card = { ...card, ...patch };
            }
            return route.fulfill({ json: { ...card, linkedCards: related.filter(item => linkedIds.has(item.id)).map(linkSummary) } });
        }
        if (path === `/api/v1/board/cards/${card.id}/attachments`) {
            const upload = route.request().postDataJSON();
            const attachment = { id: 'att_uploaded', name: upload.name, url: upload.mimeType?.startsWith('image/') ? upload.dataUrl : '', mimeType: upload.mimeType, bytes: upload.bytes };
            contents.set(attachment.id, Buffer.from(upload.dataUrl.split(',')[1], 'base64'));
            card.attachments.push(attachment);
            return route.fulfill({ json: attachment });
        }
        const deletedAttachment = path.match(/^\/api\/v1\/board\/cards\/[^/]+\/attachments\/([^/]+)$/);
        if (deletedAttachment && route.request().method() === 'DELETE') {
            card.attachments = card.attachments.filter(item => item.id !== deletedAttachment[1]);
            contents.delete(deletedAttachment[1]);
            return route.fulfill({ json: { ok: true } });
        }
        if (path.endsWith('/attachments/att_uploaded/content')) return route.fulfill({ contentType: 'application/octet-stream', body: contents.get('att_uploaded') });
        if (path === '/api/v1/board/files') {
            // The composer's @ typeahead: a tiny repo index, filtered like the server does.
            const query = (url.searchParams.get('q') || '').toLowerCase();
            const files = ['AGENTS.md', 'VibeRails/Services/Board/BoardService.cs', 'docs/with space.md', '<img src=x onerror="window.__fileXss=1">.md']
                .filter(file => file.toLowerCase().includes(query));
            return route.fulfill({ json: { files, truncated: false } });
        }
        if (path.endsWith('/launch')) return route.fulfill({ json: {
            tabId: 'board_background', cardKey: card.key, selection: route.request().postDataJSON().selection || card.assignee
        } });
        const payloads = {
            '/api/v1/context': { isInGit: true, rootPath: 'C:/board-fixture', launchDirectory: 'C:/board-fixture' },
            '/api/v1/settings': {},
            '/api/v1/environments': { environments: [{ id: 7, name: 'Card review', cli: 'claude' }] },
            '/api/v1/llm-picker/preferences': { items: [
                { key: 'base:codex', kind: 'base', group: 'Base CLIs', label: 'Codex', cli: 'codex', enabled: true, order: 0 },
                { key: 'base:claude', kind: 'base', group: 'Base CLIs', label: 'Claude', cli: 'claude', enabled: true, order: 1 },
                { key: 'env:7:claude', kind: 'environment', group: 'Custom Environments', label: 'Card review (claude)',
                    cli: 'claude', environmentId: 7, enabled: true, order: 2 }
            ] },
            '/api/v1/board/boards': { boards: [{ id: 'brd_main', name: 'Main', position: 0, cardCount: 1,
                columns },
                ...(relatedCards ? [{ id: 'brd_sprint', name: 'Sprint 2', position: 1, cardCount: 2,
                    columns: [{ id: 'col_build', name: 'Build', position: 0, color: '#06b6d4', boardId: 'brd_sprint' }] }] : [])] },
            '/api/v1/board/columns': { columns },
            '/api/v1/board/cards': { cards: [card] }
        };
        return route.fulfill({ json: payloads[path] || {} });
    });
    await page.goto('/?view=board', { waitUntil: 'domcontentloaded' });
    await expect(page.locator('#app-content [data-view="board"]')).toBeVisible();
    return requests;
}

test('description images survive editing and save', async ({ page }) => {
    await openBoard(page);
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

for (const width of [1440, 900, 390]) {
    test(`compact board controls and New card work at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 900 });
        const columns = ['Ready', 'In Progress', 'Review', 'Done'].map((name, position) => ({
            id: position ? `col_${position}` : 'col_ready', name, position, boardId: 'brd_main',
            color: ['#64748b', '#06b6d4', '#a855f7', '#10b981'][position]
        }));
        const requests = await openBoard(page, { columns });
        await expect(page.getByRole('heading', { name: 'Vibe Board', exact: true })).toBeVisible();
        await expect(page.locator('[data-board-select] option:checked')).toHaveText('Main');
        await expect(page.locator('[data-quick-add]')).toHaveCount(0);
        await expect(page.locator('.board-lane input')).toHaveCount(0);
        await expect(page.locator('[data-board-stats]')).toContainText('1 card');
        const tools = page.locator('.board-tools');
        const bounds = await tools.boundingBox();
        if (width >= 900) expect(bounds.height).toBeLessThan(120);
        expect(bounds.x + bounds.width).toBeLessThanOrEqual(width);
        expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(width);
        for (const control of await tools.locator('button:visible, input, select').all()) {
            const box = await control.boundingBox();
            expect(box.x).toBeGreaterThanOrEqual(0);
            expect(box.x + box.width).toBeLessThanOrEqual(width);
        }
        await page.screenshot({ path: testInfo.outputPath('board-header.png') });
        await page.getByRole('button', { name: 'Board settings', exact: true }).click();
        await expect(page.locator('[data-board-board-editor]')).toBeVisible();
        await page.locator('#modal-container [data-action="close-modal"]').first().click();
        await page.getByRole('button', { name: 'New card', exact: true }).click();
        await page.locator('#board-card-title').fill('A card in Review');
        await page.locator('#board-card-lane').selectOption('col_2');
        await page.locator('[data-board-save-card]').click();
        await expect(page.locator('[data-board-card-editor]')).toHaveCount(0);
        expect(requests.find(request => request.path === '/api/v1/board/cards' && request.method === 'POST').body.columnId).toBe('col_2');
    });
}

for (const width of [1440, 390]) {
    test(`uploaded images and files have visible deletion that preserves drafts at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 900 });
        const requests = await openBoard(page);
        await page.getByText('Description images', { exact: true }).click();
        const editor = page.locator('[data-board-card-editor]');
        const description = editor.locator('[data-board-composer="description"]');
        const comment = editor.locator('[data-board-composer="comment"] textarea');
        await editor.locator('[data-board-files]').setInputFiles({ name: 'notes.txt', mimeType: 'text/plain', buffer: Buffer.from('Keep this text') });
        await expect(editor.getByRole('button', { name: 'Delete notes.txt' })).toHaveCSS('opacity', '1');
        const remove = editor.getByRole('button', { name: 'Delete Screenshot.png' });
        await expect(remove).toHaveCSS('opacity', '1');
        await remove.scrollIntoViewIfNeeded();
        await page.screenshot({ path: testInfo.outputPath('attachment-delete.png') });
        await remove.click();
        await page.getByRole('alertdialog').getByRole('button', { name: 'Cancel' }).click();
        expect(requests.filter(request => request.method === 'DELETE')).toHaveLength(0);
        await editor.locator('#board-card-title').fill('Unsaved title');
        await description.getByRole('button', { name: 'Edit description' }).click();
        await description.locator('textarea').fill(`${DESCRIPTION}\nUnsaved description`);
        await comment.fill('Unsaved comment');
        await remove.click();
        await page.getByRole('alertdialog').getByRole('button', { name: 'Delete', exact: true }).click();
        await expect(remove).toHaveCount(0);
        await expect(editor.locator('[data-board-count="attachments"]')).toHaveText('1');
        await expect(editor.locator('#board-card-title')).toHaveValue('Unsaved title');
        await expect(description.locator('textarea')).toHaveValue(`${DESCRIPTION}\nUnsaved description`);
        await expect(comment).toHaveValue('Unsaved comment');
        await expect(editor.locator('img.board-image')).toHaveCount(0);
        await editor.getByRole('button', { name: 'Delete notes.txt' }).click();
        await page.getByRole('alertdialog').getByRole('button', { name: 'Delete', exact: true }).click();
        await expect(editor.locator('[data-board-attachments]')).toHaveText('No files attached.');
        await editor.locator('[data-board-save-card]').click();
        await page.getByText('Unsaved title', { exact: true }).click();
        await expect(page.locator('[data-board-count="attachments"]')).toHaveText('0');
        await expect(page.locator('img.board-image')).toHaveCount(0);
        expect(requests.filter(request => request.method === 'DELETE')).toHaveLength(2);
    });
}

test('attachment deletion blocks overlapping saves and keeps the file on failure for retry', async ({ page }) => {
    const requests = await openBoard(page);
    await page.getByText('Description images', { exact: true }).click();
    let finish;
    await page.route('**/api/v1/board/cards/card_test/attachments/att_image', async route => {
        await new Promise(resolve => { finish = resolve; });
        await route.fulfill({ status: 500, json: { error: 'Delete failed. Try again.' } });
    });
    const remove = page.getByRole('button', { name: 'Delete Screenshot.png' });
    await remove.click();
    await page.getByRole('alertdialog').getByRole('button', { name: 'Delete', exact: true }).click();
    await expect.poll(() => typeof finish).toBe('function');
    await expect(remove).toBeDisabled();
    await page.locator('[data-board-save-card]').click();
    expect(requests.filter(request => request.method === 'PUT')).toHaveLength(0);
    finish();
    await expect(remove).toBeEnabled();
    await expect(page.locator('[data-board-count="attachments"]')).toHaveText('1');
    await expect(page.locator('[data-board-composer-preview] img.board-image')).toHaveCount(1);
    await page.unroute('**/api/v1/board/cards/card_test/attachments/att_image');
    await remove.click();
    await page.getByRole('alertdialog').getByRole('button', { name: 'Delete', exact: true }).click();
    await expect(remove).toHaveCount(0);
});

test('board context settings persist default and per-type choices and preserve a conflicted draft', async ({ page }) => {
    await openBoard(page);
    let saved = { revision: 0, context: { defaultMessage: '', typeOverrides: [] } };
    let conflict = false;
    await page.route('**/api/v1/board/boards/brd_main/context', async route => {
        if (route.request().method() === 'PUT') {
            if (conflict) return route.fulfill({ status: 409, json: { error: 'Board context changed while you were editing.' } });
            const body = route.request().postDataJSON();
            expect(body.expectedRevision).toBe(saved.revision);
            saved = { revision: saved.revision + 1, context: body.context };
        }
        return route.fulfill({ json: saved });
    });
    await page.getByRole('button', { name: 'Board settings', exact: true }).click();
    await page.getByLabel('Default message', { exact: true }).fill('Read project conventions. <img src=x onerror="window.__contextXss=1">');
    await page.locator('[data-board-context] summary').filter({ hasText: /^Bug$/ }).click();
    await page.getByLabel('Bug context', { exact: true }).selectOption('append');
    await page.getByLabel('Bug message', { exact: true }).fill('Reproduce before changing code.');
    await page.getByRole('button', { name: 'Save context', exact: true }).click();
    await expect.poll(() => saved.revision).toBe(1);
    expect(saved.context.typeOverrides.find(item => item.type === 'bug')).toEqual({ type: 'bug', mode: 'append', message: 'Reproduce before changing code.' });
    await page.locator('#modal-container [data-action="close-modal"]').first().click();
    await page.getByRole('button', { name: 'Board settings', exact: true }).click();
    await expect(page.getByLabel('Default message', { exact: true })).toHaveValue(saved.context.defaultMessage);
    await page.locator('[data-board-context] summary').filter({ hasText: /^Bug$/ }).click();
    await expect(page.getByLabel('Bug context', { exact: true })).toHaveValue('append');
    expect(await page.evaluate(() => window.__contextXss)).toBeUndefined();
    await page.screenshot({ path: '../.codex-test-artifacts/vb16-context.png' });
    conflict = true;
    await page.getByLabel('Default message', { exact: true }).fill('Keep my draft');
    await page.getByRole('button', { name: 'Save context', exact: true }).click();
    await expect(page.getByText('Board context changed while you were editing.', { exact: true })).toBeVisible();
    await expect(page.getByLabel('Default message', { exact: true })).toHaveValue('Keep my draft');
});

test('lane automations select multiple jobs, persist, remove one and clear all', async ({ page }) => {
    await openBoard(page);
    let saved = { jobIds: [], revision: 0 };
    await page.route('**/api/v1/board/columns/col_ready/automation', async route => {
        if (route.request().method() === 'PUT') {
            const body = route.request().postDataJSON();
            expect(body.expectedRevision).toBe(saved.revision);
            saved = { jobIds: body.jobIds, revision: saved.revision + 1 };
        }
        return route.fulfill({ json: { ...saved, jobs: [{ id: 12, name: 'Run review', enabled: true }, { id: 13, name: 'Paused', enabled: false }, { id: 14, name: 'Run checks', enabled: true }] } });
    });
    await page.getByRole('button', { name: 'Settings for Ready', exact: true }).click();
    await expect(page.getByText(/stays for 60 seconds/)).toBeVisible();
    await page.getByRole('checkbox', { name: 'Run review', exact: true }).check();
    await page.getByRole('checkbox', { name: 'Run checks', exact: true }).check();
    await expect(page.getByRole('checkbox', { name: 'Paused (disabled)', exact: true })).toBeDisabled();
    await page.getByRole('button', { name: 'Save automations', exact: true }).click();
    await expect.poll(() => saved).toEqual({ jobIds: [12, 14], revision: 1 });
    await page.screenshot({ path: '../.codex-test-artifacts/vb22-lane.png' });
    await page.locator('#modal-container [data-action="close-modal"]').first().click();
    await page.getByRole('button', { name: 'Settings for Ready', exact: true }).click();
    await expect(page.getByRole('checkbox', { name: 'Run review', exact: true })).toBeChecked();
    await expect(page.getByRole('checkbox', { name: 'Run checks', exact: true })).toBeChecked();
    await page.getByRole('checkbox', { name: 'Run review', exact: true }).uncheck();
    await page.getByRole('button', { name: 'Save automations', exact: true }).click();
    await expect.poll(() => saved).toEqual({ jobIds: [14], revision: 2 });
    await page.getByRole('checkbox', { name: 'Run checks', exact: true }).uncheck();
    await page.getByRole('button', { name: 'Save automations', exact: true }).click();
    await expect.poll(() => saved).toEqual({ jobIds: [], revision: 3 });
});

test('lane automation conflicts preserve selections and unavailable jobs can be removed', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await openBoard(page);
    let payload;
    await page.route('**/api/v1/board/columns/col_ready/automation', route => {
        if (route.request().method() === 'PUT') {
            payload = route.request().postDataJSON();
            return route.fulfill({ status: 409, json: { error: 'Lane automation changed while you were editing.' } });
        }
        return route.fulfill({ json: { jobIds: [12, 13, 99], revision: 7, jobs: [
            { id: 12, name: '<img src=x onerror="window.__laneXss=1"> Review', enabled: true },
            { id: 13, name: 'Paused', enabled: false }, { id: 14, name: 'Checks', enabled: true }
        ] } });
    });
    await page.getByRole('button', { name: 'Settings for Ready', exact: true }).click();
    await page.getByRole('checkbox', { name: 'Paused (disabled)', exact: true }).uncheck();
    await page.getByRole('checkbox', { name: 'Unavailable Automation (99) (disabled)', exact: true }).uncheck();
    await page.getByRole('checkbox', { name: 'Checks', exact: true }).check();
    await page.getByRole('button', { name: 'Save automations', exact: true }).click();
    await expect(page.getByText('Lane automation changed while you were editing.', { exact: true })).toBeVisible();
    expect(payload).toEqual({ jobIds: [12, 14], expectedRevision: 7 });
    await expect(page.getByRole('checkbox', { name: 'Checks', exact: true })).toBeChecked();
    await expect(page.getByRole('checkbox', { name: 'Paused (disabled)', exact: true })).not.toBeChecked();
    expect(await page.evaluate(() => window.__laneXss)).toBeUndefined();
    await page.screenshot({ path: '../.codex-test-artifacts/vb22-lane-narrow.png' });
});

test('Chat defaults to the assignee, saves first and focuses the returned tab', async ({ page }) => {
    const requests = await openBoard(page, { assignee: 'base:codex' });
    await page.evaluate(() => {
        window.__chatTabs = [];
        window.app.terminalController.adoptLaunchedTab = async id => { window.__chatTabs.push(id); return true; };
    });
    await page.getByText('Description images', { exact: true }).click();
    await expect(page.locator('[data-board-chat-agent]')).toHaveValue('base:codex');
    await page.locator('#board-card-title').fill('Discuss this card');
    await page.getByRole('button', { name: 'Chat with:', exact: true }).click();
    await expect.poll(() => page.evaluate(() => window.__chatTabs)).toEqual(['board_background']);
    const writes = requests.filter(item => item.method === 'PUT' || item.path.endsWith('/launch'));
    expect(writes[0].body.title).toBe('Discuss this card');
    expect(writes[1].body).toEqual({ selection: 'base:codex', intent: 'chat' });
    await expect(page.locator('[data-board-card-editor]')).toHaveCount(0);
});

for (const [assignee, selection] of [
    ['base:codex', 'base:claude'],
    ['base:codex', 'env:7:claude'],
    [null, 'env:7:claude']
]) {
    test(`Chat can select ${selection} without changing ${assignee || 'an unassigned card'}`, async ({ page }, testInfo) => {
        const requests = await openBoard(page, { assignee });
        await page.evaluate(() => {
            window.__chatTabs = [];
            window.app.terminalController.adoptLaunchedTab = async id => { window.__chatTabs.push(id); return true; };
        });
        await page.getByText('Description images', { exact: true }).click();
        await expect(page.locator('[data-board-chat-agent]')).toHaveValue(assignee || 'base:codex');
        await page.locator('.board-chat-controls .ts-control').click();
        await page.locator(`.ts-dropdown:visible [data-value="${selection}"]`).click();
        await expect(page.locator('[data-board-chat-agent]')).toHaveValue(selection);
        await expect(page.locator('#board-card-assignee')).toHaveValue(assignee || '');
        await page.screenshot({ path: testInfo.outputPath('board-chat-picker.png') });
        await page.getByRole('button', { name: 'Chat with:', exact: true }).click();
        await expect.poll(() => page.evaluate(() => window.__chatTabs)).toEqual(['board_background']);

        const writes = requests.filter(item => item.method === 'PUT' || item.path.endsWith('/launch'));
        expect(writes[0].body.assignee).toBe(assignee || '');
        expect(writes[1].body).toEqual({ selection, intent: 'chat' });
        await expect(page.locator('[data-board-card-editor]')).toHaveCount(0);
    });
}

test('closing and reopening the card disposes the chat picker and resets its default', async ({ page }) => {
    await openBoard(page, { assignee: 'base:codex' });
    for (let attempt = 0; attempt < 2; attempt++) {
        await page.getByText('Description images', { exact: true }).click();
        await expect(page.locator('[data-board-chat-agent]')).toHaveValue('base:codex');
        await page.locator('.board-chat-controls .ts-control').click();
        await page.locator('.ts-dropdown:visible [data-value="base:claude"]').click();
        await page.locator('.board-chat-controls .ts-control').click();
        await expect(page.locator('.ts-dropdown:visible')).toHaveCount(1);
        await page.locator('#modal-container [data-action="close-modal"]').first().click();
        await expect(page.locator('.ts-dropdown:visible')).toHaveCount(0);
        expect(await page.evaluate(() => Array.from(window.app.llmPickerController.mountedPickers)
            .filter(record => record.selectEl.matches('[data-board-chat-agent], #board-card-assignee')).length)).toBe(0);
    }
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

test('a running agent exposes Go to agent while keeping Save and the session available', async ({ page }) => {
    const requests = await openBoard(page, { active: true });
    await page.evaluate(() => {
        window.__agentTabs = [];
        window.app.terminalController.adoptLaunchedTab = async id => { window.__agentTabs.push(id); return true; };
    });
    await page.getByText('Description images', { exact: true }).click();
    await expect(page.getByRole('button', { name: 'Go to agent', exact: true })).toBeEnabled();
    await expect(page.getByRole('button', { name: 'Chat with:', exact: true })).toBeDisabled();
    await expect(page.locator('[data-board-save-card]')).toBeEnabled();
    await expect(page.locator('[data-board-open-session="session_test"]')).toBeEnabled();
    await page.getByRole('button', { name: 'Go to agent', exact: true }).click();
    await expect(page.locator('[data-board-card-editor]')).toHaveCount(0);
    expect(await page.evaluate(() => window.__agentTabs)).toEqual(['agent_tab']);
    expect(requests.filter(request => request.method === 'PUT' || request.path.endsWith('/launch'))).toHaveLength(0);
});

test('Start work preserves the board and stores base launch options including YOLO', async ({ page }) => {
    const requests = await openBoard(page, { assignee: 'base:codex' });
    await page.getByText('Description images', { exact: true }).click();
    await expect(page.locator('.board-card-modal-dialog .modal-title')).toHaveText('VB-1 · Description images');
    await page.locator('[data-board-launch-model]').selectOption('gpt-6-astra');
    await page.locator('[data-board-launch-effort]').selectOption('high');
    await expect(page.locator('[data-board-launch-yolo]')).not.toBeChecked();
    await page.locator('[data-board-launch-yolo]').check();
    // Codex exposes no Start mode: its /plan is a TUI command, and nothing types into a TUI.
    await expect(page.locator('[data-board-launch-mode]')).toHaveCount(0);
    await page.locator('[data-board-start-work]').click();
    await expect(page.locator('[data-board-card-editor]')).toHaveCount(0);
    await expect(page.locator('#app-content [data-view="board"]')).toBeVisible();
    expect(requests.find(request => request.method === 'PUT' && request.path.endsWith('/card_test')).body.baseLlmOptions)
        .toEqual({ model: 'gpt-6-astra', effort: 'high', mode: '', yolo: true });
    expect(requests.filter(request => request.path.endsWith('/launch'))).toHaveLength(1);
    await page.getByText('Description images', { exact: true }).click();
    await expect(page.locator('[data-board-launch-effort]')).toHaveValue('high');
    await expect(page.locator('[data-board-launch-yolo]')).toBeChecked();
});

test('work and discussion actions remain reachable on a narrow screen', async ({ page }, testInfo) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await openBoard(page, { assignee: 'base:codex' });
    await page.getByText('Description images', { exact: true }).click();
    for (const name of ['Chat with:', 'Start work', 'Save']) {
        const button = page.locator('.board-editor-actions-main').getByRole('button', { name, exact: true });
        await expect(button).toBeVisible();
        const bounds = await button.boundingBox();
        expect(bounds.x).toBeGreaterThanOrEqual(0);
        expect(bounds.x + bounds.width).toBeLessThanOrEqual(390);
        expect(bounds.y + bounds.height).toBeLessThanOrEqual(844);
    }
    await page.screenshot({ path: testInfo.outputPath('board-chat-narrow-closed.png') });
    await page.locator('.board-chat-controls .ts-control').click();
    const dropdown = page.locator('.ts-dropdown:visible');
    await expect(dropdown.locator('[data-value="env:7:claude"]')).toBeVisible();
    const bounds = await dropdown.boundingBox();
    expect(bounds.x).toBeGreaterThanOrEqual(0);
    expect(bounds.x + bounds.width).toBeLessThanOrEqual(390);
    expect(bounds.y).toBeGreaterThanOrEqual(0);
    expect(bounds.y + bounds.height).toBeLessThanOrEqual(844);
    await page.screenshot({ path: testInfo.outputPath('board-chat-narrow.png') });
});

test('attention flags persist, paint a red card with a flag, and can be cleared', async ({ page }, testInfo) => {
    const requests = await openBoard(page);
    await page.locator('[data-card-id="card_test"]').click();
    await expect(page.locator('[data-board-history-details]')).toHaveCount(0);
    await page.getByLabel('Needs your attention', { exact: true }).check();
    await page.locator('[data-board-save-card]').click();
    const card = page.locator('.board-card[data-card-id="card_test"]');
    await expect(card).toHaveClass(/is-flagged/);
    await expect(card.locator('.fa-flag')).toBeVisible();
    await expect(card).toHaveCSS('border-top-color', 'rgb(239, 68, 68)');
    expect(requests.some(request => request.method === 'PUT' && request.body?.flagged === true)).toBeTruthy();
    await page.screenshot({ path: testInfo.outputPath('flagged-board.png') });
    await card.click();
    await expect(page.getByLabel('Needs your attention', { exact: true })).toBeChecked();
    await expect(page.getByLabel('Blocked', { exact: true })).not.toBeChecked();
    await page.getByLabel('Needs your attention', { exact: true }).uncheck();
    await page.locator('[data-board-save-card]').click();
    await expect(card).not.toHaveClass(/is-flagged/);
    await expect(card.locator('.fa-flag')).toHaveCount(0);
    expect(requests.some(request => request.path.endsWith('/history'))).toBeFalsy();
});

test('a description edit saves without touching the running agent or keeping history', async ({ page }) => {
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
});

test('typing @ in a composer opens the file typeahead, the keyboard inserts a reference and Browse is always offered', async ({ page }) => {
    const requests = await openBoard(page);
    await page.getByText('Description images', { exact: true }).click();
    const input = page.locator('[data-board-composer="comment"] textarea');
    const popup = page.locator('[data-board-composer="comment"] [data-board-file-popup]');
    const fileRows = popup.locator('[data-board-file-row]:not([data-board-file-browse])');
    await input.click();
    await input.pressSequentially('see @Board');
    await expect(popup).toBeVisible();
    await expect(fileRows).toHaveCount(1);
    await expect(popup.locator('[data-board-file-browse]')).toContainText('Browse for a file');
    await expect(popup.locator('.is-active')).toContainText('BoardService.cs');
    await input.press('Enter');
    await expect(popup).toHaveCount(0);
    await expect(input).toHaveValue('see @VibeRails/Services/Board/BoardService.cs ');

    // A path with spaces is inserted quoted; Down reaches Browse, Up comes back, Tab inserts.
    await input.pressSequentially('and @with');
    await expect(fileRows).toHaveCount(1);
    await input.press('ArrowDown');
    await expect(popup.locator('.is-active')).toContainText('Browse for a file');
    await input.press('ArrowUp');
    await input.press('Tab');
    await expect(input).toHaveValue('see @VibeRails/Services/Board/BoardService.cs and @"docs/with space.md" ');
    await expect(input).toBeFocused();

    // A bare @ lists everything. File names render as text: a hostile name is no element.
    await input.pressSequentially('@');
    await expect(fileRows).toHaveCount(4);
    await expect(popup.locator('img')).toHaveCount(0);
    expect(await page.evaluate(() => window.__fileXss)).toBeUndefined();
    // Escape closes the popup and leaves the card open; typing a space ends the token.
    await input.press('Escape');
    await expect(popup).toHaveCount(0);
    await expect(page.locator('[data-board-card-editor]')).toBeVisible();
    await input.pressSequentially('x ');
    await expect(popup).toHaveCount(0);
    expect(requests.filter(request => request.path.startsWith('/api/v1/board/cards') && request.method !== 'GET')).toHaveLength(0);
});

test('new cards queue files until Save and do not launch', async ({ page }) => {
    const requests = await openBoard(page);
    await page.getByRole('button', { name: 'New card', exact: true }).click();
    await expect(page.locator('[data-board-card-links]')).toContainText('Save the card to link other cards.');
    await page.locator('#board-card-title').fill('File first');
    await page.locator('[data-board-files]').setInputFiles({ name: 'notes.zip', mimeType: 'application/zip', buffer: Buffer.from('archive') });
    await expect(page.locator('[data-board-attachments]')).toContainText('Uploads when you save');
    const remove = page.getByRole('button', { name: 'Remove notes.zip' });
    await expect(remove).toHaveCSS('opacity', '1');
    await remove.click();
    await expect(page.locator('[data-board-count="attachments"]')).toHaveText('0');
    await page.locator('[data-board-files]').setInputFiles({ name: 'notes.zip', mimeType: 'application/zip', buffer: Buffer.from('archive') });
    await expect(page.locator('[data-board-count="attachments"]')).toHaveText('1');
    expect(requests.filter(request => request.path.endsWith('/attachments'))).toHaveLength(0);
    await page.locator('[data-board-save-card]').click();
    await expect(page.locator('[data-board-card-editor]')).toHaveCount(0);
    expect(requests.filter(request => request.path.endsWith('/attachments'))).toHaveLength(1);
    expect(requests.filter(request => request.path.endsWith('/launch'))).toHaveLength(0);
    await expect(page.locator('#app-content [data-view="board"]')).toBeVisible();
});

for (const width of [1440, 520]) {
    test(`cards link across boards, persist and unlink at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 900 });
        const requests = await openBoard(page, { relatedCards: true });
        await page.getByText('Description images', { exact: true }).click();
        await page.locator('[data-board-link-picker] > summary').click();
        await expect(page.locator('[data-board-link-card]')).toHaveCount(2);
        await expect(page.locator('[data-board-link-results] img')).toHaveCount(0);
        expect(await page.evaluate(() => window.__linkXss)).toBeUndefined();
        await page.locator('[data-board-link-search]').fill('VB-2');
        await expect(page.locator('[data-board-link-card]')).toHaveCount(1);
        await page.locator('[data-board-link-card="card_related"]').click();
        await expect(page.locator('[data-board-linked-cards]')).toContainText('VB-2 · Related work');
        await expect(page.locator('[data-board-linked-cards]')).toContainText('Sprint 2 · Build');
        await expect(page.locator('[data-board-count="linked-cards"]')).toHaveText('1');
        await expect(page.locator('[data-board-link-results] [data-board-link-card]')).toHaveCount(0);
        expect(requests.filter(request => request.method === 'PUT')).toHaveLength(0);
        await page.locator('[data-board-link-picker] > summary').click();
        const layout = await page.locator('[data-board-card-editor]').evaluate(element => ({
            width: element.clientWidth, scrollWidth: element.scrollWidth
        }));
        expect(layout.scrollWidth).toBeLessThanOrEqual(layout.width + 1);
        await page.screenshot({ path: testInfo.outputPath('linked-cards.png') });

        await page.locator('[data-board-save-card]').click();
        await page.getByText('Description images', { exact: true }).click();
        await expect(page.locator('[data-board-open-linked-card="card_related"]')).toBeVisible();
        await page.locator('[data-board-open-linked-card="card_related"]').click();
        await expect(page.locator('.board-card-modal-dialog .modal-title')).toHaveText('VB-2 · Related work');
        await expect(page.locator('[data-board-linked-cards]')).toContainText('VB-1 · Description images');
        await page.locator('[data-board-unlink-card="card_test"]').click();
        await expect(page.locator('[data-board-count="linked-cards"]')).toHaveText('0');
        await page.locator('[data-board-save-card]').click();
        await page.getByText('Description images', { exact: true }).click();
        await expect(page.locator('[data-board-linked-cards]')).toContainText('No cards linked.');
    });
}

test('link navigation protects draft edits and an unsuccessful link leaves them intact', async ({ page }) => {
    await openBoard(page, { relatedCards: true });
    await page.getByText('Description images', { exact: true }).click();
    await page.locator('#board-card-title').fill('Unsaved title');
    await page.locator('[data-board-link-picker] > summary').click();
    const failLink = route => route.fulfill({ status: 500, json: { error: 'Link unavailable' } });
    await page.route('**/cards/card_test/links', failLink);
    await page.locator('[data-board-link-card="card_related"]').click();
    await expect(page.locator('[data-board-count="linked-cards"]')).toHaveText('0');
    await expect(page.locator('#board-card-title')).toHaveValue('Unsaved title');
    await page.unroute('**/cards/card_test/links', failLink);
    await page.locator('[data-board-link-card="card_related"]').click();
    await expect(page.locator('[data-board-open-linked-card="card_related"]')).toBeVisible();
    await page.locator('[data-board-open-linked-card="card_related"]').click();
    await expect(page.getByRole('alertdialog')).toContainText('unsaved edits');
    await page.getByRole('alertdialog').getByRole('button', { name: 'Cancel' }).click();
    await expect(page.locator('#board-card-title')).toHaveValue('Unsaved title');
    await page.locator('[data-board-open-linked-card="card_related"]').click();
    await page.getByRole('alertdialog').getByRole('button', { name: 'Discard and open' }).click();
    await expect(page.locator('.board-card-modal-dialog .modal-title')).toHaveText('VB-2 · Related work');
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
