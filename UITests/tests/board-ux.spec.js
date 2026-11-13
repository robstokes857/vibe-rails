const { test, expect } = process.env.VIBERAILS_BOARD_STATIC === '1'
    ? require('@playwright/test')
    : require('./fixtures');

const IMAGE = 'data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+a5XcAAAAASUVORK5CYII=';
const DESCRIPTION = 'Repro screenshot\n![Screenshot.png](attachment:att_image)\n<img src=x onerror="window.__injected=true">';

for (const width of [1440, 390]) {
    test(`offline spelling corrects native fields and preserves undo at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 950 });
        await openBoard(page, { appSettings: { vsCodeSpellCheck: true } });
        await page.locator('.board-card').click();
        const editor = page.locator('[data-board-card-editor]');
        const description = editor.locator('[data-board-composer="description"] textarea');
        const source = 'This is mispelled.\n`codeerrror` @src/fileerrror.cs https://urlerrror.invalid\n```js\nfencederrror();\n```';
        await description.fill(source);
        const marks = editor.locator('[data-board-composer="description"] .board-spelling-error');
        await expect(marks).toHaveText(['mispelled']);
        await description.evaluate(input => { input.focus(); input.setSelectionRange(10, 10); });
        await page.keyboard.press('Alt+ArrowDown');
        const popup = page.getByRole('dialog', { name: 'Spelling suggestions for mispelled', exact: true });
        await expect(popup).toBeVisible();
        await expect(popup.getByRole('button', { name: 'misspelled', exact: true })).toBeFocused();
        await page.screenshot({ path: testInfo.outputPath(`spelling-${width}.png`) });
        const box = await popup.boundingBox();
        expect(box.x).toBeGreaterThanOrEqual(0);
        expect(box.x + box.width).toBeLessThanOrEqual(width);
        await popup.getByRole('button', { name: 'misspelled', exact: true }).click();
        await expect(description).toHaveValue(source.replace('mispelled', 'misspelled'));
        await page.keyboard.press('Control+z');
        await expect(description).toHaveValue(source);
        await editor.getByRole('button', { name: 'Check spelling: description', exact: true }).click();
        await expect(popup).toBeVisible();
        await page.keyboard.press('Escape');
        await expect(popup).toHaveCount(0);
        await expect(editor).toBeVisible();
        await expect(description).toBeFocused();
        await editor.locator('#board-card-title').fill('Speling title');
        await expect(editor.locator('#board-card-title').locator('..').locator('.board-spelling-error')).toHaveText(['Speling']);
        const comment = editor.locator('[data-board-composer="comment"] textarea');
        await comment.fill('Another errror');
        await expect(editor.locator('[data-board-composer="comment"] .board-spelling-error')).toHaveText(['errror']);
    });
}

for (const [name, prefix, marker] of [
    ['embedded long backtick runs', 'a', '`'],
    ['unfinished backtick fence lines', '', '`'],
    ['unfinished tilde fence lines', '', '~']
]) {
    test(`offline spelling scans ${name} without stalling the browser`, async ({ page }) => {
        await openBoard(page);
        const result = await page.evaluate(async ({ prefix, marker }) => {
            const { spellingWords } = await import('/js/modules/board-spelling-words.js');
            const source = prefix + marker.repeat(99999 - prefix.length) + 'x';
            const start = performance.now();
            const words = spellingWords(source);
            return { milliseconds: performance.now() - start, words };
        }, { prefix, marker });
        expect(result.words).toEqual([]);
        expect(result.milliseconds).toBeLessThan(1000);
    });
}

test('offline spelling checks prose after same-line backtick pairs', async ({ page }) => {
    await openBoard(page, { appSettings: { vsCodeSpellCheck: true } });
    await page.locator('.board-card').click();
    const editor = page.locator('[data-board-card-editor]');
    const description = editor.locator('[data-board-composer="description"] textarea');
    const marks = editor.locator('[data-board-composer="description"] .board-spelling-error');
    for (const length of [3, 4]) {
        for (const ending of ['', '\n']) {
            const ticks = '`'.repeat(length);
            await description.fill(`${ticks}code${ticks} mispelled${ending}`);
            await expect(marks).toHaveText(['mispelled']);
        }
    }
});

test('offline spelling shares ignores, persists personal words and leaves drafts unchanged', async ({ page }) => {
    const requests = await openBoard(page, { appSettings: { vsCodeSpellCheck: true } });
    await page.locator('.board-card').click();
    const editor = page.locator('[data-board-card-editor]');
    const description = editor.locator('[data-board-composer="description"] textarea');
    const comment = editor.locator('[data-board-composer="comment"] textarea');
    await description.fill('frobnizz quuxword');
    await comment.fill('frobnizz quuxword');
    await editor.getByRole('button', { name: 'Check spelling: description', exact: true }).click();
    await page.getByRole('button', { name: 'Ignore word', exact: true }).click();
    await expect(editor.locator('[data-board-composer="description"] .board-spelling-error')).toHaveText(['quuxword']);
    await expect(editor.locator('[data-board-composer="comment"] .board-spelling-error')).toHaveText(['quuxword']);
    await editor.getByRole('button', { name: 'Check spelling: description', exact: true }).click();
    await page.getByRole('button', { name: 'Add to dictionary', exact: true }).click();
    await expect(editor.locator('[data-board-composer] .board-spelling-error')).toHaveCount(0);
    await expect(description).toHaveValue('frobnizz quuxword');
    await expect(comment).toHaveValue('frobnizz quuxword');
    expect(requests.some(request => request.method === 'PUT')).toBe(false);
    await page.reload();
    await page.getByRole('navigation').getByRole('button', { name: /^Board(?: —.*)?$/ }).click();
    await page.locator('.board-card').click();
    await description.fill('frobnizz quuxword');
    await expect(editor.locator('[data-board-composer="description"] .board-spelling-error')).toHaveText(['frobnizz']);
});

test('offline spelling recovers from failed assets and works without eval, workers or external connections', async ({ page }) => {
    let fail = true;
    await page.route('**/assets/spellcheck/english.js*', route => fail ? route.abort() : route.continue());
    await openBoard(page, { appSettings: { vsCodeSpellCheck: true } });
    // The production spelling path is plain ESM, including its dictionary; no blob/eval/worker exceptions.
    await page.evaluate(() => {
        const policy = document.createElement('meta');
        policy.httpEquiv = 'Content-Security-Policy';
        policy.content = "script-src 'self'; connect-src 'self'; worker-src 'none'; object-src 'none'";
        document.head.append(policy);
    });
    await page.locator('.board-card').click();
    const editor = page.locator('[data-board-card-editor]');
    const description = editor.locator('[data-board-composer="description"] textarea');
    await description.fill('mispelled');
    await expect(editor.locator('[data-board-composer="description"] .board-spelling-status')).toContainText('Spelling unavailable');
    fail = false;
    await editor.getByRole('button', { name: 'Check spelling: description', exact: true }).click();
    await expect(page.getByRole('dialog', { name: 'Spelling suggestions for mispelled', exact: true })).toBeVisible();
    await page.keyboard.press('Escape');
    // Denied local storage must explain that the word is only ignored for this session.
    await page.evaluate(() => { Storage.prototype.setItem = () => { throw new Error('Storage disabled'); }; });
    await editor.getByRole('button', { name: 'Check spelling: description', exact: true }).click();
    await page.getByRole('button', { name: 'Add to dictionary', exact: true }).click();
    await expect(editor.locator('[data-board-composer="description"] .board-spelling-status')).toContainText('could not be saved');
    await expect(description).toHaveValue('mispelled');
});

test('offline spelling aligns clickable marks after wrapping and native field scrolling', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 950 });
    await openBoard(page, { appSettings: { vsCodeSpellCheck: true } });
    await page.locator('.board-card').click();
    const editor = page.locator('[data-board-card-editor]');
    const title = editor.locator('#board-card-title');
    await title.fill('A long title with many ordinary words and a mispelled');
    await title.press('Home');
    await title.press('End');
    await expect.poll(() => title.evaluate(element => element.scrollLeft)).toBeGreaterThan(0);
    const titleMark = title.locator('..').locator('.board-spelling-error');
    await expect(titleMark).toHaveText('mispelled');
    const popup = page.getByRole('dialog', { name: 'Spelling suggestions for mispelled', exact: true });
    let box = await titleMark.boundingBox();
    await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2);
    await expect(popup).toBeVisible();
    await page.keyboard.press('Escape');
    const comment = editor.locator('[data-board-composer="comment"] textarea');
    await comment.fill(('A line with many ordinary words to wrap.\n').repeat(35) + 'This is mispelled');
    await comment.press('Control+End');
    const mark = comment.locator('..').locator('.board-spelling-error');
    await expect(mark).toHaveText('mispelled');
    await expect.poll(() => comment.evaluate(element => element.scrollTop)).toBeGreaterThan(100);
    box = await mark.boundingBox();
    const fieldBox = await comment.boundingBox();
    expect(box.y).toBeGreaterThanOrEqual(fieldBox.y);
    expect(box.y + box.height).toBeLessThanOrEqual(fieldBox.y + fieldBox.height);
    await page.mouse.click(box.x + box.width / 2, box.y + box.height / 2, { button: 'right' });
    await expect(popup).toBeVisible();
    await popup.getByRole('button', { name: 'misspelled', exact: true }).click();
    await expect(comment).toHaveValue(('A line with many ordinary words to wrap.\n').repeat(35) + 'This is misspelled');
});

for (const width of [1440, 390]) {
    test(`spell check feature flag defaults off and saves through General settings at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 950 });
        const dictionaries = [];
        page.on('request', request => { if (request.url().includes('/spellcheck/english.js')) dictionaries.push(request.url()); });
        await openBoard(page);
        let saved = {};
        await page.route('**/api/v1/settings', route => {
            if (route.request().method() === 'POST') saved = { ...saved, ...route.request().postDataJSON() };
            return route.fulfill({ json: saved });
        });
        await page.locator('.board-card').click();
        const editor = page.locator('[data-board-card-editor]');
        const input = editor.locator('[data-board-composer="description"] textarea');
        await input.fill('mispelled');
        await expect(editor.locator('.board-spelling, .board-spelling-tools')).toHaveCount(0);
        await expect(input).toHaveAttribute('spellcheck', 'true');
        await page.waitForTimeout(500); // Negative assertion: no deferred dictionary load while off.
        expect(dictionaries).toHaveLength(0);

        await page.evaluate(() => { window.app.closeModal(); window.app.navigate('settings'); });
        const general = page.locator('#settings-panel-general');
        const toggle = general.getByRole('switch', { name: 'VS Code Spell Check', exact: true });
        await expect(general).toBeVisible();
        await expect(toggle).not.toBeChecked();
        const save = page.locator('#settings-save-button');
        await toggle.check();
        await expect(save).toBeEnabled();
        await page.screenshot({ path: testInfo.outputPath(`spell-check-setting-${width}.png`) });
        await save.click();
        await expect(save).toBeDisabled();
        expect(saved.vsCodeSpellCheck).toBe(true);

        await page.evaluate(() => window.app.navigate('board'));
        await page.locator('.board-card').click();
        await input.fill('mispelled');
        await expect(editor.locator('[data-board-composer="description"] .board-spelling-error')).toHaveText(['mispelled']);
        // Applying a saved flag also disposes an already-mounted field, preserving its draft.
        await page.evaluate(() => window.app.setAppSettings({ vsCodeSpellCheck: false }));
        await expect(editor.locator('.board-spelling, .board-spelling-tools')).toHaveCount(0);
        await expect(input).toHaveValue('mispelled');
        await expect(input).toHaveAttribute('spellcheck', 'true');

        await page.evaluate(() => { window.app.closeModal(); window.app.navigate('settings'); });
        await expect(toggle).toBeChecked();
        await toggle.uncheck();
        await save.click();
        await expect(save).toBeDisabled();
        expect(saved.vsCodeSpellCheck).toBe(false);
        await page.evaluate(() => window.app.navigate('board'));
        await page.locator('.board-card').click();
        await expect(editor.locator('.board-spelling, .board-spelling-tools')).toHaveCount(0);
    });
}

for (const width of [1440, 390]) {
    test(`lanes collapse, preserve scrolling and restore after reload at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 700 });
        let card;
        const columns = ['Ready', 'Review', 'Done'].map((name, position) => ({
            id: position ? `col_${position}` : 'col_ready', name, position, boardId: 'brd_main', color: '#3b82f6'
        }));
        const requests = await openBoard(page, { columns, onCard: value => { card = value; } });
        const cards = Array.from({ length: 24 }, (_, position) => ({
            ...card, id: `card_${position}`, position, title: `Work item ${position}`
        }));
        await page.route('**/api/v1/board/cards?**', route => route.fulfill({ json: { cards } }));
        await page.evaluate(() => window.app.boardController.refresh());
        const lane = page.locator('.board-lane[data-column-id="col_ready"]');
        const list = lane.locator('.board-lane-list');
        const hide = lane.getByRole('button', { name: 'Hide Ready lane', exact: true });
        const show = lane.getByRole('button', { name: 'Show Ready lane', exact: true });
        const expandedWidth = (await lane.boundingBox()).width;
        await list.evaluate(element => { element.scrollTop = 180; });
        await expect.poll(() => list.evaluate(element => element.scrollTop)).toBe(180);
        await hide.click();
        await expect(show).toBeFocused();
        await expect(show).toHaveAttribute('aria-expanded', 'false');
        await expect(list).toBeHidden();
        await expect(lane.locator('.board-lane-agents-button')).toBeHidden();
        await expect(show.locator('.board-card-count')).toHaveText('24');
        expect((await lane.boundingBox()).width).toBe(52);
        expect(expandedWidth).toBeGreaterThan(250);
        await page.evaluate(() => window.app.boardController.refresh());
        await expect(show).toBeVisible();
        await show.click();
        await expect(hide).toBeFocused();
        await expect(list).toBeVisible();
        await expect.poll(() => list.evaluate(element => element.scrollTop)).toBe(180);
        await hide.click();
        await page.reload();
        await expect(page.locator('#app-content #vb-terminal-panel')).toBeVisible();
        await page.getByRole('navigation').getByRole('button', { name: /^Board(?: —.*)?$/ }).click();
        await expect(show).toBeVisible();
        await expect(list).toBeHidden();
        await page.screenshot({ path: testInfo.outputPath(`collapsed-lane-${width}.png`) });
        await show.focus();
        await page.keyboard.press('Enter');
        await expect(hide).toBeFocused();
        await expect(hide).toHaveAttribute('aria-expanded', 'true');
        await expect(list).toBeVisible();
        await expect(lane.locator('.board-card')).toHaveCount(24);
        expect(await page.evaluate(() => JSON.parse(localStorage.getItem('viberails.board.collapsedLanes.v1')))).toEqual([]);
        expect(requests.filter(request => request.path.startsWith('/api/v1/board/')
            && request.method !== 'GET' && request.path !== '/api/v1/board/cards/activity')).toEqual([]);
    });
}

for (const width of [1440, 390]) {
    test(`GitHub links preserve card drafts at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 950 });
        let card;
        await openBoard(page, { onCard: value => { card = value; card.codeLinks = []; } });
        await page.route('**/api/v1/board/cards/card_test/code-links**', async route => {
            if (route.request().method() === 'DELETE') {
                card.codeLinks = [];
                return route.fulfill({ json: { ok: true } });
            }
            const { url } = route.request().postDataJSON();
            const link = { id: 'code_test', url, label: 'org/repo PR #42', kind: 'pull_request' };
            card.codeLinks = [link];
            return route.fulfill({ json: link });
        });
        await page.getByText('Description images', { exact: true }).click();
        const editor = page.locator('[data-board-card-editor]');
        const rail = editor.locator('[data-board-code-links]');
        const title = editor.locator('#board-card-title');
        const comment = editor.locator('[data-board-composer="comment"] textarea');
        await title.fill('Keep this draft');
        await comment.fill('Unposted comment');
        await rail.getByRole('textbox').fill('https://github.com/org/repo/pull/42');
        await rail.getByRole('button', { name: 'Link PR or commit', exact: true }).click();
        const link = rail.getByRole('link', { name: 'org/repo PR #42', exact: true });
        await expect(link).toHaveAttribute('href', 'https://github.com/org/repo/pull/42');
        await expect(link).toHaveAttribute('rel', 'noopener noreferrer');
        await expect(title).toHaveValue('Keep this draft');
        await expect(comment).toHaveValue('Unposted comment');
        const unlink = rail.getByRole('button', { name: 'Unlink org/repo PR #42', exact: true });
        await unlink.focus();
        await page.evaluate(() => window.app.boardController.refreshSessionActivity());
        await expect(unlink).toBeFocused();
        await rail.scrollIntoViewIfNeeded();
        await page.screenshot({ path: testInfo.outputPath('github-links.png') });
        await unlink.click();
        await expect(link).toHaveCount(0);
        await expect(title).toHaveValue('Keep this draft');
        await expect(comment).toHaveValue('Unposted comment');
    });
}

async function expectAnimatedStyle(locator, property) {
    const read = () => locator.evaluate((element, name) => getComputedStyle(element)[name], property);
    const initial = await read();
    await expect.poll(read).not.toBe(initial);
}

for (const width of [1440, 390]) {
    test(`card identity and ghost recheck preserve drafts at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 950 });
        let stored;
        const requests = await openBoard(page, { onCard: card => {
            stored = card;
            Object.assign(card, { displayId: 'VIBE-225', key: 'VB-NW72V-297', ghostReason: null,
                agentMade: true, agentMadeBy: 'Claude Opus 5.5' });
        } });
        let checks = 0;
        await page.route('**/api/v1/board/cards/card_test/ghost/recheck', route => {
            checks++;
            stored.ghostReason = null;
            return route.fulfill({ json: { status: 'clear', message: 'No ghost: code has landed.', ghostReason: null } });
        });
        await page.locator('.board-card').click();
        const editor = page.locator('[data-board-card-editor]');
        const identity = editor.locator('.board-card-identity');
        await expect(identity.getByText('Permanent ID: VB-NW72V-297')).toBeVisible();
        await expect(identity.getByText('Made by Claude Opus 5.5')).toBeVisible();
        await expect(identity.locator('#board-card-display-id')).toHaveValue('VIBE-225');
        const recheck = identity.locator('[data-board-recheck-ghost]');
        await expect(recheck).toBeHidden();
        expect(checks).toBe(0);
        await editor.locator('#board-card-title').fill('Keep this unsaved title');
        await identity.locator('#board-card-display-id').fill('CUSTOM-225');
        stored.ghostReason = 'no-code';
        await page.evaluate(() => window.app.boardController.refreshSessionActivity());
        await expect(recheck).toBeVisible();
        await expect(recheck).toHaveAccessibleName("Recheck this card's code landing");
        await identity.screenshot({ path: testInfo.outputPath(`card-identity-ghost-${width}.png`) });
        await recheck.click();
        await expect(identity.locator('[role=status]')).toHaveText('No ghost: code has landed.');
        await expect(recheck).toBeHidden();
        await expect(editor.locator('#board-card-title')).toHaveValue('Keep this unsaved title');
        await expect(identity.locator('#board-card-display-id')).toHaveValue('CUSTOM-225');
        expect(checks).toBe(1);
        expect(requests.some(request => request.method === 'PUT')).toBe(false);
        await identity.screenshot({ path: testInfo.outputPath(`card-identity-${width}.png`) });
    });
}

for (const width of [1440, 390]) {
    test(`ghost cards keep their marker visible and animation contained at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 950 });
        const columns = ['Backlog', 'Build', 'Review', 'Done'].map((name, position) => ({
            id: position ? `col_${position}` : 'col_ready', name, position, boardId: 'brd_main', color: '#64748b'
        }));
        await openBoard(page, { columns, onCard: card => {
            card.ghostReason = 'no-code';
            card.agentMade = true;
            card.displayId = width === 390 ? 'PROJECT-WITH-A-LONG-CARD-KEY-123' : 'VIBE-185';
        } });
        const tile = page.locator('.board-card');
        const marker = tile.locator('.board-ghost-mark');
        const stage = tile.locator('.board-ghost-stage');
        const runner = tile.locator('.board-ghost-runner');
        const label = tile.locator('.board-key-label');
        await expect(marker).toBeVisible();
        await expect(stage).toHaveCSS('overflow', 'hidden');
        await expectAnimatedStyle(runner, 'left');
        for (const time of [0, 2100, 3500, 5740, 7000, 13900]) {
            await tile.evaluate((element, currentTime) => {
                for (const animation of element.getAnimations({ subtree: true })) {
                    animation.pause();
                    animation.currentTime = currentTime;
                }
            }, time);
            await expect(marker).toBeVisible();
            await expect(marker).toHaveCSS('opacity', '1');
            const [markerBounds, stageBounds, pinBounds, titleBounds] = await Promise.all([
                marker.boundingBox(), stage.boundingBox(), tile.locator('.board-card-pin').boundingBox(),
                tile.locator('.board-card-title').boundingBox()
            ]);
            expect(markerBounds.x + markerBounds.width).toBeLessThanOrEqual(stageBounds.x);
            expect(stageBounds.x + stageBounds.width).toBeLessThanOrEqual(pinBounds.x);
            expect(stageBounds.y + stageBounds.height).toBeLessThanOrEqual(titleBounds.y);
            if (time === 3500) {
                await expect(label).toHaveCSS('opacity', '0');
                await expect(runner).toHaveCSS('opacity', '1');
                const ghostBounds = await runner.locator('i').boundingBox();
                expect(ghostBounds.height).toBeGreaterThan(16);
                expect(ghostBounds.y).toBeGreaterThan(stageBounds.y + 1);
                expect(ghostBounds.y + ghostBounds.height).toBeLessThan(stageBounds.y + stageBounds.height - 1);
                await tile.screenshot({ path: testInfo.outputPath(`ghost-animated-${width}.png`) });
            }
        }
        expect(await tile.evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
        await page.emulateMedia({ reducedMotion: 'reduce' });
        await expect(runner).toBeHidden();
        await expect(marker).toBeVisible();
        await expect(label).toHaveCSS('opacity', '1');
        await expect(label).toHaveCSS('animation-name', 'none');
        await tile.screenshot({ path: testInfo.outputPath(`ghost-still-${width}.png`) });
    });
}

for (const width of [1440, 390]) {
    test(`Jira boards combine General and Jira settings at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 950 });
        await openBoard(page, { onCard: card => {
            card.jiraIssueKey = 'PROJECT-WITH-A-LONG-KEY-123';
            card.remoteProvider = 'jira';
            card.remoteIssueKey = 'PROJECT-WITH-A-LONG-KEY-123';
            card.remoteIssueUrl = 'https://example.atlassian.net/browse/PROJECT-WITH-A-LONG-KEY-123';
            card.jiraIssueUrl = card.remoteIssueUrl;
        } });
        const badge = page.locator('.board-card-origin');
        await expect(badge.locator('img')).toHaveAttribute('src', 'assets/img/jira.svg');
        // The tile reads the list's summary, which has no jiraIssueUrl (VIBE-171).
        await expect(badge.getByRole('link')).toHaveAttribute('href', 'https://example.atlassian.net/browse/PROJECT-WITH-A-LONG-KEY-123');
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
        await expect(page.getByRole('tab')).toHaveText(['General', 'Context', 'History']);
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
        await expect(page.getByRole('tab', { name: 'Context', exact: true })).toBeFocused();
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
        await expect(page.getByRole('tab')).toHaveText(['General', 'Jira Cloud', 'Azure DevOps', 'Context', 'History']);
        // A saved legacy connection and retained imported cards do not override the board type.
        await page.getByRole('tab', { name: 'Jira Cloud', exact: true }).click();
        await expect(page.getByLabel('Jira board link')).toHaveValue('https://example.atlassian.net/jira/software/projects/TEST/boards/1');
        await expect(page.locator('.modal-title img')).toHaveCount(0);
        await expect(page.getByRole('tab')).toHaveText(['General', 'Jira Cloud', 'Azure DevOps', 'Context', 'History']);
        await page.getByRole('tab', { name: 'General', exact: true }).click();
        await expect(page.getByLabel('Sync this board with viberails.ai')).toBeVisible();
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
        await expect(page.getByRole('tab')).toHaveText(['General', 'Jira Cloud', 'Azure DevOps', 'Context', 'History']);
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

