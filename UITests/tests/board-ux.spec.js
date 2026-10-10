const { test, expect } = process.env.VIBERAILS_BOARD_STATIC === '1'
    ? require('@playwright/test')
    : require('./fixtures');

const IMAGE = 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a5XcAAAAASUVORK5CYII=';
const DESCRIPTION = 'Repro screenshot\n![Screenshot.png](attachment:att_image)\n<img src=x onerror="window.__injected=true">';

async function expectAnimatedStyle(locator, property) {
    const read = () => locator.evaluate((element, name) => getComputedStyle(element)[name], property);
    const initial = await read();
    await expect.poll(read).not.toBe(initial);
}

for (const width of [1440, 390]) {
    test(`Jira boards combine General and Jira settings at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 950 });
        await openBoard(page, { onCard: card => {
            card.jiraIssueKey = 'PROJECT-WITH-A-LONG-KEY-123';
            card.jiraIssueUrl = 'https://example.atlassian.net/browse/PROJECT-WITH-A-LONG-KEY-123';
        } });
        const badge = page.locator('.board-card-origin');
        await expect(badge.locator('img')).toHaveAttribute('src', 'assets/img/jira.svg');
        await expect.poll(() => badge.locator('img').evaluate(image => image.complete && image.naturalWidth > 0)).toBe(true);
        expect(await badge.evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
        await page.screenshot({ path: testInfo.outputPath(`board-jira-badge-${width}.png`) });
        let isJiraBoard = true;
        await page.route('**/api/v1/board/boards/brd_main/sync', route => route.fulfill({ json: {
            isJiraBoard, enabled: !isJiraBoard, configured: true
        } }));
        await page.route('**/api/v1/board/boards/brd_main/jira?settings=true', route => route.fulfill({ json: {
            boardId: 'brd_main', siteUrl: 'https://example.atlassian.net', hasToken: true,
            authStatus: 'saved', boardLink: 'https://example.atlassian.net/jira/software/projects/TEST/boards/1',
            jiraBoardName: 'TEST', email: 'user@example.com', enabled: true, columns: [], lanes: []
        } }));
        await page.getByRole('button', { name: 'Board settings', exact: true }).click();
        await expect(page.locator('.modal-title img')).toHaveAttribute('src', 'assets/img/jira.svg');
        await expect(page.locator('[data-board-sync-content]')).toBeEmpty();
        await expect(page.locator('[data-board-sync]')).toBeHidden();
        await expect(page.getByLabel('Sync this board with viberails.ai')).toHaveCount(0);
        await expect(page.getByRole('tab', { name: 'Jira Cloud', exact: true })).toHaveCount(0);
        await expect(page.getByRole('tab')).toHaveText(['General', 'Agent context', 'History']);
        await expect(page.getByRole('tab', { name: 'General', exact: true })).toHaveAttribute('aria-selected', 'true');
        await expect(page.getByLabel('Board name', { exact: true })).toBeVisible();
        await expect(page.getByLabel('Jira board link')).toHaveValue('https://example.atlassian.net/jira/software/projects/TEST/boards/1');
        await expect(page.getByLabel('API token', { exact: true })).toHaveAttribute('placeholder', /^•+$/);
        await expect(page.getByLabel('API token', { exact: true })).toHaveValue('');
        await expect(page.getByRole('button', { name: 'Pull now', exact: true })).toBeEnabled();
        await expect(page.locator('.modal-header [data-jira-status]')).toHaveText('Connected');
        await page.getByLabel('Board name', { exact: true }).fill('Keep this draft');
        await page.getByLabel('API token', { exact: true }).fill('unsaved-test-token');
        await page.getByRole('tab', { name: 'General', exact: true }).focus();
        await page.keyboard.press('ArrowRight');
        await expect(page.getByRole('tab', { name: 'Agent context', exact: true })).toBeFocused();
        await expect(page.getByLabel('Jira board link')).toBeHidden();
        await expect(page.locator('.modal-header [data-jira-heading]')).toBeHidden();
        await page.keyboard.press('Home');
        await expect(page.getByRole('tab', { name: 'General', exact: true })).toBeFocused();
        await expect(page.getByLabel('Board name', { exact: true })).toHaveValue('Keep this draft');
        await expect(page.getByLabel('API token', { exact: true })).toHaveValue('unsaved-test-token');
        await expect(page.locator('.modal-header [data-jira-heading]')).toBeVisible();
        await page.getByLabel('API token', { exact: true }).fill('');
        await page.getByRole('tab', { name: 'General', exact: true }).scrollIntoViewIfNeeded();
        await page.screenshot({ path: testInfo.outputPath(`board-jira-combined-${width}.png`) });
        await page.evaluate(() => window.app.closeModal());

        // An ordinary board still offers both the sync choice and its manual action.
        isJiraBoard = false;
        await page.getByRole('button', { name: 'Board settings', exact: true }).click();
        await expect(page.getByLabel('Sync this board with viberails.ai')).toBeChecked();
        await expect(page.getByRole('button', { name: 'Sync now', exact: true })).toBeVisible();
        await expect(page.getByRole('tab')).toHaveText(['General', 'Jira Cloud', 'Agent context', 'History']);
    });
}

for (const width of [1440, 390]) {
    test(`Jira unlink confirms, handles failure and preserves settings drafts at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 950 });
        await openBoard(page);
        let linked = true;
        let attempts = 0;
        await page.route('**/api/v1/board/boards/brd_main/sync', route => route.fulfill({ json: {
            isJiraBoard: linked, enabled: false, configured: true
        } }));
        await page.route(url => url.pathname === '/api/v1/board/boards/brd_main/jira', route => {
            if (route.request().method() === 'DELETE') {
                attempts++;
                if (attempts === 1) return route.fulfill({ status: 400, json: { error: 'Another Jira operation is running. Try again when it finishes.' } });
                linked = false;
                return route.fulfill({ json: { message: 'Jira unlinked' } });
            }
            return route.fulfill({ json: linked ? {
                boardId: 'brd_main', siteUrl: 'https://example.atlassian.net', hasToken: true,
                authStatus: 'saved', boardLink: 'https://example.atlassian.net/boards/1',
                jiraBoardName: 'TEST', email: 'user@example.com', enabled: true, columns: [], lanes: []
            } : { boardId: 'brd_main', authStatus: 'none', columns: [], lanes: [] } });
        });
        await page.getByRole('button', { name: 'Board settings', exact: true }).click();
        const icon = page.locator('.modal-title').getByRole('img', { name: 'Jira board', exact: true });
        await expect(icon).toBeVisible();
        await expect(page.getByLabel('API token', { exact: true })).toHaveAttribute('placeholder', '•'.repeat(32));
        await page.getByLabel('Board name', { exact: true }).fill('Keep my name draft');
        const panel = page.locator('[data-jira-panel]');
        const unlink = panel.getByRole('button', { name: 'Unlink Jira', exact: true });
        await unlink.scrollIntoViewIfNeeded();
        await page.screenshot({ path: testInfo.outputPath(`board-jira-unlink-button-${width}.png`) });
        await unlink.click();
        await expect(page.getByRole('alertdialog')).toContainText('imported cards, comments, files and sessions stay');
        await page.getByRole('alertdialog').getByRole('button', { name: 'Cancel', exact: true }).click();
        expect(attempts).toBe(0);
        await expect(unlink).toBeEnabled();

        await unlink.click();
        await page.getByRole('alertdialog').getByRole('button', { name: 'Unlink Jira', exact: true }).click();
        await expect(panel.locator('[data-jira-report]')).toContainText('Another Jira operation is running');
        await expect(icon).toBeVisible();
        await expect(unlink).toBeEnabled();

        await unlink.click();
        await page.getByRole('alertdialog').getByRole('button', { name: 'Unlink Jira', exact: true }).click();
        await expect(panel.locator('[data-jira-report]')).toContainText('Jira unlinked');
        await expect(icon).toHaveCount(0);
        await expect(page.getByRole('tab')).toHaveText(['General', 'Jira Cloud', 'Agent context', 'History']);
        await expect(unlink).toBeHidden();
        await expect(panel.getByRole('button', { name: 'Pull now', exact: true })).toBeDisabled();
        await expect(panel.getByLabel('API token', { exact: true })).toHaveValue('');
        await expect(panel.getByLabel('API token', { exact: true })).toHaveAttribute('placeholder', 'Paste an API token');
        await expect(panel.getByLabel('Jira board link')).toHaveValue('');
        await page.screenshot({ path: testInfo.outputPath(`board-jira-unlinked-${width}.png`) });
        await page.getByRole('tab', { name: 'General', exact: true }).click();
        await expect(page.getByLabel('Board name', { exact: true })).toHaveValue('Keep my name draft');
        await expect(page.getByLabel('Sync this board with viberails.ai')).toBeVisible();
        expect(attempts).toBe(2);
    });
}

for (const selected of ['Jira Cloud', 'Agent context', 'Jira input']) {
    test(`delayed Jira board status preserves drafts and the selected ${selected} section`, async ({ page }) => {
        await openBoard(page);
        let releaseStatus;
        const statusReady = new Promise(resolve => { releaseStatus = resolve; });
        await page.route('**/api/v1/board/boards/brd_main/sync', async route => {
            await statusReady;
            await route.fulfill({ json: { isJiraBoard: true } });
        });
        await page.route('**/api/v1/board/boards/brd_main/jira?settings=true', route => route.fulfill({ json: {
            boardId: 'brd_main', authStatus: 'none', columns: [], lanes: []
        } }));
        await page.getByRole('button', { name: 'Board settings', exact: true }).click();
        await page.getByLabel('Board name', { exact: true }).fill('Draft before status');
        await page.getByRole('tab', { name: 'Jira Cloud', exact: true }).click();
        await page.getByLabel('Jira board link').fill('https://example.atlassian.net/boards/42');
        await page.getByLabel('API token', { exact: true }).fill('draft-token');
        const token = page.getByLabel('API token', { exact: true });
        if (selected === 'Jira input') {
            await token.focus();
            await token.evaluate(input => input.setSelectionRange(2, 5));
        } else {
            await page.getByRole('tab', { name: selected, exact: true }).click();
        }
        releaseStatus();
        await expect(page.getByRole('tab', { name: 'Jira Cloud', exact: true })).toHaveCount(0);
        const active = selected === 'Agent context' ? selected : 'General';
        await expect(page.getByRole('tab', { name: active, exact: true })).toHaveAttribute('aria-selected', 'true');
        if (selected === 'Jira input') {
            await expect(token).toBeFocused();
            expect(await token.evaluate(input => [input.selectionStart, input.selectionEnd])).toEqual([2, 5]);
        } else {
            await expect(page.getByRole('tab', { name: active, exact: true })).toBeFocused();
        }
        await page.getByRole('tab', { name: 'General', exact: true }).click();
        await expect(page.getByLabel('Board name', { exact: true })).toHaveValue('Draft before status');
        await expect(page.getByLabel('Jira board link')).toHaveValue('https://example.atlassian.net/boards/42');
        await expect(page.getByLabel('API token', { exact: true })).toHaveValue('draft-token');
    });
}

