const { test, expect } = process.env.VIBERAILS_BOARD_STATIC === '1'
    ? require('@playwright/test')
    : require('./fixtures');

const IMAGE = 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a5XcAAAAASUVORK5CYII=';
const DESCRIPTION = 'Repro screenshot\n![Screenshot.png](attachment:att_image)\n<img src=x onerror="window.__injected=true">';

for (const width of [1440, 390]) {
    test(`card Checks preserve scope, history and safe reports at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 950 });
        await openBoard(page);
        const check = { id: 'check-1', tool: 'Code quality', status: 'Skipped/not applicable', scope: 'working-tree',
            startedUtc: '2026-09-30T12:00:00Z', endedUtc: '2026-09-30T12:01:00Z', analyzedCount: 1, findingCount: 0, skippedCount: 2,
            baseCommit: 'a'.repeat(40), headCommit: 'b'.repeat(40), runId: 'run-1', toolVersion: '1',
            summary: '<img src=x onerror="window.__checksXss=1">', limitations: ['Unsupported files were skipped.'],
            scopeFiles: ['Modified: src/example.cs', 'Added: unknown.xyz'], snapshotHash: 'c'.repeat(64), rulesHash: 'd'.repeat(64),
            resultJson: JSON.stringify({ output: ['<script>window.__checksXss=1</script>'], details: { report: JSON.stringify({
                schemaVersion: '1.0', files: [{ file: 'src/example.cs', score: 5, rating: 'Healthy', categories: [] }], overview: [], scorecard: []
            }) } }) };
        let rows = [], runs = 0;
        await page.route('**/api/v1/board/cards/card_test/checks?*', route => route.fulfill({ json: {
            checks: rows, latest: rows, pending: [], hasMore: false,
            automations: [{ id: 8, name: 'Review checks', enabled: true, scopes: ['unpushed', 'unpushed'] }]
        } }));
        await page.route('**/api/v1/board/cards/card_test/checks/check-1*', route => route.fulfill({ json: { check, freshness: route.request().url().includes('verify=true') ? 'Stale: inputs changed' : 'Unknown freshness' } }));
        await page.route('**/api/v1/code-analyzer/graph', route => route.fulfill({ status: 500, json: { error: 'Map unavailable' } }));
        await page.route('**/api/v1/board/cards/card_test/automations', route => {
            if (route.request().method() === 'POST') { runs++; return route.fulfill({ json: { success: true } }); }
            return route.fulfill({ json: { jobs: [], runs: [] } });
        });
        await page.getByText('Description images', { exact: true }).click();
        const panel = page.locator('[data-board-checks]');
        await expect(panel.locator('[data-check-summary]')).toContainText('Not run');
        expect(runs).toBe(0);
        await panel.locator('[data-check-run-options] summary').click();
        await panel.locator('[data-check-automation]').selectOption('8');
        await expect(panel.locator('[data-check-scope]')).toContainText('unpushed');
        await panel.getByRole('button', { name: 'Run checks', exact: true }).click();
        await expect.poll(() => runs).toBe(1);
        rows = [check];
        await panel.getByRole('button', { name: 'Refresh checks', exact: true }).click();
        await expect(panel.locator('[data-check-summary]')).toContainText('2 files skipped');
        await panel.locator('[data-check-summary]').getByRole('button', { name: 'View report' }).click();
        const report = panel.locator('[data-check-report]');
        await expect(report).toContainText('Unknown freshness');
        await report.getByRole('button', { name: 'Compare inputs' }).click();
        await expect(report).toContainText('Stale: inputs changed');
        await expect(report).toContainText('Unsupported files were skipped');
        await expect(report.locator('.code-report')).toBeVisible();
        await expect(report).toContainText('example.cs');
        expect(await page.evaluate(() => window.__checksXss)).toBeUndefined();
        expect(await panel.evaluate(el => el.scrollWidth <= el.clientWidth + 1)).toBe(true);
        await report.scrollIntoViewIfNeeded();
        await page.screenshot({ path: testInfo.outputPath(`checks-${width}.png`) });
        await report.getByRole('button', { name: 'Close report' }).click();
        await expect(panel.locator('.code-report')).toHaveCount(0);
        await expect(panel.getByRole('button', { name: 'Refresh checks' })).toBeFocused();
        let release;
        const gate = new Promise(resolve => { release = resolve; });
        await page.route('**/api/v1/board/cards/card_test/checks/check-1*', async route => {
            await gate; await route.fulfill({ json: { check, freshness: 'Unknown' } }).catch(() => {});
        });
        await panel.locator('[data-check-summary]').getByRole('button', { name: 'View report' }).click();
        await expect(panel.locator('[data-check-report]')).toContainText('Loading saved evidence');
        await page.evaluate(() => window.app.closeModal());
        release();
        await expect(page.locator('[data-board-checks]')).toHaveCount(0);
    });
}

for (const width of [1440, 390]) {
    test(`owner sharing modal CRUD and invitation limit at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 900 });
        await openBoard(page);
        let invites = [{ id: 'invite_1', email: 'member@example.test', status: 'accepted' }];
        const writes = [];
        await page.route('**/api/v1/board/boards/brd_main/sharing**', async route => {
            const request = route.request(); const method = request.method();
            if (method === 'GET') return route.fulfill({ json: { configured: true, isOwner: true, sharing: { limit: 3, invitations: invites } } });
            expect(request.headers().viberails_tab).toBe('board-fixture');
            writes.push({ method, body: method === 'DELETE' ? null : request.postDataJSON() });
            if (method === 'POST') invites.push({ id: 'invite_' + (invites.length + 1), email: request.postDataJSON().email, status: 'pending' });
            if (method === 'PUT') invites[0].email = request.postDataJSON().email;
            if (method === 'DELETE') invites = invites.filter(i => !request.url().endsWith('/' + i.id));
            return route.fulfill({ json: { saved: true } });
        });
        await page.getByRole('button', { name: 'Share', exact: true }).click();
        const modal = page.locator('[data-sharing-modal]');
        await expect(modal).toContainText('1 of 3');
        await modal.getByRole('textbox', { name: 'Invite email', exact: true }).fill('future@example.test');
        await modal.getByRole('button', { name: 'Invite', exact: true }).click();
        await expect(modal).toContainText('Invitation saved');
        await expect(modal.locator('[data-sharing-edit]')).toHaveCount(2);
        await modal.getByRole('textbox', { name: 'Invite email', exact: true }).fill('third@example.test');
        await modal.getByRole('button', { name: 'Invite', exact: true }).click();
        await expect(modal.locator('[data-sharing-new]')).toHaveCount(0);
        const first = modal.locator('[data-sharing-edit]').first();
        await first.getByRole('textbox').fill('changed@example.test');
        await first.getByRole('button', { name: 'Save address' }).click();
        await expect.poll(() => invites[0].email).toBe('changed@example.test');
        const bounds = await page.locator('#modal-container .modal-content').boundingBox();
        expect(bounds.x).toBeGreaterThanOrEqual(0);
        expect(bounds.x + bounds.width).toBeLessThanOrEqual(width);
        expect(await modal.evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true);
        await page.screenshot({ path: testInfo.outputPath(`sharing-${width}.png`) });
        await first.getByRole('button', { name: 'Remove' }).click();
        await page.getByRole('alertdialog').getByRole('button', { name: 'Remove', exact: true }).click();
        await expect(modal.locator('[data-sharing-edit]')).toHaveCount(2);
        await expect(modal.locator('[data-sharing-new]')).toHaveCount(1);
        expect(writes.map(w => w.method)).toEqual(['POST', 'POST', 'PUT', 'DELETE']);
        expect(await page.evaluate(() => Boolean(window.__injected))).toBe(false);
    });
}

test('shared board members have no owner CRUD and late sharing responses cannot replace another modal', async ({ page }) => {
    await openBoard(page);
    await page.route('**/api/v1/board/boards/brd_main/sharing', route => route.fulfill({ json: { configured: true, isOwner: false, sharing: null } }));
    await page.getByRole('button', { name: 'Share', exact: true }).click();
    await expect(page.locator('[data-sharing-modal]')).toContainText('Only its owner');
    await expect(page.locator('[data-sharing-modal] form')).toHaveCount(0);
    await page.evaluate(() => window.app.closeModal());
    let release;
    const gate = new Promise(resolve => { release = resolve; });
    await page.route('**/api/v1/board/boards/brd_main/sharing', async route => {
        await gate; await route.fulfill({ json: { configured: true, isOwner: true, sharing: { limit: 3, invitations: [] } } }).catch(() => {});
    });
    await page.getByRole('button', { name: 'Share', exact: true }).click();
    await expect(page.locator('[data-sharing-modal]')).toContainText('Loading');
    await page.evaluate(() => window.app.boardController.openBoardEditor(null));
    release();
    await expect(page.locator('[data-board-board-editor]')).toBeVisible();
    await expect(page.locator('[data-sharing-modal]')).toHaveCount(0);
});