for (const selected of ['Jira Cloud', 'Context', 'Jira input']) {
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
        const active = selected === 'Context' ? selected : 'General';
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
        if (path.endsWith('/sync')) return route.fulfill({ json: {
            isJiraBoard: path.includes('/brd_jira/'), enabled: false, configured: true
        } });
        if (path.endsWith('/context')) return route.fulfill({ json: { revision: 1, context: { defaultMessage: '', typeOverrides: [] } } });
        if (path.endsWith('/boards')) return route.fulfill({ json: { boards: [
            { id: 'brd_main', name: 'Main', position: 0, isJiraBoard: false },
            ...(connected ? [{ id: 'brd_jira', name: 'Jira · TEST', position: 1, isJiraBoard: true }] : [])
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
    await expect.poll(() => calls.filter(call => call === 'GET /api/v1/board/boards/brd_main/sync').length).toBe(2);
    await expect(page.locator('.modal-title img')).toHaveCount(0);
    await expect(page.getByRole('tab')).toHaveText(['General', 'Jira Cloud', 'Azure DevOps', 'Context', 'History']);
    await expect(page.locator('.board-picker-control .ts-control img')).toHaveAttribute('src', 'assets/img/jira.svg');
    await panel.getByRole('button', { name: 'Pull now' }).click();
    await expect(panel.locator('[data-jira-report]')).toHaveText('Pull complete');
    expect(calls).toContain('POST /api/v1/board/boards/brd_jira/jira/test');
    expect(calls).toContain('POST /api/v1/board/boards/brd_jira/jira/pull');
    await page.getByRole('tab', { name: 'General', exact: true }).click();
    await expect(page.locator('#board-board-name')).toHaveValue('Keep this name draft');
    await expect(page.getByLabel('Sync this board with viberails.ai')).toBeVisible();
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
        // Create board offers a local board or a tracker connection; the tracker forms wait behind their tabs.
        await page.evaluate(() => window.app.boardController.openBoardEditor(null));
        await expect(page.getByRole('tab')).toHaveText(['Local', 'Jira Cloud', 'Azure DevOps']);
        await expect(panel).toBeHidden();
        await expect(page.locator('[data-board-board-editor]')).toContainText('Save the board to configure its agent context');
        await page.evaluate(() => { window.app.closeModal(); window.app.navigate('settings'); });
        await expect(page.getByRole('tab', { name: 'General', exact: true })).toBeVisible();
        await expect(page.getByRole('tab', { name: 'Integrations', exact: true })).toHaveCount(0);
        await expect(panel).toHaveCount(0);
    });
}

// Azure DevOps (VIBE-165) mirrors the Jira settings UX: a provider-aware badge, its own tab on a
// local board, and its connection under General on its dedicated board.
const ADO_LINK = 'https://dev.azure.com/contoso/Fabrikam%20Web/_boards/board/t/Fabrikam%20Team/Stories';
const adoConnection = overrides => ({
    boardId: 'brd_main', organization: 'contoso', project: 'Fabrikam Web', team: 'Fabrikam Team', backlogLevel: 'Stories',
    boardLink: ADO_LINK, adoBoardName: 'Stories', hasToken: true, authStatus: 'saved', enabled: true, skipOldDone: true,
    dedicatedBoard: true, lastWriteWins: true, columns: [], lanes: [], ...overrides
});

for (const width of [1440, 390]) {
    test(`Azure DevOps boards combine General and Azure DevOps settings at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 950 });
        await openBoard(page, { onCard: card => {
            card.remoteProvider = 'ado';
            card.remoteIssueKey = '#4821';
            card.remoteIssueUrl = 'https://dev.azure.com/contoso/Fabrikam%20Web/_workitems/edit/4821';
        } });
        const badge = page.locator('.board-card-origin');
        await expect(badge).toContainText('Azure DevOps · #4821');
        await expect(badge.locator('img')).toHaveAttribute('src', 'assets/img/azure-devops.svg');
        await expect.poll(() => badge.locator('img').evaluate(image => image.complete && image.naturalWidth > 0)).toBe(true);
        await expect(badge.getByRole('link')).toHaveAttribute('href', 'https://dev.azure.com/contoso/Fabrikam%20Web/_workitems/edit/4821');
        expect(await badge.evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
        await page.screenshot({ path: testInfo.outputPath(`board-ado-badge-${width}.png`) });
        let provider = 'ado';
        await page.route('**/api/v1/board/boards/brd_main/sync', route => route.fulfill({ json: {
            isJiraBoard: false, remoteProvider: provider, enabled: !provider, configured: true
        } }));
        await page.route('**/api/v1/board/boards/brd_main/ado?settings=true', route => route.fulfill({ json: adoConnection() }));
        await page.getByRole('button', { name: 'Board settings', exact: true }).click();
        await expect(page.locator('.modal-title img')).toHaveAttribute('src', 'assets/img/azure-devops.svg');
        await expect(page.locator('.modal-title').getByRole('img', { name: 'Azure DevOps board', exact: true })).toBeVisible();
        await expect(page.locator('[data-board-sync]')).toBeHidden();
        await expect(page.getByLabel('Sync this board with viberails.ai')).toHaveCount(0);
        await expect(page.getByRole('tab')).toHaveText(['General', 'Context', 'History']);
        await expect(page.getByRole('tab', { name: 'General', exact: true })).toHaveAttribute('aria-selected', 'true');
        await expect(page.getByLabel('Azure DevOps board or query link')).toHaveValue(ADO_LINK);
        await expect(page.getByLabel('Jira board link')).toBeHidden();
        const token = page.getByLabel('Personal access token', { exact: true });
        await expect(token).toHaveAttribute('placeholder', '•'.repeat(32));
        await expect(token).toHaveValue('');
        await expect(page.getByRole('link', { name: 'Create a token' })).toHaveAttribute('href', 'https://dev.azure.com/contoso/_usersSettings/tokens');
        await expect(page.getByRole('link', { name: 'Create a token' })).toHaveAttribute('target', '_blank');
        await expect(page.locator('[data-ado-panel]')).toContainText('Work Items: Read & write');
        await expect(page.getByLabel('Atlassian account email')).toBeHidden();
        await expect(page.locator('.modal-header [data-ado-status]')).toHaveText('Connected');
        await expect(page.locator('.modal-header [data-jira-heading]')).toBeHidden();
        await page.getByLabel('Board name', { exact: true }).fill('Keep this draft');
        await token.fill('unsaved-test-token');
        await page.getByRole('tab', { name: 'General', exact: true }).focus();
        await page.keyboard.press('ArrowRight');
        await expect(page.getByRole('tab', { name: 'Context', exact: true })).toBeFocused();
        await expect(page.locator('.modal-header [data-ado-heading]')).toBeHidden();
        await page.keyboard.press('Home');
        await expect(page.getByLabel('Board name', { exact: true })).toHaveValue('Keep this draft');
        await expect(token).toHaveValue('unsaved-test-token');
        await expect(page.locator('.modal-header [data-ado-heading]')).toBeVisible();
        await token.fill('');
        const panel = page.locator('[data-ado-panel]');
        expect(await panel.evaluate(el => el.scrollWidth <= el.clientWidth + 1)).toBe(true);
        await page.screenshot({ path: testInfo.outputPath(`board-ado-combined-${width}.png`) });
        await page.evaluate(() => window.app.closeModal());

        // A local board offers both trackers as tabs, and the viberails.ai choice again.
        provider = null;
        await page.getByRole('button', { name: 'Board settings', exact: true }).click();
        await expect(page.getByLabel('Sync this board with viberails.ai')).toBeChecked();
        await expect(page.getByRole('tab')).toHaveText(['General', 'Jira Cloud', 'Azure DevOps', 'Context', 'History']);
        await page.getByRole('tab', { name: 'Azure DevOps', exact: true }).click();
        await expect(page.getByLabel('Azure DevOps board or query link')).toHaveValue(ADO_LINK);
        await expect(page.locator('.modal-header [data-ado-heading]')).toBeVisible();
        await expect(page.locator('.modal-title img')).toHaveCount(0);
        await page.getByRole('tab', { name: 'Azure DevOps', exact: true }).scrollIntoViewIfNeeded();
        await page.screenshot({ path: testInfo.outputPath(`board-ado-tab-${width}.png`) });
    });
}

test('Connect selects its dedicated Azure DevOps board and preserves other settings drafts', async ({ page }) => {
    await openBoard(page);
    let connected = false;
    const calls = [];
    const saves = [];
    await page.route('**/api/v1/board/**', route => {
        const path = new URL(route.request().url()).pathname;
        const method = route.request().method();
        calls.push(`${method} ${path}`);
        const connection = connected ? adoConnection({ boardId: 'brd_ado' })
            : { boardId: 'brd_main', hasToken: false, authStatus: 'none', enabled: false, columns: [], lanes: [] };
        if (path.endsWith('/ado') && method === 'PUT') {
            saves.push(route.request().postDataJSON());
            connected = true;
            return route.fulfill({ json: { ...adoConnection({ boardId: 'brd_ado', adoBoardName: null }), columns: null, lanes: null } });
        }
        if (path.endsWith('/ado')) return route.fulfill({ json: connection });
        if (path.endsWith('/ado/test')) return route.fulfill({ json: { ok: true, project: 'Fabrikam Web', boardId: 'brd_ado', board: {
            name: 'Stories', team: 'Fabrikam Team', backlogLevel: 'Stories', workItemCount: 7, countCapped: false,
            pointsField: 'Microsoft.VSTS.Scheduling.StoryPoints', columns: [{ name: 'New', laneId: 'l1', laneName: 'Ready', automatic: true }], warnings: [] } } });
        if (path.endsWith('/ado/pull')) return route.fulfill({ json: { outcome: 'ok', boardId: 'brd_ado', message: 'Pull complete' } });
        if (path.endsWith('/sync')) return route.fulfill({ json: {
            isJiraBoard: false, remoteProvider: path.includes('/brd_ado/') ? 'ado' : null, enabled: false, configured: true
        } });
        if (path.endsWith('/context')) return route.fulfill({ json: { revision: 1, context: { defaultMessage: '', typeOverrides: [] } } });
        if (path.endsWith('/boards')) return route.fulfill({ json: { boards: [
            { id: 'brd_main', name: 'Main', position: 0, isJiraBoard: false, remoteProvider: null },
            ...(connected ? [{ id: 'brd_ado', name: 'Azure DevOps · Fabrikam Team', position: 1, isJiraBoard: false, remoteProvider: 'ado' }] : [])
        ] } });
        if (path.endsWith('/columns')) return route.fulfill({ json: { columns: [] } });
        return route.fulfill({ json: { cards: [] } });
    });
    await page.getByRole('button', { name: 'Board settings', exact: true }).click();
    await page.locator('#board-board-name').fill('Keep this name draft');
    await page.getByRole('tab', { name: 'Azure DevOps', exact: true }).click();
    const panel = page.locator('[data-ado-panel]');
    await expect(panel.getByRole('link', { name: 'Create a token' })).toBeHidden();
    await panel.getByLabel('Azure DevOps board or query link').fill(ADO_LINK);
    await expect(panel.getByRole('link', { name: 'Create a token' })).toHaveAttribute('href', 'https://dev.azure.com/contoso/_usersSettings/tokens');
    await panel.getByLabel('Personal access token').fill('test-only');
    await panel.getByRole('button', { name: 'Connect', exact: true }).click();
    await expect(panel.locator('[data-ado-report]')).toHaveText('Connected to Fabrikam Web.');
    await expect(panel.locator('[data-ado-board]')).toHaveText('Azure DevOps · Fabrikam Team');
    await expect(page.locator('[data-board-select]')).toHaveValue('brd_ado');
    await expect(panel.locator('[data-ado-summary]')).toContainText('Stories (Fabrikam Team) · about 7 work items');
    await expect(panel.getByLabel('Personal access token')).toHaveValue('');
    expect(saves).toEqual([{ boardLink: ADO_LINK, personalAccessToken: 'test-only', narrowWiql: '', pointsField: '', enabled: true, skipOldDone: true }]);
    expect(calls).toContain('POST /api/v1/board/boards/brd_ado/ado/test');
    expect(calls).not.toContain('POST /api/v1/board/boards/brd_main/ado/test');
    await expect(page.locator('.modal-title img')).toHaveCount(0);
    await expect(page.getByRole('tab')).toHaveText(['General', 'Jira Cloud', 'Azure DevOps', 'Context', 'History']);
    await expect(page.locator('.board-picker-control .ts-control img')).toHaveAttribute('src', 'assets/img/azure-devops.svg');
    await panel.getByRole('button', { name: 'Pull now' }).click();
    await expect(panel.locator('[data-ado-report]')).toHaveText('Pull complete');
    expect(calls).toContain('POST /api/v1/board/boards/brd_ado/ado/pull');
    await page.getByRole('tab', { name: 'General', exact: true }).click();
    await expect(page.locator('#board-board-name')).toHaveValue('Keep this name draft');
    await expect(page.getByLabel('Sync this board with viberails.ai')).toBeVisible();
});

test('Connect on a board nothing else uses makes it the Azure DevOps board, keeping drafts', async ({ page }) => {
    await openBoard(page);
    let connected = false;
    const calls = [];
    const saves = [];
    await page.route('**/api/v1/board/**', route => {
        const path = new URL(route.request().url()).pathname;
        const method = route.request().method();
        calls.push(`${method} ${path}`);
        if (path.endsWith('/ado') && method === 'PUT') {
            saves.push(route.request().postDataJSON());
            connected = true;
            // The server kept the connection on this board: it had no cards, lane Automations or sync.
            return route.fulfill({ json: { ...adoConnection({ adoBoardName: null }), columns: null, lanes: null } });
        }
        if (path.endsWith('/ado')) return route.fulfill({ json: connected ? adoConnection()
            : { boardId: 'brd_main', hasToken: false, authStatus: 'none', enabled: false, columns: [], lanes: [] } });
        if (path.endsWith('/ado/test')) return route.fulfill({ json: { ok: true, project: 'Fabrikam Web', boardId: 'brd_main', board: {
            name: 'Stories', team: 'Fabrikam Team', backlogLevel: 'Stories', workItemCount: 7, countCapped: false,
            pointsField: 'Microsoft.VSTS.Scheduling.StoryPoints', columns: [], warnings: [] } } });
        if (path.endsWith('/sync')) return route.fulfill({ json: {
            isJiraBoard: false, remoteProvider: connected ? 'ado' : null, enabled: false, configured: true
        } });
        if (path.endsWith('/context')) return route.fulfill({ json: { revision: 1, context: { defaultMessage: '', typeOverrides: [] } } });
        if (path.endsWith('/boards')) return route.fulfill({ json: { boards: [
            { id: 'brd_main', name: 'Main', position: 0, isJiraBoard: false, remoteProvider: connected ? 'ado' : null },
            { id: 'brd_local', name: 'Local', position: 1, isJiraBoard: false, remoteProvider: null }
        ] } });
        if (path.endsWith('/columns')) return route.fulfill({ json: { columns: [] } });
        return route.fulfill({ json: { cards: [] } });
    });
    await page.getByRole('button', { name: 'Board settings', exact: true }).click();
    await page.locator('#board-board-name').fill('Keep this name draft');
    await page.getByRole('tab', { name: 'Azure DevOps', exact: true }).click();
    const panel = page.locator('[data-ado-panel]');
    await expect(panel).toContainText('An empty board becomes the Azure DevOps board itself.');
    await panel.getByLabel('Azure DevOps board or query link').fill(ADO_LINK);
    await panel.getByLabel('Personal access token').fill('test-only');
    await panel.getByRole('button', { name: 'Connect', exact: true }).click();
    await expect(panel.locator('[data-ado-report]')).toHaveText('Connected to Fabrikam Web.');
    // Board settings never send newBoard: the server decides whether this board can become it.
    expect(saves).toEqual([{ boardLink: ADO_LINK, personalAccessToken: 'test-only', narrowWiql: '', pointsField: '', enabled: true, skipOldDone: true }]);
    expect(calls).toContain('POST /api/v1/board/boards/brd_main/ado/test');
    expect(calls.filter(call => call.startsWith('POST /api/v1/board/boards/') && call.endsWith('/pull'))).toEqual([]);
    await expect(page.locator('[data-board-select]')).toHaveValue('brd_main');
    // The open board is now the Azure DevOps board: its connection moves under General.
    await expect(page.locator('.modal-title img')).toHaveAttribute('src', 'assets/img/azure-devops.svg');
    await expect(page.getByRole('tab')).toHaveText(['General', 'Context', 'History']);
    await expect(page.getByRole('tab', { name: 'General', exact: true })).toHaveAttribute('aria-selected', 'true');
    await expect(panel).toBeVisible();
    await expect(panel.getByLabel('Azure DevOps board or query link')).toHaveValue(ADO_LINK);
    await expect(page.locator('#board-board-name')).toHaveValue('Keep this name draft');
    await expect(page.getByLabel('Sync this board with viberails.ai')).toHaveCount(0);
    await expect(page.locator('.board-picker-control .ts-control img')).toHaveAttribute('src', 'assets/img/azure-devops.svg');
});

for (const width of [1440, 390]) {
    test(`Create board connects an Azure DevOps board, links the token page and pulls at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 950 });
        await openBoard(page);
        let connected = false;
        const calls = [];
        const saves = [];
        await page.route('**/api/v1/board/**', route => {
            const path = new URL(route.request().url()).pathname;
            const method = route.request().method();
            calls.push(`${method} ${path}`);
            if (path.endsWith('/ado') && method === 'PUT') {
                saves.push(route.request().postDataJSON());
                connected = true;
                return route.fulfill({ json: { ...adoConnection({ boardId: 'brd_ado', adoBoardName: null }), columns: null, lanes: null } });
            }
            if (path.endsWith('/ado')) return route.fulfill({ json: path.includes('/brd_ado/') ? adoConnection({ boardId: 'brd_ado' })
                : { boardId: 'brd_main', hasToken: false, authStatus: 'none', enabled: false, columns: [], lanes: [] } });
            if (path.endsWith('/ado/test')) return route.fulfill({ json: { ok: true, project: 'Fabrikam Web', boardId: 'brd_ado', board: {
                name: 'Stories', team: 'Fabrikam Team', backlogLevel: 'Stories', workItemCount: 7, countCapped: false,
                pointsField: 'Microsoft.VSTS.Scheduling.StoryPoints', columns: [{ name: 'New', laneId: 'l1', laneName: 'Ready', automatic: true }], warnings: [] } } });
            if (path.endsWith('/ado/pull')) return route.fulfill({ json: { outcome: 'ok', boardId: 'brd_ado', message: '7 created, 0 already imported, 0 skipped, 0 failed.' } });
            if (path.endsWith('/boards')) return route.fulfill({ json: { boards: [
                { id: 'brd_main', name: 'Main', position: 0, isJiraBoard: false, remoteProvider: null },
                ...(connected ? [{ id: 'brd_ado', name: 'Azure DevOps · Fabrikam Team', position: 1, isJiraBoard: false, remoteProvider: 'ado' }] : [])
            ] } });
            if (path.endsWith('/columns')) return route.fulfill({ json: { columns: [] } });
            return route.fulfill({ json: { cards: [] } });
        });
        await page.getByRole('button', { name: 'Create board', exact: true }).click();
        await expect(page.locator('.modal-title')).toHaveText('Create board');
        await expect(page.getByRole('tab')).toHaveText(['Local', 'Jira Cloud', 'Azure DevOps']);
        await expect(page.getByLabel('Board name', { exact: true })).toBeVisible();
        await page.getByRole('tab', { name: 'Azure DevOps', exact: true }).click();
        await expect(page.getByLabel('Board name', { exact: true })).toBeHidden();
        const panel = page.locator('[data-ado-panel]');
        await expect(panel).toContainText('Lanes copied from Main');
        await expect(page.locator('.modal-header [data-ado-status]')).toHaveText('Not connected');
        const token = panel.getByRole('link', { name: 'Create a token' });
        await expect(token).toBeHidden();
        await expect(panel).toContainText("Paste the board link above to get a link to your organization's token page.");

        // Each stage of the link says what it found; the organization alone is enough for the token page.
        const link = panel.getByLabel('Azure DevOps board or query link');
        const help = panel.locator('[data-ado-link-help]');
        await link.fill('https://contoso.visualstudio.com/Fabrikam%20Web');
        await expect(help).toContainText('older visualstudio.com address');
        await expect(token).toHaveAttribute('href', 'https://dev.azure.com/contoso/_usersSettings/tokens');
        await link.fill('https://dev.azure.com/contoso');
        await expect(help).toContainText('Organization contoso. To connect, open its team board, backlog or saved query');
        await expect(token).toHaveText(/Create a token in contoso/);
        await link.fill(ADO_LINK);
        await expect(help).toHaveText('Team Fabrikam Team, Stories in Fabrikam Web (contoso).');
        await expect(token).toHaveAttribute('href', 'https://dev.azure.com/contoso/_usersSettings/tokens');
        await expect(token).toHaveAttribute('target', '_blank');
        expect(await panel.evaluate(el => el.scrollWidth <= el.clientWidth + 1)).toBe(true);
        await page.screenshot({ path: testInfo.outputPath(`board-new-ado-${width}.png`) });

        await panel.getByLabel('Personal access token').fill('test-only');
        await panel.getByRole('button', { name: 'Connect', exact: true }).click();
        await expect(panel.locator('[data-ado-report]')).toHaveText('7 created, 0 already imported, 0 skipped, 0 failed.');
        // Create board always asks for a new board, even when Main is empty enough to become it.
        expect(saves).toEqual([{ boardLink: ADO_LINK, personalAccessToken: 'test-only', narrowWiql: '', pointsField: '', enabled: true, skipOldDone: true, newBoard: true }]);
        expect(calls).toContain('PUT /api/v1/board/boards/brd_main/ado');
        expect(calls).toContain('POST /api/v1/board/boards/brd_ado/ado/test');
        expect(calls).toContain('POST /api/v1/board/boards/brd_ado/ado/pull');
        expect(calls).not.toContain('POST /api/v1/board/boards');
        await expect(page.locator('[data-board-select]')).toHaveValue('brd_ado');
        await expect(page.locator('.modal-title')).toHaveText('Board · Azure DevOps · Fabrikam Team');
        await expect(page.locator('.modal-title img')).toHaveAttribute('src', 'assets/img/azure-devops.svg');
        // The pull reloads the saved connection, as Pull now does.
        await expect(panel.locator('[data-ado-summary]')).toContainText('Azure DevOps board Stories');
        await expect(page.locator('.modal-header [data-ado-status]')).toHaveText('Connected');
        await expect(panel.getByLabel('Personal access token')).toHaveValue('');
        await page.screenshot({ path: testInfo.outputPath(`board-new-ado-connected-${width}.png`) });
    });
}