test('Connect selects its dedicated Jira board and preserves other settings drafts', async ({ page }) => {
    await openBoard(page);
    let connected = false;
    const calls = [];
    await page.route('**/api/v1/board/**', route => {
        const path = new URL(route.request().url()).pathname;
        const method = route.request().method();
        calls.push(`${method} ${path}`);
        const connection = { boardId: connected ? 'brd_jira' : 'brd_main', email: 'ada@example.com',
            hasToken: connected, authStatus: connected ? 'saved' : 'none',
            boardLink: connected ? 'https://acme.atlassian.net/jira/software/projects/TEST/boards/1' : null,
            columns: [], lanes: [] };
        if (path.endsWith('/jira') && method === 'PUT') {
            connected = true;
            return route.fulfill({ json: { ...connection, boardId: 'brd_jira', hasToken: true, authStatus: 'saved' } });
        }
        if (path.endsWith('/jira')) return route.fulfill({ json: connection });
        if (path.endsWith('/jira/test')) return route.fulfill({ json: { ok: true, account: 'Ada' } });
        if (path.endsWith('/jira/pull')) return route.fulfill({ json: { outcome: 'ok', boardId: 'brd_jira', message: 'Pull complete' } });
        if (path.endsWith('/context')) return route.fulfill({ json: { revision: 1, context: { defaultMessage: '', typeOverrides: [] } } });
        if (path.endsWith('/boards')) return route.fulfill({ json: { boards: [
            { id: 'brd_main', name: 'Main', position: 0 }, ...(connected ? [{ id: 'brd_jira', name: 'Jira · TEST', position: 1 }] : [])
        ] } });
        if (path.endsWith('/columns')) return route.fulfill({ json: { columns: [] } });
        return route.fulfill({ json: { cards: [] } });
    });
    await page.getByRole('button', { name: 'Board settings', exact: true }).click();
    await page.locator('#board-board-name').fill('Keep this name draft');
    await page.getByRole('tab', { name: 'Jira Cloud', exact: true }).click();
    const panel = page.locator('[data-jira-panel]');
    await panel.getByLabel('Jira board link').fill('https://acme.atlassian.net/jira/software/projects/TEST/boards/1');
    await panel.getByLabel('API token').fill('test-only');
    await panel.getByRole('button', { name: 'Connect', exact: true }).click();
    await expect(panel.locator('[data-jira-board]')).toHaveText('Jira · TEST');
    await expect(page.locator('[data-board-select]')).toHaveValue('brd_jira');
    await expect(panel.locator('[data-jira-report]')).toHaveText('Connected as Ada.');
    await panel.getByRole('button', { name: 'Pull now' }).click();
    await expect(panel.locator('[data-jira-report]')).toHaveText('Pull complete');
    expect(calls).toContain('POST /api/v1/board/boards/brd_jira/jira/test');
    expect(calls).toContain('POST /api/v1/board/boards/brd_jira/jira/pull');
    await page.getByRole('tab', { name: 'General', exact: true }).click();
    await expect(page.locator('#board-board-name')).toHaveValue('Keep this name draft');
});

for (const width of [1440, 390]) {
    test(`Jira connection lives in Board Settings and saves independently at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 950 });
        const requests = await openBoard(page);
        await page.route('**/api/v1/board/boards/brd_main/context', route => route.fulfill({ json: {
            revision: 1, context: { defaultMessage: '', typeOverrides: [] }
        } }));
        // VIBE-102: one board link and a token. The server's answers are mocked; the shapes are the
        // GET ?settings=true view, the PUT save and the Connect (test) report.
        const boardLink = 'https://robstokes857.atlassian.net/jira/software/projects/SCRUM/boards/1?filter=&groupBy=none&atlOrigin=abc';
        const savedLink = 'https://robstokes857.atlassian.net/jira/software/projects/SCRUM/boards/1';
        const lanes = [{ id: 'col_ready', name: 'Ready' }, { id: 'col_done', name: 'Done' }];
        const columns = [
            { name: 'To Do', laneId: 'col_ready', laneName: 'Ready', automatic: true },
            { name: 'In Progress', laneId: null, laneName: null, automatic: true },
            { name: 'Done', laneId: 'col_done', laneName: 'Done', automatic: true }
        ];
        let saved = { boardId: 'brd_main', hasToken: false, authStatus: 'none', enabled: false, columns: [], lanes,
            suggestedEmail: 'robstokes857@gmail.com' };
        const writes = [];
        let tests = 0;
        await page.route(url => url.pathname === '/api/v1/board/boards/brd_main/jira', route => {
            if (route.request().method() === 'PUT') {
                const body = route.request().postDataJSON();
                writes.push(body);
                saved = { ...saved, ...body, apiToken: undefined, columnMap: undefined, boardLink: savedLink, jiraBoardId: '1',
                    siteUrl: 'https://robstokes857.atlassian.net', jql: 'project = "SCRUM"', hasToken: true, authStatus: 'saved' };
                return route.fulfill({ json: { ...saved, columns: null, lanes: null } });
            }
            return route.fulfill({ json: saved });
        });
        await page.route(url => url.pathname === '/api/v1/board/boards/brd_main/jira/test', route => {
            tests++;
            saved = { ...saved, jiraBoardName: 'SCRUM board', columns };
            return route.fulfill({ json: { ok: true, account: 'Rob Stokes', board: { id: '1', name: 'SCRUM board', type: 'scrum',
                issueCount: 12, storyPointsFieldId: 'customfield_10016', storyPointsFieldName: 'Story point estimate', columns,
                warnings: [] } } });
        });
        await page.locator('[data-board-action="edit-board"]').click();
        const panel = page.locator('[data-jira-panel]');
        await page.getByRole('tab', { name: 'Jira Cloud', exact: true }).click();
        await expect(panel.locator('[data-jira-board]')).toHaveText('Main');
        await expect(panel.getByLabel('Jira board link')).toHaveValue('');
        await expect(panel.getByLabel('Atlassian account email')).toHaveValue('robstokes857@gmail.com');
        await expect(panel.getByLabel('API token')).toHaveValue('');
        await expect(panel.getByRole('link', { name: 'Create a token' })).toHaveAttribute('href', 'https://id.atlassian.com/manage-profile/security/api-tokens');
        await expect(panel.getByRole('link', { name: 'Create a token' })).toHaveAttribute('target', '_blank');
        await expect(panel.getByLabel('Site URL')).toHaveCount(0);
        await expect(panel.getByLabel('JQL filter')).toHaveCount(0);

        await panel.getByLabel('Jira board link').fill(boardLink);
        await panel.getByLabel('API token').fill('typed-secret');
        await page.getByRole('tab', { name: 'General', exact: true }).click();
        await page.locator('#board-board-name').fill('Unsaved board name');
        await page.getByRole('tab', { name: 'General', exact: true }).press('ArrowRight');
        await expect(page.getByRole('tab', { name: 'Jira Cloud', exact: true })).toBeFocused();
        await expect(panel.getByLabel('API token')).toHaveValue('typed-secret');
        await expect(panel.getByLabel('Jira board link')).toHaveValue(boardLink);
        await panel.getByRole('button', { name: 'Connect' }).click();
        await expect(panel.locator('[data-jira-report]')).toHaveText('Connected as Rob Stokes.');
        expect(writes).toHaveLength(1);
        expect(writes[0]).toMatchObject({ boardLink, apiToken: 'typed-secret', email: 'robstokes857@gmail.com',
            enabled: true, skipOldDone: true, narrowJql: '' });
        expect(tests).toBe(1);
        await expect(panel.locator('[data-jira-summary]')).toContainText('SCRUM board (scrum) · about 12 issues');
        await expect(panel.locator('[data-jira-summary]')).toContainText('In Progress → Jira lane (unmatched)');
        await expect(panel.getByLabel('API token')).toHaveValue('');
        expect(requests.filter(request => request.method === 'PUT')).toEqual([]);
        await expect(page.locator('[data-board-action="jira-pull"]')).toHaveCount(0);

        // A lane picked for a column under Advanced is saved by the next Connect.
        await panel.locator('[data-jira-advanced] summary').click();
        await panel.getByLabel('In Progress', { exact: true }).selectOption('col_ready');
        await panel.getByRole('button', { name: 'Connect' }).click();
        await expect.poll(() => writes.length).toBe(2);
        expect(writes[1].columnMap).toEqual({ 'To Do': '', 'In Progress': 'col_ready', Done: '' });
        expect(writes[1].apiToken).toBe('');
        expect(writes[1].boardLink).toBe(savedLink);
        await expect(panel.locator('[data-jira-report]')).toHaveText('Connected as Rob Stokes.');

        expect(await panel.evaluate(el => el.scrollWidth <= el.clientWidth + 1)).toBe(true);
        await panel.locator('[data-jira-lanes]').scrollIntoViewIfNeeded();
        await page.screenshot({ path: testInfo.outputPath(`board-jira-connected-${width}.png`) });
        await page.getByRole('tab', { name: 'Jira Cloud', exact: true }).scrollIntoViewIfNeeded();
        await page.screenshot({ path: testInfo.outputPath(`board-jira-${width}.png`) });
        await panel.getByLabel('API token').fill('unsaved-secret');
        await page.evaluate(() => window.app.closeModal());
        await page.locator('[data-board-action="edit-board"]').click();
        await page.getByRole('tab', { name: 'Jira Cloud', exact: true }).click();
        await expect(panel.getByLabel('Jira board link')).toHaveValue(savedLink);
        await expect(panel.getByLabel('API token')).toHaveValue('');
        await page.evaluate(() => window.app.boardController.openBoardEditor(null));
        await expect(panel).toHaveCount(0);
        await expect(page.locator('[data-board-board-editor]')).toContainText('Save the board to configure Jira');
        await page.evaluate(() => { window.app.closeModal(); window.app.navigate('settings'); });
        await expect(page.getByRole('tab', { name: 'General', exact: true })).toBeVisible();
        await expect(page.getByRole('tab', { name: 'Integrations', exact: true })).toHaveCount(0);
        await expect(panel).toHaveCount(0);
    });
}

for (const width of [1440, 390]) {
    test(`attention comments stay red and readable at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 950 });
        await openBoard(page, { onCard: card => {
            card.flagged = true;
            card.comments = [{ id: 'attention', author: { kind: 'agent', label: 'Codex', cli: 'codex' },
                body: '**Security issue:** Please confirm which users may access this endpoint. <script>alert(1)</script>',
                createdAt: '2026-10-03T12:00:00Z', isAttention: true }];
        } });
        await page.getByText('Description images', { exact: true }).click();
        const comment = page.locator('.board-comment.is-attention');
        await comment.scrollIntoViewIfNeeded();
        await expect(comment).toBeVisible();
        await expect(comment).toContainText('Needs your attention');
        await expect(comment).toHaveCSS('border-left-color', 'rgb(239, 68, 68)');
        await expect(comment).toHaveCSS('background-color', 'rgb(56, 28, 36)');
        await expect(comment.locator('.board-comment-body')).toHaveCSS('color', 'rgb(252, 165, 165)');
        await expect(comment.locator('script')).toHaveCount(0);
        expect((await comment.boundingBox()).width).toBeLessThan(width);
        await page.screenshot({ path: testInfo.outputPath(`attention-comment-${width}.png`) });
    });
}