test('accepted shared boards are explicitly imported into the chosen project', async ({ page }) => {
    await openBoard(page);
    const remoteId = '11111111-1111-4111-8111-111111111111';
    await page.route('**/api/v1/board/shared', route => route.fulfill({ json: [{ remoteBoardId: remoteId, name: 'Shared <script>work</script>', isOwner: false }] }));
    let imports = 0;
    await page.route(`**/api/v1/board/shared/${remoteId}/import`, route => {
        imports++; expect(route.request().method()).toBe('POST');
        return route.fulfill({ json: { boardId: 'brd_main', name: 'Shared work' } });
    });
    await page.getByRole('button', { name: 'Shared boards', exact: true }).click();
    await expect(page.locator('[data-sharing-modal]')).toContainText('Shared <script>work</script>');
    await expect(page.locator('[data-sharing-modal] script')).toHaveCount(0);
    expect(imports).toBe(0);
    await page.getByRole('button', { name: 'Add to this project' }).click();
    await expect.poll(() => imports).toBe(1);
    await expect(page.locator('[data-sharing-modal]')).toHaveCount(0);
    await expect(page.locator('[data-board-select]')).toHaveValue('brd_main');
});

test('move and merge use explicit destinations and preserve drafts until saved', async ({ page }, testInfo) => {
    let current;
    await openBoard(page, { relatedCards: true, onCard: card => { current = card; } });
    let moved, merged;
    let finishMove;
    const moveResponse = new Promise(resolve => { finishMove = resolve; });
    await page.route('**/api/v1/board/columns/col_build/automation', route => route.fulfill({ json: { jobIds: [7] } }));
    await page.route('**/api/v1/board/cards/card_test/move', async route => {
        moved = route.request().postDataJSON();
        current.boardId = 'brd_sprint'; current.columnId = moved.columnId;
        await moveResponse;
        return route.fulfill({ json: current });
    });
    await page.route('**/api/v1/board/cards/card_test/merge', route => {
        merged = route.request().postDataJSON();
        return route.fulfill({ json: { ...current, id: 'card_related', title: 'Related work' } });
    });
    await page.getByText('Description images', { exact: true }).click();
    const editor = page.locator('[data-board-card-editor]');
    await editor.getByText('Move or merge', { exact: true }).click();
    await editor.locator('[data-move-board]').selectOption('brd_sprint');
    await expect(editor.locator('[data-move-lane]')).toHaveValue('col_build');
    await editor.locator('#board-card-title').fill('Unsaved title');
    await editor.locator('[data-move-card]').click();
    await expect(editor.locator('[data-organize-status]')).toContainText('Save your card edits');
    expect(moved).toBeUndefined();
    await editor.locator('#board-card-title').fill('Description images');
    await editor.locator('[data-move-card]').click();
    const confirmation = page.getByRole('alertdialog');
    await expect(confirmation).toContainText('1 configured Automation');
    await confirmation.getByRole('button', { name: 'Move', exact: true }).click();
    await expect.poll(() => moved).toEqual({ columnId: 'col_build' });
    await expect(editor.locator('#board-card-title')).toBeDisabled();
    finishMove();
    await expect(editor.locator('[data-board-organize] details')).not.toHaveAttribute('open', '');
    await editor.getByText('Move or merge', { exact: true }).click();
    await editor.locator('[data-merge-target]').selectOption('card_related');
    await page.screenshot({ path: testInfo.outputPath('move-and-merge.png') });
    await editor.locator('[data-merge-card]').click();
    await expect(confirmation).toContainText("destination's settings are kept");
    await confirmation.getByRole('button', { name: 'Merge', exact: true }).click();
    await expect.poll(() => merged).toEqual({ targetCard: 'card_related' });
    await expect(editor).toHaveAttribute('data-card-id', 'card_related');
});

test('human can delete an agent comment without discarding a draft', async ({ page }) => {
    let current;
    await openBoard(page, { onCard: card => { current = card; card.comments[0].author = { kind: 'agent', label: 'Codex' }; } });
    let deleted = false;
    await page.route('**/api/v1/board/cards/card_test/comments/comment_1', route => {
        deleted = route.request().method() === 'DELETE'; current.comments = [];
        return route.fulfill({ json: { ok: true } });
    });
    await page.getByText('Description images', { exact: true }).click();
    const editor = page.locator('[data-board-card-editor]');
    await editor.locator('#board-card-title').fill('Keep my draft');
    await editor.getByRole('button', { name: 'Delete comment', exact: true }).click();
    await page.getByRole('alertdialog').getByRole('button', { name: 'Delete', exact: true }).click();
    await expect(editor.locator('[data-board-comments]')).toContainText('No comments yet');
    await expect(editor.locator('#board-card-title')).toHaveValue('Keep my draft');
    expect(deleted).toBe(true);
});

test('new-card links stay in the draft and are submitted with creation', async ({ page }) => {
    const requests = await openBoard(page, { relatedCards: true });
    await page.getByRole('button', { name: 'New card', exact: true }).click();
    const editor = page.locator('[data-board-card-editor]');
    await editor.locator('#board-card-title').fill('Linked from the beginning');
    await editor.locator('[data-board-link-picker] > summary').click();
    await editor.locator('[data-board-link-card="card_related"]').click();
    await expect(editor.locator('[data-board-linked-cards]')).toContainText('Related work');
    expect(requests.filter(r => r.method === 'POST' && r.path.startsWith('/api/v1/board/cards')
        && r.path !== '/api/v1/board/cards/activity')).toEqual([]);
    await editor.locator('[data-board-unlink-card="card_related"]').click();
    await expect(editor.locator('[data-board-linked-cards]')).not.toContainText('Related work');
    await editor.locator('[data-board-link-card="card_related"]').click();
    await editor.locator('[data-board-save-card]').click();
    await expect(editor).toHaveCount(0);
    const created = requests.find(r => r.method === 'POST' && r.path === '/api/v1/board/cards');
    expect(created.body.linkedCardIds).toEqual(['card_related']);
    expect(requests.filter(r => r.method === 'POST' && r.path.endsWith('/links'))).toEqual([]);
});

test('display IDs are visible and editable while permanent IDs remain available', async ({ page }, testInfo) => {
    const requests = await openBoard(page, { assignee: 'base:codex', onCard: card => {
        card.key = 'VB-ABCDE-42'; card.displayId = 'VIBE-7';
    } });
    await expect(page.locator('.board-key')).toContainText('VIBE-7');
    await page.getByText('Description images', { exact: true }).click();
    const editor = page.locator('[data-board-card-editor]');
    await expect(page.locator('.modal-title')).toContainText('VIBE-7');
    await expect(editor.locator('.board-editor-actions')).not.toContainText('Chat with agent');
    await expect(editor.locator('.board-discussion')).toContainText('Chat with an agent about the card without starting it.');
    const picker = editor.locator('.board-chat-controls .ts-wrapper');
    const chat = editor.locator('[data-board-chat]');
    expect(await picker.evaluate((element, other) => Boolean(element.compareDocumentPosition(other) & Node.DOCUMENT_POSITION_FOLLOWING), await chat.elementHandle())).toBe(true);
    await editor.locator('.board-discussion').scrollIntoViewIfNeeded();
    await page.screenshot({ path: testInfo.outputPath('display-id-discussion.png') });
    // Agent context lives in the collapsed Advanced section and is only measured once that opens.
    let contextRequests = 0;
    await page.route('**/api/v1/board/cards/card_test/context', route => {
        contextRequests++;
        return route.fulfill({ json: { cardId: 'card_test', key: 'VB-ABCDE-42', tokens: 3100, chars: 12400, method: 'chars/4',
            sources: [{ key: 'prompt', label: 'Launch prompt', chars: 4000, tokens: 1000 }], contents: [], extras: [], lastLaunch: null } });
    });
    await expect(editor.locator('[data-board-advanced] [data-board-context-section]')).toHaveCount(1);
    await expect(editor.locator('[data-board-context-section]')).toHaveCount(1);
    await expect(editor.locator('[data-board-context-section]')).not.toBeVisible();
    expect(contextRequests).toBe(0);
    await editor.getByText('Advanced', { exact: true }).click();
    await expect(editor.locator('[data-board-context-total]')).toHaveText('≈3.1k');
    expect(contextRequests).toBe(1);
    await editor.locator('[data-board-advanced]').scrollIntoViewIfNeeded();
    await page.screenshot({ path: testInfo.outputPath('advanced-section.png') });
    await expect(editor.locator('#board-card-display-id')).toHaveValue('VIBE-7');
    await expect(editor).toContainText('Permanent ID: VB-ABCDE-42');
    await editor.locator('#board-card-display-id').fill('VIBE-88');
    await editor.locator('[data-board-save-card]').click();
    await expect(editor).toHaveCount(0);
    expect(requests.find(r => r.method === 'PUT' && r.path.endsWith('/card_test')).body).toEqual({ displayId: 'VIBE-88' });
    await expect(page.locator('.board-key')).toContainText('VIBE-88');
});