test('Azure DevOps unlink confirms, handles failure and keeps settings drafts', async ({ page }, testInfo) => {
    await openBoard(page);
    let linked = true;
    let attempts = 0;
    await page.route('**/api/v1/board/boards/brd_main/sync', route => route.fulfill({ json: {
        isJiraBoard: false, remoteProvider: linked ? 'ado' : null, enabled: false, configured: true
    } }));
    await page.route(url => url.pathname === '/api/v1/board/boards/brd_main/ado', route => {
        if (route.request().method() === 'DELETE') {
            attempts++;
            if (attempts === 1) return route.fulfill({ status: 400, json: { error: 'Another Azure DevOps operation is running. Try again when it finishes.' } });
            linked = false;
            return route.fulfill({ json: { message: 'Azure DevOps unlinked. Board and cards kept.' } });
        }
        return route.fulfill({ json: linked ? adoConnection()
            : { boardId: 'brd_main', hasToken: false, authStatus: 'none', columns: [], lanes: [] } });
    });
    await page.getByRole('button', { name: 'Board settings', exact: true }).click();
    const icon = page.locator('.modal-title').getByRole('img', { name: 'Azure DevOps board', exact: true });
    await expect(icon).toBeVisible();
    await page.getByLabel('Board name', { exact: true }).fill('Keep my name draft');
    const panel = page.locator('[data-ado-panel]');
    const unlink = panel.getByRole('button', { name: 'Unlink Azure DevOps', exact: true });
    await unlink.click();
    await expect(page.getByRole('alertdialog')).toContainText('imported cards, comments, files and sessions stay in VibeRails');
    await page.getByRole('alertdialog').getByRole('button', { name: 'Cancel', exact: true }).click();
    expect(attempts).toBe(0);

    await unlink.click();
    await page.getByRole('alertdialog').getByRole('button', { name: 'Unlink Azure DevOps', exact: true }).click();
    await expect(panel.locator('[data-ado-report]')).toContainText('Another Azure DevOps operation is running');
    await expect(icon).toBeVisible();
    await expect(unlink).toBeEnabled();

    await unlink.click();
    await page.getByRole('alertdialog').getByRole('button', { name: 'Unlink Azure DevOps', exact: true }).click();
    await expect(panel.locator('[data-ado-report]')).toHaveText('Azure DevOps unlinked. The board and its cards have been kept.');
    await expect(icon).toHaveCount(0);
    await expect(page.getByRole('tab')).toHaveText(['General', 'Jira Cloud', 'Azure DevOps', 'Context', 'History']);
    await expect(page.getByRole('tab', { name: 'Azure DevOps', exact: true })).toHaveAttribute('aria-selected', 'true');
    await expect(unlink).toBeHidden();
    await expect(panel.getByRole('button', { name: 'Pull now', exact: true })).toBeDisabled();
    await expect(panel.getByLabel('Personal access token', { exact: true })).toHaveAttribute('placeholder', 'Paste a personal access token');
    await expect(panel.getByLabel('Azure DevOps board or query link')).toHaveValue('');
    await page.screenshot({ path: testInfo.outputPath('board-ado-unlinked.png') });
    await page.getByRole('tab', { name: 'General', exact: true }).click();
    await expect(page.getByLabel('Board name', { exact: true })).toHaveValue('Keep my name draft');
    await expect(page.getByLabel('Sync this board with viberails.ai')).toBeVisible();
    expect(attempts).toBe(2);
});

for (const selected of ['Azure DevOps', 'Context', 'Azure DevOps input']) {
    test(`delayed Azure DevOps board status preserves drafts and the selected ${selected} section`, async ({ page }) => {
        await openBoard(page);
        let releaseStatus;
        const statusReady = new Promise(resolve => { releaseStatus = resolve; });
        await page.route('**/api/v1/board/boards/brd_main/sync', async route => {
            await statusReady;
            await route.fulfill({ json: { isJiraBoard: false, remoteProvider: 'ado' } });
        });
        await page.route('**/api/v1/board/boards/brd_main/ado?settings=true', route => route.fulfill({ json: {
            boardId: 'brd_main', authStatus: 'none', columns: [], lanes: []
        } }));
        await page.getByRole('button', { name: 'Board settings', exact: true }).click();
        await page.getByLabel('Board name', { exact: true }).fill('Draft before status');
        await page.getByRole('tab', { name: 'Azure DevOps', exact: true }).click();
        await page.getByLabel('Azure DevOps board or query link').fill(ADO_LINK);
        const token = page.getByLabel('Personal access token', { exact: true });
        await token.fill('draft-token');
        if (selected === 'Azure DevOps input') {
            await token.focus();
            await token.evaluate(input => input.setSelectionRange(2, 5));
        } else {
            await page.getByRole('tab', { name: selected, exact: true }).click();
        }
        releaseStatus();
        await expect(page.getByRole('tab', { name: 'Azure DevOps', exact: true })).toHaveCount(0);
        await expect(page.getByRole('tab', { name: 'Jira Cloud', exact: true })).toHaveCount(0);
        const active = selected === 'Context' ? selected : 'General';
        await expect(page.getByRole('tab', { name: active, exact: true })).toHaveAttribute('aria-selected', 'true');
        if (selected === 'Azure DevOps input') {
            await expect(token).toBeFocused();
            expect(await token.evaluate(input => [input.selectionStart, input.selectionEnd])).toEqual([2, 5]);
        } else {
            await expect(page.getByRole('tab', { name: active, exact: true })).toBeFocused();
        }
        await page.getByRole('tab', { name: 'General', exact: true }).click();
        await expect(page.getByLabel('Board name', { exact: true })).toHaveValue('Draft before status');
        await expect(page.getByLabel('Azure DevOps board or query link')).toHaveValue(ADO_LINK);
        await expect(token).toHaveValue('draft-token');
    });
}

test('a tile tracker badge opens its issue without opening the card editor', async ({ page }) => {
    const url = 'https://dev.azure.com/contoso/Fabrikam%20Web/_workitems/edit/4821';
    const opened = [];
    await page.context().route('https://dev.azure.com/**', route => {
        opened.push(route.request().url());
        return route.abort();
    });
    const requests = await openBoard(page, { onCard: card => Object.assign(card, {
        remoteProvider: 'ado', remoteIssueKey: '#4821', remoteIssueUrl: url }) });
    const detailReads = () => requests.filter(request => request.method === 'GET' && request.path === '/api/v1/board/cards/card_test').length;
    const popup = page.waitForEvent('popup');
    await page.locator('.board-card-origin').getByRole('link').click();
    await popup;
    await expect.poll(() => opened).toEqual([url]);
    // A badge click that also opened the editor would read the card twice, or cover the title.
    await page.getByText('Description images', { exact: true }).click();
    await expect(page.locator('[data-board-card-editor]')).toBeVisible();
    expect(detailReads()).toBe(1);
});

test('an Azure DevOps card posts comments to its work item unless unchecked', async ({ page }) => {
    let current;
    const posted = [];
    await openBoard(page, { onCard: card => {
        current = card;
        Object.assign(card, { remoteProvider: 'ado', remoteIssueKey: '#4821', remoteDeliveries: [
            { id: 'd1', kind: 'session', status: 'failed', message: 'Sign in to your VibeRails account before sharing.' }] });
    } });
    await page.route('**/api/v1/board/cards/card_test/comments', route => {
        const body = route.request().postDataJSON();
        posted.push(body);
        const comment = { id: `posted_${posted.length}`, body: body.body, syncToJira: body.syncToRemote,
            author: { kind: 'human', label: 'You' }, createdAt: current.createdAt };
        current.comments.push(comment);
        return route.fulfill({ json: comment });
    });
    await page.getByText('Description images', { exact: true }).click();
    const editor = page.locator('[data-board-card-editor]');
    await expect(editor.locator('[data-board-remote-delivery-status]')).toContainText('Azure DevOps session link: Sign in to your VibeRails account before sharing.');
    const sync = editor.getByLabel('Post this comment to Azure DevOps');
    await expect(sync).toBeChecked();
    const composer = page.locator('[data-board-composer="comment"]');
    await composer.locator('textarea').fill('Shared with the work item');
    await composer.locator('textarea').press('Control+Enter');
    await expect(composer.locator('textarea')).toHaveValue('');
    await sync.uncheck();
    await composer.locator('textarea').fill('Internal only');
    await composer.locator('textarea').press('Control+Enter');
    await expect.poll(() => posted.length).toBe(2);
    expect(posted.map(body => body.syncToRemote)).toEqual([true, false]);
    expect(posted.every(body => !('syncToJira' in body))).toBe(true);
});

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
    test(`Markdown is always enabled and composers have no preview at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 950 });
        await page.addInitScript(() => localStorage.setItem('viberails.board.markdown', 'off'));
        const text = '# Heading\n**bold** and *italic*\n- [x] checked\n' + DESCRIPTION;
        await openBoard(page, { onCard: card => {
            card.comments = [{ id: 'markdown_comment', body: text, author: { kind: 'user', label: 'You' }, createdAt: card.createdAt }];
        } });
        await page.getByText('Description images', { exact: true }).click();
        const description = page.locator('[data-board-composer="description"]');
        await expect(description.locator('textarea')).toBeVisible();
        await expect(page.getByRole('button', { name: 'Markdown', exact: true })).toHaveCount(0);
        await description.locator('textarea').fill(text);
        const composer = page.locator('[data-board-composer="comment"]');
        await composer.locator('textarea').fill(text);
        // Each composer is one native field: nothing renders beneath the text, images included.
        await expect(page.locator('[data-board-composer] :is([data-board-composer-live], [data-board-composer-preview], [data-board-composer-toggle], .prompt-image-previews, img)')).toHaveCount(0);
        const posted = page.locator('[data-board-comments] [data-board-comment-body]');
        await expect(posted.locator('h1')).toHaveText('Heading');
        await expect(posted.locator('strong')).toHaveText('bold');
        await expect(posted.locator('img[data-board-image]')).toHaveCount(1);
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
    // Comments are the only preview surface; the composers stay plain text.
    await expect(page.locator('[data-board-composer] [data-board-image]')).toHaveCount(0);
    expect(reads).toBe(1);
    await page.evaluate(() => window.app.boardController.refreshSessionActivity());
    await expect(image).toHaveAttribute('src', /^blob:/);
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
    const session = { id: '12345678-1234-1234-1234-123456789abc', displayName: 'Earlier investigation', workingDirectory: 'C:/board-fixture', cli: 'codex', createdAt: '2026-10-05T15:30:00Z', active: false };
    const commit = { sha: 'abcdef0123456789abcdef0123456789abcdef01', shortSha: 'abcdef0', message: 'Fix the issue' };
    await openBoard(page, { onCard: card => {
        current = card;
        card.comments.push({ id: 'reference_comment', author: { kind: 'user', label: 'You' }, createdAt: card.createdAt,
            body: '#' + commit.sha + ' !' + session.id.replaceAll('-', '').toUpperCase() });
    } });
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
    const posted = page.locator('[data-board-comments]');
    await expect(posted.locator('[data-board-ref-commit]')).toBeVisible();
    await page.evaluate(() => { window.app.boardController.openSessionReplay = session => { window.__openedReferenceSession = session; }; });
    await posted.locator('[data-board-ref-session]').click();
    expect(await page.evaluate(() => window.__openedReferenceSession.id)).toBe(session.id);
});

for (const { width, mouse } of [{ width: 1280, mouse: false }, { width: 375, mouse: true }]) {
    for (const choice of ['session', 'card']) test(`new-card ${choice} reference stops searching before existing text at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 950 });
        await openBoard(page);
        const session = { id: '12345678-1234-1234-1234-123456789abc', displayName: 'Earlier investigation', cli: 'codex' };
        const searches = [];
        await page.route('**/api/v1/board/cards/link-candidates?*', route => {
            searches.push(new URL(route.request().url()).searchParams.get('q'));
            return route.fulfill({ json: { cards: [{ id: 'other_card', key: 'VB-AAAAA-7', displayId: 'VIBE-7', title: 'Other card' }] } });
        });
        await page.route('**/api/v1/board/cards/other_card/sessions', route => route.fulfill({ json: [session] }));
        await page.getByRole('button', { name: 'Create card', exact: true }).click();
        const composer = page.locator('[data-board-composer="description"]');
        const input = composer.locator('textarea');
        const popup = composer.locator('[data-board-file-popup]');
        await input.fill('See  for details.');
        await input.press('Home');
        for (let i = 0; i < 4; i++) await input.press('ArrowRight');
        await input.pressSequentially('!Other card');
        await expect(popup.getByRole('option')).toContainText(['Other card']);
        if (mouse) await popup.getByRole('option').click();
        else await input.press('Enter');
        await expect(popup.getByRole('option')).toContainText(['Earlier investigation', 'Reference card VIBE-7']);
        const searchCount = searches.length;
        if (mouse) await popup.getByRole('option').nth(choice === 'card' ? 1 : 0).click();
        else {
            if (choice === 'card') await input.press('ArrowDown');
            await input.press('Enter');
        }
        const reference = choice === 'card' ? '![VIBE-7](card:other_card)' : `!${session.id}`;
        await expect(input).toHaveValue(`See ${reference} for details.`);
        await expect(popup).toHaveCount(0);
        await expect(input).toBeFocused();
        await input.pressSequentially('more ');
        await expect(input).toHaveValue(`See ${reference} more for details.`);
        // Let the search debounce elapse to catch a popup reopened by continued typing.
        await page.waitForTimeout(300);
        await expect(popup).toHaveCount(0);
        expect(searches).toHaveLength(searchCount);
        await page.screenshot({ path: testInfo.outputPath(`selected-${choice}-${width}.png`) });

        // Completing a reference must not disable future reference searches.
        await input.press('End');
        await input.pressSequentially(' !Other');
        await expect(popup).toContainText('Other card');
        expect(searches).toHaveLength(searchCount + 1);
    });
}

for (const { width, mouse } of [{ width: 1280, mouse: false }, { width: 375, mouse: true }]) {
    test(`typing after a selected card ID stops reference search at ${width}px`, async ({ page }) => {
        await page.setViewportSize({ width, height: 950 });
        await openBoard(page);
        const searches = [];
        await page.route('**/api/v1/board/cards/link-candidates?*', route => {
            searches.push(new URL(route.request().url()).searchParams.get('q'));
            return route.fulfill({ json: { cards: [{ id: 'other_card', key: 'VB-AAAAA-7', displayId: 'VIBE-7', title: 'Other card' }] } });
        });
        await page.route('**/api/v1/board/cards/other_card/sessions', route => route.fulfill({ json: [] }));
        await page.getByRole('button', { name: 'Create card', exact: true }).click();
        const composer = page.locator('[data-board-composer="description"]');
        const input = composer.locator('textarea');
        const popup = composer.locator('[data-board-file-popup]');
        await input.fill('See  for details.');
        await input.press('Home');
        for (let i = 0; i < 4; i++) await input.press('ArrowRight');
        await input.pressSequentially('!Other card');
        await expect(popup.getByRole('option')).toContainText(['Other card']);
        if (mouse) await popup.getByRole('option').click();
        else await input.press('Enter');
        await expect(input).toHaveValue('See !VB-AAAAA-7 for details.');
        await expect(popup).toContainText('Reference card VIBE-7');
        const searchCount = searches.length;

        // Continue the sentence after the selected ID, leaving the session picker behind.
        await input.press('End');
        await input.pressSequentially(' More details.');
        await page.waitForTimeout(300);
        await expect(popup).toHaveCount(0);
        expect(searches).toHaveLength(searchCount);
        await expect(input).toHaveValue('See !VB-AAAAA-7 for details. More details.');

        await input.pressSequentially(' !Other');
        await expect(popup).toContainText('Other card');
        expect(searches).toHaveLength(searchCount + 1);
    });
}

for (const width of [320, 375, 768, 1024, 1280]) test(`Board descriptions share prompt highlighting and compact variables at ${width}px`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 950 });
    const requests = await openBoard(page);
    await page.getByRole('button', { name: 'Create card', exact: true }).click();
    const editor = page.locator('[data-board-card-editor]');
    const composer = editor.locator('[data-board-composer="description"]');
    const input = composer.locator('textarea');
    const help = editor.locator('.prompt-variable-help');
    const variables = help.getByRole('group', { name: 'Insert a prompt variable' });
    await editor.locator('#board-card-title').fill('Shared composer');
    const source = 'Review @src/example.cs with !VIBE-123 and #abcdef0 at {{time}}.\n`@src/plain.cs` <img src=x onerror="window.__injected=true">';
    await input.fill(source);
    const mirror = composer.locator('.prompt-reference-mirror');
    await expect(mirror.locator('[data-prompt-reference]')).toHaveText(['@src/example.cs', '!VIBE-123', '#abcdef0', '{{time}}']);
    expect(await mirror.textContent()).toBe(source + '\u200b');
    await expect(mirror.locator('img,script')).toHaveCount(0);
    await expect(composer.locator('[data-board-composer-live]')).toHaveCount(0);
    await expect(composer.locator('.prompt-reference-overlay')).toHaveAttribute('aria-hidden', 'true');
    await expect(input).toHaveAttribute('aria-describedby', 'board-description-variable-help');
    await expect(variables.getByRole('button')).toHaveCount(9);
    await expect(help.locator('details')).not.toHaveAttribute('open', '');
    const date = variables.locator('[data-prompt-variable="{{date}}"]');
    expect((await date.boundingBox()).height).toBe(width <= 768 ? 44 : 32);
    expect(await variables.evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true);
    expect(await editor.evaluate(el => el.scrollWidth <= el.clientWidth)).toBe(true);
    await input.scrollIntoViewIfNeeded();
    const point = await mirror.locator('[data-prompt-reference="file"]').evaluate(el => {
        const rect = el.getBoundingClientRect(); return { x: rect.left + 3, y: rect.top + rect.height / 2 };
    });
    await page.mouse.click(point.x, point.y);
    const caret = await input.evaluate(el => el.selectionStart);
    expect(caret).toBeGreaterThanOrEqual(source.indexOf('@src'));
    expect(caret).toBeLessThanOrEqual(source.indexOf('@src') + 2);
    await input.evaluate(el => el.setSelectionRange(el.value.indexOf('{{time}}'), el.value.indexOf('{{time}}') + 8));
    await date.click();
    await expect(input).toBeFocused();
    await expect(input).toHaveValue(source.replace('{{time}}', '{{date}}'));
    await input.press('ControlOrMeta+z');
    await expect(input).toHaveValue(source);
    await date.focus();
    await date.press('Enter');
    await expect(input).toHaveValue(source.replace('{{time}}', '{{date}}'));
    await expect(mirror.locator('[data-prompt-reference="variable"]')).toHaveText('{{date}}');
    await help.locator('summary').click();
    await expect(help.getByText('Replace', { exact: false })).toBeVisible();
    await help.locator('summary').click();
    await input.scrollIntoViewIfNeeded();
    await page.screenshot({ path: testInfo.outputPath(`board-composer-${width}.png`) });
    await editor.locator('[data-board-save-card]').click();
    expect(requests.find(r => r.method === 'POST' && r.path === '/api/v1/board/cards').body.description).toBe(source.replace('{{time}}', '{{date}}'));
    await page.getByText('Shared composer', { exact: true }).click();
    await expect(input).toHaveValue(source.replace('{{time}}', '{{date}}'));
    await expect(mirror.locator('[data-prompt-reference="variable"]')).toHaveText('{{date}}');
});

for (const width of [375, 1280]) test(`draft images are listed in Attachments and survive creation at ${width}px`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 950 });
    const requests = await openBoard(page, { inlineImages: false });
    await page.getByRole('button', { name: 'Create card', exact: true }).click();
    await page.locator('#board-card-title').fill('Draft image');
    const composer = page.locator('[data-board-composer="description"]');
    await composer.locator('textarea').fill('Use this layout with @src/example.cs at {{date}}.');
    await composer.locator('[data-board-composer-file]').setInputFiles({ name: 'draft.png', mimeType: 'image/png', buffer: Buffer.from(IMAGE.split(',')[1], 'base64') });
    const pending = page.locator('[data-board-attachments] .board-attachment-row', { hasText: 'draft.png' });
    await expect(pending).toContainText('Uploads when you save');
    await pending.scrollIntoViewIfNeeded();
    await expect.poll(() => pending.locator('img.board-attachment-thumb').evaluate(img => img.naturalWidth)).toBe(1);
    // The description stays one native field; the image is listed only under Attachments.
    await expect(composer.locator(':is(img, .prompt-image-previews, [data-board-composer-live])')).toHaveCount(0);
    expect(requests.filter(r => r.path.includes('/attachments/'))).toEqual([]);
    await expect(composer.locator('textarea')).toHaveValue(/attachment:pending_/);
    await page.screenshot({ path: testInfo.outputPath(`board-pending-image-${width}.png`) });
    await page.locator('[data-board-save-card]').click();
    // Saved metadata omits inline data; the viewer must use the Board-owned content loader.
    await page.getByText('Draft image', { exact: true }).click();
    await expect(composer.locator('textarea')).not.toHaveValue(/attachment:pending_/);
    await page.locator('[data-board-view-attachment="att_uploaded"]').click();
    const viewer = page.getByRole('dialog', { name: 'Attachment preview', exact: true });
    await expect.poll(() => viewer.locator('img').evaluate(img => img.naturalWidth)).toBe(1);
    expect(requests.some(r => r.path === '/api/v1/board/cards/card_created/attachments/att_uploaded/content')).toBe(true);
    await viewer.getByRole('button', { name: 'Close attachment', exact: true }).click();
    await page.locator('[data-board-remove-attachment="att_uploaded"]').click();
    await page.getByRole('button', { name: 'Delete', exact: true }).last().click();
    await expect(page.locator('[data-board-view-attachment="att_uploaded"]')).toHaveCount(0);
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
    expect(reads).toBe(1); // Only Comments render the image; the description stays plain text.
    await page.evaluate(() => window.app.closeModal());
    expect(await page.evaluate(() => window.__blobUrls.length > 0 && window.__blobUrls.every(url => window.__revokedUrls.includes(url)))).toBe(true);
});

test('a failed authenticated preview can retry without reopening the card', async ({ page }) => {
    await openBoard(page, { onCard: card => {
        card.description = '';
        card.attachments[0].url = '';
        card.comments = [{ id: 'image_comment', body: DESCRIPTION, author: { kind: 'user', label: 'You' }, createdAt: card.createdAt }];
    } });
    let reads = 0;
    await page.route('**/api/v1/board/cards/card_test/attachments/att_image/content', route => {
        reads++;
        if (reads === 1) return route.fulfill({ status: 503, json: { error: 'Temporary failure' } });
        return route.fulfill({ contentType: 'application/octet-stream', body: Buffer.from(IMAGE.split(',')[1], 'base64') });
    });
    const failed = page.waitForResponse(response => response.url().endsWith('/attachments/att_image/content') && response.status() === 503);
    await page.getByText('Description images', { exact: true }).click();
    await failed;
    // A later render of Comments retries the failed image. Changing the filter re-renders them;
    // keep toggling until the failed request has settled and a retry lands.
    const filter = page.locator('[data-board-comment-filter]');
    const image = page.locator('[data-board-comments] img[data-board-image]');
    let choice = 'all';
    await expect.poll(async () => {
        if (await image.evaluate(img => img.complete && img.naturalWidth > 0)) return true;
        choice = choice === 'all' ? 'human' : 'all';
        await filter.selectOption(choice);
        return false;
    }).toBe(true);
    expect(reads).toBe(2);
});

test('posting a comment clears the input and shows it in Comments', async ({ page }) => {
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
    await expect(composer.locator('[data-board-composer-live], strong')).toHaveCount(0);
    await composer.locator('textarea').press('Control+Enter');
    await expect(composer.locator('textarea')).toHaveValue('');
    await expect(page.locator('[data-board-comments] strong')).toContainText('posted');
});