for (const width of [1440, 390]) {
    test(`Markdown is always enabled and Description has no preview at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 950 });
        await page.addInitScript(() => localStorage.setItem('viberails.board.markdown', 'off'));
        await openBoard(page);
        await page.getByText('Description images', { exact: true }).click();
        const description = page.locator('[data-board-composer="description"]');
        await expect(description.locator('textarea')).toBeVisible();
        await expect(description.locator('[data-board-composer-live], [data-board-composer-preview], [data-board-composer-toggle]')).toHaveCount(0);
        await expect(page.getByRole('button', { name: 'Markdown', exact: true })).toHaveCount(0);
        const text = '# Heading\n**bold** and *italic*\n- [x] checked\n' + DESCRIPTION;
        await description.locator('textarea').fill(text);
        const composer = page.locator('[data-board-composer="comment"]');
        await composer.locator('textarea').fill(text);
        const live = composer.locator('[data-board-composer-live]');
        await expect(live.locator('h1')).toHaveText('Heading');
        await expect(live.locator('strong')).toHaveText('bold');
        await expect(live.locator('img')).toBeVisible();
        const image = await live.locator('img').boundingBox();
        expect(image.width).toBeLessThanOrEqual(120);
        expect(image.height).toBeLessThanOrEqual(80);
        expect(await page.evaluate(() => window.__injected)).toBeUndefined();
        await composer.scrollIntoViewIfNeeded();
        await page.screenshot({ path: testInfo.outputPath(`composer-${width}.png`) });
        await page.locator('[data-board-save-card]').click();
        await page.getByText('Description images', { exact: true }).click();
        await expect(description.locator('textarea')).toHaveValue(text);
    });
}

for (const width of [1440, 390]) {
    test(`Previous work and checks stay beside the description at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 950 });
        await openBoard(page, { onCard: card => {
            card.previousWork = { outcome: 'Retained handoff', author: { label: 'Codex' }, files: [] };
            card.comments = [
                { id: 'old', body: 'Human question', author: { kind: 'user' }, createdAt: '2026-10-01' },
                { id: 'agent', body: 'Routine agent progress', author: { kind: 'agent' }, createdAt: '2026-10-02' },
                { id: 'flag', body: 'Important decision', isAttention: true, author: { kind: 'agent' }, createdAt: '2026-10-03' }
            ];
        } });
        await page.getByText('Description images', { exact: true }).click();
        const editor = page.locator('[data-board-card-editor]');
        const previous = editor.locator('.board-editor-side [data-board-previous-work] details');
        await expect(previous).not.toHaveAttribute('open');
        await expect(editor.locator('.board-editor-main [data-board-checks], .board-editor-main [data-board-previous-work]')).toHaveCount(0);
        await expect(editor.locator('.board-editor-side [data-board-checks]')).toHaveCount(1);
        await expect(editor.locator('.board-editor-main [data-board-add-files]')).toHaveCount(1);
        const comments = editor.locator('[data-board-comments] .board-comment');
        await expect(comments.first()).toContainText('Important decision');
        await editor.getByLabel('Show comments').selectOption('human');
        await expect(comments).toHaveCount(2);
        await expect(comments.first()).toContainText('Important decision');
        await expect(comments.last()).toContainText('Human question');
        await editor.getByLabel('Show comments').selectOption('agent');
        await expect(comments).toHaveCount(2);
        await expect(comments.last()).toContainText('Routine agent progress');
        await previous.locator('summary').click();
        await expect(previous).toContainText('Retained handoff');
        expect(await editor.evaluate(el => el.scrollWidth <= el.clientWidth + 1)).toBe(true);
        await page.screenshot({ path: testInfo.outputPath(`editor-sidebar-${width}.png`) });
    });
}

test('the timer refreshes moved cards and comments while keeping editor drafts and filters', async ({ page }) => {
    let current;
    const columns = [{ id: 'col_ready', name: 'Ready', position: 0 }, { id: 'review', name: 'Review', position: 1 }];
    const requests = await openBoard(page, { columns, onCard: card => { current = card; } });
    await page.getByText('Description images', { exact: true }).click();
    await page.locator('#board-card-title').fill('Unsaved title');
    const description = page.locator('[data-board-composer="description"] textarea');
    const comment = page.locator('[data-board-composer="comment"] textarea');
    await description.fill('Unsaved description');
    await comment.fill('Unsaved comment');
    await page.getByLabel('Show comments').selectOption('human');
    current.columnId = 'review'; current.title = 'Updated remotely';
    current.comments.push({ id: 'flag', body: 'New agent flag', isAttention: true, author: { kind: 'agent' }, createdAt: '2026-10-03' });
    await expect(page.locator('[data-column-id="review"] .board-card-title')).toHaveText('Updated remotely', { timeout: 15000 });
    await expect(page.locator('[data-board-comments] .board-comment').first()).toContainText('New agent flag');
    await expect(page.locator('#board-card-title')).toHaveValue('Unsaved title');
    await expect(description).toHaveValue('Unsaved description');
    await expect(comment).toHaveValue('Unsaved comment');
    await expect(page.getByLabel('Show comments')).toHaveValue('human');
    const reads = requests.filter(r => r.path === '/api/v1/board/cards').length;
    current.title = 'Changed again';
    await page.evaluate(() => window.dispatchEvent(new Event('focus')));
    await expect(page.locator('[data-column-id="review"] .board-card-title')).toHaveText('Changed again');
    expect(requests.filter(r => r.path === '/api/v1/board/cards').length).toBeGreaterThan(reads);
});

test('remote image comments refresh their metadata and preserve composer drafts', async ({ page }) => {
    let current;
    await openBoard(page, { onCard: card => { current = card; } });
    await page.getByText('Description images', { exact: true }).click();
    const description = page.locator('[data-board-composer="description"] textarea');
    const comment = page.locator('[data-board-composer="comment"] textarea');
    await description.fill('Unsaved description');
    await comment.fill('Unsaved comment');
    current.attachments.push({ id: 'remote-image', name: 'Remote.png', mimeType: 'image/png', url: '', bytes: 68 });
    current.commits.push({ sha: 'abcdef0123456', shortSha: 'abcdef0', message: 'Remote commit' });
    current.comments.push({ id: 'remote-comment', author: { kind: 'agent' },
        body: '![Remote](attachment:remote-image) #abcdef0', createdAt: '2026-10-03' });
    let reads = 0;
    await page.route('**/api/v1/board/cards/card_test/attachments/remote-image/content', route => {
        reads++;
        expect(route.request().headers().viberails_tab).toBe('board-fixture');
        return route.fulfill({ contentType: 'application/octet-stream', body: Buffer.from(IMAGE.split(',')[1], 'base64') });
    });
    await page.evaluate(() => window.app.boardController.refreshSessionActivity());
    const image = page.locator('[data-board-comments] [data-board-image="remote-image"]');
    await expect(image).toHaveAttribute('src', /^blob:/);
    await expect.poll(() => image.evaluate(img => img.complete && img.naturalWidth > 0)).toBe(true);
    expect(reads).toBe(1);
    await expect(page.locator('[data-board-comments] [data-board-ref-commit]')).toHaveAttribute('title', 'Remote commit');
    await expect(page.locator('[data-board-attachments]')).toContainText('Remote.png');
    await expect(page.locator('[data-board-commits]')).toContainText('Remote commit');
    await expect(description).toHaveValue('Unsaved description');
    await expect(comment).toHaveValue('Unsaved comment');
});

test('purpose filters keep relevant agent comments, attention and unsaved drafts on refresh', async ({ page }) => {
    let current;
    await openBoard(page, { onCard: card => {
        current = card;
        card.comments = [
            { id: 'tests', body: 'Suite passed', purpose: 'testing', author: { kind: 'agent' }, createdAt: '2026-10-03' },
            { id: 'review', body: 'Review result', purpose: 'code_review', author: { kind: 'agent' }, createdAt: '2026-10-03' }
        ];
    } });
    await page.getByText('Description images', { exact: true }).click();
    await page.locator('#board-card-title').fill('Unsaved title');
    const draft = page.locator('[data-board-composer="comment"] textarea');
    await draft.fill('Unsaved comment');
    await page.getByLabel('Show comments').selectOption('testing');
    const comments = page.locator('[data-board-comments]');
    await expect(comments).toContainText('Suite passed');
    await expect(comments).not.toContainText('Review result');
    current.comments.push({ id: 'attention', body: 'Needs your decision', purpose: 'deploying', isAttention: true,
        author: { kind: 'agent' }, createdAt: '2026-10-04' });
    await page.evaluate(() => window.app.boardController.refreshSessionActivity());
    await expect(comments).toContainText('Needs your decision');
    await expect(comments).not.toContainText('Review result');
    await expect(page.getByLabel('Show comments')).toHaveValue('testing');
    await expect(page.locator('#board-card-title')).toHaveValue('Unsaved title');
    await expect(draft).toHaveValue('Unsaved comment');
});

test('reference search routes GUIDs directly, finds card sessions and attaches commits without saving drafts', async ({ page }) => {
    let current;
    await openBoard(page, { onCard: card => { current = card; } });
    const session = { id: '12345678-1234-1234-1234-123456789abc', displayName: 'Earlier investigation', workingDirectory: 'C:/board-fixture', cli: 'codex', createdAt: '2026-10-05T15:30:00Z', active: false };
    const commit = { sha: 'abcdef0123456789abcdef0123456789abcdef01', shortSha: 'abcdef0', message: 'Fix the issue' };
    let fileReads = 0, cardReads = 0, historyReads = 0, sessionLinks = 0, commitLinks = 0;
    await page.route('**/api/v1/board/files?*', route => { fileReads++; return route.fulfill({ json: { files: [] } }); });
    await page.route('**/api/v1/chatHistory/*', route => { historyReads++; expect(route.request().url()).toContain(session.id); return route.fulfill({ json: session }); });
    await page.route('**/api/v1/board/cards/link-candidates?*', route => { cardReads++; return route.fulfill({ json: { cards: [{ id: 'other_card', key: 'VB-AAAAA-7', displayId: 'VIBE-7', title: 'Other card' }] } }); });
    await page.route('**/api/v1/board/cards/other_card/sessions', route => route.fulfill({ json: [session] }));
    await page.route('**/api/v1/board/cards/card_test/sessions', route => {
        sessionLinks++; current.sessions.push(session); return route.fulfill({ json: session });
    });
    await page.route('**/api/v1/board/cards/card_test/commits', route => {
        commitLinks++; current.commits.push(commit); return route.fulfill({ json: commit });
    });
    await page.getByText('Description images', { exact: true }).click();
    const composer = page.locator('[data-board-composer="description"]');
    await page.locator('#board-card-title').fill('Keep this draft');
    const input = composer.locator('textarea');
    const popup = composer.locator('[data-board-file-popup]');
    const initialCardReads = cardReads;
    await input.fill('@' + session.id.replaceAll('-', ''));
    await expect(popup).toContainText('No matching files');
    expect(historyReads).toBe(0); expect(fileReads).toBe(1); expect(cardReads).toBe(initialCardReads);
    await input.fill('!' + session.id.replaceAll('-', ''));
    await expect(popup).toContainText('Earlier investigation');
    expect(historyReads).toBe(1); expect(fileReads).toBe(1); expect(cardReads).toBe(initialCardReads);
    await input.press('Enter');
    await expect(input).toHaveValue('!' + session.id + ' ');
    expect(sessionLinks).toBe(1);
    await input.fill('!Other card');
    await expect(popup).toContainText('Other card');
    expect(historyReads).toBe(1);
    await input.press('Enter');
    await expect(popup).toContainText('Earlier investigation');
    await expect(popup).toContainText('codex');
    await expect(popup).toContainText(/Linked .*2026/);
    await input.press('Enter');
    await expect(input).toHaveValue('!' + session.id + ' ');
    expect(historyReads).toBe(1); expect(fileReads).toBe(1); expect(sessionLinks).toBe(1);
    await input.fill('#abcdef0');
    await expect(popup).toContainText('Link commit');
    await input.press('Enter');
    await expect(input).toHaveValue('#' + commit.sha + ' ');
    expect(commitLinks).toBe(1);
    await expect(page.locator('#board-card-title')).toHaveValue('Keep this draft');
    const comment = page.locator('[data-board-composer="comment"]');
    await comment.locator('textarea').fill('#' + commit.sha);
    await expect(comment.locator('[data-board-composer-live] [data-board-ref-commit]')).toBeVisible();
    await page.evaluate(() => { window.app.boardController.openSessionReplay = session => { window.__openedReferenceSession = session; }; });
    await comment.locator('textarea').fill('!' + session.id.replaceAll('-', '').toUpperCase());
    await comment.locator('[data-board-composer-live] [data-board-ref-session]').click();
    expect(await page.evaluate(() => window.__openedReferenceSession.id)).toBe(session.id);
});

