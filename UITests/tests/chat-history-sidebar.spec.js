// @ts-check
//
// E2E coverage for the Chat History sidebar keeping pace with the session lifecycle
// (VibeRails/wwwroot/js/modules/chat-history-sidebar.js plus the hooks in
// terminal-multitab.js). The sidebar used to fetch only on mount and on its refresh
// button, so a session started from the very view it sits in never showed up until the
// user left and came back — and a session with no prompt yet was filtered out entirely.
//
// Backend is spawned with VIBERAILS_TEST_FAKE_CLI=1 (see terminal-multitab.spec.js), so a
// "session" is `echo VIBERAILS_FAKE_CLI_READY:<llm>; sleep 600` under the real PTY path.
// Typed input still reaches the InputAccumulator, so Enter records a UserInputs row and
// publishes the session_input event exactly as a real CLI would.

const { test, expect, selectors } = require('./fixtures');

const FAKE_MARKER = /VIBERAILS_FAKE_CLI_READY/;

async function navigateToTerminals(page) {
    await page.goto('/');
    await expect(page.locator('.view[data-view="terminal-focus"]')).toBeVisible({ timeout: 15_000 });
    await expect(page.locator('#terminal-header-select')).toBeAttached({ timeout: 15_000 });
    await expect(page.locator('#terminal-start-btn')).toBeVisible({ timeout: 15_000 });
    await expect(page.locator(selectors.tabItems)).toHaveCount(1, { timeout: 15_000 });
    await expect.poll(() => page.evaluate(() =>
        !!document.querySelector('#terminal-header-select')?.tomselect
    ), { timeout: 15_000 }).toBe(true);
}

async function launchWebTerminal(page, cli) {
    const selector = page.locator('#terminal-header-select');
    const selection = `base:${cli}`;
    await selector.evaluate((select, value) => {
        window.app?.llmPickerController?.setValue(select, value);
    }, selection);
    await expect.poll(() => selector.evaluate(select =>
        select.tomselect?.getValue?.() || select.value
    )).toBe(selection);
    await page.locator('#terminal-start-btn').click();
    await expect(page.locator(selectors.tabItems)).toHaveCount(1, { timeout: 10_000 });
    await expect.poll(() => page.evaluate(() =>
        window.app?.terminalController?.manager?.getActiveTab()?.state?.hasActiveSession === true
    ), { timeout: 15_000 }).toBe(true);
}

async function readTerminalText(page, tabId) {
    return page.evaluate((id) => {
        const tab = window.app?.terminalController?.manager?.tabs?.get(id);
        return tab?.instance?.vibeTerminal?.getPlainText?.() || '';
    }, tabId);
}

async function waitForFakeCliReady(page, tabId) {
    await expect.poll(() => readTerminalText(page, tabId), { timeout: 30_000 })
        .toMatch(FAKE_MARKER);
}

async function readActiveSessionId(page) {
    return page.evaluate(() =>
        window.app?.terminalController?.manager?.getActiveTab()?.state?.sessionId || null
    );
}

function historyRow(page, sessionId) {
    return page.locator(`#ch-sidebar .ch-item[data-id="${sessionId}"]`);
}

test.describe('chat-history-sidebar', () => {
    test.beforeEach(async ({ context }) => {
        const response = await context.request.get('/api/v1/terminal/tabs');
        if (!response.ok()) return;
        const data = await response.json();
        for (const tab of (data.tabs || [])) {
            if (tab?.tabId) {
                await context.request.delete(`/api/v1/terminal/tabs/${encodeURIComponent(tab.tabId)}`);
            }
        }
    });

    test('a session started from the Terminals view appears, gets named by its first prompt, and ends — without a manual refresh', async ({ page }) => {
        // Open the sidebar up front so the expanded list, not just the collapsed rail, is under test.
        await page.addInitScript(() => {
            localStorage.setItem('viberails_history_sidebar_open', '1');
            localStorage.setItem('viberails_history_sidebar_seen', '1');
        });
        await navigateToTerminals(page);
        await expect(page.locator('#ch-sidebar')).not.toHaveClass(/ch-sidebar-collapsed/);

        // The sidebar's own refresh button must stay untouched for the whole test; every
        // update below has to come from the lifecycle hooks.
        let refreshClicks = 0;
        await page.exposeFunction('__countHistoryRefreshClick', () => { refreshClicks += 1; });
        await page.evaluate(() => {
            document.getElementById('ch-sidebar-refresh-btn')
                ?.addEventListener('click', () => window.__countHistoryRefreshClick());
        });

        await launchWebTerminal(page, 'claude');
        const tabId = /** @type {string} */ (
            await page.locator(selectors.tabItems).first().getAttribute('data-tab-id')
        );
        await waitForFakeCliReady(page, tabId);
        const sessionId = /** @type {string} */ (await readActiveSessionId(page));
        expect(sessionId, 'the started tab must expose its session id').toBeTruthy();

        // 1. A live placeholder row shows up on its own, named after the CLI.
        const row = historyRow(page, sessionId);
        await expect(row).toBeVisible({ timeout: 15_000 });
        await expect(row).toHaveClass(/ch-item-active/);
        await expect(row.locator('.ch-item-name')).toHaveClass(/ch-item-name-placeholder/);
        await expect(row.locator('.ch-item-name')).toHaveText(/New Claude session/);

        // 2. The first submitted prompt names the row.
        const prompt = 'hello from the sidebar spec';
        await page.locator('.xterm-helper-textarea').first().focus();
        await page.keyboard.type(prompt);
        await page.keyboard.press('Enter');
        await expect(row.locator('.ch-item-name')).toHaveText(prompt, { timeout: 15_000 });
        await expect(row.locator('.ch-item-name')).not.toHaveClass(/ch-item-name-placeholder/);
        await expect(row).toHaveClass(/ch-item-active/);

        // 3. Committing the tab close ends the session; the row flips from Live to Ended
        //    even though the child's own session_completed event is lost to the DELETE.
        await page.locator(selectors.tabItem(tabId)).hover();
        await page.locator(selectors.tabClose(tabId)).click();
        await expect(page.locator(selectors.undoWrapper)).toBeVisible();
        await page.locator(selectors.undoBtn).click();
        await page.locator(selectors.undoDropdownKill(tabId)).click();
        await expect(page.locator(selectors.tabItem(tabId))).toHaveCount(0, { timeout: 5_000 });

        await expect(row).not.toHaveClass(/ch-item-active/, { timeout: 20_000 });
        await expect(row.locator('.ch-meta-label')).toHaveText(/Ended/);
        await expect(row.locator('.ch-item-name')).toHaveText(prompt);

        expect(refreshClicks, 'the manual refresh button must never have been used').toBe(0);
    });
});