for (const width of [1440, 390]) {
    test(`card Checks stay under Advanced and preserve scope and safe reports at ${width}px`, async ({ page }, testInfo) => {
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
        await expect(page.locator('[data-board-advanced] [data-board-checks]')).toHaveCount(1);
        await expect(panel).not.toBeVisible();
        await page.locator('[data-board-advanced] > summary').click();
        await expect(panel).toBeVisible();
        await expect(panel.locator('[data-check-summary]')).toContainText('Not run');
        expect(runs).toBe(0);
        await expect(panel.locator('[data-check-run-options]')).toHaveCount(0);
        await expect(panel.locator('[data-check-history]')).toHaveCount(0);
        await expect(page.locator('[data-board-reviews]')).toHaveCount(1);
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
    await expect(page.locator('[data-board-shared-notice]')).toBeHidden(); // an owned board has no shared notice
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

// VIBE-235: collaborators read shared boards on viberails.ai. Nothing imports one, and a copy an
// older version imported says it is read-only and no longer syncs.
test('boards shared with you are not imported; a retained copy shows a read-only notice', async ({ page }) => {
    const url = 'https://viberails.ai/Boards/11111111-1111-4111-8111-111111111111';
    const requests = await openBoard(page, { mainBoard: { shared: true, sharedUrl: url } });
    await expect(page.getByRole('button', { name: 'Shared boards', exact: true })).toHaveCount(0);
    const notice = page.locator('[data-board-shared-notice]');
    await expect(notice).toBeVisible();
    await expect(notice).toContainText('Shared with you (read-only).');
    await expect(notice).toContainText('cannot be edited or used to start work');
    const link = notice.getByRole('link', { name: 'Open on viberails.ai' });
    await expect(link).toHaveAttribute('href', url);
    await expect(link).toHaveAttribute('rel', 'noopener noreferrer');
    expect(requests.filter(request => request.path.startsWith('/api/v1/board/shared'))).toEqual([]);
});

test('a shared copy never links a non-https address', async ({ page }) => {
    await openBoard(page, { mainBoard: { shared: true, sharedUrl: 'javascript:alert(1)' } });
    const notice = page.locator('[data-board-shared-notice]');
    await expect(notice).toContainText('Shared with you (read-only).');
    await expect(notice.getByRole('link')).toHaveCount(0);
});

test('move and merge use explicit destinations and preserve drafts until saved', async ({ page }, testInfo) => {
    let current;
    await openBoard(page, { relatedCards: true, onCard: card => { current = card; } });
    let moved, merged;
    let finishMove;
    const moveResponse = new Promise(resolve => { finishMove = resolve; });
    await page.route('**/api/v1/board/columns/col_build/launches', route => route.fulfill({ json: { selections: [{ definitionId: "preset_7", definitionType: "preset", gate: "result" }] } }));
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
    await expect(confirmation).toContainText('1 configured Agent or Action');
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

test('move rechecks edits made while loading the destination before confirmation', async ({ page }) => {
    await openBoard(page, { relatedCards: true });
    let releaseDestination;
    const destinationResponse = new Promise(resolve => { releaseDestination = resolve; });
    let destinationRequested = false;
    let moved = false;
    await page.route('**/api/v1/board/columns/col_build/launches', async route => {
        destinationRequested = true;
        await destinationResponse;
        return route.fulfill({ json: { jobIds: [] } });
    });
    await page.route('**/api/v1/board/cards/card_test/move', route => {
        moved = true;
        return route.fulfill({ json: {} });
    });
    await page.getByText('Description images', { exact: true }).click();
    const editor = page.locator('[data-board-card-editor]');
    await editor.getByText('Move or merge', { exact: true }).click();
    await editor.locator('[data-move-board]').selectOption('brd_sprint');
    await expect(editor.locator('[data-move-lane]')).toHaveValue('col_build');
    await editor.locator('[data-move-card]').click();
    await expect.poll(() => destinationRequested).toBe(true);
    await editor.locator('#board-card-title').fill('Keep this new draft');
    releaseDestination();
    await page.getByRole('alertdialog').getByRole('button', { name: 'Move', exact: true }).click();
    await expect(editor.locator('[data-organize-status]')).toContainText('Save your edits');
    await expect(editor.locator('#board-card-title')).toHaveValue('Keep this new draft');
    await expect(editor.locator('[data-move-card]')).toBeEnabled();
    expect(moved).toBe(false);
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
    await page.getByRole('button', { name: 'Create card', exact: true }).click();
    const editor = page.locator('[data-board-card-editor]');
    await editor.locator('#board-card-title').fill('Linked from the beginning');
    await editor.locator('[data-board-link-picker] > summary').click();
    await expect(editor.locator('[data-board-link-card]')).toHaveCount(0);
    await editor.locator('[data-board-link-search]').fill('Related');
    await editor.locator('[data-board-link-card="card_related"]').click();
    await expect(editor.locator('[data-board-linked-cards]')).toContainText('Related work');
    await expect(editor.locator('[data-board-link-search]')).toHaveValue('');
    await expect(editor.locator('[data-board-link-search]')).toBeFocused();
    await expect(editor.locator('[data-board-link-card]')).toHaveCount(0);
    expect(requests.filter(r => r.method === 'POST' && r.path.startsWith('/api/v1/board/cards')
        && r.path !== '/api/v1/board/cards/activity')).toEqual([]);
    await editor.locator('[data-board-unlink-card="card_related"]').click();
    await expect(editor.locator('[data-board-linked-cards]')).not.toContainText('Related work');
    await expect(editor.locator('[data-board-link-card]')).toHaveCount(0);
    await editor.locator('[data-board-link-search]').fill('Related');
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
    await expect(editor.getByRole('region', { name: 'Discuss this card' })).toContainText('Leave blank for an open discussion.');
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

// BoardCardSummaryResponse: the lane list never carries detail-only fields such as jiraIssueUrl,
// comments or attachments, so a tile that needs one fails here as it would against the server.
const SUMMARY_FIELDS = ['id', 'key', 'columnId', 'position', 'title', 'description', 'assignee', 'priority', 'points',
    'tags', 'blocked', 'commentCount', 'activeSessionId', 'activeTabId', 'createdAt', 'updatedAt', 'baseLlmOptions',
    'type', 'boardId', 'flagged', 'hasActiveAutomation', 'displayId', 'agentMade', 'hasWaitingAutomation', 'agentMadeBy',
    'agentMadeSessionId', 'jiraIssueKey', 'workflowMode', 'managerSelection', 'managerBaseLlmOptions',
    'hasActiveDesktopAgent', 'remoteProvider', 'remoteIssueKey', 'remoteIssueUrl', 'pinned', 'ghostReason', 'remoteCreated'];
const cardSummary = card => Object.fromEntries(SUMMARY_FIELDS.filter(field => field in card).map(field => [field, card[field]]));

async function openBoard(page, { active = false, assignee = null, relatedCards = false, empty = false, inlineImages = true,
    onCard = () => {}, extraPickerItems = [], mainBoard = {}, appSettings = {},
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
            expect(relatedCards ? ['brd_main', 'brd_sprint'] : ['brd_main']).toContain(boardId);
            expect(cardIds.length).toBeLessThanOrEqual(100);
            return route.fulfill({ json: { cards: [card, ...related]
                .filter(item => item.boardId === boardId && cardIds.includes(item.id))
                .map(item => ({ id: item.id,
                    activeSessionId: item.activeSessionId, activeTabId: item.activeTabId,
                    hasActiveAutomation: Boolean(item.hasActiveAutomation),
                    hasActiveDesktopAgent: Boolean(item.hasActiveDesktopAgent) })) } });
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
            const attachment = { id: 'att_uploaded', name: upload.name, url: inlineImages && upload.mimeType?.startsWith('image/') ? upload.dataUrl : '', mimeType: upload.mimeType, bytes: upload.bytes };
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
            '/api/v1/settings': appSettings,
            '/api/v1/environments': { environments: [{ id: 7, name: 'Card review', cli: 'claude' }] },
            '/api/v1/llm-picker/preferences': { items: [
                { key: 'base:codex', kind: 'base', group: 'Base CLIs', label: 'Codex', cli: 'codex', enabled: true, order: 0 },
                { key: 'base:claude', kind: 'base', group: 'Base CLIs', label: 'Claude', cli: 'claude', enabled: true, order: 1 },
                { key: 'preset:fixture_7:claude', kind: 'agent', group: 'AI Agents', label: 'Card review (claude)',
                    cli: 'claude', environmentId: 7, enabled: true, order: 2 },
                ...extraPickerItems
            ] },
            '/api/v1/board/boards': { boards: [{ id: 'brd_main', name: 'Main', position: 0, cardCount: empty ? 0 : 1,
                columns, ...mainBoard },
                ...(relatedCards ? [{ id: 'brd_sprint', name: 'Sprint 2', position: 1, cardCount: 2,
                    columns: [{ id: 'col_build', name: 'Build', position: 0, color: '#06b6d4', boardId: 'brd_sprint' }] }] : [])] },
            '/api/v1/board/columns': { columns },
            '/api/v1/board/cards': { cards: empty ? [] : [cardSummary(card)] }
        };
        return route.fulfill({ json: payloads[path] || {} });
    });
    await page.goto('/?view=board', { waitUntil: 'domcontentloaded' });
    await expect(page.locator('#app-content [data-view="board"]')).toBeVisible();
    return requests;
}

for (const width of [1440, 390]) {
    test(`remote push origin filters cards and search and persists at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 950 });
        let template;
        await openBoard(page, { onCard: card => { template = card; } });
        const cards = [
            { ...template, id: 'remote-bug', title: 'Remote bug', type: 'bug', remoteCreated: true },
            { ...template, id: 'remote-task', title: 'Remote task', type: 'task', remoteCreated: true },
            { ...template, id: 'local-task', title: 'Local task', type: 'task', remoteCreated: false }
        ];
        const requests = [];
        const matches = params => cards.filter(card =>
            (!params.get('origin') || params.get('origin') === 'remote' && card.remoteCreated)
            && (!params.get('type') || params.get('type') === card.type));
        await page.route('**/api/v1/board/cards?*', route => {
            const params = new URL(route.request().url()).searchParams;
            requests.push(Object.fromEntries(params));
            return route.fulfill({ json: { cards: matches(params) } });
        });
        await page.route('**/api/v1/board/cards/search?*', route => {
            const params = new URL(route.request().url()).searchParams;
            requests.push(Object.fromEntries(params));
            return route.fulfill({ json: { cards: matches(params).map(card => ({ ...card,
                isCurrentProject: true, boardName: 'Main', columnName: 'Ready', snippet: card.title })) } });
        });
        await page.evaluate(() => window.app.boardController.refresh());
        await expect(page.locator('.board-card')).toHaveCount(3);
        const origin = page.getByRole('combobox', { name: 'Filter by who made the card' });
        await origin.selectOption({ label: 'Remote push' });
        await expect(page.locator('.board-card')).toHaveCount(2);
        await expect.poll(() => requests.at(-1)?.origin).toBe('remote');
        await page.locator('[data-board-filter-type]').selectOption('bug');
        await expect(page.locator('.board-card')).toHaveCount(1);
        await page.locator('[data-board-search]').fill('Remote');
        await expect(page.locator('[data-board-search-card]')).toHaveCount(1);
        await expect.poll(() => requests.at(-1)?.q).toBe('Remote');
        expect(requests.at(-1)).toMatchObject({ q: 'Remote', origin: 'remote', type: 'bug' });
        await page.reload();
        await expect(page.locator('#app-content #vb-terminal-panel')).toBeVisible();
        await page.getByRole('navigation').getByRole('button', { name: /^Board(?: —.*)?$/ }).click();
        await expect(origin).toHaveValue('remote');
        await expect(page.locator('[data-board-search-card]')).toHaveCount(1);
        await origin.focus();
        await expect(origin).toBeFocused();
        await page.screenshot({ path: testInfo.outputPath(`remote-origin-${width}.png`) });
        await page.locator('[data-board-clear-filters]').click();
        await expect(origin).toHaveValue('');
        await expect(page.locator('.board-card')).toHaveCount(3);
    });

    test(`lane colours save custom selections and stay in sync with presets at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 900 });
        const columns = [{ id: 'col_ready', name: 'Ready', position: 0, color: '#3b82f6', boardId: 'brd_main' }];
        await openBoard(page, { columns, empty: true });
        const writes = [];
        await page.route('**/api/v1/board/columns', route => {
            if (route.request().method() !== 'POST') return route.fulfill({ json: { columns } });
            const body = route.request().postDataJSON();
            writes.push(body);
            const column = { ...body, id: 'col_custom', position: columns.length };
            columns.push(column);
            return route.fulfill({ json: column });
        });
        await page.route('**/api/v1/board/columns/col_custom', route => {
            const body = route.request().postDataJSON();
            writes.push(body);
            Object.assign(columns[1], body);
            return route.fulfill({ json: columns[1] });
        });

        await page.locator('[data-board-action="add-lane"]').click();
        const editor = page.locator('[data-board-lane-editor]');
        const picker = editor.getByLabel('Custom colour', { exact: true });
        const selectedPresets = editor.locator('[data-board-color][aria-pressed="true"]');
        await expect(picker).toHaveValue('#3b82f6');
        await picker.fill('#cf6c48');
        await expect(selectedPresets).toHaveCount(0);
        await expect(editor.locator('output')).toHaveText('#cf6c48');
        await editor.getByRole('button', { name: 'Lane colour #10b981', exact: true }).click();
        await expect(picker).toHaveValue('#10b981');
        await expect(editor.locator('output')).toHaveText('#10b981');
        await expect(selectedPresets).toHaveCount(1);
        await picker.fill('#cf6c48');
        await editor.getByLabel('Name', { exact: true }).fill('Custom lane');
        await page.screenshot({ path: testInfo.outputPath(`lane-colour-${width}.png`) });
        expect(await editor.evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
        await editor.getByRole('button', { name: 'Create lane', exact: true }).click();
        await expect(editor).toHaveCount(0);
        expect(writes[0]).toEqual({ name: 'Custom lane', color: '#cf6c48', boardId: 'brd_main' });

        const lane = page.locator('.board-lane[data-column-id="col_custom"]');
        await expect(lane).toHaveCSS('--lane-color', '#cf6c48');
        await page.getByRole('button', { name: 'Settings for Custom lane', exact: true }).click();
        await expect(picker).toHaveValue('#cf6c48');
        await expect(selectedPresets).toHaveCount(0);
        await picker.fill('#000000');
        await editor.getByRole('button', { name: 'Save lane', exact: true }).click();
        await expect(editor).toHaveCount(0);
        expect(writes[1]).toEqual({ name: 'Custom lane', color: '#000000' });
        await expect(lane).toHaveCSS('--lane-color', '#000000');

        await page.getByRole('button', { name: 'Settings for Custom lane', exact: true }).click();
        await expect(picker).toHaveValue('#000000');
        await editor.getByRole('button', { name: 'Lane colour #a855f7', exact: true }).click();
        await editor.getByRole('button', { name: 'Save lane', exact: true }).click();
        await expect(editor).toHaveCount(0);
        await page.getByRole('button', { name: 'Settings for Custom lane', exact: true }).click();
        await expect(picker).toHaveValue('#a855f7');
        await expect(selectedPresets).toHaveAttribute('data-board-color', '#a855f7');
    });
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
    test(`compact board controls and Create card work at ${width}px`, async ({ page }, testInfo) => {
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
        // Shared 16px fields and 40px targets wrap to two filter rows at tablet widths.
        if (width >= 900) expect(bounds.height).toBeLessThan(width >= 1200 ? 120 : 164);
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
        await page.getByRole('button', { name: 'Create card', exact: true }).click();
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

for (const width of [1440, 390]) {
    test(`larger cards can be pinned and unpinned without opening the editor at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 950 });
        let card;
        await openBoard(page, { onCard: value => { card = value; card.pinned = false; } });
        const writes = [];
        let failSave = false;
        await page.route('**/api/v1/board/cards/card_test/pin', async route => {
            const body = route.request().postDataJSON();
            writes.push(body);
            if (failSave) return route.fulfill({ status: 500, json: { error: 'Pin save failed.' } });
            card.pinned = body.pinned;
            return route.fulfill({ json: card });
        });
        const tile = page.locator('.board-card[data-card-id="card_test"]');
        const pin = tile.locator('[data-board-action="toggle-pin"]');
        await expect(pin).toHaveAttribute('aria-pressed', 'false');
        await expect.poll(async () => (await tile.boundingBox()).height).toBeGreaterThanOrEqual(175);
        await pin.click();
        await expect(pin).toHaveAttribute('aria-pressed', 'true');
        await expect(pin).toHaveAccessibleName('Unpin VB-1');
        await expect(page.locator('[data-board-card-editor]')).toHaveCount(0);
        expect(writes[0]).toEqual({ pinned: true });
        await page.evaluate(() => window.app.boardController.refreshBoardSnapshot());
        await expect(pin).toHaveAttribute('aria-pressed', 'true');
        await page.screenshot({ path: testInfo.outputPath('pinned-card.png') });
        failSave = true;
        await pin.click();
        await expect(page.getByText('Pin save failed.', { exact: true })).toBeVisible();
        await expect(pin).toHaveAttribute('aria-pressed', 'true');
        await expect(pin).toBeEnabled();
        failSave = false;
        await pin.click();
        await expect(pin).toHaveAttribute('aria-pressed', 'false');
    });

    test(`Context tab shows every level broadest first and saves each with its own revision at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 950 });
        await openBoard(page);
        let saved = { message: '', revision: 0 };
        let appContext = { message: 'Applies everywhere', revision: 3 };
        let failSave = false;
        const writes = [];
        await page.route('**/api/v1/board/boards/brd_main/every-context', async route => {
            if (route.request().method() === 'PUT') {
                writes.push(route.request().postDataJSON());
                if (failSave) return route.fulfill({ status: 500, json: { error: 'Context save failed.' } });
                expect(writes.at(-1).expectedRevision).toBe(saved.revision);
                saved = { message: writes.at(-1).message, revision: saved.revision + 1 };
            }
            return route.fulfill({ json: saved });
        });
        await page.route('**/api/v1/board/app-context', async route => {
            if (route.request().method() === 'PUT') {
                const body = route.request().postDataJSON();
                expect(body.expectedRevision).toBe(appContext.revision);
                appContext = { message: body.message, revision: appContext.revision + 1 };
            }
            return route.fulfill({ json: appContext });
        });
        let cardContext = { revision: 0, context: { defaultMessage: 'Board-only instructions', typeOverrides: [] } };
        await page.route('**/api/v1/board/boards/brd_main/context', route => {
            if (route.request().method() === 'PUT') {
                const body = route.request().postDataJSON();
                expect(body.expectedRevision).toBe(cardContext.revision);
                cardContext = { revision: cardContext.revision + 1, context: body.context };
            }
            return route.fulfill({ json: cardContext });
        });
        const open = async () => {
            await page.getByRole('button', { name: 'Board settings', exact: true }).click();
            await page.getByRole('tab', { name: 'Context', exact: true }).click();
            await expect(page.getByRole('tab', { name: 'Context', exact: true })).toHaveAttribute('aria-selected', 'true');
            await expect(page.getByRole('tab', { name: 'Context', exact: true })).toHaveClass(/\bactive\b/);
            await expect(page.getByRole('tab', { name: 'General', exact: true })).toHaveAttribute('aria-selected', 'false');
        };
        await open();
        for (const name of ['App context', 'Every Context', 'Agent context']) {
            await expect(page.getByRole('tab', { name, exact: true })).toHaveCount(0);
        }
        const contextPanel = page.locator('#board-settings-context');
        // VIBE-229: one editor, broadest level first, each saying which launches it reaches.
        await expect(contextPanel.locator('[data-context-level] > summary, [data-context-level] > h6')).toHaveText(['App context', 'Board context', 'Card context', 'Card type context']);
        const disclosures = contextPanel.locator('details');
        await expect(disclosures).toHaveCount(8);
        await expect(contextPanel.locator('details[open] > summary')).toHaveText(['App context']);
        await expect(contextPanel.getByRole('button', { name: /Save/ })).toHaveCount(1);
        await page.screenshot({ path: testInfo.outputPath('context-default.png'), animations: 'disabled' });
        await contextPanel.locator('[data-context-level="board"] > summary').focus();
        await page.keyboard.press('Enter');
        await contextPanel.locator('[data-context-level="card"] > summary').click();
        await expect(contextPanel.locator('[data-context-level="app"] [data-context-reach]')).toContainText('launches with no board');
        await expect(contextPanel.locator('[data-context-level="board"] [data-context-reach]')).toContainText('currently selected board');
        const appMessage = page.getByLabel('App context', { exact: true });
        const message = page.getByLabel('Board context', { exact: true });
        const cardMessage = page.getByLabel('Card context', { exact: true });
        await expect(appMessage).toHaveValue('Applies everywhere');
        await expect(message).toHaveValue('');
        await expect(message).toHaveAttribute('maxlength', '4000');
        await expect(cardMessage).toHaveValue('Board-only instructions');
        await cardMessage.fill('Unsaved Card context draft');
        await appMessage.fill('Unsaved App draft');
        const text = 'Use project conventions.\n</textarea><img src=x onerror="window.__everyContextXss=1">';
        await message.fill(text);
        await page.getByRole('tab', { name: 'General', exact: true }).click();
        await page.getByRole('tab', { name: 'Context', exact: true }).click();
        await expect(message).toHaveValue(text);
        await page.getByRole('button', { name: 'Save context', exact: true }).click();
        await expect.poll(() => saved.message).toBe(text);
        expect(Object.keys(writes[0])).toEqual(['message', 'expectedRevision']);
        // One action saves changed levels, retaining every draft and its own revision.
        await expect(cardMessage).toHaveValue('Unsaved Card context draft');
        await expect(appMessage).toHaveValue('Unsaved App draft');
        await expect.poll(() => appContext.message).toBe('Unsaved App draft');
        await expect.poll(() => cardContext.context.defaultMessage).toBe('Unsaved Card context draft');
        await expect(page.getByRole('button', { name: 'Save context', exact: true })).toBeEnabled();
        await page.evaluate(() => window.app.closeModal());
        await open();
        await contextPanel.locator('[data-context-level="board"] > summary').click();
        await expect(message).toHaveValue(text);
        await expect(appMessage).toHaveValue('Unsaved App draft');
        expect(await page.evaluate(() => window.__everyContextXss)).toBeUndefined();
        const panel = page.locator('[data-board-board-editor]');
        expect(await panel.evaluate(el => el.scrollWidth <= el.clientWidth + 1)).toBe(true);
        await page.screenshot({ path: testInfo.outputPath('context-levels.png'), animations: 'disabled' });
        failSave = true;
        await message.fill('Preserve this draft after failure');
        await appMessage.fill('Saved despite Board failure');
        await page.getByRole('button', { name: 'Save context', exact: true }).click();
        await expect(page.getByText('Context save failed.', { exact: true })).toBeVisible();
        await expect(message).toHaveValue('Preserve this draft after failure');
        await expect.poll(() => appContext.message).toBe('Saved despite Board failure');
        const appRevision = appContext.revision;
        await expect(page.getByRole('button', { name: 'Save context', exact: true })).toBeEnabled();
        failSave = false;
        await message.fill('');
        await page.getByRole('button', { name: 'Save context', exact: true }).click();
        await expect.poll(() => saved.message).toBe('');
        expect(appContext.revision).toBe(appRevision);
        await page.evaluate(() => window.app.closeModal());
        await open();
        await expect(message).toHaveValue('');
    });
}

test('Card and Card type context persist default and per-type choices and preserve a conflicted draft', async ({ page }, testInfo) => {
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
    await page.getByRole('tab', { name: 'Context', exact: true }).click();
    await page.locator('[data-context-level="card"] > summary').click();
    await page.getByLabel('Card context', { exact: true }).fill('Read project conventions. <img src=x onerror="window.__contextXss=1">');
    await page.locator('[data-context-level="type"] summary').filter({ hasText: /^Bug$/ }).click();
    await page.getByLabel('Bug cards', { exact: true }).selectOption('append');
    await page.getByLabel('Bug card type context', { exact: true }).fill('Reproduce before changing code.');
    await page.getByRole('button', { name: 'Save context', exact: true }).click();
    await expect.poll(() => saved.revision).toBe(1);
    expect(saved.context.typeOverrides.find(item => item.type === 'bug')).toEqual({ type: 'bug', mode: 'append', message: 'Reproduce before changing code.' });
    await page.locator('#modal-container [data-action="close-modal"]').first().click();
    await page.getByRole('button', { name: 'Board settings', exact: true }).click();
    await page.getByRole('tab', { name: 'Context', exact: true }).click();
    await page.locator('[data-context-level="card"] > summary').click();
    await expect(page.getByLabel('Card context', { exact: true })).toHaveValue(saved.context.defaultMessage);
    await page.locator('[data-context-level="type"] summary').filter({ hasText: /^Bug$/ }).click();
    await expect(page.getByLabel('Bug cards', { exact: true })).toHaveValue('append');
    expect(await page.evaluate(() => window.__contextXss)).toBeUndefined();
    await page.screenshot({ path: testInfo.outputPath('board-context.png') });
    conflict = true;
    await page.getByLabel('Card context', { exact: true }).fill('Keep my draft');
    await page.getByRole('button', { name: 'Save context', exact: true }).click();
    await expect(page.getByText('Board context changed while you were editing.', { exact: true })).toBeVisible();
    await expect(page.getByLabel('Card context', { exact: true })).toHaveValue('Keep my draft');
});

test('Context save waits for loading and preserves edits made during an in-flight save', async ({ page }) => {
    await openBoard(page);
    await page.route('**/api/v1/board/boards/brd_main/context', route => route.fulfill({
        json: { revision: 0, context: { defaultMessage: '', typeOverrides: [] } }
    }));
    await page.route('**/api/v1/board/boards/brd_main/every-context', route => route.fulfill({
        json: { revision: 0, message: '' }
    }));
    let finishLoad;
    let finishSave;
    let saved = { message: 'Original', revision: 4 };
    const writes = [];
    await page.route('**/api/v1/board/app-context', async route => {
        if (route.request().method() === 'GET') {
            await new Promise(resolve => { finishLoad = resolve; });
        } else {
            const body = route.request().postDataJSON();
            writes.push(body);
            expect(body.expectedRevision).toBe(saved.revision);
            if (writes.length === 1) await new Promise(resolve => { finishSave = resolve; });
            saved = { message: body.message, revision: saved.revision + 1 };
        }
        return route.fulfill({ json: saved });
    });
    await page.getByRole('button', { name: 'Board settings', exact: true }).click();
    await page.getByRole('tab', { name: 'Context', exact: true }).click();
    const save = page.locator('[data-context-save]');
    await expect(save).toBeDisabled();
    await expect.poll(() => Boolean(finishLoad)).toBe(true);
    finishLoad();
    await expect(save).toBeEnabled();
    const message = page.getByLabel('App context', { exact: true });
    await message.fill('First draft');
    await save.click();
    await expect(save).toBeDisabled();
    await expect(save).toHaveText('Saving…');
    await expect.poll(() => Boolean(finishSave)).toBe(true);
    await message.fill('Newer draft');
    finishSave();
    await expect(save).toBeEnabled();
    await expect(message).toHaveValue('Newer draft');
    expect(saved.message).toBe('First draft');
    await save.click();
    await expect.poll(() => saved.message).toBe('Newer draft');
    await expect(save).toBeEnabled();
    await save.click();
    await expect(save).toBeEnabled();
    expect(writes).toEqual([
        { message: 'First draft', expectedRevision: 4 },
        { message: 'Newer draft', expectedRevision: 5 }
    ]);
});

test('Settings reuses the App-only context disclosure and single save action', async ({ page }) => {
    await openBoard(page);
    let saved = { message: 'Every launch', revision: 2 };
    await page.route('**/api/v1/board/app-context', route => {
        if (route.request().method() === 'PUT') {
            const body = route.request().postDataJSON();
            expect(body.expectedRevision).toBe(saved.revision);
            saved = { message: body.message, revision: saved.revision + 1 };
        }
        return route.fulfill({ json: saved });
    });
    await page.evaluate(() => window.app.navigate('settings'));
    await page.getByRole('tab', { name: 'Context', exact: true }).click();
    const editor = page.locator('[data-context-editor]');
    await expect(editor.locator('details')).toHaveCount(1);
    await expect(editor.locator('details[open] > summary')).toHaveText(['App context']);
    await expect(editor.getByRole('button')).toHaveCount(1);
    await editor.getByLabel('App context', { exact: true }).fill('All projects');
    await editor.getByRole('button', { name: 'Save context', exact: true }).click();
    await expect.poll(() => saved.message).toBe('All projects');
});

test('waiting badge follows server activity and skip preserves editor drafts', async ({ page }, testInfo) => {
    let waiting = true;
    await openBoard(page, { onCard: card => { card.hasWaitingAutomation = true; } });
    const state = await installLaunchFixture(page);
    state.activity = [{ cardLabel: 'VIBE-1', definitionName: 'Review', entry: {
        id: 'entry-waiting', cardId: 'card_test', definitionId: 'preset_review', current: true,
        processStatus: 'queued', gate: 'review', summary: 'Waiting for the preceding run.'
    } }];
    await page.route('**/api/v1/board/cards/activity', route => route.fulfill({ json: {
        cards: [{ id: 'card_test', activeSessionId: null, activeTabId: null, hasActiveAutomation: false, hasWaitingAutomation: waiting }]
    } }));
    await page.route('**/api/v1/board/cards/card_test/launches/entry-waiting/skip', async route => {
        state.skips++; waiting = false; state.activity[0].entry.skipRequested = true;
        return route.fulfill({ status: 204 });
    });
    await page.evaluate(() => window.app.boardController.refreshSessionActivity());
    await expect(page.locator('.board-automation-waiting')).toBeVisible();
    await page.screenshot({ path: testInfo.outputPath('waiting-card.png') });
    await page.locator('.board-automation-waiting').click();
    await page.locator('#board-card-title').fill('Keep my draft');
    await page.getByRole('button', { name: 'Stop and skip', exact: true }).click();
    await expect(page.locator('[data-board-automation-runs]')).toContainText('Skip requested');
    await expect(page.locator('#board-card-title')).toHaveValue('Keep my draft');
    expect(state.skips).toBe(1);
    await expect(page.locator('.board-automation-waiting')).toHaveCount(0);
    waiting = true;
    await page.evaluate(() => window.app.boardController.refreshSessionActivity());
    await expect(page.locator('.board-automation-waiting')).toHaveCount(1);
    await expect(page.locator('#board-card-title')).toHaveValue('Keep my draft');
});

// VIBE-145: the lane card's assignee chip assigns without opening the editor. The whole chip is
// the selector and never filters (a filter that looked like a selector made cards seem to vanish),
// and targets hidden from the launch picker are still assignable.
test('the assignee chip assigns from the card face, hidden targets included, and never filters', async ({ page }, testInfo) => {
    const requests = await openBoard(page, { extraPickerItems: [
        { key: 'base:grok', kind: 'base', group: 'Base CLIs', label: 'Grok', cli: 'grok', enabled: false, order: 3 }
    ] });
    const chip = page.locator('.board-card .board-assignee-chip');
    await expect(chip).toHaveText(/Unassigned/);
    await chip.click();
    const menu = page.locator('#board-assign-panel');
    await expect(menu).toBeVisible();
    await expect(page.locator('[data-board-card-editor]')).toHaveCount(0, { timeout: 500 });
    await expect(menu.locator('.board-assign-group-title')).toHaveText(['Built-in CLIs', 'Agents']);
    await expect(menu.locator('.board-assign-option-label')).toHaveText(['Codex', 'Claude', 'Grok', 'Card review (claude)']);
    await expect(menu.locator('.board-assign-unassign')).toHaveCount(0);
    await page.screenshot({ path: testInfo.outputPath('assign-menu.png') });

    // Typing narrows the list; Enter takes the first match.
    await menu.locator('[data-assign-search]').fill('card rev');
    await expect(menu.locator('[data-assign-value]')).toHaveText(['Card review (claude)']);
    await menu.locator('[data-assign-search]').press('Enter');
    await expect(menu).toHaveCount(0);
    const write = requests.find(item => item.method === 'PUT' && item.path === '/api/v1/board/cards/card_test');
    expect(write.body).toEqual({ assignee: 'preset:fixture_7:claude' });
    await expect(chip).toHaveText(/Card review/);
    await expect(chip).not.toHaveAttribute('data-assignee-id');
    const writes = () => requests.filter(item => item.method === 'PUT' && item.path === '/api/v1/board/cards/card_test');

    // Anywhere on the assigned chip, name or avatar, opens the menu; the board is never filtered.
    await chip.locator('.board-assignee-name').click();
    await expect(menu).toBeVisible();
    await expect(menu.locator('[aria-checked="true"]')).toHaveText(/Card review/);
    await expect(page.locator('[data-board-filter-assignee]')).toHaveValue('', { timeout: 500 });
    await menu.locator('[data-assign-value="base:grok"]').click();
    await expect(menu).toHaveCount(0);
    expect(writes().at(-1).body).toEqual({ assignee: 'base:grok',
        baseLlmOptions: { model: '', effort: '', mode: '', yolo: false, speed: '' } });
    await expect(chip).toHaveText(/Grok/);
    await expect(page.locator('.board-card')).toHaveCount(1);
    await chip.hover();
    await page.screenshot({ path: testInfo.outputPath('assigned-chip.png') });
    await expect(page.locator('[data-board-filter-assignee]')).toHaveValue('');

    await chip.locator('.board-avatar').click();
    await expect(menu).toBeVisible();
    await menu.locator('.board-assign-unassign').click();
    await expect(menu).toHaveCount(0);
    expect(writes().at(-1).body).toEqual({ assignee: '' });
    await expect(page.locator('.board-card .board-assignee-chip.is-empty')).toHaveText(/Unassigned/);
    await expect(page.locator('[data-board-filter-assignee]')).toHaveValue('');

    // The editor's Assignee picker offers the hidden target too.
    await page.getByText('Description images', { exact: true }).click();
    await expect.poll(() => page.evaluate(() => Object.keys(document.querySelector('#board-card-assignee')?.tomselect?.options || {})))
        .toContain('base:grok');
});

test('the assignee menu preserves the card on a defaults failure and waits on retry', async ({ page }) => {
    const requests = await openBoard(page, { assignee: 'base:claude' });
    let fail = true;
    let release;
    const defaults = { model: 'gpt-6-astra', effort: 'high', mode: '', yolo: true, speed: 'ultrafast' };
    await page.route('**/api/v1/llm-picker/defaults/codex', async route => {
        if (fail) return route.fulfill({ status: 503, json: { error: 'Defaults unavailable' } });
        await new Promise(resolve => { release = resolve; });
        return route.fulfill({ json: defaults });
    });
    const chip = page.locator('.board-card .board-assignee-chip');
    await chip.click();
    const menu = page.locator('#board-assign-panel');
    const codex = menu.locator('[data-assign-value="base:codex"]');
    await codex.click();
    await expect(page.getByText('Defaults unavailable', { exact: true })).toBeVisible();
    await expect(menu).toBeVisible();
    await expect(codex).toBeEnabled();
    await expect(chip).toHaveText(/Claude/);
    expect(requests.filter(item => item.method === 'PUT')).toEqual([]);
    fail = false;
    await codex.click();
    await expect.poll(() => Boolean(release)).toBe(true);
    await expect(codex).toBeDisabled();
    expect(requests.filter(item => item.method === 'PUT')).toEqual([]);
    release();
    await expect(menu).toHaveCount(0);
    await expect(chip).toHaveText(/Codex/);
    expect(requests.find(item => item.method === 'PUT').body).toEqual({ assignee: 'base:codex', baseLlmOptions: defaults });
});

test('the assignee menu closes on Escape and outside clicks without saving', async ({ page }) => {
    const requests = await openBoard(page, { assignee: 'base:codex' });
    await page.locator('.board-card .board-assignee-chip').click();
    const menu = page.locator('#board-assign-panel');
    await expect(menu).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(menu).toHaveCount(0);
    await expect(page.locator('[data-board-card-editor]')).toHaveCount(0);
    await page.locator('.board-card .board-assignee-chip').click();
    await expect(menu).toBeVisible();
    await page.locator('.board-lane-head').first().click();
    await expect(menu).toHaveCount(0);
    expect(requests.filter(item => item.method === 'PUT')).toEqual([]);
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
    await page.getByRole('button', { name: 'Start discussion', exact: true }).click();
    await expect.poll(() => page.evaluate(() => window.__chatTabs)).toEqual(['board_background']);
    const writes = requests.filter(item => item.method === 'PUT' || item.path.endsWith('/launch'));
    expect(writes[0].body.title).toBe('Discuss this card');
    expect(writes[1].body).toEqual({ selection: 'base:codex', intent: 'chat' });
    await expect(page.locator('[data-board-card-editor]')).toHaveCount(0);
});

for (const width of [1440, 1024, 768, 390, 375, 320]) {
    test(`previous work and an initial question remain usable alongside work at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 950 });
        const discussionTarget = width === 375 ? 'preset:fixture_17:codex' : 'base:codex';
        const requests = await openBoard(page, { active: true, assignee: discussionTarget, extraPickerItems: [
            { key: 'preset:fixture_17:codex', kind: 'agent', group: 'AI Agents',
                label: 'Card history and implementation review assistant (codex)',
                cli: 'codex', environmentId: 17, enabled: true, order: 3 }
        ], onCard: card => {
            card.previousWork = { outcome: 'Description overflow fixed', decisions: 'Keep a single scroll region', validation: 'Browser regression passed', outstanding: 'None',
                author: { label: 'Codex', sessionId: 'earlier-session' }, createdUtc: '2026-10-02T00:00:00Z',
                files: [{ path: 'VibeRails/wwwroot/js/modules/board-controller.js', reason: 'Open the card editor here', role: 'implementation', symbol: 'openCardEditor', commit: 'cf302fe', status: 'historical reference; verify current code' }] };
        } });
        await page.evaluate(() => {
            window.__discussionTabs = [];
            window.app.terminalController.adoptLaunchedTab = async id => { window.__discussionTabs.push(id); return true; };
        });
        await page.getByText('Description images', { exact: true }).click();
        if (width === 375) await page.addStyleTag({ content: '.board-discussion { zoom: 1.25; }' });
        await page.locator('[data-board-previous-work] summary').click();
        await expect(page.locator('[data-board-previous-work]')).toContainText('Open the card editor here');
        const question = page.getByLabel('Initial question (optional)');
        await question.fill('Why did we choose one scroll region?');
        await expect(question).toHaveAccessibleDescription('Leave blank for an open discussion.');
        const discussion = page.getByRole('region', { name: 'Discuss this card' });
        await expect(discussion.locator('[data-board-chat-agent]')).toHaveValue(discussionTarget);
        const picker = discussion.locator('.ts-control');
        const start = discussion.getByRole('button', { name: 'Start discussion', exact: true });
        const questionBounds = await question.boundingBox();
        const pickerBounds = await picker.boundingBox();
        const startBounds = await start.boundingBox();
        expect(pickerBounds.y).toBeGreaterThan(questionBounds.y + questionBounds.height);
        expect(startBounds.y).toBeGreaterThanOrEqual(pickerBounds.y + pickerBounds.height);
        expect(startBounds.width).toBeCloseTo(questionBounds.width, 0);
        expect(startBounds.height).toBeGreaterThanOrEqual(width <= 520 ? 44 : 40);
        for (const bounds of [questionBounds, pickerBounds, startBounds]) {
            expect(bounds.x).toBeGreaterThanOrEqual(0);
            expect(bounds.x + bounds.width).toBeLessThanOrEqual(width);
        }
        await discussion.scrollIntoViewIfNeeded();
        await page.screenshot({ path: testInfo.outputPath(`recall-${width}.png`) });
        await question.focus();
        await page.keyboard.press('Tab');
        await expect(page.locator('.ts-dropdown:visible').getByRole('textbox', { name: 'Search…', exact: true })).toBeFocused();
        await page.keyboard.press('Enter');
        await expect(discussion.locator('[data-board-chat-agent]')).toHaveValue(discussionTarget);
        await page.keyboard.press('Tab');
        await expect(start).toBeFocused();
        await start.click();
        await expect.poll(() => page.evaluate(() => window.__discussionTabs)).toEqual(['board_background']);
        const launch = requests.find(item => item.path.endsWith('/launch'));
        expect(launch.body).toEqual({ selection: discussionTarget, intent: 'chat', question: 'Why did we choose one scroll region?' });
    });
}

for (const [assignee, selection] of [
    ['base:codex', 'base:claude'],
    ['base:codex', 'preset:fixture_7:claude'],
    [null, 'preset:fixture_7:claude']
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
        await page.getByRole('button', { name: 'Start discussion', exact: true }).click();
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
    await page.getByRole('button', { name: 'Create card', exact: true }).click();
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
    await page.getByRole('button', { name: 'Create card', exact: true }).click();
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

// The assignee chip and the work action share ONE row in every card: same vertical centre, chip
// flush left, action flush right, at least 8px between them, nothing spilling out of the tile.
// With `fullName: false` the lane is too narrow for both, so the chip's name may ellipsize; the
// action label never does.
async function expectAlignedCardControls(tile, { fullName = true } = {}) {
    const chip = tile.locator('.board-assignee-chip');
    const action = tile.locator('.board-card-start-work');
    const [chipBounds, actionBounds, footerBounds] = await Promise.all([
        chip.boundingBox(), action.boundingBox(), tile.locator('.board-card-foot').boundingBox()
    ]);
    const chipMiddle = chipBounds.y + chipBounds.height / 2;
    const actionMiddle = actionBounds.y + actionBounds.height / 2;
    expect(Math.abs(chipMiddle - actionMiddle)).toBeLessThan(1);
    expect(Math.abs(chipBounds.x - footerBounds.x)).toBeLessThan(1);
    expect(Math.abs(actionBounds.x + actionBounds.width - footerBounds.x - footerBounds.width)).toBeLessThan(1);
    expect(actionBounds.x - chipBounds.x - chipBounds.width).toBeGreaterThanOrEqual(7.9);
    expect(await action.locator('[data-board-start-work-label]').evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
    if (fullName)
        expect(await chip.locator('.board-assignee-name').evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
    expect(await tile.evaluate(element => element.scrollWidth <= element.clientWidth)).toBe(true);
}

for (const width of [1920, 1440, 390]) {
    test(`the card face keeps the assignee beside Start work at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 900 });
        // Four lanes: roomy at 1920px, compact at 1440px and at the lane floor on phones.
        const columns = ['Backlog', 'Build', 'Review', 'Done'].map((name, position) => ({
            id: position ? `col_${position}` : 'col_ready', name, position, boardId: 'brd_main', color: '#64748b'
        }));
        await openBoard(page, { assignee: 'preset:fixture_7:claude', columns, onCard: card => {
            card.jiraIssueKey = 'SCRUM-5';
            card.flagged = true;
        } });
        const tile = page.locator('.board-card');
        const chip = tile.locator('.board-assignee-chip');
        const start = tile.getByRole('button', { name: 'Start work on VB-1', exact: true });
        await expect(chip).toContainText('Card review (claude)');
        await page.screenshot({ path: testInfo.outputPath(`board-face-long-assignee-${width}.png`) });
        await expect(start).toBeVisible();
        // Four lanes at 1440px and the 288px floor at 390px are both too narrow for this long
        // environment name beside Start work, so the chip ellipsizes it there; 1920px has room.
        await expectAlignedCardControls(tile, { fullName: width >= 1920 });
    });

    test(`Start work on the card face launches saved work and fits a narrow lane at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 900 });
        let saved;
        const requests = await openBoard(page, { assignee: 'base:codex',
            columns: ['Ready', 'Build', 'Review', 'PR', 'Ship', 'Done'].map((name, position) => ({
                id: position ? `col_${position}` : 'col_ready', name, position, boardId: 'brd_main', color: '#64748b'
            })),
            onCard: card => {
                saved = card;
                card.title = 'Linux forkpty child runs managed code: JIT test host segfaults / deadlocks';
                card.type = 'bug';
                card.baseLlmOptions = { model: 'gpt-6-astra', effort: 'high', speed: 'ultrafast', yolo: false };
            }
        });
        await page.evaluate(() => {
            window.__faceTabs = [];
            window.__faceMetadata = [];
            window.app.terminalController.rememberTabLaunch = (id, metadata) => window.__faceMetadata.push({ id, metadata });
            window.app.terminalController.adoptLaunchedTab = async id => { window.__faceTabs.push(id); return true; };
        });
        const tile = page.locator('.board-card');
        const start = tile.getByRole('button', { name: 'Start work on VB-1', exact: true });
        await expect(start).toBeVisible();
        await expectAlignedCardControls(tile);
        await page.screenshot({ path: testInfo.outputPath(`board-face-start-work-${width}.png`) });

        let releaseLaunch;
        const launching = new Promise(resolve => { releaseLaunch = resolve; });
        const launches = [];
        await page.route('**/api/v1/board/cards/card_test/launch', async route => {
            launches.push(route.request().postDataJSON());
            await launching;
            saved.activeSessionId = 'session_work';
            saved.activeTabId = 'tab_work';
            saved.sessions.push({ id: 'session_work', tabId: 'tab_work', active: true, selection: 'base:codex' });
            await route.fulfill({ json: { tabId: 'tab_work', selection: 'base:codex' } });
        });
        await start.focus();
        await page.keyboard.press('Enter');
        await expect(start).toBeDisabled();
        await page.evaluate(() => window.app.boardController.renderLanes());
        await expect(start).toBeDisabled();
        await expect(page.locator('[data-board-card-editor]')).toHaveCount(0);
        releaseLaunch();
        const go = tile.getByRole('button', { name: 'Go to agent on VB-1', exact: true });
        await expect(go).toBeEnabled();
        await expect(page.locator('#app-content [data-view="board"]')).toBeVisible();
        expect(launches).toEqual([{ selection: 'base:codex', intent: 'work' }]);
        expect(requests.filter(request => request.method === 'PUT')).toHaveLength(0);
        expect(await page.evaluate(() => window.__faceTabs)).toEqual([]);
        expect(await page.evaluate(() => window.__faceMetadata[0].metadata.taskKey)).toBe('board-card:card_test');
        expect(saved.baseLlmOptions.speed).toBe('ultrafast');
        await go.click();
        await expect.poll(() => page.evaluate(() => window.__faceTabs)).toEqual(['tab_work']);
        expect(launches).toHaveLength(1);
    });
}

for (const { assignee, active, action } of [
    { assignee: 'base:claude', active: true, action: 'Go to agent on VB-1' },
    { assignee: null, active: false, action: 'Start work on VB-1' }
]) {
    test(`${action} sits beside a short assignee on one row`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width: 1440, height: 900 });
        const columns = ['Backlog', 'Ready'].map((name, position) => ({
            id: position ? `col_${position}` : 'col_ready', name, position, boardId: 'brd_main', color: '#64748b'
        }));
        await openBoard(page, { assignee, active, columns, onCard: card => {
            if (active) card.activeTabId = 'agent_tab';
        } });
        const tile = page.locator('.board-card');
        const chip = tile.locator('.board-assignee-chip');
        const start = tile.getByRole('button', { name: action, exact: true });
        await expect(chip).toContainText(assignee ? 'Claude' : 'Unassigned');
        await expect(start).toBeVisible();
        await expectAlignedCardControls(tile);
        await page.screenshot({ path: testInfo.outputPath(`board-face-lineup-${active ? 'go-to-agent' : 'start-work'}.png`) });
    });
}

const compactCardLayouts = [16, 20].flatMap(rootSize => [1440, 1024, 768, 390, 375, 320].flatMap(width =>
    [false, true].map(active => ({ rootSize, width, active }))));
for (const { rootSize, width, active } of compactCardLayouts) {
    test(`compact card keeps ${active ? 'Go to agent beside Claude' : 'Start work beside Unassigned'} at ${rootSize}px root size and ${width}px viewport`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 1200 });
        const columns = ['Ready', 'Build', 'Review', 'PR', 'Ship', 'Done'].map((name, position) => ({
            id: position ? `col_${position}` : 'col_ready', name, position, boardId: 'brd_main', color: '#64748b'
        }));
        await openBoard(page, { columns, active, assignee: active ? 'base:claude' : null, onCard: card => {
            card.title = 'Preserve full attachment and commit content; retire automatic full snapshots';
        } });
        await page.addStyleTag({ content: `html { font-size: ${rootSize}px; }` });
        const tile = page.locator('.board-card');
        const start = tile.locator('.board-card-start-work');
        await expect(start).toBeInViewport();
        const laneWidth = await tile.evaluate(element => element.closest('.board-lane').getBoundingClientRect().width);
        expect(laneWidth).toBeCloseTo(Math.max(288, 18 * rootSize), 0);
        await tile.screenshot({ path: testInfo.outputPath(`compact-card-${rootSize}-${width}-${active ? 'active' : 'unassigned'}.png`) });
        await expectAlignedCardControls(tile);
    });
}

test('Start work on an unassigned card face opens assignment without launching', async ({ page }) => {
    const requests = await openBoard(page);
    await page.locator('.board-card').getByRole('button', { name: 'Start work on VB-1', exact: true }).click();
    const editor = page.locator('[data-board-card-editor]');
    await expect(editor).toBeVisible();
    await expect(editor.locator('[data-board-start-work]')).toBeEnabled();
    await expect(editor.locator('#board-card-assignee')).toHaveValue('');
    expect(requests.filter(request => request.method === 'PUT' || request.path.endsWith('/launch'))).toHaveLength(0);
});

for (const origin of ['launch', 'chat_work']) {
test(`a running ${origin} agent exposes Go to agent while keeping Save and the session available`, async ({ page }) => {
    const requests = await openBoard(page, { active: true, onCard: card => { card.sessions[0].origin = origin; } });
    await page.evaluate(() => {
        window.__agentTabs = [];
        window.app.terminalController.adoptLaunchedTab = async id => { window.__agentTabs.push(id); return true; };
    });
    await page.getByText('Description images', { exact: true }).click();
    await expect(page.getByRole('button', { name: 'Go to agent', exact: true })).toBeEnabled();
    await expect(page.getByRole('button', { name: 'Start discussion', exact: true })).toBeEnabled();
    await expect(page.locator('[data-board-save-card]')).toBeEnabled();
    await expect(page.locator('[data-board-open-session="session_test"]')).toBeEnabled();
    await page.getByRole('button', { name: 'Go to agent', exact: true }).click();
    await expect(page.locator('[data-board-card-editor]')).toHaveCount(0);
    expect(await page.evaluate(() => window.__agentTabs)).toEqual(['agent_tab']);
    expect(requests.filter(request => request.method === 'PUT' || request.path.endsWith('/launch'))).toHaveLength(0);
});
}

for (const width of [1440, 390]) {
    test(`Agent workflow keeps manager and worker choices separate at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 950 });
        const requests = await openBoard(page, { assignee: 'base:claude' });
        await page.getByText('Description images', { exact: true }).click();
        const editor = page.locator('[data-board-card-editor]');
        await expect(editor.locator('[data-board-manager-fields]')).toBeHidden();
        await editor.locator('#board-card-workflow').selectOption('agent');
        await expect(editor.locator('[data-board-manager-fields]')).toBeVisible();
        await expect(editor.locator('[data-board-assignee-label]')).toHaveText('Worker');
        await expect(editor.locator('[data-board-start-work]')).toHaveText('Start Agent workflow');
        await editor.locator('[data-board-save-card]').click();
        await expect(page.getByText('Choose a manager agent for the Agent workflow.', { exact: true })).toBeVisible();
        expect(requests.filter(request => request.method === 'PUT')).toHaveLength(0);
        await expect.poll(() => editor.locator('#board-card-manager').evaluate(select => Boolean(select.tomselect))).toBe(true);
        await editor.locator('#board-card-manager').evaluate(select => select.tomselect.setValue('base:codex'));
        const manager = editor.locator('[data-board-manager-launch-options]');
        const worker = editor.locator('[data-board-worker-launch-options]');
        await manager.locator('[data-board-launch-model]').selectOption('gpt-6-astra');
        await manager.locator('[data-board-launch-speed]').selectOption('ultrafast');
        await manager.locator('[data-board-launch-effort]').selectOption('high');
        await worker.locator('[data-board-launch-effort]').selectOption('low');
        await worker.locator('[data-board-launch-yolo]').check();
        await expect(manager.locator('[data-board-launch-yolo]')).not.toBeChecked();
        await editor.locator('#board-card-workflow').selectOption('manual');
        await expect(manager).toBeHidden();
        await editor.locator('#board-card-workflow').selectOption('agent');
        await expect(manager.locator('[data-board-launch-speed]')).toHaveValue('ultrafast');
        await manager.scrollIntoViewIfNeeded();
        await page.screenshot({ path: testInfo.outputPath(`board-agent-workflow-${width}.png`) });
        await editor.locator('[data-board-start-work]').click();
        await expect(editor).toHaveCount(0);
        await expect(page.locator('#app-content [data-view="board"]')).toBeVisible();
        const saved = requests.find(request => request.method === 'PUT').body;
        expect(saved.workflowMode).toBe('agent');
        expect(saved.managerSelection).toBe('base:codex');
        expect(saved.managerBaseLlmOptions).toEqual({ model: 'gpt-6-astra', effort: 'high', mode: '', yolo: false, speed: 'ultrafast' });
        expect(saved.baseLlmOptions.effort).toBe('low');
        expect(saved.baseLlmOptions.yolo).toBe(true);
        expect(saved).not.toHaveProperty('assignee');
        expect(requests.find(request => request.path.endsWith('/launch')).body).toEqual({ selection: 'base:codex', intent: 'manager' });
        await expect(page.locator('.board-workflow-chip')).toHaveText('Agent');
        await expect(page.locator('.board-manager-chip')).toContainText('Manager · Codex');
        await page.getByText('Description images', { exact: true }).click();
        await expect(page.locator('#board-card-workflow')).toHaveValue('agent');
        await expect(page.locator('#board-card-manager')).toHaveValue('base:codex');
        await expect(page.locator('#board-card-assignee')).toHaveValue('base:claude');
        await expect(page.locator('[data-board-manager-launch-options] [data-board-launch-speed]')).toHaveValue('ultrafast');
        await expect(page.locator('[data-board-worker-launch-options] [data-board-launch-yolo]')).toBeChecked();
    });
}