test('draft image attachments survive creation', async ({ page }) => {
    await openBoard(page);
    await page.getByRole('button', { name: 'New card', exact: true }).click();
    await page.locator('#board-card-title').fill('Draft image');
    const composer = page.locator('[data-board-composer="description"]');
    await composer.locator('[data-board-composer-file]').setInputFiles({ name: 'draft.png', mimeType: 'image/png', buffer: Buffer.from(IMAGE.split(',')[1], 'base64') });
    await expect(page.locator('[data-board-attachments]')).toContainText('draft.png');
    await expect(composer.locator('textarea')).toHaveValue(/attachment:pending_/);
    await page.locator('[data-board-save-card]').click();
    await page.getByText('Draft image', { exact: true }).click();
    await expect(composer.locator('textarea')).not.toHaveValue(/attachment:pending_/);
    await expect(page.locator('[data-board-attachments] .board-attachment-thumb')).toBeVisible();
});

test('lane Agents shows running agents and adds VCA and Code quality for working changes', async ({ page }) => {
    const { jobs, settings } = await openLaneAgentsBoard(page);
    settings.lane_2.runningAgents = [{ runId: 'run_1', name: 'Code reviewer', cardId: 'card_test', cardLabel: 'VIBE-34 · My card', terminalSessionId: 'recording_1' }];
    const created = [];
    await page.route('**/api/v1/jobs', route => {
        const body = route.request().postDataJSON();
        created.push(body); const job = { ...body, id: 40 + created.length }; jobs.push(job);
        return route.fulfill({ json: job });
    });
    const button = page.getByRole('button', { name: 'Agents on entry to Review', exact: true });
    await button.click();
    const panel = page.locator('.board-lane-agents-panel');
    await expect(panel.locator('[data-lane-running]')).toContainText('VIBE-34 · My card');
    for (const [choice, scope] of [['check:3', 'working-tree'], ['check:2', 'working-tree']]) {
        await panel.getByRole('button', { name: 'Add agent', exact: true }).click();
        await panel.getByLabel('What would you like to add?').selectOption(choice);
        await expect(panel.locator('[data-agent-scope]')).toHaveCount(0);
        await panel.getByRole('button', { name: 'Add to lane', exact: true }).click();
        await expect(panel.getByRole('button', { name: 'Add agent', exact: true })).toBeVisible();
    }
    expect(created.map(job => job.actions)).toEqual([[{ kind: 3, arguments: ['working-tree'] }], [{ kind: 2, arguments: ['working-tree'] }]]);
    expect(settings.lane_2.jobIds).toEqual([12, 14, 41, 42]);
    await page.evaluate(() => { window.app.boardController.goToCardAutomation = (...args) => { window.__openedLaneAgent = args; }; });
    await panel.locator('[data-lane-running-id="run_1"]').click();
    expect(await page.evaluate(() => window.__openedLaneAgent)).toEqual(['card_test', 'recording_1']);
});

test('large inline rasters use authenticated content and release Blob URLs on close', async ({ page }) => {
    await openBoard(page, { onCard: card => { card.attachments[0].url = ''; } });
    let reads = 0;
    await page.route('**/api/v1/board/cards/card_test/attachments/att_image/content', route => {
        reads++; expect(route.request().headers().viberails_tab).toBe('board-fixture');
        return route.fulfill({ contentType: 'application/octet-stream', body: Buffer.from(IMAGE.split(',')[1], 'base64') });
    });
    await page.evaluate(() => {
        window.__blobUrls = []; window.__revokedUrls = [];
        const create = URL.createObjectURL, revoke = URL.revokeObjectURL;
        URL.createObjectURL = blob => { const url = create(blob); window.__blobUrls.push(url); return url; };
        URL.revokeObjectURL = url => { window.__revokedUrls.push(url); revoke(url); };
    });
    await page.getByText('Description images', { exact: true }).click();
    const composer = page.locator('[data-board-composer="comment"]');
    await composer.locator('textarea').fill(DESCRIPTION);
    const image = composer.locator('[data-board-composer-live] img');
    await expect(image).toHaveAttribute('src', /^blob:/);
    await expect.poll(() => image.evaluate(img => img.complete && img.naturalWidth > 0)).toBe(true);
    expect(reads).toBeGreaterThan(0);
    await page.evaluate(() => window.app.closeModal());
    expect(await page.evaluate(() => window.__blobUrls.every(url => window.__revokedUrls.includes(url)))).toBe(true);
});

test('posted Comments load authenticated raster previews and release them on close', async ({ page }) => {
    await openBoard(page, { onCard: card => {
        card.description = '';
        card.attachments[0].url = '';
        card.comments = [{ id: 'image_comment', body: DESCRIPTION, author: { kind: 'human', label: 'You' }, createdAt: card.createdAt }];
    } });
    let reads = 0;
    await page.route('**/api/v1/board/cards/card_test/attachments/att_image/content', route => {
        reads++; expect(route.request().headers().viberails_tab).toBe('board-fixture');
        return route.fulfill({ contentType: 'application/octet-stream', body: Buffer.from(IMAGE.split(',')[1], 'base64') });
    });
    await page.evaluate(() => {
        window.__blobUrls = []; window.__revokedUrls = [];
        const create = URL.createObjectURL, revoke = URL.revokeObjectURL;
        URL.createObjectURL = blob => { const url = create(blob); window.__blobUrls.push(url); return url; };
        URL.revokeObjectURL = url => { window.__revokedUrls.push(url); revoke(url); };
    });
    await page.getByText('Description images', { exact: true }).click();
    const image = page.locator('[data-board-comments] img[data-board-image]');
    await image.scrollIntoViewIfNeeded();
    await expect.poll(() => image.evaluate(img => img.complete && img.naturalWidth > 0)).toBe(true);
    expect(reads).toBe(1);
    await page.evaluate(() => window.app.closeModal());
    expect(await page.evaluate(() => window.__blobUrls.length > 0 && window.__blobUrls.every(url => window.__revokedUrls.includes(url)))).toBe(true);
});

test('a failed authenticated preview can retry without reopening the card', async ({ page }) => {
    await openBoard(page, { onCard: card => { card.attachments[0].url = ''; card.comments = []; card.commentCount = 0; } });
    let reads = 0;
    await page.route('**/api/v1/board/cards/card_test/attachments/att_image/content', route => {
        reads++;
        if (reads === 1) return route.fulfill({ status: 503, json: { error: 'Temporary failure' } });
        return route.fulfill({ contentType: 'application/octet-stream', body: Buffer.from(IMAGE.split(',')[1], 'base64') });
    });
    const failed = page.waitForResponse(response => response.url().endsWith('/attachments/att_image/content') && response.status() === 503);
    await page.getByText('Description images', { exact: true }).click();
    const composer = page.locator('[data-board-composer="comment"]');
    await composer.locator('textarea').fill(DESCRIPTION);
    await failed;
    await composer.locator('textarea').fill(DESCRIPTION + '\nRetry');
    const image = composer.locator('[data-board-composer-live] img');
    await expect.poll(() => image.evaluate(img => img.complete && img.naturalWidth > 0)).toBe(true);
    expect(reads).toBe(2);
});

