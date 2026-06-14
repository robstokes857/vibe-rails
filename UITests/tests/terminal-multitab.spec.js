// @ts-check
//
// E2E coverage for the terminal multi-tab UI (VibeRails/wwwroot/js/modules/terminal-multitab.js).
// Backend is spawned with VIBERAILS_TEST_FAKE_CLI=1 so each "session" is a portable
// `echo VIBERAILS_FAKE_CLI_READY:<llm>; sleep 600` running under the real PTY+WS+xterm path.
//
// What we cover here:
//   1. Tab creation via dashboard launch button → tab item + xterm output appear
//   2. Tab creation via the in-strip add button → second tab appears, two coexist
//   3. Multi-tab isolation → output from tab A doesn't leak into tab B
//   4. Tab close → enters pending-close state; kill-X in undo dropdown commits the DELETE
//   5. Reload → the active tab persists across full page reload (sessionStorage path)

const { test, expect, selectors } = require('./fixtures');

const FAKE_MARKER = /VIBERAILS_FAKE_CLI_READY/;

async function navigateToDashboard(page) {
    await page.goto('/');
    await page.getByRole('button', { name: 'Dashboard' }).click();
    await expect(page.locator(selectors.launchWebTerminalButton('codex')).first())
        .toBeVisible({ timeout: 15_000 });
}

async function launchWebTerminal(page, cli) {
    await page.locator(selectors.launchWebTerminalButton(cli)).first().click();
    // The dashboard switches to the terminal view as a side effect of startTerminal.
    await expect(page.locator(selectors.tabItems)).toHaveCount(1, { timeout: 10_000 });
}

async function waitForFakeCliReady(page, tabId) {
    const panel = page.locator(selectors.tabPanel(tabId));
    await expect(panel.locator('.xterm-rows')).toContainText(FAKE_MARKER, { timeout: 30_000 });
}