for (const width of [1440, 390]) {
    test(`LLM defaults restore model, speed, effort and YOLO across selectors at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 950 });
        const requests = await openBoard(page, { assignee: 'base:codex' });
        const defaults = {};
        const writes = [];
        await page.route('**/api/v1/llm-picker/defaults/*', route => {
            const cli = new URL(route.request().url()).pathname.split('/').at(-1);
            if (route.request().method() === 'PUT') {
                defaults[cli] = route.request().postDataJSON();
                writes.push(cli);
            }
            return route.fulfill({ json: defaults[cli] || {} });
        });
        await page.getByText('Description images', { exact: true }).click();
        const editor = page.locator('[data-board-card-editor]');
        const worker = editor.locator('[data-board-worker-launch-options]');
        await worker.getByRole('combobox', { name: 'Model', exact: true }).selectOption('gpt-6-astra');
        await worker.getByRole('combobox', { name: 'Speed', exact: true }).selectOption('ultrafast');
        await worker.getByRole('combobox', { name: 'Effort', exact: true }).selectOption('xhigh');
        await worker.getByLabel('YOLO mode', { exact: true }).check();
        const save = worker.getByRole('button', { name: 'Make Default For LLM', exact: true });
        await expect(save).toHaveClass(/\bbtn-link\b/);
        await expect(save.locator('svg[aria-hidden="true"]')).toHaveCount(1);
        await save.click();
        await expect.poll(() => writes.length).toBe(1);
        expect(defaults.codex).toEqual({ model: 'gpt-6-astra', effort: 'xhigh', mode: '', yolo: true, speed: 'ultrafast' });
        expect(requests.filter(request => request.method === 'PUT')).toHaveLength(0);
        await save.scrollIntoViewIfNeeded();
        await page.screenshot({ path: testInfo.outputPath('llm-default-action.png'), animations: 'disabled' });
        const choose = selection => editor.locator('#board-card-assignee').evaluate((select, value) => select.tomselect.setValue(value), selection);
        await choose('base:claude');
        await worker.getByRole('combobox', { name: 'Start mode', exact: true }).selectOption('plan');
        await worker.getByRole('button', { name: 'Make Default For LLM', exact: true }).click();
        await expect.poll(() => defaults.claude?.mode).toBe('plan');
        await choose('base:codex');
        await expect(worker.getByRole('combobox', { name: 'Speed', exact: true })).toHaveValue('ultrafast');
        await expect(worker.getByRole('combobox', { name: 'Effort', exact: true })).toHaveValue('xhigh');
        await expect(worker.getByLabel('YOLO mode', { exact: true })).toBeChecked();
        await editor.locator('#board-card-workflow').selectOption('agent');
        await expect.poll(() => editor.locator('#board-card-manager').evaluate(select => Boolean(select.tomselect))).toBe(true);
        await editor.locator('#board-card-manager').evaluate(select => select.tomselect.setValue('base:codex'));
        const manager = editor.locator('[data-board-manager-launch-options]');
        await expect(manager.getByRole('combobox', { name: 'Speed', exact: true })).toHaveValue('ultrafast');
        await expect(manager.getByLabel('YOLO mode', { exact: true })).toBeChecked();
        await editor.locator('#board-card-workflow').selectOption('manual');
        await worker.getByRole('combobox', { name: 'Effort', exact: true }).selectOption('low');
        await editor.locator('[data-board-save-card]').click();
        await expect(editor).toHaveCount(0);
        await page.getByText('Description images', { exact: true }).click();
        await expect(worker.getByRole('combobox', { name: 'Effort', exact: true })).toHaveValue('low');
        expect(defaults.codex.effort).toBe('xhigh');

        // The Board's card-face picker must apply the same saved defaults as the editor.
        await editor.locator('[data-board-save-card]').click();
        await expect(editor).toHaveCount(0);
        const chip = page.locator('.board-card .board-assignee-chip');
        const menu = page.locator('#board-assign-panel');
        for (const cli of ['claude', 'codex']) {
            await chip.click();
            await menu.locator(`[data-assign-value="base:${cli}"]`).click();
            await expect(menu).toHaveCount(0);
            const patch = requests.filter(request => request.method === 'PUT' && request.path.endsWith('/card_test')).at(-1).body;
            expect(patch).toEqual({ assignee: `base:${cli}`, baseLlmOptions: defaults[cli] });
        }
        await page.getByText('Description images', { exact: true }).click();
        await expect(worker.getByRole('combobox', { name: 'Model', exact: true })).toHaveValue('gpt-6-astra');
        await expect(worker.getByRole('combobox', { name: 'Speed', exact: true })).toHaveValue('ultrafast');
        await expect(worker.getByRole('combobox', { name: 'Effort', exact: true })).toHaveValue('xhigh');
        await expect(worker.getByLabel('YOLO mode', { exact: true })).toBeChecked();
    });
}

test('a late LLM default cannot overwrite a draft or a different selection', async ({ page }) => {
    await openBoard(page, { assignee: 'base:codex' });
    let release;
    let replies = 0;
    await page.route('**/api/v1/llm-picker/defaults/codex', async route => {
        await new Promise(resolve => { release = resolve; });
        await route.fulfill({ json: { model: 'gpt-6-astra', effort: 'xhigh', speed: 'ultrafast', yolo: true } });
        replies++;
    });
    await page.getByText('Description images', { exact: true }).click();
    const editor = page.locator('[data-board-card-editor]');
    const worker = editor.locator('[data-board-worker-launch-options]');
    await editor.locator('#board-card-assignee').evaluate(select => select.tomselect.setValue('base:claude'));
    await editor.locator('#board-card-assignee').evaluate(select => select.tomselect.setValue('base:codex'));
    await expect.poll(() => Boolean(release)).toBe(true);
    await worker.getByRole('combobox', { name: 'Model', exact: true }).selectOption('gpt-6.1-terra');
    release();
    await expect.poll(() => replies).toBe(1);
    await expect(worker.getByRole('combobox', { name: 'Model', exact: true })).toHaveValue('gpt-6.1-terra');
    await expect(worker.getByLabel('YOLO mode', { exact: true })).not.toBeChecked();
    release = null;
    await editor.locator('#board-card-assignee').evaluate(select => select.tomselect.setValue('base:claude'));
    await editor.locator('#board-card-assignee').evaluate(select => select.tomselect.setValue('base:codex'));
    await expect.poll(() => Boolean(release)).toBe(true);
    await editor.locator('#board-card-assignee').evaluate(select => select.tomselect.setValue('base:claude'));
    release();
    await expect.poll(() => replies).toBe(2);
    await expect(worker.getByRole('combobox', { name: 'Start mode', exact: true })).toBeVisible();
    await expect(worker.getByRole('combobox', { name: 'Speed', exact: true })).toHaveCount(0);
});

test('Start work waits for saved LLM defaults before saving or launching', async ({ page }) => {
    const requests = await openBoard(page, { assignee: 'base:codex' });
    let release;
    await page.route('**/api/v1/llm-picker/defaults/codex', async route => {
        await new Promise(resolve => { release = resolve; });
        await route.fulfill({ json: { model: 'gpt-6-astra', effort: 'high', speed: 'ultrafast', yolo: true } });
    });
    await page.getByText('Description images', { exact: true }).click();
    await page.locator('#board-card-assignee').evaluate(select => select.tomselect.setValue('base:claude'));
    await page.locator('#board-card-assignee').evaluate(select => select.tomselect.setValue('base:codex'));
    await expect.poll(() => Boolean(release)).toBe(true);
    await page.locator('[data-board-start-work]').click();
    await page.locator('[data-board-start-work]').click();
    expect(requests.filter(request => request.method === 'PUT' || request.path.endsWith('/launch'))).toHaveLength(0);
    release();
    await expect(page.locator('[data-board-card-editor]')).toHaveCount(0);
    expect(requests.find(request => request.method === 'PUT').body.baseLlmOptions)
        .toEqual({ model: 'gpt-6-astra', effort: 'high', speed: 'ultrafast', mode: '', yolo: true });
    expect(requests.filter(request => request.path.endsWith('/launch'))).toHaveLength(1);
});

test('saved empty worker and manager options stay YOLO-off after normalization to null', async ({ page }) => {
    let card;
    const requests = await openBoard(page, { assignee: 'base:codex', onCard: value => {
        card = value;
        Object.assign(card, { workflowMode: 'agent', managerSelection: 'base:codex',
            baseLlmOptions: { yolo: true }, managerBaseLlmOptions: { yolo: true } });
    } });
    let defaultReads = 0;
    await page.route('**/api/v1/llm-picker/defaults/*', route => {
        defaultReads++;
        return route.fulfill({ json: { yolo: true } });
    });
    // Mirror the production BaseLlmOptionsBuilder normalization, pinned by BoardServiceTests.
    await page.route('**/api/v1/board/cards/card_test', route => {
        if (route.request().method() === 'PUT') {
            Object.assign(card, route.request().postDataJSON());
            for (const key of ['baseLlmOptions', 'managerBaseLlmOptions']) {
                const options = card[key];
                if (options && !options.yolo && !options.model && !options.effort && !options.mode && !options.speed) card[key] = null;
            }
        }
        return route.fulfill({ json: card });
    });
    await page.getByText('Description images', { exact: true }).click();
    const workerYolo = page.locator('[data-board-worker-launch-options] [data-board-launch-yolo]');
    const managerYolo = page.locator('[data-board-manager-launch-options] [data-board-launch-yolo]');
    await workerYolo.uncheck();
    await managerYolo.uncheck();
    await page.locator('[data-board-save-card]').click();
    await expect(page.locator('[data-board-card-editor]')).toHaveCount(0);
    expect(card.baseLlmOptions).toBeNull();
    expect(card.managerBaseLlmOptions).toBeNull();
    await page.getByText('Description images', { exact: true }).click();
    await expect(workerYolo).not.toBeChecked();
    await expect(managerYolo).not.toBeChecked();
    await page.locator('[data-board-start-work]').click();
    await expect(page.locator('[data-board-card-editor]')).toHaveCount(0);
    expect(defaultReads).toBe(0);
    expect(card.baseLlmOptions).toBeNull();
    expect(card.managerBaseLlmOptions).toBeNull();
    expect(requests.filter(request => request.path.endsWith('/launch'))).toHaveLength(1);
});

test('selecting an LLM restores its escaped custom default model', async ({ page }) => {
    await openBoard(page, { assignee: 'base:codex' });
    await page.route('**/api/v1/llm-picker/defaults/claude', route => route.fulfill({
        json: { model: 'team/custom-deployment', mode: 'plan', yolo: false }
    }));
    await page.getByText('Description images', { exact: true }).click();
    await page.locator('#board-card-assignee').evaluate(select => select.tomselect.setValue('base:claude'));
    const model = page.locator('[data-board-worker-launch-options] [data-board-launch-model]');
    await expect(model).toHaveValue('team/custom-deployment');
    await expect(model.locator('option:checked')).toHaveText('team/custom-deployment (custom)');
    await expect(page.locator('[data-board-worker-launch-options] [data-board-launch-mode]')).toHaveValue('plan');
});

test('Start work preserves the board and stores base launch options including YOLO', async ({ page }) => {
    const requests = await openBoard(page, { assignee: 'base:codex' });
    await page.getByText('Description images', { exact: true }).click();
    await expect(page.locator('.board-card-modal-dialog .modal-title')).toHaveText('VB-1 · Description images');
    await page.locator('[data-board-launch-model]').selectOption('gpt-6.1-sol');
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
        .toEqual({ model: 'gpt-6.1-sol', effort: 'high', mode: '', yolo: true, speed: 'ultrafast' });
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
        expect(await model.locator('option').evaluateAll(options => options.map(option => option.value)))
            .toEqual(['', 'gpt-6-astra', 'gpt-6.1-sol', 'gpt-6.1-terra', 'gpt-6-sol', 'gpt-6-luna', 'gpt-5.6-sol', 'gpt-5.6-terra', 'gpt-5.6-luna']);
        await model.selectOption('gpt-6.1-sol');
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
        await model.selectOption('gpt-6-astra');
        await expect(speed).toHaveValue('ultrafast');
        await expect(speed.locator('option[value="ultrafast"]')).toBeEnabled();
        await page.locator('[data-board-launch-effort]').selectOption('ultra');
        await model.selectOption('gpt-6.1-terra');
        await expect(speed).toHaveValue('');
        await expect(speed.locator('option[value="ultrafast"]')).toBeDisabled();
        await expect(page.locator('[data-board-launch-effort]')).toHaveValue('ultra');
        await expect(page.locator('[data-board-launch-effort] option[value="ultra"]')).toBeEnabled();
        await speed.selectOption('fast');
        await page.locator('[data-board-save-card]').click();
        await expect(page.locator('[data-board-card-editor]')).toHaveCount(0);
        expect(requests.filter(request => request.method === 'PUT' && request.path.endsWith('/card_test')).at(-1).body.baseLlmOptions)
            .toMatchObject({ model: 'gpt-6.1-terra', effort: 'ultra', speed: 'fast' });
        await page.getByText('Description images', { exact: true }).click();
        await expect(model).toHaveValue('gpt-6.1-terra');
        await expect(speed).toHaveValue('fast');
        await expect(page.locator('[data-board-launch-effort]')).toHaveValue('ultra');
        await model.selectOption('gpt-6-astra');
        await speed.selectOption('ultrafast');
        await model.selectOption('gpt-6-luna');
        await expect(speed).toHaveValue('');
        await expect(speed.locator('option[value="fast"]')).toBeEnabled();
        await expect(speed.locator('option[value="ultrafast"]')).toBeDisabled();
        await expect(page.locator('[data-board-launch-effort]')).toHaveValue('max');
        await expect(page.locator('[data-board-launch-effort] option[value="ultra"]')).toBeDisabled();
        await speed.selectOption('fast');
        await page.locator('[data-board-save-card]').click();
        await expect(page.locator('[data-board-card-editor]')).toHaveCount(0);
        expect(requests.filter(request => request.method === 'PUT' && request.path.endsWith('/card_test')).at(-1).body.baseLlmOptions)
            .toMatchObject({ model: 'gpt-6-luna', effort: 'max', speed: 'fast' });
        await page.getByText('Description images', { exact: true }).click();
        await expect(model).toHaveValue('gpt-6-luna');
        await expect(speed).toHaveValue('fast');
        await expect(page.locator('[data-board-launch-effort]')).toHaveValue('max');
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
    await expect(dropdown.locator('[data-value="preset:fixture_7:claude"]')).toBeVisible();
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
    await page.getByRole('button', { name: 'Create card', exact: true }).click();
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
        const search = page.locator('[data-board-link-search]');
        const candidateReads = () => requests.filter(request => request.path.endsWith('/links/candidates')).length;
        await expect(page.locator('[data-board-link-status]')).toContainText('Type a card ID or title');
        await expect(page.locator('[data-board-link-card]')).toHaveCount(0);
        expect(candidateReads()).toBe(0);
        await search.fill('VB-');
        await expect(page.locator('[data-board-link-card]')).toHaveCount(2);
        await expect(page.locator('[data-board-link-results] img')).toHaveCount(0);
        expect(await page.evaluate(() => window.__linkXss)).toBeUndefined();
        await search.fill('   ');
        await expect(page.locator('[data-board-link-card]')).toHaveCount(0);
        await expect(page.locator('[data-board-link-status]')).toContainText('Type a card ID or title');
        await page.waitForTimeout(300);
        expect(candidateReads()).toBe(1);
        await search.fill('VB-2');
        await expect(page.locator('[data-board-link-card]')).toHaveCount(1);
        const readsBeforeLink = candidateReads();
        if (width === 1440) {
            await search.press('ArrowDown');
            await expect(page.locator('[data-board-link-card="card_related"]')).toBeFocused();
            await page.keyboard.press('Enter');
        } else await page.locator('[data-board-link-card="card_related"]').click();
        await expect(page.locator('[data-board-linked-cards]')).toContainText('VB-2 · Related work');
        await expect(page.locator('[data-board-linked-cards]')).toContainText('Sprint 2 · Build');
        await expect(page.locator('[data-board-count="linked-cards"]')).toHaveText('1');
        await expect(page.locator('[data-board-link-results] [data-board-link-card]')).toHaveCount(0);
        await expect(search).toHaveValue('');
        await expect(search).toBeFocused();
        await expect(page.locator('[data-board-link-status]')).toContainText('Type a card ID or title');
        await page.waitForTimeout(300);
        expect(candidateReads()).toBe(readsBeforeLink);
        expect(requests.filter(request => request.method === 'PUT')).toHaveLength(0);
        await page.locator('[data-board-link-picker] > summary').click();
        await page.locator('[data-board-link-picker] > summary').click();
        await expect(page.locator('[data-board-link-card]')).toHaveCount(0);
        expect(candidateReads()).toBe(readsBeforeLink);
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
    await page.locator('[data-board-link-search]').fill('Related');
    const failLink = route => route.fulfill({ status: 500, json: { error: 'Link unavailable' } });
    await page.route('**/cards/card_test/links', failLink);
    await page.locator('[data-board-link-card="card_related"]').click();
    await expect(page.locator('[data-board-count="linked-cards"]')).toHaveText('0');
    await expect(page.locator('#board-card-title')).toHaveValue('Unsaved title');
    await expect(page.locator('[data-board-link-search]')).toHaveValue('Related');
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

test('uploaded Markdown previews safely and preserves Source and card drafts', async ({ page }, testInfo) => {
    await openBoard(page);
    const external = [];
    await page.route('https://attacker.invalid/**', route => { external.push(route.request().url()); return route.abort(); });
    const source = '# CLI Options\n\n**Model settings**\n\n<img src=x onerror="window.__boardXss=1">\n\n'
        + '![remote](https://attacker.invalid/track)\n\n[bad](javascript:alert(1))';
    await page.getByText('Description images', { exact: true }).click();
    await page.locator('#board-card-title').fill('Unsaved Markdown title');
    await page.locator('[data-board-files]').setInputFiles({ name: 'CLI_OPTIONS.MD', mimeType: 'text/markdown', buffer: Buffer.from(source) });
    await page.locator('[data-board-view-attachment="att_uploaded"]').click();
    const viewer = page.getByRole('dialog', { name: 'Attachment preview' });
    await expect(viewer.locator('.vb-board-attachment-markdown h1')).toHaveText('CLI Options');
    await expect(viewer.locator('.vb-board-attachment-markdown strong')).toHaveText('Model settings');
    await expect(viewer.locator('.vb-board-attachment-markdown')).toContainText('<img src=x onerror=');
    await page.screenshot({ path: testInfo.outputPath('markdown-preview.png') });
    await viewer.getByRole('button', { name: 'Source', exact: true }).click();
    await expect(viewer.locator('.vb-board-attachment-text')).toContainText('# CLI Options');
    await expect(viewer.locator('.vb-board-attachment-text')).toContainText('**Model settings**');
    await expect(viewer.locator('.vb-board-attachment-text')).toContainText('<img src=x onerror=');
    await expect(viewer.locator('[data-attachment-body]')
        .locator('img,iframe,script,a[href^="javascript:"]')).toHaveCount(0);
    expect(await page.evaluate(() => window.__boardXss)).toBeUndefined();
    expect(external).toEqual([]);
    await viewer.getByRole('button', { name: 'Close attachment' }).click();
    await expect(page.locator('[data-board-card-editor]')).toBeVisible();
    await expect(page.locator('#board-card-title')).toHaveValue('Unsaved Markdown title');
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

for (const width of [1440, 390]) {
    test(`Desktop agent activity keeps distinct indicators at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 900 });
        const columns = ['Desktop app', 'CLI terminal', 'Desktop and CLI'].map((name, index) => ({
            id: `col_${index}`, name, position: index, color: '#64748b', boardId: 'brd_main'
        }));
        let template;
        await openBoard(page, { columns, assignee: 'base:codex', onCard(value) {
            template = { ...value, description: '', attachments: [], comments: [], commentCount: 0, sessions: [] };
        } });
        const cards = columns.map((column, index) => ({
            ...template, id: `activity_${index}`, key: `VB-${index + 1}`, columnId: column.id,
            title: column.name, hasActiveDesktopAgent: index !== 1,
            activeTabId: index === 0 ? null : `tab_${index}`,
            activeSessionId: index === 0 ? null : `session_${index}`
        }));
        await page.route(url => url.pathname === '/api/v1/board/cards', route => route.fulfill({ json: { cards } }));
        await page.route('**/api/v1/board/cards/activity', route => route.fulfill({ json: { cards } }));
        await page.evaluate(() => window.app.boardController.refresh());
        const tiles = cards.map(card => page.locator(`.board-card[data-card-id="${card.id}"]`));
        await expect(tiles[0]).toHaveClass(/is-desktop-live/);
        await expect(tiles[0].getByRole('img', { name: 'Desktop app is working on this card' })).toBeVisible();
        await expect(tiles[0].locator('.board-live-dot')).toHaveCount(0);
        await expect(tiles[1]).not.toHaveClass(/is-desktop-live/);
        await expect(tiles[1].locator('.board-desktop-active')).toHaveCount(0);
        await expect(tiles[1].locator('.board-live-dot')).toHaveCount(1);
        await expect(tiles[2]).not.toHaveClass(/is-desktop-live/);
        await expect(tiles[2].locator('.board-desktop-active')).toHaveCount(1);
        await expect(tiles[2].locator('.board-live-dot')).toHaveCount(1);

        for (const [index, tile] of tiles.entries()) {
            await tile.scrollIntoViewIfNeeded();
            await expect(tile).toHaveClass(/is-live/);
            const style = await tile.evaluate(element => ({
                image: getComputedStyle(element).backgroundImage,
                overflow: element.scrollWidth > element.clientWidth
            }));
            expect(style.image).toContain(index === 0 ? 'rgb(168, 85, 247)' : 'rgb(6, 182, 212)');
            expect(style.overflow).toBe(false);
            if (index !== 1) await expect(tile.locator('.board-desktop-active')).toHaveCSS('color', 'rgb(168, 85, 247)');
            if (index !== 0) await expect(tile.locator('.board-live-dot')).toHaveCSS('background-color', 'rgb(34, 197, 94)');
            await expectAnimatedStyle(tile, 'backgroundPosition');
            if (width === 390) await tile.screenshot({ path: testInfo.outputPath(`activity-${index}-${width}.png`) });
        }
        if (width === 1440) await page.screenshot({ path: testInfo.outputPath('desktop-cli-indicators.png') });
        await page.emulateMedia({ reducedMotion: 'reduce' });
        for (const tile of tiles) await expect(tile).toHaveCSS('animation-name', 'none');
        for (const tile of [tiles[1], tiles[2]]) await expect(tile.locator('.board-live-dot')).toHaveCSS('animation-name', 'none');
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