test('posting a comment clears its live preview along with the input', async ({ page }) => {
    let current;
    await openBoard(page, { onCard: card => { current = card; } });
    await page.route('**/api/v1/board/cards/card_test/comments', route => {
        const comment = { id: 'posted_comment', ...route.request().postDataJSON(), author: { kind: 'human', label: 'You' }, createdAt: current.createdAt };
        current.comments.push(comment);
        return route.fulfill({ json: comment });
    });
    await page.getByText('Description images', { exact: true }).click();
    const composer = page.locator('[data-board-composer="comment"]');
    await composer.locator('textarea').fill('**posted**');
    await expect(composer.locator('[data-board-composer-live] strong')).toHaveText('posted');
    await composer.locator('textarea').press('Control+Enter');
    await expect(composer.locator('textarea')).toHaveValue('');
    await expect(composer.locator('[data-board-composer-live]')).toBeEmpty();
    await expect(page.locator('[data-board-comments] strong')).toContainText('posted');
});

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
        await expect(panel.locator('[data-check-run-options]')).toHaveCount(0);
        await expect(panel.locator('[data-check-history]')).toHaveCount(0);
        await expect(page.locator('[data-board-reviews]')).toHaveCount(0);
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
    await expect(editor.locator('.board-discussion')).toContainText('Ask about previous work. Leave the question empty to open a discussion and wait.');
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
        columnId: item.columnId, columnName: item.columnName || 'Ready', isCurrentProject: true, projectPath: 'C:/board-fixture' });
    await page.routeWebSocket('**/api/v1/events/ws*', () => {});
    await page.route('**/api/v1/**', async route => {
        const url = new URL(route.request().url());
        const path = url.pathname;
        requests.push({ path, method: route.request().method(), body: route.request().postDataJSON() });
        if (path.startsWith('/api/v1/board/local-cards/')) {
            const target = [card, ...related].find(item => path === `/api/v1/board/local-cards/${item.id}`);
            if (target) return route.fulfill({ json: { card: target, isCurrentProject: true,
                projectPath: 'C:/board-fixture', boardName: target.boardName || 'Main', columns } });
        }
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

test('description source and attachments survive editing and save', async ({ page }) => {
    await openBoard(page);
    await page.getByText('Description images', { exact: true }).click();
    const input = page.locator('[data-board-composer="description"] textarea');
    await expect(input).toBeVisible();
    await expect(input).toHaveValue(DESCRIPTION);
    await expect(page.locator('[data-board-comments] img.board-image')).toBeVisible();
    expect(await page.evaluate(() => window.__injected)).toBeUndefined();
    await input.fill(`${DESCRIPTION}\nEdited context`);
    await page.locator('[data-board-save-card]').click();
    await expect(page.locator('[data-board-card-editor]')).toHaveCount(0);
    await page.getByText('Description images', { exact: true }).click();
    await expect(input).toHaveValue(`${DESCRIPTION}\nEdited context`);
    await expect(page.locator('[data-board-attachments] .board-attachment-thumb')).toBeVisible();
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
        await expect(page.locator('.board-picker-control .ts-control')).toBeVisible();
        await expect(page.locator('[data-quick-add]')).toHaveCount(0);
        await expect(page.locator('.board-lane input')).toHaveCount(0);
        await expect(page.locator('[data-board-stats]')).toContainText('1 card');
        const tools = page.locator('.board-tools');
        const bounds = await tools.boundingBox();
        if (width >= 900) expect(bounds.height).toBeLessThan(120);
        expect(bounds.x + bounds.width).toBeLessThanOrEqual(width);
        expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(width);
        for (const control of await tools.locator('button:visible, input:visible, select:visible, .ts-control:visible').all()) {
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
    await expect(page.locator('[data-board-comments] img.board-image')).toHaveCount(1);
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
    await page.getByRole('tab', { name: 'Agent context', exact: true }).click();
    await page.getByLabel('Default message', { exact: true }).fill('Read project conventions. <img src=x onerror="window.__contextXss=1">');
    await page.locator('[data-board-context] summary').filter({ hasText: /^Bug$/ }).click();
    await page.getByLabel('Bug context', { exact: true }).selectOption('append');
    await page.getByLabel('Bug message', { exact: true }).fill('Reproduce before changing code.');
    await page.getByRole('button', { name: 'Save context', exact: true }).click();
    await expect.poll(() => saved.revision).toBe(1);
    expect(saved.context.typeOverrides.find(item => item.type === 'bug')).toEqual({ type: 'bug', mode: 'append', message: 'Reproduce before changing code.' });
    await page.locator('#modal-container [data-action="close-modal"]').first().click();
    await page.getByRole('button', { name: 'Board settings', exact: true }).click();
    await page.getByRole('tab', { name: 'Agent context', exact: true }).click();
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
        return route.fulfill({ json: { ...current, jobs: jobs.map(({ id, name, enabled, setup }) => ({ id, name, enabled, setup })) } });
    });
    await page.route('**/api/v1/jobs/scripts', route => route.fulfill({ json: { scripts: [
        { path: 'scripts/validate.ps1', runtime: 1, approved: true },
        { path: 'scripts/test.py', runtime: 0, approved: false },
        { path: 'scripts/missing.sh', runtime: 2, approved: false, unavailableReason: 'Bash unavailable' }
    ], hasMore: false } }));
    await page.evaluate(() => window.app.boardController.refresh());
    return { jobs, settings, writes };
}

test('adding an Automation offers a separate optional Worker purpose', async ({ page }) => {
    const { writes } = await openLaneAgentsBoard(page);
    await page.route('**/api/v1/environments', route => route.fulfill({ json: { environments: [
        { id: 7, name: 'Test Worker', cli: 'codex', purpose: 'work' }
    ] } }));
    const updates = [];
    await page.route('**/api/v1/environments/Test%20Worker', route => {
        updates.push(route.request().postDataJSON());
        return route.fulfill({ json: { success: true } });
    });
    await page.getByRole('button', { name: 'Agents on entry to Backlog', exact: true }).click();
    await page.getByRole('button', { name: 'Add agent', exact: true }).click();
    await page.locator('#board-lane-agent-choice').selectOption('automation');
    await page.locator('#board-lane-automation').selectOption('14');
    await expect(page.locator('#board-lane-purpose')).toHaveValue('work');
    await page.locator('#board-lane-purpose').selectOption('testing');
    await page.getByRole('button', { name: 'Add to lane', exact: true }).click();
    await expect.poll(() => updates).toEqual([{ purpose: 'testing' }]);
    await expect.poll(() => writes.some(w => w.kind === 'selection' && w.body.jobIds.includes(14))).toBe(true);
});

for (const width of [1440, 390]) {
    test(`lane script creation and retry after a settings conflict at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 900 });
        const { jobs, settings } = await openLaneAgentsBoard(page);
        let creates = 0;
        let conflict = true;
        await page.route('**/api/v1/jobs', route => {
            const job = { ...route.request().postDataJSON(), id: 71 };
            jobs.push(job); creates++;
            return route.fulfill({ json: job });
        });
        await page.route('**/api/v1/board/columns/lane_2/automation', async route => {
            if (route.request().method() !== 'PUT') return route.fallback();
            if (conflict) { conflict = false; return route.fulfill({ status: 409, json: { error: 'Selection changed. Retry.' } }); }
            return route.fallback();
        });
        await page.getByRole('button', { name: 'Agents on entry to Review', exact: true }).click();
        const panel = page.locator('.board-lane-agents-panel');
        await panel.getByRole('button', { name: 'Add agent', exact: true }).click();
        await panel.getByLabel('What would you like to add?').selectOption('script');
        await panel.getByLabel('Script file', { exact: true }).selectOption('scripts/validate.ps1');
        await expect(panel).toContainText('2 valid scripts · 1 approved');
        await expect(panel.getByLabel('Automation name', { exact: true })).toHaveAttribute('maxlength', '100');
        await panel.getByLabel('Automation name', { exact: true }).evaluate(input => {
            input.value = 'x'.repeat(101);
            input.dispatchEvent(new Event('input', { bubbles: true }));
        });
        await panel.getByRole('button', { name: 'Add to lane', exact: true }).click();
        await expect(panel).toContainText('Give this script Automation a name of up to 100 characters.');
        expect(creates).toBe(0);
        await panel.getByLabel('Automation name', { exact: true }).fill('Validate release');
        await panel.getByLabel('Script file', { exact: true }).selectOption('scripts/validate.ps1');
        await panel.getByLabel('Arguments — one per line').fill('--message\ntwo words\n$(literal)');
        expect(await panel.evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true);
        await page.screenshot({ path: testInfo.outputPath(`lane-script-${width}.png`) });
        await panel.getByRole('button', { name: 'Add to lane', exact: true }).click();
        await expect(panel).toContainText('Selection changed. Retry.');
        await expect(panel.getByLabel('Automation', { exact: true })).toHaveValue('71');
        await panel.getByRole('button', { name: 'Add to lane', exact: true }).click();
        await expect.poll(() => settings.lane_2.jobIds).toEqual([12, 14, 71]);
        expect(creates).toBe(1);
        expect(jobs.find(job => job.id === 71).actions).toEqual([{ kind: 1, scriptPath: 'scripts/validate.ps1',
            scriptRuntime: 1, arguments: ['--message', 'two words', '$(literal)'] }]);
    });
}

test('waiting badge follows server activity and skip preserves editor drafts', async ({ page }, testInfo) => {
    let waiting = true;
    await openBoard(page, { onCard: card => { card.hasWaitingAutomation = true; } });
    const entry = { jobId: 17, eventKey: 'entry-waiting', name: 'Review', status: 'Waiting', reason: 'Busy with an earlier card.' };
    await page.route('**/api/v1/board/cards/activity', route => route.fulfill({ json: {
        cards: [{ id: 'card_test', activeSessionId: null, activeTabId: null, hasActiveAutomation: false, hasWaitingAutomation: waiting }]
    } }));
    const response = () => ({ jobs: [], runs: [], laneEntries: [entry] });
    await page.route('**/api/v1/board/cards/card_test/automations', route => route.fulfill({ json: response() }));
    const skips = [];
    await page.route('**/api/v1/board/cards/card_test/automations/skip', async route => {
        skips.push(route.request().postDataJSON());
        await new Promise(resolve => setTimeout(resolve, 100));
        waiting = false; entry.status = 'Skipped'; entry.reason = 'User chose to continue without this Automation.';
        return route.fulfill({ json: response() });
    });
    await expect(page.locator('.board-automation-waiting')).toBeVisible();
    await page.screenshot({ path: testInfo.outputPath('waiting-card.png') });
    await page.locator('.board-automation-waiting').click();
    await page.locator('#board-card-title').fill('Keep my draft');
    const skip = page.getByRole('button', { name: 'Skip this step', exact: true });
    await expect(skip).toBeVisible();
    await skip.click();
    await expect(page.locator('[data-board-automation-runs]')).toContainText('Skipped');
    await expect(page.locator('#board-card-title')).toHaveValue('Keep my draft');
    expect(skips).toEqual([{ jobId: 17, eventKey: 'entry-waiting' }]);
    await expect(page.locator('.board-automation-waiting')).toHaveCount(0);
    waiting = true;
    await page.evaluate(() => window.app.boardController.refreshSessionActivity());
    await expect(page.locator('.board-automation-waiting')).toHaveCount(1);
    await expect(page.locator('#board-card-title')).toHaveValue('Keep my draft');
});

for (const width of [1440, 390]) {
    test(`starter reviewer is editable and removable from the first lane at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 900 });
        const { jobs, settings, writes } = await openLaneAgentsBoard(page);
        jobs[0].setup = 'Setup needed: Install/sign in to claude. No provider will be substituted. <img src=x onerror="window.__setupXss=1">';
        settings.lane_0.jobIds = [12];
        const environments = [{ id: 8, name: 'Starter Worker', cli: 'codex', purpose: 'code_review',
            reviewerRouting: { mode: 'switch', mappings: [
                { sourceProvider: 'claude', reviewer: { selection: 'base:codex' } },
                { sourceProvider: 'codex', reviewer: { selection: 'base:claude' } }
            ], fallback: { selection: 'base:codex' } } }];
        await page.route('**/api/v1/environments', route => route.fulfill({ json: { environments } }));
        const reviewerWrites = [];
        await page.route('**/api/v1/environments/Starter%20Worker', route => {
            const body = route.request().postDataJSON(); reviewerWrites.push(body);
            Object.assign(environments[0], body);
            return route.fulfill({ json: environments[0] });
        });
        const launches = [];
        page.on('request', request => { if (request.method() === 'POST' && /\/(launch|run|run-now)$/.test(new URL(request.url()).pathname)) launches.push(request.url()); });
        await page.evaluate(() => window.app.boardController.refresh());
        const button = page.getByRole('button', { name: 'Agents on entry to Backlog', exact: true });
        await expect(button).toBeVisible();
        const buttonBox = await button.boundingBox();
        expect(buttonBox.x).toBeGreaterThanOrEqual(0);
        await button.click();
        const panel = page.getByRole('dialog', { name: 'Lane agents', exact: true });
        await expect(panel).toContainText('Claude → Codex; Codex → Claude');
        await expect(panel).toContainText('Code review report');
        await expect(panel).toContainText('Each step waits for the previous step to pass or be skipped.');
        await expect(panel).toContainText('Setup needed: Install/sign in to claude');
        expect(await page.evaluate(() => window.__setupXss)).toBeUndefined();
        await expect(panel.getByRole('button', { name: 'Choose reviewer / edit mappings' })).toHaveCount(0);
        await expect(panel.getByRole('button', { name: 'Edit Code reviewer', exact: true })).toBeEnabled();
        expect(reviewerWrites).toHaveLength(0);
        expect(await panel.evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true);
        await page.screenshot({ path: testInfo.outputPath(`starter-reviewer-${width}.png`) });
        await panel.getByRole('button', { name: 'Remove Code reviewer from this lane' }).click();
        await page.getByRole('alertdialog').getByRole('button', { name: 'Remove', exact: true }).click();
        await expect.poll(() => settings.lane_0.jobIds).toEqual([]);
        await panel.getByRole('button', { name: 'Close lane agents' }).click();
        await button.click();
        await expect(panel).toContainText('No agents on entry');
        expect(writes.every(write => write.kind === 'selection')).toBe(true);
        expect(launches).toEqual([]);
    });
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
        const robot = button.locator('.fa-robot');
        await expectAnimatedStyle(robot, 'opacity');
        await expect(page.locator('.board-card .board-automation-running')).toHaveCount(1);
        await button.scrollIntoViewIfNeeded();
        await button.click();
        const panel = page.getByRole('dialog', { name: 'Lane agents', exact: true });
        await expect(panel).toContainText('GPT 6.1 Sol · Extra high effort');
        await expect(panel).toContainText('Opus 5.5 · Maximum effort');
        await expect(panel).not.toContainText('[1m]');
        expect(await panel.evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true);
        await page.screenshot({ path: testInfo.outputPath(`lane-running-${width}.png`) });
        await page.emulateMedia({ reducedMotion: 'reduce' });
        expect(await button.evaluate(el => getComputedStyle(el, '::after').animationName)).toBe('none');
        await expect(robot).toHaveCSS('animation-name', 'none');
        await expect(robot).toHaveCSS('opacity', '1');
        await expect(robot).not.toHaveCSS('filter', 'none');
        expect(await page.locator('.board-automation-running').evaluate(el => getComputedStyle(el).animationName)).toBe('none');
        // Activity updates preserve the open panel and its text drafts.
        await panel.getByRole('button', { name: 'Add agent', exact: true }).click();
        await panel.getByLabel('What would you like to add?').selectOption('script');
        await panel.getByLabel('Script file', { exact: true }).selectOption('scripts/validate.ps1');
        const draft = panel.getByLabel('Automation name', { exact: true });
        await draft.fill('Keep this draft while the agent finishes');
        active = false;
        await page.evaluate(() => window.app.boardController.refreshSessionActivity());
        await expect(button).not.toHaveClass(/is-running/);
        await expect(button.locator('.board-lane-agents-caption')).toHaveText('Agents');
        await page.emulateMedia({ reducedMotion: 'no-preference' });
        await expect(robot).toHaveCSS('animation-name', 'none');
        await expect(robot).toHaveCSS('filter', 'none');
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
        await expect(buttons).toHaveCount(4);
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
        await expect(panel.getByRole('button', { name: /Edit description|Choose reviewer/ })).toHaveCount(0);
        await panel.getByRole('button', { name: 'Add agent', exact: true }).click();
        await expect(panel.locator('[data-agent-id]')).toHaveCount(0);
        await expect(panel.locator('[data-lane-running]')).toBeHidden();
        if (width > 720) expect((await panel.boundingBox()).width).toBeGreaterThan(600);
        await panel.getByLabel('What would you like to add?').selectOption('automation');
        await panel.getByLabel('Automation', { exact: true }).selectOption('15');
        await panel.getByRole('button', { name: 'Add to lane', exact: true }).click();
        await expect(button.locator('.board-lane-agents-count')).toHaveText('3');
        await expect(panel.locator('[data-agent-id="15"] .board-lane-agent-copy')).toContainText('Script workflow');
        await panel.getByRole('button', { name: 'Remove Test runner from this lane', exact: true }).click();
        await page.getByRole('alertdialog').getByRole('button', { name: 'Remove', exact: true }).click();
        await expect(panel.getByText('Test runner', { exact: true })).toHaveCount(0);
        expect(settings.lane_2.jobIds).toEqual([12, 15]);
        expect(jobs).toHaveLength(3);
        await page.keyboard.press('Escape');
        await expect(panel).toHaveCount(0);
        await expect(button).toBeFocused();
        await button.click();
        await panel.getByRole('button', { name: 'Edit Code reviewer', exact: true }).click();
        await expect(page.locator('[data-job-editor] #job-name')).toHaveValue('Code reviewer');
        await expect(page.locator('[data-job-editor] #job-description')).toHaveValue('Checks the changes');
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
    await expect(panel.locator('[data-agent-action="edit"]').first()).toBeEnabled();
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
    await expect(panel.locator('[data-agent-id="99"]')).toContainText('No description yet.');
    await expect(panel.locator('[data-agent-id="99"] textarea')).toHaveCount(0);
    await panel.getByRole('button', { name: 'Add agent', exact: true }).click();
    await panel.getByLabel('What would you like to add?').selectOption('automation');
        await panel.getByLabel('Automation', { exact: true }).selectOption('15');
    await panel.getByRole('button', { name: 'Add to lane', exact: true }).click();
    await expect(panel.getByRole('alert')).toContainText('Enable or remove disabled/unavailable selections');
    expect(writes).toHaveLength(0);
    await panel.getByRole('button', { name: 'Cancel', exact: true }).click();
    await panel.getByRole('button', { name: 'Remove Test runner from this lane' }).click();
    const confirmation = page.getByRole('alertdialog');
    await expect(confirmation).toContainText('Code reviewer, 99');
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
    await panel.getByLabel('What would you like to add?').selectOption('new');
    await panel.getByRole('button', { name: 'Open Automation editor', exact: true }).click();
    await expect(page.locator('[data-job-editor] #job-name')).toHaveValue('');
    await expect(page.locator('[data-job-editor]')).toContainText('Create automation');
    await expect(panel).toHaveCount(0);
});