async function openBoard(page, { active = false, assignee = null, relatedCards = false, empty = false,
    onCard = () => {},
    columns = [{ id: 'col_ready', name: 'Ready', position: 0, color: '#3b82f6', boardId: 'brd_main' }] } = {}) {
    if (process.env.VIBERAILS_BOARD_STATIC === '1') {
        await page.addInitScript(() => sessionStorage.setItem('viberails_tab', 'board-fixture'));
    }
    let card = {
        id: 'card_test', key: 'VB-1', boardId: 'brd_main', columnId: 'col_ready', position: 0,
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
    onCard(card);
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
    const linkSummary = item => ({ id: item.id, key: item.key, displayId: item.displayId, title: item.title,
        boardId: item.boardId || 'brd_main', boardName: item.boardName || 'Main',
        columnId: item.columnId, columnName: item.columnName || 'Ready' });
    await page.routeWebSocket('**/api/v1/events/ws*', () => {});
    await page.route('**/api/v1/**', async route => {
        const url = new URL(route.request().url());
        const path = url.pathname;
        requests.push({ path, method: route.request().method(), body: route.request().postDataJSON() });
        if (path === '/api/v1/board/cards/link-candidates') {
            const query = (url.searchParams.get('q') || '').toLowerCase();
            return route.fulfill({ json: { cards: related.filter(item => `${item.displayId || item.key} ${item.title}`.toLowerCase().includes(query)).map(linkSummary) } });
        }
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
        if (path === '/api/v1/board/cards/activity') {
            const { boardId, cardIds } = route.request().postDataJSON();
            expect(boardId).toBe('brd_main');
            expect(cardIds.length).toBeLessThanOrEqual(100);
            return route.fulfill({ json: { cards: cardIds.includes(card.id) ? [{ id: card.id,
                activeSessionId: card.activeSessionId, activeTabId: card.activeTabId,
                hasActiveAutomation: Boolean(card.hasActiveAutomation) }] : [] } });
        }
        if (relatedCard) {
            if (route.request().method() === 'PUT') Object.assign(relatedCard, route.request().postDataJSON());
            return route.fulfill({ json: { ...relatedCard, linkedCards: linkedIds.has(relatedCard.id) ? [linkSummary(card)] : [] } });
        }
        if (path === '/api/v1/board/cards' && route.request().method() === 'POST') {
            for (const id of route.request().postDataJSON().linkedCardIds || []) linkedIds.add(id);
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
            '/api/v1/board/boards': { boards: [{ id: 'brd_main', name: 'Main', position: 0, cardCount: empty ? 0 : 1,
                columns },
                ...(relatedCards ? [{ id: 'brd_sprint', name: 'Sprint 2', position: 1, cardCount: 2,
                    columns: [{ id: 'col_build', name: 'Build', position: 0, color: '#06b6d4', boardId: 'brd_sprint' }] }] : [])] },
            '/api/v1/board/columns': { columns },
            '/api/v1/board/cards': { cards: empty ? [] : [card] }
        };
        return route.fulfill({ json: payloads[path] || {} });
    });
    await page.goto('/?view=board', { waitUntil: 'domcontentloaded' });
    await expect(page.locator('#app-content [data-view="board"]')).toBeVisible();
    return requests;
}

for (const width of [1440, 900, 390]) {
    test(`empty board can add lanes beyond six at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 900 });
        const columns = ['Needs refinement', 'Ready', 'Build', 'Agent Code Review', 'PR', 'Build And Deploy']
            .map((name, position) => ({ id: `col_${position}`, name, position, boardId: 'brd_main', color: '#64748b' }));
        await openBoard(page, { columns, empty: true });
        const created = [];
        await page.route('**/api/v1/board/columns', route => {
            if (route.request().method() !== 'POST') return route.fulfill({ json: { columns } });
            const body = route.request().postDataJSON();
            created.push(body);
            const column = { ...body, id: `col_${columns.length}`, position: columns.length };
            columns.push(column);
            return route.fulfill({ json: column });
        });

        const canvas = page.locator('[data-board-canvas]');
        const addLane = page.locator('[data-board-action="add-lane"]');
        for (const count of [6, 7, 8]) {
            await expect(page.locator('.board-lane')).toHaveCount(count);
            await expect(page.locator('.board-card')).toHaveCount(0);
            await canvas.evaluate(element => { element.scrollLeft = element.scrollWidth; });
            const lastLane = await page.locator('.board-lane').last().boundingBox();
            const button = await addLane.boundingBox();
            expect(button.x, 'Add lane must sit after the last lane, without overlap').toBeGreaterThanOrEqual(lastLane.x + lastLane.width);
            // Scroll offsets round to whole pixels while the flex gaps can be fractional.
            await expect(addLane).toBeInViewport({ ratio: 0.99 });
            expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(width);
            if (count === 8) break;
            await addLane.click({ timeout: 3000 });
            await page.locator('#board-lane-name').fill(`Lane ${count + 1}`);
            await page.locator('[data-board-save-lane]').click();
            await expect(page.locator('[data-board-lane-editor]')).toHaveCount(0);
        }
        expect(created.map(({ name, boardId }) => ({ name, boardId }))).toEqual([
            { name: 'Lane 7', boardId: 'brd_main' }, { name: 'Lane 8', boardId: 'brd_main' }
        ]);
        await page.screenshot({ path: testInfo.outputPath('eight-empty-lanes.png') });
    });
}

test('legacy notes join Comments and History loads only from card settings', async ({ page }) => {
    await openBoard(page, { onCard: card => {
        card.boardId = 'brd_main';
        card.notes = [{ id: 'note_one', author: { kind: 'agent', label: 'Codex' }, body: 'Agent scratchpad', createdAt: card.createdAt }];
    } });
    let historyRequests = 0;
    await page.route('**/api/v1/board/boards/brd_main/history?*', route => {
        historyRequests++;
        return route.fulfill({ json: { entries: [{ id: 'change_one', body: 'Title edited', author: 'You', createdUtc: '2026-09-27T12:00:00Z' }], hasMore: false, nextOffset: 1 } });
    });
    await page.getByText('Description images', { exact: true }).click();
    const editor = page.locator('[data-board-card-editor]');
    await expect(editor).toBeVisible();
    await expect(editor.locator('[data-board-comments]')).toContainText('Agent scratchpad');
    await expect(editor.locator('[data-board-history-view]')).not.toBeVisible();
    expect(historyRequests).toBe(0);
    await expect(editor.locator('[data-board-notes-details]')).toHaveCount(0);
    await editor.getByText('Advanced', { exact: true }).click();
    expect(historyRequests).toBe(0);
    await editor.locator('[data-board-history-view] > summary').click();
    await expect(editor.locator('[data-history-entries]')).toContainText('Title edited');
    expect(historyRequests).toBe(1);
});

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

async function openLaneAgentsBoard(page) {
    const columns = ['Backlog', 'In Progress', 'Review', 'Done'].map((name, position) => ({
        id: `lane_${position}`, name, position, boardId: 'brd_main', color: ['#64748b', '#06b6d4', '#a855f7', '#10b981'][position]
    }));
    await openBoard(page, { columns, onCard: card => { card.columnId = 'lane_0'; } });
    const jobs = [
        { id: 12, name: 'Code reviewer', description: 'Checks the changes', enabled: true },
        { id: 14, name: 'Test runner', description: 'Runs the test suite', enabled: true },
        { id: 15, name: 'Release checks', description: 'Checks the release', enabled: true }
    ].map(job => ({ ...job, projectPath: 'C:/board-fixture', llm: 1, prompt: 'Keep this prompt',
        timeoutMinutes: 25, environmentId: null, launchMinimized: true, triggers: [{ kind: 3 }],
        actions: [{ id: 'script_1', kind: 1, scriptPath: 'checks.py', scriptRuntime: 0, arguments: ['--check'] }] }));
    // Resolve the actual Worker, even when a script runs first or the legacy job LLM differs.
    jobs[0].llm = 2;
    jobs[0].actions.push({ id: 'worker_1', kind: 0, environmentId: 8, environmentName: 'Review Worker', llm: 1 });
    jobs[1].actions.push({ id: 'worker_2', kind: 0, environmentId: 7, environmentName: 'Test Worker', llm: 2 });
    const settings = Object.fromEntries(columns.map(column => [column.id, { jobIds: column.id === 'lane_2' ? [12, 14] : [], revision: 3 }]));
    const writes = [];
    await page.route('**/api/v1/jobs?**', route => route.fulfill({ json: { jobs } }));
    await page.route(/\/api\/v1\/jobs\/\d+$/, route => {
        const id = Number(new URL(route.request().url()).pathname.split('/').pop());
        const job = jobs.find(item => item.id === id);
        if (route.request().method() === 'PUT') {
            const body = route.request().postDataJSON();
            writes.push({ kind: 'description', id, body });
            Object.assign(job, body);
        }
        return route.fulfill({ json: job });
    });
    await page.route('**/api/v1/board/columns/*/automation', route => {
        const id = new URL(route.request().url()).pathname.split('/').at(-2);
        const current = settings[id];
        if (route.request().method() === 'PUT') {
            const body = route.request().postDataJSON();
            expect(body.expectedRevision).toBe(current.revision);
            expect(body.jobIds.every(jobId => jobs.some(job => job.id === jobId && job.enabled))).toBe(true);
            writes.push({ kind: 'selection', id, body });
            current.jobIds = body.jobIds;
            current.revision++;
        }
        return route.fulfill({ json: { ...current, jobs: jobs.map(({ id, name, enabled }) => ({ id, name, enabled })) } });
    });
    await page.evaluate(() => window.app.boardController.refresh());
    return { jobs, settings, writes };
}

for (const width of [1440, 390]) {
    test(`lane agent model labels and shared running indicators at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 900 });
        await openLaneAgentsBoard(page);
        const environments = [
            { id: 8, cli: 'codex', name: 'Review Worker', customArgs: '-m gpt-6.1-sol -c model_reasoning_effort="xhigh"' },
            { id: 7, cli: 'claude', name: 'Test Worker', customArgs: '--model claude-opus-5-5[1m] --effort max' }
        ];
        await page.route('**/api/v1/environments', route => route.fulfill({ json: { environments } }));
        let active = true;
        await page.route('**/api/v1/board/cards/activity', route => route.fulfill({ json: {
            cards: [{ id: 'card_test', activeSessionId: null, activeTabId: null, hasActiveAutomation: active }],
            activeAutomationColumnIds: active ? ['lane_2'] : []
        } }));
        await page.evaluate(() => window.app.boardController.refreshSessionActivity());
        const button = page.getByRole('button', { name: 'Agents on entry to Review', exact: true });
        await expect(button).toHaveClass(/is-running/);
        await expect(button.locator('.board-lane-agents-caption')).toHaveText('Running');
        await expect(button.locator('.board-lane-agents-count')).toHaveText('2');
        expect(await button.evaluate(el => getComputedStyle(el, '::after').animationName)).toBe('board-lane-agent-pulse');
        await expect(page.locator('.board-card .board-automation-running')).toHaveCount(1);
        await button.scrollIntoViewIfNeeded();
        await button.click();
        const panel = page.getByRole('dialog', { name: 'Lane agents', exact: true });
        await expect(panel).toContainText('GPT 6.1 Sol · Extra high effort');
        await expect(panel).toContainText('Claude Opus 5.5 (1M context) · Maximum effort');
        expect(await panel.evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true);
        await page.screenshot({ path: testInfo.outputPath(`lane-running-${width}.png`) });
        await page.emulateMedia({ reducedMotion: 'reduce' });
        expect(await button.evaluate(el => getComputedStyle(el, '::after').animationName)).toBe('none');
        expect(await page.locator('.board-automation-running').evaluate(el => getComputedStyle(el).animationName)).toBe('none');
        // Activity updates preserve the open panel and its text drafts.
        await panel.getByRole('button', { name: 'Edit description for Code reviewer', exact: true }).click();
        const draft = panel.getByRole('textbox', { name: 'Agent description for Code reviewer', exact: true });
        await draft.fill('Keep this draft while the agent finishes');
        active = false;
        await page.evaluate(() => window.app.boardController.refreshSessionActivity());
        await expect(button).not.toHaveClass(/is-running/);
        await expect(button.locator('.board-lane-agents-caption')).toHaveText('Agents');
        await expect(page.locator('.board-automation-running')).toHaveCount(0);
        await expect(draft).toHaveValue('Keep this draft while the agent finishes');
        // Unknown model identifiers render as text.
        await page.keyboard.press('Escape');
        environments[0].customArgs = '--model "<img src=x onerror=alert(1)>"';
        await button.click();
        await expect(panel.locator('.board-lane-agent-model').first()).toContainText('<img src=x onerror=alert(1)>');
        await expect(panel.locator('.board-lane-agent-model img')).toHaveCount(0);
    });
}