const savedReview = (id, changes = {}) => ({
    id, reviewer: 'Codex', provider: 'codex', processStatus: 'Succeeded', result: 'Findings',
    createdUtc: '2026-11-07T10:00:00Z', reportedUtc: '2026-11-07T10:05:00Z',
    scope: 'working-tree', scopeDescription: 'Uncommitted changes', freshness: 'Unknown freshness',
    workspace: 'C:/example/project', scopeFiles: ['src/example.js'],
    findings: 'Medium-high: preserve the saved result.\n<img src=x onerror="window.__reviewXss=1">',
    validation: 'Reviewed the changed files and ran the focused tests.', limitations: 'No live service was contacted.',
    ...changes
});

for (const width of [320, 375, 768, 1024]) {
    test(`card displays MCP review findings and preserves drafts at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 950 });
        const requests = await openBoard(page);
        const report = savedReview('review-1', { reviewer: "Codex's reviewer", terminalSessionId: 'session_test', workspace: `C:/example/${'long-path-'.repeat(30)}` });
        let reviews = [savedReview('pending', { result: null, reportedUtc: null, processStatus: 'Succeeded' })];
        const reads = [];
        await page.route('**/api/v1/board/cards/card_test/reviews**', route => {
            expect(route.request().method()).toBe('GET');
            reads.push(route.request().url());
            return route.fulfill({ json: route.request().url().includes('/review-1?')
                ? { ...report, freshness: 'Stale: inputs changed' } : { reviews, latest: report, hasMore: false } });
        });
        await page.locator('.board-card').click();
        const editor = page.locator('[data-board-card-editor]');
        const panel = editor.getByRole('region', { name: 'Code reviews', exact: true });
        await expect(panel.locator('[data-review-latest]')).toContainText('Report missing');
        await expect(panel.locator('[data-review-latest]')).toContainText('Latest report');
        await expect(panel.locator('[data-review-run], [data-review-settings]')).toHaveCount(0);
        await panel.getByRole('button', { name: 'View report', exact: true }).focus();
        await page.evaluate(() => window.app.boardController.refreshSessionActivity());
        await expect(panel.getByRole('button', { name: 'View report', exact: true })).toBeFocused();
        await editor.locator('#board-card-title').fill('Unsaved review investigation');
        await editor.locator('[data-board-composer="description"] textarea').fill('Description draft');
        await editor.locator('[data-board-composer="comment"] textarea').fill('Comment draft');
        await panel.getByRole('button', { name: 'View report', exact: true }).click();
        const detail = panel.getByRole('region', { name: 'Review report', exact: true });
        await expect(detail).toContainText(report.findings);
        await expect(detail).toContainText(report.validation);
        await expect(detail).toContainText(report.limitations);
        await expect(detail).toContainText('Stale: inputs changed');
        await expect(detail).toBeFocused();
        await expect(detail.locator('img')).toHaveCount(0);
        expect(await page.evaluate(() => window.__reviewXss)).toBeUndefined();
        await detail.getByText('Captured files', { exact: true }).click();
        await expect(detail).toContainText('src/example.js');
        await page.screenshot({ path: testInfo.outputPath(`card-review-${width}.png`) });
        expect(await editor.evaluate(el => el.scrollWidth <= el.clientWidth + 1)).toBe(true);
        await detail.getByRole('button', { name: 'Compare review inputs' }).click();
        await expect(detail).toContainText('Stale: inputs changed');
        reviews = [savedReview('pending', { result: 'No findings reported' })];
        await page.evaluate(() => window.app.boardController.refreshSessionActivity());
        await expect(panel.locator('[data-review-latest]')).toContainText('No findings reported');
        await expect(detail).toContainText(report.findings);
        await expect(editor.locator('#board-card-title')).toHaveValue('Unsaved review investigation');
        await expect(editor.locator('[data-board-composer="description"] textarea')).toHaveValue('Description draft');
        await expect(editor.locator('[data-board-composer="comment"] textarea')).toHaveValue('Comment draft');
        await page.evaluate(() => { window.app.boardController.openSessionReplay = session => { window.reviewReplay = session.id; }; });
        await panel.getByRole('button', { name: 'Terminal / replay' }).click();
        expect(await page.evaluate(() => window.reviewReplay)).toBe('session_test');
        expect(reads.filter(url => url.endsWith('/review-1?verify=true')).length).toBe(2);
        expect(reads.some(url => /settings|preview/.test(url))).toBe(false);
        expect(requests.some(request => request.path.startsWith('/api/v1/board/cards/card_test') && request.method !== 'GET')).toBe(false);
    });
}

test('card review history survives polling and keeps the latest saved report visible', async ({ page }) => {
    await openBoard(page);
    let reviews = Array.from({ length: 55 }, (_, i) => savedReview(`run-${i}`, { reportedUtc: null, result: null }));
    const latest = savedReview('older-report');
    await page.route('**/api/v1/board/cards/card_test/reviews?*', route => {
        const offset = Number(new URL(route.request().url()).searchParams.get('offset'));
        return route.fulfill({ json: { reviews: reviews.slice(offset, offset + 50), latest, hasMore: offset + 50 < reviews.length } });
    });
    await page.locator('.board-card').click();
    const panel = page.locator('[data-board-reviews]');
    await expect(panel.locator('[data-review-view="older-report"]')).toBeVisible();
    await panel.getByText('Older runs', { exact: true }).click();
    await panel.getByRole('button', { name: 'Load older runs' }).click();
    await expect(panel.locator('[data-review-history] article')).toHaveCount(54);
    reviews.unshift(savedReview('new-run', { reportedUtc: null, result: null }));
    await page.evaluate(() => window.app.boardController.refreshSessionActivity());
    await expect(panel.locator('[data-review-history] article')).toHaveCount(55);
    await expect(panel.locator('[data-review-view="older-report"]')).toBeVisible();
    await expect(panel.getByRole('button', { name: 'Load older runs' })).toBeHidden();
});

test('card review failures recover and late reports cannot overwrite another report or reopened card', async ({ page }) => {
    await openBoard(page);
    let failList = true, failReport = true;
    await page.route('**/api/v1/board/cards/card_test/reviews?*', route => failList
        ? route.fulfill({ status: 503, json: { error: 'Reviews unavailable' } })
        : route.fulfill({ json: { reviews: [savedReview('a'), savedReview('b')], hasMore: false } }));
    await page.route('**/api/v1/board/cards/card_test/reviews/*', route => failReport
        ? route.fulfill({ status: 503, json: { error: 'Report unavailable' } })
        : route.fulfill({ json: savedReview('b', { findings: 'Second report' }) }));
    await page.locator('.board-card').click();
    const panel = page.locator('[data-board-reviews]');
    await expect(panel.locator('[data-review-message]')).toContainText('Reviews unavailable');
    failList = false;
    await panel.getByRole('button', { name: 'Refresh reviews' }).click();
    await panel.locator('[data-review-view="a"]').click();
    await expect(panel.locator('[data-review-report]')).toContainText('Report unavailable');
    failReport = false;
    await page.evaluate(() => {
        const app = window.app, original = app.apiCall.bind(app);
        app.apiCall = (url, ...args) => url.includes('/reviews/a?')
            ? new Promise(resolve => { window.releaseReview = resolve; window.reviewSignal = args[2].signal; })
            : original(url, ...args);
    });
    await panel.locator('[data-review-view="a"]').click();
    await panel.getByText('Older runs', { exact: true }).click();
    await panel.locator('[data-review-view="b"]').click();
    await expect(panel.locator('[data-review-report]')).toContainText('Second report');
    expect(await page.evaluate(() => window.reviewSignal.aborted)).toBe(true);
    await page.evaluate(row => window.releaseReview(row), savedReview('a', { findings: 'Obsolete report' }));
    await expect(panel.locator('[data-review-report]')).not.toContainText('Obsolete report');
    await panel.locator('[data-review-view="a"]').click();
    await page.evaluate(() => window.app.closeModal());
    expect(await page.evaluate(() => window.reviewSignal.aborted)).toBe(true);
    await page.locator('.board-card').click();
    await page.evaluate(row => window.releaseReview(row), savedReview('a', { findings: 'Closed card report' }));
    await expect(panel.locator('[data-review-report]')).toBeHidden();
});

test('Agent definition uses the shared provider without a purpose field', async ({ page }) => {
    await openBoard(page);
    const writes = [];
    await page.route('**/api/v1/presets', async route => {
        const body = route.request().postDataJSON(); writes.push(body);
        return route.fulfill({ json: { preset: { ...body, id: 'reviewer', revision: 1 }, affectedBindings: [] } });
    });
    await page.evaluate(() => window.app.aiAgentsController.openEditor());
    const editor = page.locator('[data-launch-editor]');
    await expect(editor.locator('[data-launch-provider]')).toHaveValue('base:codex');
    await editor.getByLabel('Name', { exact: true }).fill('My reviewer');
    await editor.locator('[data-launch-provider]').selectOption('base:claude');
    await editor.locator('[data-board-composer-input]').fill('Review the scoped changes and save the report.');
    await editor.getByText('Additional settings', { exact: true }).click();
    await expect(editor.getByLabel('Purpose', { exact: true })).toHaveCount(0);
    await editor.getByRole('button', { name: 'Create agent', exact: true }).click();
    await expect.poll(() => writes.length).toBe(1);
    expect(writes[0]).toMatchObject({ name: 'My reviewer', cli: 'claude', purpose: 'work' });
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
        await expect(editor.locator('[data-board-reviews]')).toHaveCount(1);
        await expect(editor.locator('[data-review-run], [data-review-settings], [data-review-override]')).toHaveCount(0);
        await expect(editor.locator('[data-check-run-options], [data-check-history]')).toHaveCount(0);
        await expect(editor.locator('[data-board-checks]')).not.toBeVisible();
        await expect(editor.locator('[data-board-sessions] [data-session-id]')).toHaveCount(1);
        await expect(editor.locator('[data-board-automations] [data-session-id="review-session"]')).toHaveCount(1);
        await editor.locator('#board-card-title').fill('Keep this unsaved draft');
        await page.evaluate(() => window.app.boardController.refreshSessionActivity());
        await expect(editor.locator('#board-card-title')).toHaveValue('Keep this unsaved draft');
        expect(reviewReads).toBeGreaterThan(0);
        await page.screenshot({ path: testInfo.outputPath(`compact-card-${width}.png`) });
    });
}

for (const width of [1440, 390]) {
    test(`full-text board search combines every filter at ${width}px`, async ({ page }, testInfo) => {
        await page.setViewportSize({ width, height: 950 });
        await openBoard(page, { assignee: 'base:codex' });
        const cards = [
            { id: 'local-feature', key: 'VB-10', title: 'Local work', type: 'feature', assignee: 'base:codex', priority: 'high',
                origin: 'human', boardId: 'brd_main', boardName: 'Main', columnName: 'Ready', isCurrentProject: true,
                snippet: 'cobalt found inside a comment' },
            { id: 'foreign-bug', key: 'OTHER-11', title: 'Foreign repair', type: 'bug', assignee: 'base:claude', priority: 'high',
                origin: 'agent', boardId: 'outside', boardName: 'Other board', columnName: 'Build', isCurrentProject: false,
                projectPath: 'C:/other-project', snippet: 'cobalt found deep inside the description' },
            { id: 'local-bug', key: 'VB-12', title: 'Local repair', type: 'bug', assignee: 'base:codex', priority: 'low',
                origin: 'human', boardId: 'brd_main', boardName: 'Main', columnName: 'Ready', isCurrentProject: true,
                snippet: 'cobalt found in previous work' }
        ];
        const requests = [];
        await page.route('**/api/v1/board/cards/search?*', route => {
            const params = new URL(route.request().url()).searchParams;
            requests.push(Object.fromEntries(params));
            const matches = cards.filter(card => ['type', 'assignee', 'priority', 'origin']
                .every(key => !params.get(key) || params.get(key) === card[key]));
            return route.fulfill({ json: { cards: matches } });
        });
        const search = page.locator('[data-board-search]');
        const results = page.locator('[data-board-search-card]');
        await search.fill('cobalt');
        await expect(results).toHaveCount(3);
        await expect(page.getByRole('heading', { name: 'Search results', exact: true })).toBeVisible();
        await expect(results.first().locator('mark')).toHaveText('cobalt');
        await expect(results.first()).toContainText('inside a comment');
        for (const [key, value, count] of [
            ['type', 'bug', 2], ['assignee', 'base:claude', 1], ['priority', 'high', 1], ['origin', 'agent', 1]
        ]) {
            const control = page.locator(`[data-board-filter-${key}]`);
            await expect(control).toBeEnabled();
            await control.selectOption(value);
            await expect.poll(() => requests.at(-1)?.[key]).toBe(value);
            await expect(results).toHaveCount(count);
            await expect(search).toHaveValue('cobalt');
        }
        expect(requests.at(-1)).toEqual({ q: 'cobalt', type: 'bug', assignee: 'base:claude', priority: 'high', origin: 'agent' });
        await expect(results).toContainText('Foreign repair');
        await expect(results).toContainText('deep inside the description');
        expect(await results.evaluateAll(elements => elements.every(element => element.scrollWidth <= element.clientWidth + 1))).toBe(true);
        await page.screenshot({ path: testInfo.outputPath('filtered-search.png') });
        await page.locator('[data-board-filter-priority]').selectOption('low');
        await expect(results).toHaveCount(0);
        await expect(page.locator('[data-board-search-status]')).toContainText('selected filters');
        await page.locator('[data-board-clear-filters]').click();
        await expect(search).toHaveValue('');
        await expect(page.locator('[data-board-canvas]')).toBeVisible();
        for (const key of ['type', 'assignee', 'priority', 'origin']) {
            await expect(page.locator(`[data-board-filter-${key}]`)).toHaveValue('');
            await expect(page.locator(`[data-board-filter-${key}]`)).toBeEnabled();
        }
    });

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
        await expect(page.locator('[data-board-filter-type]')).toBeEnabled();
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
        await editor.getByLabel('Add a comment', { exact: true }).fill('Comment saved on outside card');
        await editor.getByRole('button', { name: 'Post comment' }).click();
        await expect(editor.locator('[data-local-card-comments]')).toContainText('Comment saved on outside card');
        await expect(editor.getByLabel('Description', { exact: true })).toHaveValue('Preserve this unsaved description');
        await editor.locator('[data-board-link-picker] > summary').click();
        await editor.locator('[data-board-link-search]').fill('Description');
        await editor.locator('[data-board-link-card="card_test"]').click();
        await expect(editor.locator('[data-board-linked-cards]')).toContainText('Description images');
        await editor.locator('[data-board-unlink-card="card_test"]').click();
        await expect(editor.locator('[data-board-linked-cards]')).toContainText('No cards linked');
        await expect(editor.locator('[data-board-link-card]')).toHaveCount(0);
        await editor.locator('[data-board-link-search]').fill('Description');
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
                created++; const body = request.postDataJSON();
                const link = { id: created, displayName: body.displayName, access: body.access, recipients: body.emails,
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
        await expect(panel).toContainText('No sharing links');
        await editor.locator('#board-card-title').fill('Keep my unsaved title');
        await panel.getByRole('button', { name: 'Create link', exact: true }).click();
        await expect(panel.locator('[role="status"]')).toContainText('drafts are still here');
        expect(created).toBe(0);
        await editor.locator('#board-card-title').fill('Description images');
        const draft = editor.locator('[data-board-composer="comment"] textarea');
        await draft.fill('Unposted comment');
        await panel.getByRole('button', { name: 'Create link', exact: true }).click();
        expect(created).toBe(0); await expect(draft).toHaveValue('Unposted comment');
        await draft.fill('');
        await panel.locator('[data-card-share-name]').fill('<img src=x onerror="window.shareInjected=true">');
        await panel.locator('[data-card-share-audience-mode][value="email"]').check();
        await panel.locator('[data-card-share-audience-emails]').fill('reviewer@example.test, <b>lead</b>@example.test');
        await panel.getByRole('button', { name: 'Create link', exact: true }).click();
        await expect(panel.locator('[data-card-share-id]')).toHaveCount(1);
        await expect(panel.locator('[data-card-share-audience-summary]')).toContainText('2 people: reviewer@example.test, <b>lead</b>@example.test');
        expect(links[0].access).toBe('email'); expect(links[0].recipients).toEqual(['reviewer@example.test', '<b>lead</b>@example.test']);
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

test('Request review shows the staged preview, creates and sends after the disclosure, and marks approvals on the header and commits', async ({ page }, testInfo) => {
    const errors = []; page.on('pageerror', error => errors.push(error.message));
    await page.addInitScript(() => { window.confirm = () => { throw new Error('window.confirm is banned; use confirmDialog'); }; });
    await page.route('https://viberails.ai/**', route => route.abort());
    const tree = 'b'.repeat(40), commitSha = 'e'.repeat(40);
    const trailer = `vibe-signature: v1 key=6f1a2b3c-4d5e-4f60-8a9b-0c1d2e3f4a5b fp=${'f'.repeat(64)} tree=${tree} sig=${'A'.repeat(344)}`;
    const requests = await openBoard(page, { onCard: card => {
        card.commits = [{ sha: commitSha, shortSha: 'eeeeeee', author: 'Dev', message: 'Approved change', committedAt: '2026-10-24T12:00:00Z', linkedAt: '2026-10-24T12:05:00Z' }];
    } });
    const approved = { id: '11111111-1111-4111-8111-111111111111', scope: 'staged', treeId: tree, status: 'approved', reviewers: ['lead@example.test', '<img src=x onerror="window.reviewInjected=true">'],
        cardKey: 'VB-1', fileCount: 1, sessionCount: 1, createdUtc: '2026-10-23T12:00:00Z', updatedUtc: '2026-10-23T13:00:00Z',
        decisions: [{ id: 1, reviewer: 'lead@example.test', decision: 'approved', trailer, decidedUtc: '2026-10-23T13:00:00Z', verifiedUtc: '2026-10-23T13:00:00Z' }],
        remote: { id: 'rvw_' + '0'.repeat(32), sentUtc: '2026-10-23T12:30:00Z', expiresUtc: '2026-11-23T12:30:00Z' } };
    const stale = { ...approved, id: '22222222-2222-4222-8222-222222222222', status: 'open', decisions: [], remote: null, cardKey: 'VB-9', createdUtc: '2026-10-22T12:00:00Z' };
    let list = [approved, stale];
    const bodies = { create: [], send: [], cancel: [], deleted: [] };
    await page.route('**/api/v1/review-requests**', async route => {
        const request = route.request(), method = request.method(), path = new URL(request.url()).pathname;
        expect(request.headers().viberails_tab).toBe('board-fixture');
        if (path === '/api/v1/review-requests' && method === 'GET') return route.fulfill({ json: { requests: list } });
        if (path === '/api/v1/review-requests/staged') return route.fulfill({ json: {
            repository: 'board-fixture', treeId: tree, baseTree: 'd'.repeat(40), baseCommit: 'c'.repeat(40),
            files: [{ path: 'db.cs', change: 'modified', changedLines: 1, added: 1, removed: 1, textIncluded: true },
                { path: 'logo.png', change: 'added', textIncluded: false, omitted: 'binary' }],
            filesWithText: 1, filesWithoutText: 1, textBytes: 44, suggestedReviewers: ['lead@example.test'],
            rules: [{ rule: "Signed Approval('db.cs', 'lead@example.test')", sourcePath: 'vc.rules.md', path: 'db.cs', approvers: ['lead@example.test'], matchedFiles: ['db.cs'] }],
            destination: 'https://viberails.ai', audience: 'email', sessionDisclosure: 'Each attached session uploads to your viberails.ai account after it ends.', expiresUtc: '2026-11-24T12:00:00Z' } });
        if (path === '/api/v1/review-requests/commit-approvals') return route.fulfill({ json: { commits: [{ sha: commitSha, treeId: tree, approvals: [{ reviewer: 'lead@example.test', requestId: approved.id, decidedUtc: '2026-10-23T13:00:00Z' }] }] } });
        if (path === '/api/v1/review-requests' && method === 'POST') {
            const body = request.postDataJSON(); bodies.create.push(body);
            const created = { ...stale, id: '33333333-3333-4333-8333-333333333333', cardKey: body.cardKey, reviewers: body.reviewers, sessionCount: body.sessions.length, createdUtc: '2026-10-24T14:00:00Z' };
            list = [created, ...list];
            return route.fulfill({ status: 201, json: created });
        }
        const match = path.match(/^\/api\/v1\/review-requests\/([^/]+)(?:\/(disclosure|send|cancel))?$/);
        const target = list.find(item => item.id === match?.[1]);
        if (match?.[2] === 'disclosure') return route.fulfill({ json: { id: target.id, destination: 'https://viberails.ai', audience: 'email', repository: 'board-fixture', treeId: tree,
            baseTree: 'd'.repeat(40), files: [{ path: 'db.cs', change: 'modified', changedLines: 1, textIncluded: true }], filesWithText: 1, filesWithoutText: 0, textBytes: 44,
            sessions: [{ id: 'session_test', name: 'Codex session', cli: 'codex', startedUtc: '2026-10-24T12:00:00Z', live: true }],
            sessionDisclosure: 'Each attached session uploads to your viberails.ai account after it ends.', reviewers: target.reviewers, cardKey: target.cardKey, note: 'Check the migration',
            expiresUtc: '2026-11-24T14:00:00Z', sent: false } });
        if (match?.[2] === 'send') {
            bodies.send.push(request.postDataJSON());
            if (target.cardKey !== 'VB-1') return route.fulfill({ status: 502, json: { error: 'viberails.ai does not support review requests yet. Nothing was sent; try again after the service is updated.', code: 'server_update_required' } });
            target.remote = { id: 'rvw_' + '1'.repeat(32), sentUtc: '2026-10-24T14:10:00Z', expiresUtc: '2026-11-24T14:10:00Z' };
            return route.fulfill({ json: { request: target, message: 'Sent. Attached sessions upload after they end; keep VibeRails open until they do. Decisions are checked every 15 minutes.' } });
        }
        if (match?.[2] === 'cancel') { bodies.cancel.push(target.id); target.status = 'cancelled'; return route.fulfill({ json: target }); }
        if (method === 'DELETE') { bodies.deleted.push(target.id); list = list.filter(item => item !== target); return route.fulfill({ status: 204 }); }
        return route.fulfill({ status: 404, json: { error: 'Review request not found.', code: 'not_found' } });
    });

    await page.getByText('Description images', { exact: true }).click();
    const editor = page.locator('[data-board-card-editor]');
    await expect(editor).toBeVisible();
    // Header and commit rail read the ledger as soon as the card opens, without expanding the section.
    const headerBadge = page.locator('#modal-container .modal-header [data-board-review-status]');
    await expect(headerBadge).toHaveText('Review approved by lead@example.test');
    await expect(editor.locator('[data-board-commits] .board-review-approved')).toHaveText('Approved by lead@example.test');
    expect(await page.evaluate(() => window.reviewInjected)).toBeUndefined();

    const panel = editor.locator('[data-card-review-request]');
    await panel.locator('summary').click();
    await expect(panel.locator('[data-card-review-files]')).toContainText('modified db.cs');
    await expect(panel.locator('[data-card-review-files]')).toContainText('+1 −1');
    await expect(panel.locator('[data-card-review-files]')).toContainText('no text (binary)');
    await expect(panel.locator('[data-card-review-counts]')).toContainText('tree bbbbbbbbbbbb');
    await expect(panel.locator('[data-card-review-rules]')).toContainText('db.cs');
    await expect(panel.locator('[data-card-review-reviewers]')).toHaveValue('lead@example.test');
    await expect(panel.locator('[data-card-review-session][value="session_test"]')).toBeChecked();
    await expect(panel.locator('[data-card-review-id]')).toHaveCount(2);
    await expect(panel.locator('[data-card-review-id]').first()).toContainText('Approved by lead@example.test');
    await expect(panel.locator('[data-card-review-id]').first().locator('[data-card-review-trailer]')).toHaveValue(trailer);
    await expect(panel.locator('[data-card-review-id]').first().getByRole('button', { name: 'Copy approval' })).toBeVisible();

    await editor.locator('#board-card-title').fill('Keep my unsaved title');
    await panel.getByRole('button', { name: 'Create request', exact: true }).click();
    await expect(panel.locator('[role="status"]')).toContainText('drafts are still here');
    expect(bodies.create).toEqual([]);
    await editor.locator('#board-card-title').fill('Description images');
    await panel.locator('[data-card-review-note]').fill('Check the migration');
    await panel.getByRole('button', { name: 'Create request', exact: true }).click();
    await expect(panel.locator('[role="status"]')).toContainText('Nothing has been sent');
    expect(bodies.create).toEqual([{ scope: 'staged', reviewers: ['lead@example.test'], sessions: ['session_test'], cardKey: 'VB-1', note: 'Check the migration' }]);
    await expect(panel.locator('[data-card-review-id]')).toHaveCount(3);
    const created = panel.locator('[data-card-review-id="33333333-3333-4333-8333-333333333333"]');
    await expect(created.locator('[data-card-review-status]')).toHaveText('Open, not sent yet');

    // Send asks first, with what leaves the computer; cancelling the dialog sends nothing.
    await created.getByRole('button', { name: 'Send…', exact: true }).click();
    const dialog = page.getByRole('alertdialog');
    await expect(dialog).toContainText('readable only by lead@example.test');
    await expect(dialog).toContainText('db.cs');
    await expect(dialog).toContainText('Codex session (still running: uploads after it ends)');
    await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
    await expect(panel.locator('[role="status"]')).toHaveText('Not sent.');
    expect(bodies.send).toEqual([]);
    await created.getByRole('button', { name: 'Send…', exact: true }).click();
    await page.getByRole('alertdialog').getByRole('button', { name: 'Send', exact: true }).click();
    await expect(panel.locator('[role="status"]')).toContainText('Decisions are checked every 15 minutes');
    expect(bodies.send).toEqual([{ confirmed: true }]);
    await expect(created.locator('[data-card-review-status]')).toHaveText('Sent, awaiting decisions');
    await expect(created.getByRole('button', { name: 'Send…', exact: true })).toHaveCount(0);

    // An older viberails.ai refuses the send; the fixed wording and code reach the person and the request stays open.
    const other = panel.locator('[data-card-review-id="22222222-2222-4222-8222-222222222222"]');
    await other.getByRole('button', { name: 'Send…', exact: true }).click();
    await page.getByRole('alertdialog').getByRole('button', { name: 'Send', exact: true }).click();
    await expect(panel.locator('[role="status"]')).toContainText('does not support review requests yet');
    await expect(other.locator('[data-card-review-status]')).toHaveText('Open, not sent yet');

    await panel.scrollIntoViewIfNeeded();
    expect(await panel.evaluate(el => el.scrollWidth <= el.clientWidth + 1)).toBe(true);
    await page.screenshot({ path: testInfo.outputPath('card-request-review.png') });

    await other.getByRole('button', { name: 'Cancel request', exact: true }).click();
    await page.getByRole('alertdialog').getByRole('button', { name: 'Cancel request', exact: true }).click();
    await expect(other.locator('[data-card-review-status]')).toHaveText('Cancelled');
    expect(bodies.cancel).toEqual(['22222222-2222-4222-8222-222222222222']);
    await other.getByRole('button', { name: 'Delete', exact: true }).click();
    await page.getByRole('alertdialog').getByRole('button', { name: 'Delete', exact: true }).click();
    await expect(panel.locator('[data-card-review-id]')).toHaveCount(2);
    expect(bodies.deleted).toEqual(['22222222-2222-4222-8222-222222222222']);
    expect(await page.evaluate(() => window.reviewInjected)).toBeUndefined();
    expect(errors).toEqual([]);
    expect(requests.filter(r => r.method === 'PUT' && r.path === '/api/v1/board/cards/card_test')).toEqual([]);
    await page.evaluate(() => window.app.closeModal());
    await expect(page.locator('[data-board-review-status]')).toHaveCount(0);
});

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
    await expect(panel).toContainText('No sharing links');
    await panel.getByRole('button', { name: 'Create link', exact: true }).click();
    await expect(panel.locator('[data-card-share-rename]')).toHaveValue('Created despite timeout');
    expect(creations).toBe(1);
    await panel.getByRole('button', { name: 'Create link', exact: true }).click();
    await expect.poll(() => Boolean(release)).toBe(true);
    await page.evaluate(() => window.app.closeModal()); release();
    await expect(page.locator('[data-card-sharing]')).toHaveCount(0);
    expect(creations).toBe(2);
});
// Unified definitions replace the retired Job/lane builder. These tests exercise
// the production Board popup and saved-card adapters with the typed launch API.
async function installLaunchFixture(page, { conflict = false } = {}) {
    const items = [
        { id:'preset_review',definitionType:'preset',kind:'agent',name:'Review',cli:'codex',enabled:true,canLaunch:true,canRunAutomatically:true },
        { id:'action_check',definitionType:'action',kind:'vca',name:'Check',enabled:true,canLaunch:true,canRunAutomatically:true },
        { id:'preset_quick',definitionType:'preset',kind:'quick-launch',name:'Quick',cli:'codex',enabled:true,canLaunch:true,canRunAutomatically:false }
    ];
    const state={items,conflict,writes:[],revision:3,selections:[{definitionId:'preset_review',definitionType:'preset',gate:'review'},{definitionId:'action_check',definitionType:'action',gate:'result'}],activity:[],runs:[],posts:0,skips:0,retries:0};
    await page.route('**/api/v1/launch-catalog*',route=>route.fulfill({json:{items}}));
    await page.route('**/api/v1/launch-projects',route=>route.fulfill({json:[]}));
    await page.route('**/api/v1/board/columns/*/launches',async route=>{
        if(route.request().method()==='PUT'){
            const body=route.request().postDataJSON();state.writes.push(body);
            if(state.conflict)return route.fulfill({status:409,json:{error:'Lane changed elsewhere. Reopen to reload.'}});
            state.selections=body.selections;state.revision++;
        }
        return route.fulfill({json:{selections:state.selections,revision:state.revision}});
    });
    await page.route('**/api/v1/board/columns/*/launches/activity',route=>route.fulfill({json:state.activity}));
    await page.route('**/api/v1/board/cards/card_test/launches',async route=>{
        if(route.request().method()==='POST'){
            state.posts++;state.lastRun=route.request().postDataJSON();
            if(state.hold)await state.hold;
            const run={id:'run_manual',status:'queued',snapshot:{preset:{name:'Review'},context:{cardId:'card_test'}}};state.runs.push(run);return route.fulfill({json:run});
        }
        return route.fulfill({json:state.activity.map(row=>row.entry)});
    });
    await page.route('**/api/v1/board/cards/card_test/launches/*/skip',route=>{state.skips++;state.activity[0].entry.skipRequested=true;return route.fulfill({status:204});});
    await page.route('**/api/v1/board/cards/card_test/launches/runs',route=>route.fulfill({json:{runs:state.runs,nextCursor:null}}));
    await page.route('**/api/v1/runs/*/retry',route=>{state.retries++;state.activity[0].run.status='queued';return route.fulfill({json:state.activity[0].run});});
    return state;
}

for (const width of [320,375,768,1024]) test(`unified lane order and gates persist at ${width}px`,async({page},testInfo)=>{
    await page.setViewportSize({width,height:900});await openBoard(page);
    const state=await installLaunchFixture(page);
    const trigger=page.getByRole('button',{name:'Agents and Actions on entry to Ready',exact:true});await trigger.click();
    const panel=page.locator('.board-lane-agents-panel');
    await expect(panel.locator('[data-lane-add-choice] option[value="preset_quick"]')).toHaveCount(0);
    await expect(panel.locator('[data-lane-index="1"] [data-lane-gate] option[value="review"]')).toHaveCount(0);
    const up=panel.getByRole('button',{name:'Move Check up',exact:true});await up.focus();await up.press('Enter');
    await panel.getByRole('button',{name:'Save lane settings',exact:true}).click();
    await expect.poll(()=>state.writes.length).toBe(1);
    expect(state.writes[0]).toMatchObject({revision:3,selections:[{definitionId:'action_check',gate:'result'},{definitionId:'preset_review',gate:'review'}]});
    expect(await panel.evaluate(el=>el.scrollWidth<=el.clientWidth+1)).toBe(true);
    await page.screenshot({path:testInfo.outputPath(`unified-lane-${width}.png`)});
    await panel.getByRole('button',{name:'Close lane panel'}).click();await expect(trigger).toBeFocused();await trigger.click();
    await expect(panel.locator('[data-lane-selection-list] strong')).toHaveText(['Check','Review']);
});

test('unified lane conflicts preserve edits and retired selections remain explicit',async({page})=>{
    await openBoard(page);const state=await installLaunchFixture(page,{conflict:true});
    state.selections.push({definitionId:'retired',definitionType:'preset',gate:'process'});
    await page.getByRole('button',{name:'Agents and Actions on entry to Ready',exact:true}).click();
    const panel=page.locator('.board-lane-agents-panel');
    await expect(panel).toContainText('Unavailable / retired selection');
    await panel.getByRole('button',{name:'Move Check up',exact:true}).click();
    await panel.getByRole('button',{name:'Save lane settings',exact:true}).click();
    await expect(panel.locator('[data-lane-error]')).toContainText('Lane changed elsewhere');
    await expect(panel.locator('[data-lane-selection-list] strong')).toHaveText(['Check','Review','Unavailable / retired selection']);
    expect(state.writes[0].revision).toBe(3);
    await panel.getByRole('button',{name:'Close lane panel'}).click();
    await page.getByRole('button',{name:'Cancel',exact:true}).click();
    await expect(panel).toBeVisible();
    await expect(panel.locator('[data-lane-selection-list] strong')).toHaveText(['Check','Review','Unavailable / retired selection']);
    state.conflict=false;
    await panel.getByRole('button',{name:'Reload latest',exact:true}).click();
    await page.getByRole('button',{name:'Discard',exact:true}).click();
    await expect(panel.locator('[data-lane-selection-list] strong')).toHaveText(['Review','Check','Unavailable / retired selection']);
});

for (const width of [320, 1024]) test(`lane Tom Select shows icons with search, selection, Escape and cleanup at ${width}px`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 900 });
    await openBoard(page);
    const state = await installLaunchFixture(page);
    state.selections = [];
    const scriptName = 'Build <script> & verify a release with a long descriptive name';
    state.items.push(
        { id: 'script', definitionType: 'action', kind: 'script', name: scriptName, canRunAutomatically: true },
        { id: 'unknown', definitionType: 'preset', kind: 'agent', name: 'Other provider', cli: 'unknown', canRunAutomatically: true }
    );
    const trigger = page.getByRole('button', { name: 'Agents and Actions on entry to Ready', exact: true });
    await trigger.click();
    const panel = page.locator('.board-lane-agents-panel');
    const picker = panel.locator('.ts-control');
    await expect(picker).toBeVisible();
    await picker.click();
    const menu = page.locator('.launch-lane-dropdown');
    const review = menu.getByRole('option', { name: 'Review · Agent', exact: true });
    await expect(review.locator('img')).toHaveAttribute('src', 'assets/img/openai.svg');
    await expect(menu.getByRole('option', { name: 'Check · VCA check', exact: true }).locator('img')).toHaveAttribute('src', 'assets/img/logo.png');
    await expect(menu.getByRole('option', { name: 'Code quality check', exact: true }).locator('img')).toHaveAttribute('src', 'assets/img/logo.png');
    await expect(menu.getByRole('option', { name: `${scriptName} · Script`, exact: true }).locator('.fa-code')).toBeVisible();
    await expect(menu.getByRole('option', { name: 'Other provider · Agent', exact: true }).locator('.fa-robot')).toBeVisible();
    await expect(menu.locator('[data-value=""] .launch-lane-choice-icon')).toHaveCount(0);
    await expect.poll(() => menu.locator('img').evaluateAll(images => images.every(img => img.complete && img.naturalWidth > 0))).toBe(true);
    expect(await menu.evaluate(el => el.scrollWidth <= el.clientWidth + 1)).toBe(true);
    await page.screenshot({ path: testInfo.outputPath('lane-picker-icons.png') });
    const search = page.getByRole('textbox', { name: 'Search agents, scripts and checks…', exact: true });
    await search.fill('Review');
    await expect(menu.locator('.option:visible')).toHaveText(['Review · Agent']);
    await search.press('Enter');
    await expect(panel.locator('[data-lane-add-choice]')).toHaveValue('preset_review');
    await expect(picker.locator('img')).toHaveAttribute('src', 'assets/img/openai.svg');
    await picker.click();
    await search.fill('quality');
    await expect(page.locator('.launch-lane-dropdown .option:visible')).toHaveText(['Code quality check']);
    await page.screenshot({ path: testInfo.outputPath('lane-picker-search.png') });
    await search.press('Escape');
    await expect(panel).toBeVisible();
    await expect(page.locator('.ts-dropdown:visible')).toHaveCount(0);
    await picker.click();
    await search.fill('quality');
    await page.locator('.launch-lane-dropdown').getByRole('option', { name: 'Code quality check', exact: true }).click();
    await expect(picker.locator('img')).toHaveAttribute('src', 'assets/img/logo.png');
    await expect(picker.locator('img')).toHaveAttribute('alt', '');
    await expect(panel).toBeVisible();
    await expect(panel.locator('[data-lane-add]')).toBeEnabled();
    await panel.getByRole('button', { name: 'Close lane panel', exact: true }).click();
    await expect(panel).toHaveCount(0);
    await expect(page.locator('.launch-lane-dropdown')).toHaveCount(0);
    await expect(trigger).toBeFocused();
});

test('unified lane progress distinguishes process and review, retries and skips exact card entries',async({page})=>{
    await openBoard(page);const state=await installLaunchFixture(page);
    state.activity=[{cardLabel:'VIBE-1',definitionName:'Review',entry:{id:'entry_1',cardId:'card_test',definitionId:'preset_review',current:true,processStatus:'succeeded',verdict:null,gate:'review',summary:'Review not reported'},run:{id:'run_1',status:'succeeded'}}];
    await page.getByRole('button',{name:'Agents and Actions on entry to Ready',exact:true}).click();
    const panel=page.locator('.board-lane-agents-panel');
    await expect(panel).toContainText('Process: succeeded · Result: Not reported');
    await panel.getByRole('button',{name:'Retry',exact:true}).click();await expect.poll(()=>state.retries).toBe(1);
    await expect(panel).toContainText('Process: queued');await panel.getByRole('button',{name:'Stop and skip',exact:true}).click();
    await expect.poll(()=>state.skips).toBe(1);await expect(panel.getByRole('button',{name:'Stop and skip'})).toHaveCount(0);
});

test('unified manual card launch preserves title and comment drafts and prevents duplicate starts',async({page})=>{
    await openBoard(page);const state=await installLaunchFixture(page);
    let release;state.hold=new Promise(resolve=>{release=resolve;});
    await page.getByText('Description images',{exact:true}).click();
    const editor=page.locator('[data-board-card-editor]');
    await editor.locator('#board-card-title').fill('Draft title');await editor.locator('[data-board-composer="comment"] textarea').fill('Draft comment');
    await editor.locator('[data-board-automation-choice]').selectOption('preset_review');
    const run=editor.locator('[data-board-run-automation]');await run.click();await run.evaluate(el=>el.click());expect(state.posts).toBe(1);release();
    await expect(editor.locator('[data-board-automation-message]')).toContainText('Run queued');
    expect(state.lastRun).toMatchObject({definitionId:'preset_review',definitionType:'preset'});
    await expect(editor.locator('#board-card-title')).toHaveValue('Draft title');
    await expect(editor.locator('[data-board-composer="comment"] textarea')).toHaveValue('Draft comment');
    await expect(editor.locator('[data-board-automation-runs]')).toContainText('queued');
});

for (const width of [320,375,768,1024]) test(`shared Board status switches remain draft fields at ${width}px`, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 900 });
    const requests = await openBoard(page, { assignee: 'base:codex' });
    await page.getByText('Description images', { exact: true }).click();
    const editor = page.locator('[data-board-card-editor]');
    const status = editor.getByRole('region', { name: 'Card status' });
    const attention = status.getByRole('switch', { name: 'Needs your attention' });
    const blocked = status.getByRole('switch', { name: 'Blocked' });
    await attention.check(); await blocked.check();
    expect(requests.filter(request => request.method === 'PUT' && request.path.endsWith('/card_test'))).toHaveLength(0);
    expect(await status.evaluate(el => Boolean(el.previousElementSibling.classList.contains('board-discussion')))).toBe(true);
    await status.scrollIntoViewIfNeeded();
    await page.screenshot({ path: testInfo.outputPath(`board-status-${width}.png`) });
    await expect(page.locator('.board-card-modal-dialog .btn-close')).toHaveCSS('filter', 'none');
    await editor.locator('[data-board-save-card]').click();
    await expect(editor).toHaveCount(0);
    expect(requests.find(request => request.method === 'PUT' && request.path.endsWith('/card_test')).body).toMatchObject({ flagged: true, blocked: true });
});