test('script search reaches beyond a truncated catalog and ignores stale results without losing drafts', async ({ page }) => {
    const { jobs } = await openLaneAgentsBoard(page);
    let releaseOld, oldRequested = false;
    const old = new Promise(resolve => { releaseOld = resolve; });
    const prefix = Array.from({ length: 200 }, (_, i) => ({ path: `scripts/a${i}.py`, runtime: 0, approved: false }));
    await page.route(/\/api\/v1\/jobs\/scripts(?:\?.*)?$/, async route => {
        const q = new URL(route.request().url()).searchParams.get('q') || '';
        if (q === 'old') { oldRequested = true; await old; }
        await route.fulfill({ json: q === 'deploy' ? { scripts: [{ path: 'scripts/z-deploy.py', runtime: 0, approved: true }], hasMore: false }
            : { scripts: prefix, hasMore: true } });
    });
    const creates = [];
    await page.route('**/api/v1/jobs', route => {
        const created = { ...route.request().postDataJSON(), id: 71 };
        jobs.push(created); creates.push(created);
        return route.fulfill({ json: created });
    });
    await page.getByRole('button', { name: 'Agents on entry to Backlog', exact: true }).click();
    const panel = page.getByRole('dialog', { name: 'Lane agents', exact: true });
    await panel.getByRole('button', { name: 'Add agent', exact: true }).click();
    await panel.getByLabel('What would you like to add?').selectOption('script');
    await expect(panel).toContainText('More matches exist');
    await panel.getByLabel('Automation name', { exact: true }).fill('Deploy script');
    await panel.getByLabel('Arguments — one per line').fill('--verify');
    const search = panel.getByLabel('Find script', { exact: true });
    await search.fill('old');
    await search.press('Enter');
    await expect.poll(() => oldRequested).toBe(true);
    await expect(panel.getByRole('button', { name: 'Add to lane', exact: true })).toBeDisabled();
    await search.fill('deploy');
    await search.press('Enter');
    const paths = panel.getByLabel('Script file', { exact: true });
    await expect(paths.locator('option')).toHaveCount(2);
    await paths.selectOption('scripts/z-deploy.py');
    const staleResponse = page.waitForResponse('**/api/v1/jobs/scripts?q=old');
    releaseOld();
    await staleResponse;
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
    await expect(paths).toHaveValue('scripts/z-deploy.py');
    await expect(search).toHaveValue('deploy');
    await expect(panel.getByLabel('Automation name', { exact: true })).toHaveValue('Deploy script');
    await panel.getByRole('button', { name: 'Add to lane', exact: true }).click();
    await expect.poll(() => creates.length).toBe(1);
    expect(creates[0].actions[0]).toMatchObject({ scriptPath: 'scripts/z-deploy.py', arguments: ['--verify'] });
});

test('lane agent loading failures recover and navigation disposes the panel', async ({ page }) => {
    const { jobs } = await openLaneAgentsBoard(page);
    await page.route('**/api/v1/jobs?**', route => route.fulfill({ status: 503, json: { error: 'Catalog unavailable' } }));
    await page.getByRole('button', { name: 'Agents on entry to Review', exact: true }).click();
    const panel = page.getByRole('dialog', { name: 'Lane agents', exact: true });
    await expect(panel).toContainText('Could not load lane agents');
    await page.route('**/api/v1/jobs?**', route => route.fulfill({ json: { jobs } }));
    await panel.getByRole('button', { name: 'Retry', exact: true }).click();
    await expect(panel).toContainText('Code reviewer');
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
    await panel.getByRole('button', { name: 'Add agent', exact: true }).click();
    await panel.getByLabel('What would you like to add?').selectOption('script');
    await panel.getByLabel('Script file', { exact: true }).selectOption('scripts/validate.ps1');
    const name = panel.getByLabel('Automation name', { exact: true });
    await name.fill('Keep this script draft');
    await name.evaluate(el => el.setSelectionRange(5, 12));
    release();
    await expect(page.locator('.board-card')).toHaveCount(60);
    await expect(name).toHaveValue('Keep this script draft');
    await expect(name).toBeFocused();
    expect(await name.evaluate(el => [el.selectionStart, el.selectionEnd])).toEqual([5, 12]);
    await expect(button).toHaveAttribute('aria-expanded', 'true');
    expect(writes).toHaveLength(0);
    await page.keyboard.press('Escape');
    await expect(panel).toHaveCount(0);
    await expect(button).toBeFocused();
});