for (const width of [1440, 390]) {
    test(`compact lane agents use existing controls and fit at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 900 });
        const { jobs, settings, writes } = await openLaneAgentsBoard(page);
        const buttons = page.locator('.board-lane-agents-button');
        await expect(buttons).toHaveCount(3);
        const button = page.getByRole('button', { name: 'Agents on entry to Review', exact: true });
        await expect(button.locator('.board-lane-agents-count')).toHaveText('2');
        await button.scrollIntoViewIfNeeded();
        const geometry = await button.evaluate(el => {
            const bounds = el.getBoundingClientRect();
            const lane = el.closest('.board-lane').getBoundingClientRect();
            const previous = el.closest('.board-lane').previousElementSibling.getBoundingClientRect();
            return { overlapLeft: bounds.left < previous.right, overlapRight: bounds.right > lane.left,
                above: bounds.top < lane.top, below: bounds.bottom > lane.top, gap: lane.left - previous.right };
        });
        expect(geometry).toMatchObject({ overlapLeft: true, overlapRight: true, above: true, below: true });
        expect(geometry.gap).toBeLessThan(12);
        await button.click();
        const panel = page.getByRole('dialog', { name: 'Lane agents', exact: true });
        await expect(panel).toContainText('Code reviewer');
        await expect(panel).toContainText('60 seconds');
        const codexLogo = panel.locator('[data-agent-id="12"]').getByRole('img', { name: 'Codex', exact: true });
        const claudeLogo = panel.locator('[data-agent-id="14"]').getByRole('img', { name: 'Claude', exact: true });
        await expect(codexLogo).toHaveAttribute('src', /openai\.svg$/);
        await expect(claudeLogo).toHaveAttribute('src', /claude-color\.svg$/);
        await expect.poll(() => codexLogo.evaluate(img => img.complete && img.naturalWidth > 0)).toBe(true);
        await expect.poll(() => claudeLogo.evaluate(img => img.complete && img.naturalWidth > 0)).toBe(true);
        await expect(panel.locator('.board-lane-agent-icon .fa-robot')).toHaveCount(0);
        const bounds = await panel.boundingBox();
        expect(bounds.x).toBeGreaterThanOrEqual(0);
        expect(bounds.x + bounds.width).toBeLessThanOrEqual(width);
        expect(await panel.evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true);
        await page.screenshot({ path: testInfo.outputPath(`lane-agents-${width}.png`) });
        await expect(panel.getByRole('switch')).toHaveCount(0);
        await expect(panel.locator('[data-agent-description]')).toHaveCount(0);
        await expect(panel.locator('[data-agent-id="12"] .board-lane-agent-description-preview')).toHaveText('Checks the changes');
        const editDescription = panel.getByRole('button', { name: 'Edit description for Code reviewer', exact: true });
        await editDescription.click();
        const description = panel.getByRole('textbox', { name: 'Agent description for Code reviewer', exact: true });
        const otherDescription = panel.getByRole('textbox', { name: 'Agent description for Test runner', exact: true });
        await expect(description).toBeFocused();
        await expect(otherDescription).toHaveCount(0);
        // Resizing a description keeps the panel and its scrollable actions in the viewport.
        await description.evaluate(el => { el.style.height = '250px'; });
        await expect.poll(async () => {
            const resized = await panel.boundingBox();
            return resized.y >= 0 && resized.y + resized.height <= 900;
        }).toBe(true);
        await description.evaluate(el => { el.style.height = ''; });
        await expect(description).toHaveValue('Checks the changes');
        await expect(description).toHaveAttribute('maxlength', '2000');
        await expect(panel.getByRole('button', { name: 'Save description for Code reviewer' })).toBeDisabled();
        const savedDescription = 'Review the changes for correctness.\nReport findings on the Board card.';
        await description.fill(savedDescription);
        await panel.getByRole('button', { name: 'Edit description for Test runner', exact: true }).click();
        await otherDescription.fill('Keep this unsaved test-runner description');
        await panel.getByRole('button', { name: 'Save description for Code reviewer' }).click();
        await expect(panel.locator('[data-agent-id="12"] [role="status"]')).toHaveText('Description saved');
        await expect(description).toHaveCount(0);
        await expect(editDescription).toBeFocused();
        expect(jobs[0].description).toBe(savedDescription);
        expect(jobs[0].enabled).toBe(true);
        expect(writes[0].body.prompt).toBe('Keep this prompt');
        expect(writes[0].body.actions).toBeUndefined();
        expect(writes[0].body.triggers).toEqual([{ kind: 3 }]);
        expect(settings.lane_2.jobIds).toEqual([12, 14]);
        expect(settings.lane_2.revision).toBe(3);
        await expect(otherDescription).toHaveValue('Keep this unsaved test-runner description');
        await panel.getByRole('button', { name: 'Add agent', exact: true }).click();
        await expect(otherDescription).toHaveValue('Keep this unsaved test-runner description');
        await panel.getByLabel('Existing Automation').selectOption('15');
        await panel.getByRole('button', { name: 'Add to lane', exact: true }).click();
        await expect(button.locator('.board-lane-agents-count')).toHaveText('3');
        await expect(panel.locator('[data-agent-id="15"] .board-lane-agent-copy')).toContainText('Script workflow');
        await expect(panel.locator('[data-agent-id="15"] .board-lane-agent-icon img')).toHaveCount(0);
        await panel.getByRole('button', { name: 'Remove Test runner from this lane', exact: true }).click();
        await page.getByRole('alertdialog').getByRole('button', { name: 'Remove', exact: true }).click();
        await expect(panel.getByText('Test runner', { exact: true })).toHaveCount(0);
        expect(settings.lane_2.jobIds).toEqual([12, 15]);
        expect(jobs).toHaveLength(3);
        await page.keyboard.press('Escape');
        await expect(panel).toHaveCount(0);
        await expect(button).toBeFocused();
        await button.click();
        await expect(panel).toContainText('Release checks');
        await expect(description).toHaveCount(0);
        await editDescription.click();
        await expect(description).toHaveValue(savedDescription);
        const writesBeforeCancel = writes.length;
        await description.fill('Discard this description draft');
        await panel.getByRole('button', { name: 'Cancel description for Code reviewer', exact: true }).click();
        await expect(description).toHaveCount(0);
        await expect(editDescription).toBeFocused();
        expect(writes).toHaveLength(writesBeforeCancel);
        expect(jobs[0].description).toBe(savedDescription);
        await editDescription.click();
        await expect(description).toHaveValue(savedDescription);
        await description.fill('');
        await panel.getByRole('button', { name: 'Save description for Code reviewer' }).click();
        await expect(panel.locator('[data-agent-id="12"] [role="status"]')).toHaveText('Description saved');
        await expect(description).toHaveCount(0);
        await expect(panel.locator('[data-agent-id="12"] .board-lane-agent-description-preview')).toHaveText('No description yet.');
        expect(jobs[0].description).toBe('');
        await panel.getByRole('button', { name: 'Edit Code reviewer', exact: true }).click();
        await expect(page.locator('[data-job-editor] #job-name')).toHaveValue('Code reviewer');
        await expect(page.locator('[data-job-editor] #job-description')).toHaveValue('');
        await expect(panel).toHaveCount(0);
    });
}

test('lane agents recover from conflicts, escape text and discard late loads', async ({ page }) => {
    const { jobs } = await openLaneAgentsBoard(page);
    jobs[0].name = '<img src=x onerror="window.__laneAgentXss=1">';
    jobs[0].description = '</textarea><script>window.__laneAgentXss=1</script>';
    const button = page.getByRole('button', { name: 'Agents on entry to Review', exact: true });
    await button.click();
    const panel = page.getByRole('dialog', { name: 'Lane agents', exact: true });
    await expect(panel).toContainText(jobs[0].description);
    await expect(panel.locator('script, img[onerror]')).toHaveCount(0);
    await expect(panel.locator('.board-lane-agent-icon img')).toHaveCount(2);
    await page.route('**/api/v1/board/columns/lane_2/automation', route => {
        if (route.request().method() === 'PUT') return route.fulfill({ status: 409, json: { error: 'Lane changed. Reload before saving.' } });
        return route.fulfill({ json: { jobIds: [12, 14], revision: 7, jobs } });
    });
    await panel.getByRole('button', { name: 'Remove Test runner from this lane' }).click();
    await page.getByRole('alertdialog').getByRole('button', { name: 'Cancel', exact: true }).click();
    await expect(panel.locator('[data-agent-action="edit-description"]').first()).toBeEnabled();
    await panel.getByRole('button', { name: 'Remove Test runner from this lane' }).click();
    await page.getByRole('alertdialog').getByRole('button', { name: 'Remove', exact: true }).click();
    await expect(panel.getByRole('alert')).toContainText('Lane changed');
    await expect(panel).toContainText('Test runner');
    await panel.getByRole('button', { name: 'Reload', exact: true }).click();
    await expect(panel.getByRole('alert')).toBeEmpty();
    await page.keyboard.press('Escape');
    let release;
    const gate = new Promise(resolve => { release = resolve; });
    await page.route('**/api/v1/board/columns/lane_2/automation', async route => {
        await gate;
        await route.fulfill({ json: { jobIds: [], revision: 8, jobs } }).catch(() => {});
    });
    await button.click();
    await expect(panel).toContainText('Loading agents');
    await page.evaluate(() => window.app.navigate('jobs'));
    release();
    await expect(panel).toHaveCount(0);
    expect(await page.evaluate(() => window.__laneAgentXss)).toBeUndefined();
});

test('lane agents handle paused selections, sync the settings badge, and open the existing create editor', async ({ page }) => {
    const { jobs, settings, writes } = await openLaneAgentsBoard(page);
    jobs[0].enabled = false;
    settings.lane_2.jobIds.push(99);
    const button = page.getByRole('button', { name: 'Agents on entry to Review', exact: true });
    await button.click();
    const panel = page.getByRole('dialog', { name: 'Lane agents', exact: true });
    await expect(panel).toContainText('Unavailable Automation (99)');
    await expect(panel.locator('[data-agent-id="99"]')).toContainText('Description unavailable');
    await expect(panel.locator('[data-agent-id="99"] textarea')).toHaveCount(0);
    await panel.getByRole('button', { name: 'Add agent', exact: true }).click();
    await panel.getByLabel('Existing Automation').selectOption('15');
    await panel.getByRole('button', { name: 'Add to lane', exact: true }).click();
    await expect(panel.getByRole('alert')).toContainText('Enable disabled Automations in the Automation editor');
    expect(writes).toHaveLength(0);
    await panel.getByRole('button', { name: 'Remove Test runner from this lane' }).click();
    const confirmation = page.getByRole('alertdialog');
    await expect(confirmation).toContainText('Code reviewer, Automation 99');
    await confirmation.getByRole('button', { name: 'Remove', exact: true }).click();
    await expect(panel).toContainText('No agents on entry');
    expect(settings.lane_2.jobIds).toEqual([]);
    await page.keyboard.press('Escape');
    await page.getByRole('button', { name: 'Settings for Review', exact: true }).click();
    await page.getByRole('checkbox', { name: 'Release checks', exact: true }).check();
    await page.getByRole('button', { name: 'Save automations', exact: true }).click();
    // The shared modal makes the board inert; the badge still updates behind it.
    await expect(page.locator('[data-board-action="lane-agents"][data-column-id="lane_2"] .board-lane-agents-count')).toHaveText('1');
    await page.locator('#modal-container [data-action="close-modal"]').first().click();
    await button.click();
    await panel.getByRole('button', { name: 'Add agent', exact: true }).click();
    await panel.getByRole('button', { name: 'Create Automation…', exact: true }).click();
    await expect(page.locator('[data-job-editor] #job-name')).toHaveValue('');
    await expect(page.locator('[data-job-editor]')).toContainText('Create automation');
    await expect(panel).toHaveCount(0);
});

test('lane agent loading and description-save failures preserve drafts for retry', async ({ page }) => {
    const { jobs, writes } = await openLaneAgentsBoard(page);
    await page.route('**/api/v1/jobs?**', route => route.fulfill({ status: 503, json: { error: 'Catalog unavailable' } }));
    const button = page.getByRole('button', { name: 'Agents on entry to Review', exact: true });
    await button.click();
    const panel = page.getByRole('dialog', { name: 'Lane agents', exact: true });
    await expect(panel).toContainText('Catalog unavailable');
    await page.route('**/api/v1/jobs?**', route => route.fulfill({ json: { jobs } }));
    await panel.getByRole('button', { name: 'Retry', exact: true }).click();
    await panel.getByRole('button', { name: 'Edit description for Code reviewer', exact: true }).click();
    const description = panel.getByRole('textbox', { name: 'Agent description for Code reviewer', exact: true });
    const save = panel.getByRole('button', { name: 'Save description for Code reviewer', exact: true });
    await expect(description).toHaveValue('Checks the changes');
    await description.fill('Keep my description draft');
    await page.route('**/api/v1/jobs/12', route => route.request().method() === 'PUT'
        ? route.fulfill({ status: 500, json: { error: 'Could not save Automation' } })
        : route.fulfill({ json: jobs[0] }));
    await save.click();
    await expect(panel.getByRole('alert')).toContainText('Could not save Automation');
    await expect(description).toHaveValue('Keep my description draft');
    await expect(description).toBeFocused();
    await expect(save).toBeEnabled();
    expect(jobs[0].description).toBe('Checks the changes');
    expect(writes).toHaveLength(0);
    await panel.getByRole('button', { name: 'Reload', exact: true }).click();
    await expect(panel.getByRole('alert')).toBeEmpty();
    await expect(description).toHaveValue('Keep my description draft');
    // The fresh read must preserve an enable-state change made in the Automation editor.
    jobs[0].enabled = false;
    await page.route('**/api/v1/jobs/12', route => {
        if (route.request().method() === 'PUT') Object.assign(jobs[0], route.request().postDataJSON());
        return route.fulfill({ json: jobs[0] });
    });
    await save.click();
    await expect(panel.locator('[data-agent-id="12"] [role="status"]')).toHaveText('Description saved');
    await expect(description).toHaveCount(0);
    expect(jobs[0].description).toBe('Keep my description draft');
    expect(jobs[0].enabled).toBe(false);
    await expect(panel).toContainText('Disabled · manage in the Automation editor');
    await page.getByRole('button', { name: 'New card', exact: true }).click();
    await expect(panel).toHaveCount(0);
    await expect(page.locator('[data-board-card-editor]')).toBeVisible();
});

test('lane agent drafts survive a background pagination response and remain saveable', async ({ page }) => {
    const { jobs, writes } = await openLaneAgentsBoard(page);
    const cards = Array.from({ length: 60 }, (_, index) => ({
        id: `paged-${index}`, key: `VB-${index + 1}`, title: `Backlog task ${index + 1}`,
        description: '', columnId: 'lane_0', position: index, type: 'task', priority: 'medium', tags: []
    }));
    let release;
    let requested = false;
    const gate = new Promise(resolve => { release = resolve; });
    await page.route('**/api/v1/board/cards?**', async route => {
        const offset = Number(new URL(route.request().url()).searchParams.get('offset') || 0);
        if (offset) { requested = true; await gate; }
        await route.fulfill({ json: {
            cards: cards.slice(offset, offset + 30),
            lanes: [{ columnId: 'lane_0', totalCount: 60, filteredCount: 60,
                nextOffset: offset + 30, hasMore: offset === 0 }],
            totalCount: 60, filteredCount: 60, blockedCount: 0, remainingPoints: 0, assignees: [], tags: []
        } });
    });
    await page.evaluate(() => window.app.boardController.refresh());
    await expect(page.locator('.board-card')).toHaveCount(30);
    await page.locator('.board-lane-list[data-column-id="lane_0"]').evaluate(el => { el.scrollTop = el.scrollHeight; });
    await expect.poll(() => requested).toBe(true);

    const button = page.getByRole('button', { name: 'Agents on entry to Review', exact: true });
    await button.click();
    const panel = page.getByRole('dialog', { name: 'Lane agents', exact: true });
    await panel.getByRole('button', { name: 'Edit description for Code reviewer', exact: true }).click();
    const description = panel.getByRole('textbox', { name: 'Agent description for Code reviewer', exact: true });
    await description.fill('Keep the reviewer draft');
    await panel.getByRole('button', { name: 'Edit description for Test runner', exact: true }).click();
    const otherDescription = panel.getByRole('textbox', { name: 'Agent description for Test runner', exact: true });
    await otherDescription.fill('Keep the test runner draft');
    await otherDescription.evaluate(el => el.setSelectionRange(5, 12));

    release();
    await expect(page.locator('.board-card')).toHaveCount(60);
    await expect(panel).toBeVisible();
    await expect(description).toHaveValue('Keep the reviewer draft');
    await expect(otherDescription).toHaveValue('Keep the test runner draft');
    await expect(otherDescription).toBeFocused();
    expect(await otherDescription.evaluate(el => [el.selectionStart, el.selectionEnd])).toEqual([5, 12]);
    await expect(button).toHaveAttribute('aria-expanded', 'true');
    await expect(button).toHaveAttribute('aria-controls', 'board-lane-agents-panel');
    expect(writes).toHaveLength(0);

    await panel.getByRole('button', { name: 'Save description for Code reviewer', exact: true }).click();
    await expect(panel.locator('[data-agent-id="12"] [role="status"]')).toHaveText('Description saved');
    await expect(otherDescription).toHaveValue('Keep the test runner draft');
    expect(jobs[0].description).toBe('Keep the reviewer draft');
    expect(jobs[1].description).toBe('Runs the test suite');
    expect(writes).toHaveLength(1);
    await page.keyboard.press('Escape');
    await expect(panel).toHaveCount(0);
    await expect(button).toBeFocused();
    await expect(button).toHaveAttribute('aria-expanded', 'false');
    await button.click();
    await expect(panel.locator('[data-agent-id="12"] .board-lane-agent-description-preview')).toHaveText('Keep the reviewer draft');
    await page.evaluate(() => window.app.navigate('settings'));
    await expect(panel).toHaveCount(0);
});

test('closing lane agents during the description pre-save read prevents a late write', async ({ page }) => {
    const { jobs, writes } = await openLaneAgentsBoard(page);
    await page.getByRole('button', { name: 'Agents on entry to Review', exact: true }).click();
    const panel = page.getByRole('dialog', { name: 'Lane agents', exact: true });
    await panel.getByRole('button', { name: 'Edit description for Code reviewer', exact: true }).click();
    await panel.getByRole('textbox', { name: 'Agent description for Code reviewer', exact: true }).fill('Not saved after closing');
    let release;
    let requested = false;
    const gate = new Promise(resolve => { release = resolve; });
    await page.route('**/api/v1/jobs/12', async route => {
        requested = true;
        await gate;
        await route.fulfill({ json: jobs[0] }).catch(() => {});
    });
    await panel.getByRole('button', { name: 'Save description for Code reviewer' }).click();
    await expect.poll(() => requested).toBe(true);
    await page.keyboard.press('Escape');
    release();
    await expect(panel).toHaveCount(0);
    await page.getByRole('button', { name: 'Agents on entry to Review', exact: true }).click();
    await panel.getByRole('button', { name: 'Edit description for Code reviewer', exact: true }).click();
    await expect(panel.getByRole('textbox', { name: 'Agent description for Code reviewer', exact: true })).toHaveValue('Checks the changes');
    expect(writes).toHaveLength(0);
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
    await page.getByRole('button', { name: 'Chat with agent', exact: true }).click();
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
        await page.getByRole('button', { name: 'Chat with agent', exact: true }).click();
        await expect.poll(() => page.evaluate(() => window.__chatTabs)).toEqual(['board_background']);

        const writes = requests.filter(item => item.method === 'PUT' || item.path.endsWith('/launch'));
        // An unchanged assignee must be omitted so a stale editor cannot overwrite a sync edit.
        expect(writes[0].body).not.toHaveProperty('assignee');
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
    await expect(page.getByRole('button', { name: 'Chat with agent', exact: true })).toBeDisabled();
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
    for (const name of ['Start work', 'Save']) {
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
    expect(requests.filter(request => request.path.startsWith('/api/v1/board/cards')
        && request.path !== '/api/v1/board/cards/activity' && request.method !== 'GET')).toHaveLength(0);
});

test('new cards queue files until Save and do not launch', async ({ page }) => {
    const requests = await openBoard(page);
    await page.getByRole('button', { name: 'New card', exact: true }).click();
    await expect(page.locator('[data-board-card-links]')).toContainText('Links will be saved when you create this card.');
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


test('Automation recordings have their own rail and live robot without overwriting the draft', async ({ page }, testInfo) => {
    let card;
    const requests = await openBoard(page, { assignee: 'base:codex', onCard(value) {
        card = value;
        card.priority = 'critical'; card.points = 8; card.tags = ['keep-stored'];
        card.hasActiveAutomation = true;
        card.activeSessionId = 'automation_session'; card.activeTabId = 'automation_tab';
        card.sessions.push({ id: 'automation_session', tabId: 'automation_tab', displayName: 'Review code',
            cli: 'shell', isAutomation: true, active: true, createdAt: '2026-09-25T18:00:00Z' });
        card.sessions.push({ id: 'older_automation', displayName: 'Earlier review', cli: 'codex',
            isAutomation: true, active: false, createdAt: '2026-09-24T18:00:00Z' });
    } });
    const tile = page.locator('.board-card[data-card-id="card_test"]');
    await expect(tile).not.toContainText('keep-stored');
    await expect(page.locator('[data-board-filter-tag], .board-tag')).toHaveCount(0);
    await expect(tile).toHaveClass(/is-live/);
    await expect(tile.getByRole('button', { name: 'Go to running Automation' })).toBeVisible();
    await page.screenshot({ path: testInfo.outputPath('board-automation-card.png'), fullPage: true });
    await page.evaluate(() => {
        window.__focusedAutomation = null;
        window.app.boardController.focusSessionTab = async (_card, session) => { window.__focusedAutomation = session.id; };
    });
    await tile.getByRole('button', { name: 'Go to running Automation' }).click();
    await expect.poll(() => page.evaluate(() => window.__focusedAutomation)).toBe('automation_session');
    await expect(page.locator('[data-board-card-editor]')).toHaveCount(0);
    await tile.click();
    const editor = page.locator('[data-board-card-editor]');
    await expect(editor.locator('[data-board-sessions] [data-session-id]')).toHaveCount(1);
    await expect(editor.locator('[data-board-automations] [data-session-id]')).toHaveCount(2);
    await expect(editor.locator('[data-board-count="sessions"]')).toHaveText('1');
    await expect(editor.locator('[data-board-count="automations"]')).toHaveText('2');
    await expect(editor.locator('[data-board-automations] [data-board-open-session="automation_session"]')).toBeEnabled();
    await expect(editor.locator('#board-card-priority, #board-card-points, #board-card-tags')).toHaveCount(0);
    await expect(editor.getByText('Uses this CLI', { exact: false })).toHaveCount(0);
    await expect(editor.locator('[data-board-launch-yolo]')).toBeVisible();
    await editor.locator('#board-card-title').fill('My unsaved title');
    await editor.locator('[data-board-automations]').scrollIntoViewIfNeeded();
    await page.screenshot({ path: testInfo.outputPath('board-automation-rails.png'), fullPage: true });
    card.sessions[1].active = false;
    card.hasActiveAutomation = false; card.activeSessionId = null; card.activeTabId = null;
    requests.length = 0;
    await page.evaluate(() => window.app.boardController.refreshSessionActivity());
    expect(requests.some(request => request.path === '/api/v1/board/cards')).toBe(false);
    expect(requests.find(request => request.path === '/api/v1/board/cards/activity')?.body)
        .toEqual({ boardId: 'brd_main', cardIds: ['card_test'] });
    await expect(tile.getByRole('button', { name: 'Go to running Automation' })).toHaveCount(0);
    await expect(tile).not.toHaveClass(/is-live/);
    await expect(editor.locator('[data-board-automations] .board-automation-running')).toHaveCount(0);
    await expect(editor.locator('#board-card-title')).toHaveValue('My unsaved title');
    await expect(editor.locator('[data-board-automations]')).toContainText('Earlier review');
    const savedRequest = page.waitForRequest(request => request.method() === 'PUT' && new URL(request.url()).pathname === '/api/v1/board/cards/card_test');
    await editor.locator('[data-board-save-card]').click();
    const payload = (await savedRequest).postDataJSON();
    for (const field of ['priority', 'points', 'tags']) expect(Object.hasOwn(payload, field)).toBe(false);
    expect(card.priority).toBe('critical'); expect(card.points).toBe(8); expect(card.tags).toEqual(['keep-stored']);
});

for (const width of [1440, 390]) {
    test(`card Automation runs stay linked and preserve drafts at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 900 });
        await page.addInitScript(() => localStorage.setItem('viberails.board.filters.v1', JSON.stringify({ tag: 'retired-filter' })));
        let card;
        const requests = await openBoard(page, { onCard(value) { card = value; card.tags = ['hidden-tag']; } });
        let posts = 0;
        let release;
        const gate = new Promise(resolve => { release = resolve; });
        const run = { id: 'run_card', name: 'Review this card', status: 0 };
        await page.route('**/api/v1/board/cards/card_test/automations', async route => {
            if (route.request().method() === 'POST') {
                posts++;
                expect(route.request().postDataJSON()).toEqual({ jobId: 11 });
                await gate;
                return route.fulfill({ json: { success: true, runId: run.id } });
            }
            return route.fulfill({ json: {
                jobs: [{ id: 11, name: 'Review this card', enabled: true }, { id: 12, name: 'Paused', enabled: false }],
                runs: posts && !card.sessions.some(session => session.id === 'new_recording') ? [run] : []
            } });
        });
        expect(requests.filter(request => request.path === '/api/v1/board/cards').every(request => !request.body?.tag)).toBe(true);
        expect(await page.evaluate(() => window.app.boardController.state.filters.tag)).toBeUndefined();
        await expect(page.locator('[data-board-filter-tag], .board-tag')).toHaveCount(0);
        await page.getByText('Description images', { exact: true }).click();
        await page.locator('#board-card-title').fill('Unsaved title');
        const comment = page.locator('[data-board-composer="comment"] textarea');
        await comment.fill('Unsaved comment');
        const select = page.getByRole('combobox', { name: 'Automation to run' });
        await expect(select.locator('option[value="12"]')).toBeDisabled();
        await select.selectOption('11');
        const button = page.getByRole('button', { name: 'Run automation', exact: true });
        await button.scrollIntoViewIfNeeded();
        const bounds = await button.boundingBox();
        expect(bounds.x).toBeGreaterThanOrEqual(0);
        expect(bounds.x + bounds.width).toBeLessThanOrEqual(width);
        await page.screenshot({ path: testInfo.outputPath('card-automation-controls.png') });
        await button.click();
        await expect(button).toBeDisabled();
        await button.evaluate(element => element.click());
        expect(posts).toBe(1);
        release();
        await expect(page.locator('[data-board-automation-runs]')).toContainText('Queued');
        await expect(page.locator('[data-board-count="automations"]')).toHaveText('1');
        await expect(page.locator('#board-card-title')).toHaveValue('Unsaved title');
        await expect(comment).toHaveValue('Unsaved comment');
        expect(requests.filter(request => request.method === 'PUT')).toHaveLength(0);

        run.status = 3; run.errorMessage = 'Terminal could not start.';
        await page.evaluate(() => window.app.boardController.refreshSessionActivity());
        await expect(page.locator('[data-board-automation-runs]')).toContainText('Terminal could not start.');
        await page.locator('#modal-container [data-action="close-modal"]').first().click();
        await page.getByText('Description images', { exact: true }).click();
        await expect(page.locator('[data-board-automation-runs]')).toContainText('Failed');
        // When a recording arrives it owns the open/replay action in the existing rail.
        card.sessions.push({ id: 'new_recording', displayName: 'Review this card', isAutomation: true,
            cli: 'shell', active: false, createdAt: '2026-09-26T05:00:00Z' });
        await page.evaluate(() => window.app.boardController.refreshSessionActivity());
        await expect(page.locator('[data-board-automations] [data-board-open-session="new_recording"]')).toBeVisible();
        await expect(page.locator('[data-board-automation-runs] [data-board-run-id]')).toHaveCount(0);
        await page.locator('[data-board-automations]').scrollIntoViewIfNeeded();
        await page.screenshot({ path: testInfo.outputPath('card-automation-linked.png') });
    });
}

