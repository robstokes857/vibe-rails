// @ts-check
//
// E2E coverage for the terminal multi-tab UI (VibeRails/wwwroot/js/modules/terminal-multitab.js).
// Backend is spawned with VIBERAILS_TEST_FAKE_CLI=1 so each "session" is a portable
// `echo VIBERAILS_FAKE_CLI_READY:<llm>; sleep 600` running under the real PTY+WS+xterm path.
//
// What we cover here:
//   1. Tab creation via the preserved Code quality page terminal → tab item + xterm output appear
//   2. Tab creation via the in-strip add button → second tab appears, two coexist
//   3. Multi-tab isolation → output from tab A doesn't leak into tab B
//   4. Tab close → enters pending-close state; kill-X in undo dropdown commits the DELETE
//   5. Reload → the active tab persists across full page reload (sessionStorage path)

const { test, expect, selectors } = require('./fixtures');

const FAKE_MARKER = /VIBERAILS_FAKE_CLI_READY/;

async function navigateToRules(page) {
    // The terminal dock lives on the Code quality page (the app's home view).
    await page.goto('/');
    await page.getByRole('button', { name: 'CODE QUALITY' }).click();
    await expect(page.locator('#terminal-header-select')).toBeAttached({ timeout: 15_000 });
    await expect(page.locator('#terminal-start-btn')).toBeVisible({ timeout: 15_000 });
    // The controls render before the manager finishes restoring/creating its
    // initial blank tab. Wait for that lifecycle boundary and the mounted picker
    // so updateUi() cannot overwrite the selection made by the test.
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

async function waitForFakeCliReady(page, tabId) {
    await expect.poll(() => readTerminalText(page, tabId), { timeout: 30_000 })
        .toMatch(FAKE_MARKER);
}

async function readTerminalText(page, tabId) {
    return page.evaluate((id) => {
        const tab = window.app?.terminalController?.manager?.tabs?.get(id);
        return tab?.instance?.vibeTerminal?.getPlainText?.() || '';
    }, tabId);
}

async function captureShiftEnterPayload(page, tabId, bracketedPasteEnabled) {
    return page.evaluate(async ({ id, enabled }) => {
        const tab = window.app?.terminalController?.manager?.tabs?.get(id);
        const instance = tab?.instance;
        const terminal = instance?.vibeTerminal;
        const socket = instance?.socket;
        if (!terminal || !socket) {
            throw new Error('Active terminal session was not available.');
        }

        await terminal.writeAsync(enabled ? '\x1b[?2004h' : '\x1b[?2004l');
        const payloads = [];
        const xtermInputs = [];
        const originalSend = socket.send;
        const originalInput = terminal.input;
        socket.send = (data) => payloads.push(data);
        terminal.input = (data, wasUserInput) => {
            xtermInputs.push({ data, wasUserInput });
            return originalInput.call(terminal, data, wasUserInput);
        };
        try {
            for (const type of ['keydown', 'keypress']) {
                terminal.textarea.dispatchEvent(new KeyboardEvent(type, {
                    key: 'Enter',
                    code: 'Enter',
                    shiftKey: true,
                    bubbles: true,
                    cancelable: true,
                }));
            }
        } finally {
            terminal.input = originalInput;
            socket.send = originalSend;
        }

        return {
            bracketedPasteEnabled: terminal.isBracketedPasteModeEnabled(),
            xtermInputs,
            payloads,
        };
    }, { id: tabId, enabled: bracketedPasteEnabled });
}

test.describe('terminal-multitab', () => {
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

    test('the Rules terminal creates a tab and runs the fake CLI', async ({ page }) => {
        await navigateToRules(page);
        await launchWebTerminal(page, 'codex');

        const tabId = await page.locator(selectors.tabItems)
            .first().getAttribute('data-tab-id');
        expect(tabId, 'first tab should have a data-tab-id').toBeTruthy();

        await waitForFakeCliReady(page, /** @type {string} */(tabId));
    });

    test('Codex Shift+Enter emits raw LF through xterm regardless of bracketed-paste state', async ({ page }) => {
        await navigateToRules(page);
        await launchWebTerminal(page, 'codex');

        const tabId = /** @type {string} */ (
            await page.locator(selectors.tabItems).first().getAttribute('data-tab-id')
        );
        await waitForFakeCliReady(page, tabId);

        const withoutBracketedPaste = await captureShiftEnterPayload(page, tabId, false);
        expect(withoutBracketedPaste.bracketedPasteEnabled).toBe(false);
        expect(withoutBracketedPaste.xtermInputs).toEqual([
            { data: '\n', wasUserInput: true },
        ]);
        expect(withoutBracketedPaste.payloads).toEqual(['\n']);

        const withBracketedPaste = await captureShiftEnterPayload(page, tabId, true);
        expect(withBracketedPaste.bracketedPasteEnabled).toBe(true);
        expect(withBracketedPaste.xtermInputs).toEqual([
            { data: '\n', wasUserInput: true },
        ]);
        expect(withBracketedPaste.payloads).toEqual(['\n']);
    });

    test('OpenCode-backed Shift+Enter keeps its bracketed-paste newline path', async ({ page }) => {
        await navigateToRules(page);
        await launchWebTerminal(page, 'glm-5.3');

        const tabId = /** @type {string} */ (
            await page.locator(selectors.tabItems).first().getAttribute('data-tab-id')
        );
        await waitForFakeCliReady(page, tabId);

        const withBracketedPaste = await captureShiftEnterPayload(page, tabId, true);
        expect(withBracketedPaste.bracketedPasteEnabled).toBe(true);
        expect(withBracketedPaste.xtermInputs).toEqual([]);
        expect(withBracketedPaste.payloads).toEqual(['\x1b[200~\n\x1b[201~']);
    });

    test('Grok paste is always bracketed and CRLF-normalized even when ?2004 is off', async ({ page }) => {
        await navigateToRules(page);
        await launchWebTerminal(page, 'grok-4.6');

        const tabId = /** @type {string} */ (
            await page.locator(selectors.tabItems).first().getAttribute('data-tab-id')
        );
        await waitForFakeCliReady(page, tabId);

        const grokPaste = await page.evaluate(async (id) => {
            const tab = window.app?.terminalController?.manager?.tabs?.get(id);
            const instance = tab?.instance;
            const terminal = instance?.vibeTerminal;
            const socket = instance?.socket;
            if (!instance || !terminal || !socket) {
                throw new Error('Active terminal session was not available.');
            }

            await terminal.writeAsync('\x1b[?2004l');
            const payloads = [];
            const originalSend = socket.send;
            socket.send = (data) => payloads.push(data);
            let oversizedAccepted;
            try {
                instance.injectText('‣ line one\r\nline two');
                oversizedAccepted = instance.injectText('x'.repeat(256 * 1024));
            } finally {
                socket.send = originalSend;
            }

            return {
                cli: tab.state.cli,
                bracketedPasteEnabled: terminal.isBracketedPasteModeEnabled(),
                oversizedAccepted,
                payloads,
            };
        }, tabId);

        expect((grokPaste.cli || '').toLowerCase()).toBe('grok-4.6');
        expect(grokPaste.bracketedPasteEnabled).toBe(false);
        expect(grokPaste.oversizedAccepted).toBe(false);
        expect(grokPaste.payloads).toEqual(['\x1b[200~‣ line one\nline two\x1b[201~']);
    });

    test('the in-strip + button opens a second tab independent of the first', async ({ page }) => {
        await navigateToRules(page);
        await launchWebTerminal(page, 'codex');

        const firstTabId = await page.locator(selectors.tabItems)
            .first().getAttribute('data-tab-id');
        await waitForFakeCliReady(page, /** @type {string} */(firstTabId));

        await page.locator(selectors.tabAddBtn).click();
        await expect(page.locator(selectors.tabItems)).toHaveCount(2, { timeout: 10_000 });
    });

    test('empty tabs cannot be minimized but remain dismissible', async ({ page }) => {
        await navigateToRules(page);
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
        await expect(page.locator(selectors.tabClose(/** @type {string} */(emptyTabId)))).toBeVisible();
    });

    test('closing a tab enters pending-close; kill-X in undo dropdown commits the DELETE', async ({ page }) => {
        await navigateToRules(page);
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
        await navigateToRules(page);

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
        const firstPanelText = await readTerminalText(page, /** @type {string} */(firstTabId));
        expect(firstPanelText).toMatch(FAKE_MARKER);

        const secondPanelText = await readTerminalText(page, /** @type {string} */(secondTabId));
        expect(secondPanelText, 'second tab panel must not contain first tab fake-CLI marker')
            .not.toMatch(FAKE_MARKER);
    });

    test('reload preserves the active tab id', async ({ page }) => {
        await navigateToRules(page);
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