test('closing lane agents during script discovery discards the late result', async ({ page }) => {
    const { writes } = await openLaneAgentsBoard(page);
    let release, requested = false;
    const gate = new Promise(resolve => { release = resolve; });
    await page.route('**/api/v1/jobs/scripts', async route => {
        requested = true; await gate;
        await route.fulfill({ json: { scripts: [], hasMore: false } }).catch(() => {});
    });
    await page.getByRole('button', { name: 'Agents on entry to Review', exact: true }).click();
    const panel = page.getByRole('dialog', { name: 'Lane agents', exact: true });
    await panel.getByRole('button', { name: 'Add agent', exact: true }).click();
    await panel.getByLabel('What would you like to add?').selectOption('script');
    await expect.poll(() => requested).toBe(true);
    await page.keyboard.press('Escape'); release();
    await expect(panel).toHaveCount(0);
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

for (const width of [1440, 390]) {
    test(`previous work and an initial question remain usable alongside work at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 950 });
        const requests = await openBoard(page, { active: true, assignee: 'base:codex', onCard: card => {
            card.previousWork = { outcome: 'Description overflow fixed', decisions: 'Keep a single scroll region', validation: 'Browser regression passed', outstanding: 'None',
                author: { label: 'Codex', sessionId: 'earlier-session' }, createdUtc: '2026-10-02T00:00:00Z',
                files: [{ path: 'VibeRails/wwwroot/js/modules/board-controller.js', reason: 'Open the card editor here', role: 'implementation', symbol: 'openCardEditor', commit: 'cf302fe', status: 'historical reference; verify current code' }] };
        } });
        await page.evaluate(() => {
            window.__discussionTabs = [];
            window.app.terminalController.adoptLaunchedTab = async id => { window.__discussionTabs.push(id); return true; };
        });
        await page.getByText('Description images', { exact: true }).click();
        await page.locator('[data-board-previous-work] summary').click();
        await expect(page.locator('[data-board-previous-work]')).toContainText('Open the card editor here');
        const question = page.getByLabel('Initial question (optional)');
        await question.fill('Why did we choose one scroll region?');
        await question.scrollIntoViewIfNeeded();
        await page.screenshot({ path: testInfo.outputPath(`recall-${width}.png`) });
        await page.getByRole('button', { name: 'Chat with agent', exact: true }).click();
        await expect.poll(() => page.evaluate(() => window.__discussionTabs)).toEqual(['board_background']);
        const launch = requests.find(item => item.path.endsWith('/launch'));
        expect(launch.body).toEqual({ selection: 'base:codex', intent: 'chat', question: 'Why did we choose one scroll region?' });
    });
}

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

test('description attaches newly uploaded images and new cards start in edit mode', async ({ page }) => {
    await openBoard(page);
    await page.getByText('Description images', { exact: true }).click();
    const description = page.locator('[data-board-composer="description"]');
    await description.locator('input[type="file"]').setInputFiles({
        name: 'pasted.png', mimeType: 'image/png', buffer: Buffer.from(IMAGE.split(',')[1], 'base64')
    });
    await expect(description.locator('textarea')).toHaveValue(/attachment:att_uploaded/);
    await expect(page.locator('[data-board-attachments] .board-attachment-thumb')).toHaveCount(2);
    await expect(page.locator('[data-board-view-attachment="att_uploaded"]')).toBeVisible();
    await page.locator('[data-board-save-card]').click();
    await page.getByRole('button', { name: 'New card', exact: true }).click();
    await expect(description.locator('textarea')).toBeVisible();
    await expect(description.locator('textarea')).toHaveValue('');
    await expect(description.locator('[data-board-composer-preview]')).toHaveCount(0);
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
    await expect(page.getByRole('button', { name: 'Chat with agent', exact: true })).toBeEnabled();
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
    await page.locator('[data-board-launch-speed]').selectOption('ultrafast');
    await page.locator('[data-board-launch-effort]').selectOption('high');
    await expect(page.locator('[data-board-launch-yolo]')).not.toBeChecked();
    await page.locator('[data-board-launch-yolo]').check();
    // Codex exposes no Start mode: its /plan is a TUI command, and nothing types into a TUI.
    await expect(page.locator('[data-board-launch-mode]')).toHaveCount(0);
    await page.locator('[data-board-start-work]').click();
    await expect(page.locator('[data-board-card-editor]')).toHaveCount(0);
    await expect(page.locator('#app-content [data-view="board"]')).toBeVisible();
    expect(requests.find(request => request.method === 'PUT' && request.path.endsWith('/card_test')).body.baseLlmOptions)
        .toEqual({ model: 'gpt-6-astra', effort: 'high', mode: '', yolo: true, speed: 'ultrafast' });
    expect(requests.filter(request => request.path.endsWith('/launch'))).toHaveLength(1);
    await page.getByText('Description images', { exact: true }).click();
    await expect(page.locator('[data-board-launch-effort]')).toHaveValue('high');
    await expect(page.locator('[data-board-launch-speed]')).toHaveValue('ultrafast');
    await expect(page.locator('[data-board-launch-yolo]')).toBeChecked();
});

for (const width of [1440, 390]) {
    test(`Codex Board speed saves, reopens and follows the selected model at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 950 });
        const requests = await openBoard(page, { assignee: 'base:codex' });
        await page.getByText('Description images', { exact: true }).click();
        const model = page.locator('[data-board-launch-model]');
        const speed = page.getByRole('combobox', { name: 'Speed', exact: true });
        await expect(speed.locator('option[value="fast"]')).toBeDisabled();
        await model.selectOption('gpt-6-astra');
        for (const tier of ['ultrafast', 'fast', '']) {
            await speed.selectOption(tier);
            await page.locator('[data-board-save-card]').click();
            await expect(page.locator('[data-board-card-editor]')).toHaveCount(0);
            expect(requests.filter(request => request.method === 'PUT' && request.path.endsWith('/card_test')).at(-1).body.baseLlmOptions.speed).toBe(tier);
            await page.getByText('Description images', { exact: true }).click();
            await expect(speed).toHaveValue(tier);
        }
        await speed.selectOption('ultrafast');
        await speed.scrollIntoViewIfNeeded();
        await page.screenshot({ path: testInfo.outputPath(`board-codex-speed-${width}.png`) });
        await model.selectOption('gpt-6.1-sol');
        await expect(speed).toHaveValue('');
        await expect(speed.locator('option[value="ultrafast"]')).toBeDisabled();
        await speed.selectOption('fast');
        await model.selectOption('');
        await expect(speed).toHaveValue('');
        await expect(speed.locator('option[value="fast"]')).toBeDisabled();
    });
}

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
        await expect(editor.locator('[data-board-attachments] .board-attachment-thumb')).toBeVisible();
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


test('Switch reviewer Worker preset saves a default reviewer and a different alternate', async ({ page }, testInfo) => {
    await openBoard(page);
    const writes = [];
    await page.route('**/api/v1/environments', async route => {
        if (route.request().method() === 'POST') {
            writes.push(route.request().postDataJSON());
            return route.fulfill({ json: { id: 100, ...writes.at(-1) } });
        }
        return route.fulfill({ json: [] });
    });
    await page.evaluate(() => window.app.environmentController.showEnvironmentForm({ mode: 'create', automationWorker: true }));
    const form = page.locator('#env-form');
    await form.getByRole('button', { name: 'Switch reviewer', exact: true }).click();
    await expect(form.locator('#env-purpose')).toHaveValue('code_review');
    await expect(form.locator('#env-reviewer-mode')).toHaveValue('switch');
    const defaultReviewer = form.locator('[data-reviewer-fallback] [data-reviewer-target]');
    const alternate = form.locator('[data-reviewer-alternate] [data-reviewer-target]');
    await expect(defaultReviewer).toHaveValue('base:codex');
    await expect(alternate).toHaveValue('base:claude');
    expect(await alternate.evaluate(select => Object.keys(select.tomselect.options))).not.toContain('base:codex');
    await page.screenshot({ path: testInfo.outputPath('switch-worker-preset.png') });
    await form.getByRole('button', { name: 'Create Worker', exact: true }).click();
    await expect.poll(() => writes.length).toBe(1);
    expect(writes[0]).toMatchObject({ name: 'Switch reviewer', purpose: 'code_review', automationWorker: true,
        reviewerRouting: { mode: 'switch', mappings: [
            { sourceProvider: 'codex', reviewer: { selection: 'base:claude' } },
            { sourceProvider: 'claude', reviewer: { selection: 'base:codex' } }
        ], fallback: { selection: 'base:codex' } } });
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
    await form.getByRole('button', { name: 'Code review', exact: true }).click();
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
    test(`compact card keeps review recordings without reviewer controls at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 1000 });
        await openBoard(page, { onCard: card => {
            card.sessions = [
                { id: 'work-session', displayName: 'Working agent', cli: 'codex', origin: 'launch', active: false },
                { id: 'review-session', displayName: 'Review agent', cli: 'codex', origin: 'code_review', isAutomation: true, isReview: true, active: false }
            ];
        } });
        let reviewReads = 0;
        await page.route('**/api/v1/board/cards/card_test/reviews**', route => { reviewReads++; return route.fulfill({ json: {} }); });
        await page.getByText('Description images', { exact: true }).click();
        const editor = page.locator('[data-board-card-editor]');
        await expect(editor.locator('[data-board-reviews]')).toHaveCount(0);
        await expect(editor.locator('[data-check-run-options], [data-check-history]')).toHaveCount(0);
        await expect(editor.locator('[data-board-checks]')).toBeVisible();
        await expect(editor.locator('[data-board-sessions] [data-session-id]')).toHaveCount(1);
        await expect(editor.locator('[data-board-automations] [data-session-id="review-session"]')).toHaveCount(1);
        await editor.locator('#board-card-title').fill('Keep this unsaved draft');
        await page.evaluate(() => window.app.boardController.refreshSessionActivity());
        await expect(editor.locator('#board-card-title')).toHaveValue('Keep this unsaved draft');
        expect(reviewReads).toBe(0);
        await page.screenshot({ path: testInfo.outputPath(`compact-card-${width}.png`) });
    });
}

for (const width of [1440, 390]) {
    test(`all-board search edits and links foreign cards with visible ownership at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 950 });
        let current;
        await openBoard(page, { onCard: card => { current = card; } });
        const local = { ...current, boardName: 'Main', columnName: 'Ready', isCurrentProject: true, projectPath: 'C:/board-fixture' };
        const foreign = { ...current, id: 'foreign-card', key: 'OTHER-ABCDE-2', title: 'Authentication recovery',
            description: 'Original outside description', boardId: 'outside-board', columnId: 'outside-ready',
            assignee: 'env:404:codex', comments: [], attachments: [], sessions: [], linkedCards: [] };
        const ownership = { boardName: 'Outside <board>', columnName: 'Ready', projectPath: 'C:/other/<project>', isCurrentProject: false };
        const requests = [];
        await page.route('**/api/v1/board/cards/search?*', route => route.fulfill({ json: { cards: [
            { ...local, snippet: 'Retry details from a comment' },
            { ...foreign, ...ownership, snippet: 'Related recovery work from a note <script>window.__searchXss=1</script>' }
        ] } }));
        await page.route('**/api/v1/board/local-cards/**', route => {
            const request = route.request(), path = new URL(request.url()).pathname, method = request.method();
            expect(request.headers().viberails_tab).toBe('board-fixture');
            requests.push({ path, method, body: request.postDataJSON() });
            if (path.endsWith('/links/candidates')) return route.fulfill({ json: { cards: foreign.linkedCards.length ? [] : [local] } });
            if (path.endsWith('/links') && method === 'POST') {
                foreign.linkedCards = [local]; return route.fulfill({ json: local });
            }
            if (path.endsWith('/links/card_test') && method === 'DELETE') {
                foreign.linkedCards = []; return route.fulfill({ json: { ok: true } });
            }
            if (path.endsWith('/comments')) {
                const comment = { id: 'foreign-comment', author: { label: 'You' }, body: request.postDataJSON().body, createdAt: '2026-10-04T12:00:00Z' };
                foreign.comments.push(comment); return route.fulfill({ json: comment });
            }
            if (path.endsWith('/card_test')) return route.fulfill({ json: { card: current, ...local } });
            if (method === 'PUT') { Object.assign(foreign, request.postDataJSON()); return route.fulfill({ json: foreign }); }
            return route.fulfill({ json: { card: foreign, ...ownership, columns: [{ id: 'outside-ready', name: 'Ready' }] } });
        });
        const search = page.locator('[data-board-search]');
        await search.fill('retry behavior');
        const results = page.locator('[data-board-search-card]');
        await expect(results).toHaveCount(2);
        await expect(results.first()).toContainText('Description images');
        await expect(results.last()).toContainText('Another project');
        await expect(results.last()).toContainText('C:/other/<project>');
        await expect(page.locator('[data-board-filter-type]')).toBeDisabled();
        await expect(page.locator('[data-board-canvas]')).toBeHidden();
        expect(await page.evaluate(() => window.__searchXss)).toBeUndefined();
        await results.last().click();
        const editor = page.locator('[data-local-board-card-editor]');
        await expect(editor).toContainText("another project's board");
        await expect(editor).toContainText('Outside <board>');
        await expect(editor.locator('#local-card-assignee')).toHaveValue('env:404:codex');
        await page.evaluate(() => window.app.llmPickerController.refreshAll());
        await expect(editor.locator('#local-card-assignee')).toHaveValue('env:404:codex');
        await editor.getByLabel('Title', { exact: true }).fill('Edited outside card');
        await editor.getByRole('button', { name: 'Save card', exact: true }).click();
        await expect(editor.locator('[data-local-card-status]')).toContainText('Saved');
        expect(requests.find(request => request.method === 'PUT').body).toEqual({ title: 'Edited outside card' });
        await editor.getByLabel('Description', { exact: true }).fill('Preserve this unsaved description');
        await editor.getByLabel('Add a comment').fill('Comment saved on outside card');
        await editor.getByRole('button', { name: 'Post comment' }).click();
        await expect(editor.locator('[data-local-card-comments]')).toContainText('Comment saved on outside card');
        await expect(editor.getByLabel('Description', { exact: true })).toHaveValue('Preserve this unsaved description');
        await editor.locator('[data-board-link-picker] > summary').click();
        await editor.locator('[data-board-link-card="card_test"]').click();
        await expect(editor.locator('[data-board-linked-cards]')).toContainText('Description images');
        await editor.locator('[data-board-unlink-card="card_test"]').click();
        await expect(editor.locator('[data-board-linked-cards]')).toContainText('No cards linked');
        await editor.locator('[data-board-link-card="card_test"]').click();
        expect(requests.filter(request => request.method === 'POST' && request.path.endsWith('/links')).length).toBe(2);
        expect(await editor.evaluate(element => element.scrollWidth <= element.clientWidth + 1)).toBe(true);
        await page.screenshot({ path: testInfo.outputPath(`local-card-search-${width}.png`) });
        await editor.locator('[data-board-open-linked-card="card_test"]').click();
        await page.getByRole('alertdialog').getByRole('button', { name: 'Discard and open' }).click();
        await expect(page.locator('[data-board-card-editor]')).toHaveAttribute('data-card-id', 'card_test');
        await page.evaluate(() => window.app.closeModal());
        await search.fill('');
        await expect(page.locator('[data-board-canvas]')).toBeVisible();
        await expect(page.locator('[data-board-filter-type]')).toBeEnabled();
    });
}