test('lane Automation entries show waiting and terminal reasons before a session exists', async ({ page }) => {
    await openBoard(page);
    const entry = { eventKey: 'entry-1', name: '<img src=x onerror="window.__entryXss=1">', status: 'Waiting',
        reason: 'Automation is busy; waiting for its turn.' };
    await page.route('**/api/v1/board/cards/card_test/automations', route => route.fulfill({ json: {
        jobs: [], runs: [], laneEntries: [entry]
    } }));
    await page.getByText('Description images', { exact: true }).click();
    const list = page.locator('[data-board-automation-runs]');
    await expect(list).toContainText('Waiting');
    await expect(list).toContainText('Automation is busy; waiting for its turn.');
    await expect(list.locator('img')).toHaveCount(0);
    await expect(page.locator('[data-board-count="automations"]')).toHaveText('1');
    await page.locator('#board-card-title').fill('Unsaved waiting card');
    for (const status of ['Queued', 'Running', 'Failed', 'Skipped', 'Cancelled']) {
        entry.status = status;
        entry.reason = status === 'Cancelled' ? 'Card left the destination lane.' : `Automation ${status.toLowerCase()}.`;
        await page.evaluate(() => window.app.boardController.refreshSessionActivity());
        await expect(list).toContainText(status);
        await expect(list).toContainText(entry.reason);
        await expect(page.locator('#board-card-title')).toHaveValue('Unsaved waiting card');
    }
    expect(await page.evaluate(() => window.__entryXss)).toBeUndefined();
});