test.describe('terminal-multitab', () => {
    test('clicking a launch-web-terminal button creates a tab and runs the fake CLI', async ({ page }) => {
        await navigateToDashboard(page);
        await launchWebTerminal(page, 'codex');

        const tabId = await page.locator(selectors.tabItems)
            .first().getAttribute('data-tab-id');
        expect(tabId, 'first tab should have a data-tab-id').toBeTruthy();

        await waitForFakeCliReady(page, /** @type {string} */(tabId));
    });

    test('the in-strip + button opens a second tab independent of the first', async ({ page }) => {
        await navigateToDashboard(page);
        await launchWebTerminal(page, 'codex');

        const firstTabId = await page.locator(selectors.tabItems)
            .first().getAttribute('data-tab-id');
        await waitForFakeCliReady(page, /** @type {string} */(firstTabId));

        await page.locator(selectors.tabAddBtn).click();
        await expect(page.locator(selectors.tabItems)).toHaveCount(2, { timeout: 10_000 });
    });

    test('empty tabs cannot be minimized or closed before a session is started', async ({ page }) => {
        await navigateToDashboard(page);
        await launchWebTerminal(page, 'codex');

        const firstTabId = await page.locator(selectors.tabItems)
            .first().getAttribute('data-tab-id');
        await waitForFakeCliReady(page, /** @type {string} */(firstTabId));

        await page.locator(selectors.tabAddBtn).click();
        await expect(page.locator(selectors.tabItems)).toHaveCount(2, { timeout: 10_000 });

        const tabIds = await page.locator(selectors.tabItems)
            .evaluateAll((els) => els.map((el) => el.getAttribute('data-tab-id')));
        const emptyTabId = tabIds.find((id) => id && id !== firstTabId);
        expect(emptyTabId).toBeTruthy();

        await page.locator(selectors.tabItem(/** @type {string} */(emptyTabId))).hover();
        await expect(page.locator(selectors.tabMinimize(/** @type {string} */(emptyTabId)))).toBeHidden();
        await expect(page.locator(selectors.tabClose(/** @type {string} */(emptyTabId)))).toBeHidden();
    });

    test('closing a tab enters pending-close; kill-X in undo dropdown commits the DELETE', async ({ page }) => {
        await navigateToDashboard(page);
        await launchWebTerminal(page, 'codex');

        const tabId = /** @type {string} */ (
            await page.locator(selectors.tabItems).first().getAttribute('data-tab-id')
        );
        await waitForFakeCliReady(page, tabId);

        // Track DELETE calls for this tab so we can assert it does NOT fire on the
        // initial close (pending-close grace window) and DOES fire when the user
        // hits the per-row red X in the undo dropdown.
        let deleteRequestCount = 0;
        page.on('request', (req) => {
            if (req.method() === 'DELETE' &&
                req.url().includes(`/api/v1/terminal/tabs/${encodeURIComponent(tabId)}`)) {
                deleteRequestCount++;
            }
        });

        // 1. Click close → tab item is hidden (still in DOM) and the undo wrapper appears.
        // The action cluster (rename/minimize/close) is hover-revealed, so hover
        // the tab first to make the close button interactable.
        await page.locator(selectors.tabItem(tabId)).hover();
        await page.locator(selectors.tabClose(tabId)).click();
        await expect(page.locator(selectors.tabItem(tabId))).toBeHidden({ timeout: 5_000 });
        await expect(page.locator(selectors.undoWrapper)).toBeVisible();
        expect(deleteRequestCount, 'pending-close must not fire DELETE during the grace window').toBe(0);

        // 2. Open the undo dropdown and click the kill-X for that tab → commit DELETE.
        await page.locator(selectors.undoBtn).click();
        await page.locator(selectors.undoDropdownKill(tabId)).click();

        await expect(page.locator(selectors.tabItem(tabId)))
            .toHaveCount(0, { timeout: 5_000 });
        expect(deleteRequestCount, 'kill-X must fire exactly one DELETE').toBe(1);
    });

    test('output from one tab does not leak into another tab', async ({ page }) => {
        await navigateToDashboard(page);

        await launchWebTerminal(page, 'codex');
        const firstTabId = await page.locator(selectors.tabItems)
            .first().getAttribute('data-tab-id');
        await waitForFakeCliReady(page, /** @type {string} */(firstTabId));

        await page.locator(selectors.tabAddBtn).click();
        await expect(page.locator(selectors.tabItems)).toHaveCount(2);

        const tabIds = await page.locator(selectors.tabItems)
            .evaluateAll((els) => els.map((el) => el.getAttribute('data-tab-id')));
        const secondTabId = tabIds.find((id) => id && id !== firstTabId);
        expect(secondTabId).toBeTruthy();

        // The fake CLI prints once and sleeps; the second tab won't have output until
        // a CLI is selected, so we narrow this to the legitimate isolation check we
        // can make: the first tab's panel still contains its own marker after a
        // sibling tab is created. Real cross-tab leak would show the marker in the
        // second tab's panel, which we assert against below.
        const firstPanelText = await page.locator(selectors.tabPanel(/** @type {string} */(firstTabId)))
            .locator('.xterm-rows').innerText();
        expect(firstPanelText).toMatch(FAKE_MARKER);

        const secondPanelText = await page.locator(selectors.tabPanel(/** @type {string} */(secondTabId)))
            .locator('.xterm-rows').innerText().catch(() => '');
        expect(secondPanelText, 'second tab panel must not contain first tab fake-CLI marker')
            .not.toMatch(FAKE_MARKER);
    });

    test('reload preserves the active tab id', async ({ page }) => {
        await navigateToDashboard(page);
        await launchWebTerminal(page, 'codex');

        const firstTabId = await page.locator(selectors.tabItems)
            .first().getAttribute('data-tab-id');
        expect(firstTabId).toBeTruthy();
        await waitForFakeCliReady(page, /** @type {string} */(firstTabId));

        const sessionStored = await page.evaluate(() =>
            window.sessionStorage.getItem('viberails_terminal_active_tab_id')
        );
        expect(sessionStored).toBe(firstTabId);

        await page.reload();
        await expect(page.locator(selectors.tabItem(/** @type {string} */(firstTabId))))
            .toBeVisible({ timeout: 15_000 });
    });
});