for (const width of [1440, 390]) {
    test(`ordered lane workflow status and per-card skip at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 950 });
        await page.clock.install();
        const { jobs, settings, writes } = await openLaneAgentsBoard(page);
        jobs.find(job => job.id === 14).name = 'VCA';
        jobs.find(job => job.id === 14).actions = [{ kind: 3, arguments: ['working-tree'] }];
        Object.assign(jobs.find(job => job.id === 15), { name: 'Code quality', actions: [{ kind: 2, arguments: ['working-tree'] }] });
        settings.lane_2.jobIds = [12, 14, 15];
        const steps = [
            { jobId: 12, eventKey: 'review-entry', status: 'Running', stepStatus: 'Reviewing', canSkip: true, reason: 'Review in progress.' },
            { jobId: 14, eventKey: 'vca-entry', status: 'Waiting', stepStatus: 'Waiting', canSkip: true, reason: 'Waiting for Code Review.' },
            { jobId: 15, eventKey: 'quality-entry', status: 'Waiting', stepStatus: 'Waiting', canSkip: true, reason: 'Waiting for VCA.' }
        ];
        settings.lane_2.workflows = [{ cardId: 'card_test', cardLabel: 'VIBE-123 · Example change', steps }];
        const response = () => ({ runningAgents: [], workflows: settings.lane_2.workflows });
        let holdPoll = false, heldPoll, releasePoll;
        // Keep one old poll in flight across Skip to verify the mutation cannot be repainted.
        await page.route('**/api/v1/board/columns/lane_2/automation/running', async route => {
            if (holdPoll) {
                holdPoll = false;
                const snapshot = structuredClone(response());
                await new Promise(resolve => { heldPoll = true; releasePoll = resolve; });
                return route.fulfill({ json: snapshot });
            }
            return route.fulfill({ json: response() });
        });
        const skips = [];
        await page.route('**/api/v1/board/cards/card_test/automations/skip', route => {
            const body = route.request().postDataJSON();
            skips.push(body);
            const step = steps.find(step => step.eventKey === body.eventKey);
            step.stepStatus = 'Skipped'; step.status = 'Skipped'; step.canSkip = false; step.reason = 'User skipped this step.';
            return route.fulfill({ json: { laneEntries: steps } });
        });
        await page.getByRole('button', { name: 'Agents on entry to Review', exact: true }).click();
        const panel = page.getByRole('dialog', { name: 'Lane agents', exact: true });
        await expect(panel).toContainText('run top to bottom');
        await expect(panel.locator('[data-lane-step-id="12"]')).toContainText('Reviewing');
        await expect(panel.locator('[data-lane-step-id="12"]')).toContainText('VIBE-123');
        await expect(panel.locator('.board-workflow-arrow')).toHaveCount(2);
        await expectAnimatedStyle(panel.locator('[data-lane-step-id="12"] .fa-spinner'), 'transform');
        for (const state of ['Failed', 'Fixing', 'Reviewing', 'Passed']) {
            steps[0].stepStatus = state;
            steps[0].reason = state === 'Passed' ? 'No blocking findings.' : state + ' in progress.';
            steps[0].canSkip = state !== 'Passed';
            steps[0].status = state === 'Passed' ? 'Succeeded' : 'Running';
            await page.clock.fastForward(10100);
            await expect(panel.locator('[data-lane-step-id="12"] .board-workflow-state')).toHaveText(state);
        }
        await expect(panel.locator('[data-lane-arrow-id="12"] .is-complete')).toBeVisible();
        holdPoll = true;
        await page.clock.fastForward(10100);
        await expect.poll(() => heldPoll).toBe(true);
        await panel.locator('[data-lane-step-id="14"]').getByRole('button', { name: 'Skip', exact: true }).click();
        await expect(panel.locator('[data-lane-step-id="14"]')).toContainText('Skipped');
        releasePoll();
        await page.clock.fastForward(100);
        await expect(panel.locator('[data-lane-step-id="14"]')).toContainText('Skipped');
        expect(skips).toEqual([{ jobId: 14, eventKey: 'vca-entry' }]);
        expect(writes).toEqual([]);
        expect(await panel.evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true);
        await page.screenshot({ path: testInfo.outputPath(`ordered-workflow-${width}.png`) });
        await panel.getByRole('button', { name: 'Close lane agents' }).click();
        await page.clock.fastForward(20000);
        await expect(panel).toHaveCount(0);
    });
}


for (const width of [1440, 390]) {
    test(`Public card links support CRUD and preserve editor drafts at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 1000 });
        const errors = []; page.on('pageerror', error => errors.push(error.message));
        await page.route('https://viberails.ai/**', route => route.abort());
        const requests = await openBoard(page);
        let links = [], created = 0, refreshes = 0;
        const date = '2026-10-10T12:00:00Z';
        await page.route('**/api/v1/board/cards/card_test/sharing-links**', async route => {
            const request = route.request(), method = request.method(), path = new URL(request.url()).pathname;
            expect(request.headers().viberails_tab).toBe('board-fixture');
            if (method === 'GET') return route.fulfill({ json: { success: true, links } });
            if (path.endsWith('/refresh')) { refreshes++; return route.fulfill({ json: { success: true, message: 'Shared card updated.' } }); }
            if (method === 'POST') {
                created++; const link = { id: created, displayName: request.postDataJSON().displayName,
                    sharePath: '/shared/card#key=' + 'a'.repeat(64), createdUtc: date, expiresUtc: '2026-11-10T12:00:00Z', updatedUtc: date, status: 'active' };
                links.unshift(link);
                return route.fulfill({ json: { success: true, link, message: 'Read-only card link created.' } });
            }
            if (method === 'PATCH') links[0].displayName = request.postDataJSON().displayName;
            if (method === 'DELETE') links[0].status = 'revoked';
            return route.fulfill({ json: { success: true, message: method === 'DELETE' ? 'Link revoked.' : 'Link renamed.' } });
        });
        await page.getByText('Description images', { exact: true }).click();
        const editor = page.locator('[data-board-card-editor]');
        const panel = editor.locator('[data-card-sharing]');
        await panel.locator('summary').click();
        await expect(panel).toContainText('No public links');
        await editor.locator('#board-card-title').fill('Keep my unsaved title');
        await panel.getByRole('button', { name: 'Create public link' }).click();
        await expect(panel.locator('[role="status"]')).toContainText('drafts are still here');
        expect(created).toBe(0);
        await editor.locator('#board-card-title').fill('Description images');
        const draft = editor.locator('[data-board-composer="comment"] textarea');
        await draft.fill('Unposted comment');
        await panel.getByRole('button', { name: 'Create public link' }).click();
        expect(created).toBe(0); await expect(draft).toHaveValue('Unposted comment');
        await draft.fill('');
        await panel.locator('[data-card-share-name]').fill('<img src=x onerror="window.shareInjected=true">');
        await panel.getByRole('button', { name: 'Create public link' }).click();
        await expect(panel.locator('[data-card-share-id]')).toHaveCount(1);
        await expect(panel.locator('[data-card-share-open]')).toHaveAttribute('href', 'https://viberails.ai/shared/card#key=' + 'a'.repeat(64));
        expect(await page.evaluate(() => window.shareInjected)).toBeUndefined();
        await editor.locator('#board-card-title').fill('Draft retained through link management');
        await panel.locator('[data-card-share-rename]').fill('Release review');
        await panel.getByRole('button', { name: 'Rename', exact: true }).click();
        await expect(panel.locator('[data-card-share-rename]')).toHaveValue('Release review');
        await expect(editor.locator('#board-card-title')).toHaveValue('Draft retained through link management');
        await panel.getByRole('button', { name: 'Refresh shared card' }).click();
        expect(refreshes).toBe(0);
        await editor.locator('#board-card-title').fill('Description images');
        await panel.getByRole('button', { name: 'Refresh shared card' }).click();
        await expect.poll(() => refreshes).toBe(1);
        await panel.scrollIntoViewIfNeeded();
        expect(await panel.evaluate(el => el.scrollWidth <= el.clientWidth + 1)).toBe(true);
        await page.screenshot({ path: testInfo.outputPath(`card-sharing-${width}.png`) });
        await panel.getByRole('button', { name: 'Revoke', exact: true }).click();
        await page.getByRole('alertdialog').getByRole('button', { name: 'Revoke', exact: true }).click();
        await expect(panel.locator('[data-card-share-open]')).toHaveCount(0);
        await expect(panel.locator('[data-card-share-id]')).toContainText('revoked');
        expect(created).toBe(1); expect(errors).toEqual([]);
        expect(requests.filter(r => r.method === 'PUT' && r.path === '/api/v1/board/cards/card_test')).toEqual([]);
    });
}

test('Public card links reconcile uncertain creation once and ignore a result after closing', async ({ page }) => {
    await openBoard(page);
    let creations = 0, links = [], release;
    await page.route('**/api/v1/board/cards/card_test/sharing-links**', async route => {
        if (route.request().method() === 'GET') return route.fulfill({ json: { success: true, links } });
        creations++;
        if (creations === 1) {
            links = [{ id: 1, displayName: 'Created despite timeout', status: 'active', sharePath: '/shared/card#key=' + 'a'.repeat(64),
                createdUtc: '2026-10-10T12:00:00Z', updatedUtc: '2026-10-10T12:00:00Z', expiresUtc: '2026-11-10T12:00:00Z' }];
            return route.fulfill({ json: { success: false, message: 'The request may already have created a link.' } });
        }
        await new Promise(resolve => { release = resolve; });
        await route.fulfill({ json: { success: true, message: 'Late creation' } });
    });
    await page.getByText('Description images', { exact: true }).click();
    const panel = page.locator('[data-card-sharing]'); await panel.locator('summary').click();
    await expect(panel).toContainText('No public links');
    await panel.getByRole('button', { name: 'Create public link' }).click();
    await expect(panel.locator('[data-card-share-rename]')).toHaveValue('Created despite timeout');
    expect(creations).toBe(1);
    await panel.getByRole('button', { name: 'Create public link' }).click();
    await expect.poll(() => Boolean(release)).toBe(true);
    await page.evaluate(() => window.app.closeModal()); release();
    await expect(page.locator('[data-card-sharing]')).toHaveCount(0);
    expect(creations).toBe(2);
});