test('card Automation load and launch failures can be retried without losing the selection', async ({ page }) => {
    await openBoard(page);
    let loadFailed = true;
    let launchFailed = true;
    await page.route('**/api/v1/board/cards/card_test/automations', route => {
        if (route.request().method() === 'POST') return route.fulfill(launchFailed
            ? { status: 409, json: { error: 'Automation is already running.' } }
            : { json: { success: true, runId: 'retry_run' } });
        return route.fulfill(loadFailed ? { status: 500, json: { error: 'Could not load Automations.' } }
            : { json: { jobs: [{ id: 11, name: '<img src=x onerror="window.__automationXss=1">', enabled: true }], runs: [] } });
    });
    await page.getByText('Description images', { exact: true }).click();
    const run = page.getByRole('button', { name: 'Run automation', exact: true });
    await expect(run).toBeDisabled();
    loadFailed = false;
    await page.getByRole('button', { name: 'Reload automations', exact: true }).click();
    const select = page.getByRole('combobox', { name: 'Automation to run' });
    await select.selectOption('11');
    await run.click();
    await expect(page.locator('[data-board-automation-message]')).toHaveText('Automation is already running.');
    await expect(select).toHaveValue('11');
    await expect(run).toBeEnabled();
    launchFailed = false;
    await run.click();
    await expect(page.getByText('Automation queued and linked to this card.', { exact: true })).toBeVisible();
    expect(await page.evaluate(() => window.__automationXss)).toBeUndefined();
});

test('closing a card while its Automation catalog loads cannot repaint the next editor', async ({ page }) => {
    await openBoard(page);
    let release;
    let requested;
    const started = new Promise(resolve => { requested = resolve; });
    const gate = new Promise(resolve => { release = resolve; });
    await page.route('**/api/v1/board/cards/card_test/automations', async route => {
        requested();
        await gate;
        await route.fulfill({ json: { jobs: [{ id: 11, name: 'Stale workflow', enabled: true }], runs: [] } }).catch(() => {});
    });
    await page.getByText('Description images', { exact: true }).click();
    await started;
    await page.locator('#modal-container [data-action="close-modal"]').first().click();
    await page.getByRole('button', { name: 'New card', exact: true }).click();
    release();
    await expect(page.getByText('Save the card to run an Automation.', { exact: true })).toBeVisible();
    await expect(page.locator('[data-board-automation-choice]')).toHaveCount(0);
    await expect(page.getByText('Stale workflow', { exact: true })).toHaveCount(0);
});


test('Switch reviewer Worker preset supports same-provider routing and editable fallback', async ({ page }, testInfo) => {
    await openBoard(page);
    const writes = [];
    await page.route('**/api/v1/environments', async route => {
        if (route.request().method() === 'POST') {
            writes.push(route.request().postDataJSON());
            return route.fulfill({ json: { success: true } });
        }
        return route.fulfill({ json: [] });
    });
    await page.evaluate(() => window.app.environmentController.showEnvironmentForm({ mode: 'create', automationWorker: true }));
    const form = page.locator('#env-form');
    await form.getByRole('button', { name: 'Switch reviewer preset', exact: true }).click();
    await expect(form.locator('#env-purpose')).toHaveValue('code_review');
    await expect(form.locator('#env-reviewer-mode')).toHaveValue('switch');
    const choices = form.locator('[data-reviewer-mappings] [data-reviewer-target]');
    await expect(choices.nth(0)).toHaveValue('base:codex');
    await expect(choices.nth(1)).toHaveValue('base:claude');
    await choices.nth(1).evaluate(select => select.tomselect.setValue('base:codex'));
    await form.locator('[data-reviewer-fallback] [data-reviewer-target]').evaluate(select => select.tomselect.setValue('base:claude'));
    await page.screenshot({ path: testInfo.outputPath('switch-worker-preset.png') });
    await form.getByRole('button', { name: 'Create Worker', exact: true }).click();
    await expect.poll(() => writes.length).toBe(1);
    expect(writes[0]).toMatchObject({ name: 'Switch reviewer', purpose: 'code_review', automationWorker: true,
        reviewerRouting: { mode: 'switch', mappings: [
            { sourceProvider: 'claude', reviewer: { selection: 'base:codex' } },
            { sourceProvider: 'codex', reviewer: { selection: 'base:codex' } }
        ], fallback: { selection: 'base:claude' } } });
});

test('Code review Worker preset defaults to Codex and remains editable', async ({ page }) => {
    await openBoard(page);
    const writes = [];
    await page.route('**/api/v1/environments', async route => {
        if (route.request().method() === 'POST') {
            writes.push(route.request().postDataJSON());
            return route.fulfill({ json: { success: true } });
        }
        return route.fulfill({ json: [] });
    });
    await page.evaluate(() => window.app.environmentController.showEnvironmentForm({ mode: 'create', automationWorker: true }));
    const form = page.locator('#env-form');
    await form.getByRole('button', { name: 'Code review preset', exact: true }).click();
    await expect(form.locator('#env-cli')).toHaveValue('codex');
    await expect(form.locator('#env-purpose')).toHaveValue('code_review');
    await expect(form.locator('#env-initial-message')).toHaveValue(/Save the review on the originating card/);
    await form.locator('#env-name').fill('My reviewer');
    await form.locator('#env-cli').evaluate(select => select.tomselect ? select.tomselect.setValue('claude') : select.value = 'claude');
    await form.locator('#env-initial-message').fill('Review the scoped changes and save the report.');
    await form.getByRole('button', { name: 'Create Worker', exact: true }).click();
    await expect.poll(() => writes.length).toBe(1);
    expect(writes[0]).toMatchObject({ name: 'My reviewer', cli: 'claude', purpose: 'code_review', automationWorker: true });
});

for (const width of [1440, 390]) {
    test(`Code reviews keep drafts, separate recordings and show scope at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 1000 });
        const requests = await openBoard(page, { onCard: card => {
            card.sessions = [
                { id: 'work-session', displayName: 'Working agent', cli: 'codex', origin: 'launch', active: false },
                { id: 'review-session', displayName: 'Review agent', cli: 'codex', origin: 'code_review', isAutomation: true, isReview: true, active: false }
            ];
        } });
        const report = { id: 'review-1', reviewer: 'Codex <img src=x onerror="window.__reviewsXss=1">', provider: 'codex',
            createdUtc: '2026-10-01T10:00:00Z', processStatus: 'Succeeded', sessionId: 'review-session',
            result: 'Findings', reportedUtc: '2026-10-01T10:02:00Z', workspace: 'C:/actual-checkout', scope: 'working-tree',
            scopeDescription: 'Dirty changes for this feature', includeDirty: true, scopeFiles: ['Modified: src/example.cs'],
            baseCommit: 'a'.repeat(40), headCommit: 'a'.repeat(40), findings: 'src/example.cs:10 — <script>window.__reviewsXss=1</script>',
            validation: 'Unit tests', limitations: 'No live provider', freshness: 'Unknown' };
        let rows = [report];
        let launches = [];
        await page.route('**/api/v1/board/cards/card_test/reviews?*', route => route.fulfill({ json: { reviews: rows, hasMore: false } }));
        await page.route('**/api/v1/board/cards/card_test/reviews/review-1*', route => route.fulfill({ json: {
            ...report, freshness: route.request().url().includes('verify=true') ? 'Stale — reviewed changes differ now' : 'Unknown'
        } }));
        await page.route('**/api/v1/board/cards/card_test/launch', route => {
            launches.push(route.request().postDataJSON());
            rows = [{ id: 'queued', reviewer: 'Codex', provider: 'codex', createdUtc: '2026-10-01T11:00:00Z', processStatus: 'Failed', error: 'CLI unavailable' }, report];
            return route.fulfill({ json: { tabId: 'tab-review', sessionId: 'session-new', cli: 'codex', cardId: 'card_test', cardKey: 'VB-1' } });
        });
        await page.getByText('Description images', { exact: true }).click();
        const editor = page.locator('[data-board-card-editor]');
        const reviews = editor.locator('[data-board-reviews]');
        await expect(reviews).toContainText('Findings');
        await expect(editor.locator('[data-board-sessions] [data-session-id]')).toHaveCount(1);
        await expect(editor.locator('[data-board-automations] [data-session-id]')).toHaveCount(0);
        await editor.locator('#board-card-title').fill('Keep this unsaved draft');
        await reviews.getByRole('button', { name: 'Run review', exact: true }).click();
        await expect.poll(() => launches.length).toBe(1);
        expect(launches[0]).toEqual({ selection: null, intent: 'code_review', review: { override: null } });
        await expect(editor.locator('#board-card-title')).toHaveValue('Keep this unsaved draft');
        await expect(reviews.locator('[data-review-latest]')).toContainText('Report missing');
        await expect(reviews.locator('[data-review-latest]')).toContainText('CLI unavailable');
        expect(requests.filter(r => r.method === 'PUT' && r.path === '/api/v1/board/cards/card_test')).toHaveLength(0);
        await reviews.locator('[data-review-latest]').getByRole('button', { name: 'View report' }).click();
        await expect(reviews.locator('[data-review-report]')).toContainText('C:/actual-checkout');
        await reviews.getByRole('button', { name: 'Compare review inputs' }).click();
        await expect(reviews.locator('[data-review-report]')).toContainText('Stale');
        await expect(reviews.locator('[data-review-report]')).toContainText('src/example.cs:10');
        await expect(reviews.locator('.board-check-result script, .board-check-result img, [data-review-report] script, [data-review-report] img')).toHaveCount(0);
        expect(await page.evaluate(() => Boolean(window.__reviewsXss))).toBe(false);
        await expect(editor.locator('#board-card-title')).toHaveValue('Keep this unsaved draft');
        await reviews.scrollIntoViewIfNeeded();
        await page.screenshot({ path: testInfo.outputPath(`reviews-${width}.png`) });
    });
}
