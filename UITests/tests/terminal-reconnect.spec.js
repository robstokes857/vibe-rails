// @ts-check
//
// E2E coverage for terminal auto-reconnect (terminal-reconnect.js wired into
// terminal-tab.js / terminal-multitab.js). Backend spawned with
// VIBERAILS_TEST_FAKE_CLI=1, so each "session" is `echo VIBERAILS_FAKE_CLI_READY:<llm>;
// sleep 600` under the real PTY + WS + xterm path. The fake runs inside an
// interactive pwsh, so the buffer holds the marker twice (the echoed command line
// and its output) — which is why "no duplicate replay" is asserted as "the replayed
// text equals the live text", never as a marker count.
//
// What we cover here:
//   1. Navigate away and back → every restored tab reconnects, not just the active
//      one; the background tab was pinned to the visible tab's geometry and, once
//      activated, shows exactly the buffer it had before navigation.
//   2. Kill switch (localStorage viberails_terminal_autoReconnect=off) → only the
//      active tab reconnects and tab selection stays UI-only (Connect button).
//   3. A socket that drops on its own reconnects by itself within the first delay.
//   4. A "Session taken over" close is never retried — the Connect button waits.

const { test, expect, selectors } = require('./fixtures');

const FAKE_MARKER = /VIBERAILS_FAKE_CLI_READY/;
const AUTO_RECONNECT_KEY = 'viberails_terminal_autoReconnect';

async function openTerminals(page) {
    await page.goto('/');
    await expect(page.locator('.view[data-view="terminal-focus"]')).toBeVisible({ timeout: 15_000 });
    await expect(page.locator('#terminal-header-select')).toBeAttached({ timeout: 15_000 });
    await expect(page.locator('#terminal-start-btn')).toBeVisible({ timeout: 15_000 });
    await expect(page.locator(selectors.tabItems)).toHaveCount(1, { timeout: 15_000 });
    await expect.poll(() => page.evaluate(() =>
        !!document.querySelector('#terminal-header-select')?.tomselect
    ), { timeout: 15_000 }).toBe(true);
}

// Launch `cli` in whichever tab is active (the blank tab the manager created, or
// the one the add button just opened) and return its tab id.
async function launchInActiveTab(page, cli) {
    const selector = page.locator('#terminal-header-select');
    const selection = `base:${cli}`;
    await selector.evaluate((select, value) => {
        window.app?.llmPickerController?.setValue(select, value);
    }, selection);
    await expect.poll(() => selector.evaluate(select =>
        select.tomselect?.getValue?.() || select.value
    )).toBe(selection);
    await page.locator('#terminal-start-btn').click();
    await expect.poll(() => page.evaluate(() =>
        window.app?.terminalController?.manager?.getActiveTab()?.state?.hasActiveSession === true
    ), { timeout: 15_000 }).toBe(true);
    const tabId = await page.evaluate(() => window.app?.terminalController?.manager?.activeTabId || null);
    expect(tabId).toBeTruthy();
    return /** @type {string} */ (tabId);
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

// The live buffer once the fake CLI has printed and gone quiet. A reconnect must
// replay exactly this — same lines, same wrapping — or the snapshot/geometry path
// has regressed (double print, wrong pinned width, lost scrollback).
async function settledTerminalText(page, tabId) {
    await waitForFakeCliReady(page, tabId);
    let previous = await readTerminalText(page, tabId);
    for (let i = 0; i < 10; i += 1) {
        await page.waitForTimeout(400);
        const next = await readTerminalText(page, tabId);
        if (next === previous) return next;
        previous = next;
    }
    return previous;
}

async function readTabProbe(page, tabId) {
    return page.evaluate((id) => {
        const manager = window.app?.terminalController?.manager;
        const tab = manager?.tabs?.get(id);
        if (!tab) return null;
        const item = document.querySelector(`#vb-terminal-tab-list .vb-terminal-tab-item[data-tab-id="${id}"]`);
        return {
            open: !!tab.instance.hasOpenSocket(),
            status: tab.state.status,
            cols: tab.instance.vibeTerminal?.cols ?? null,
            rows: tab.instance.vibeTerminal?.rows ?? null,
            resizeSignature: tab.instance.lastResizeSignature,
            attempts: tab.instance.autoReconnect?.attempts ?? null,
            pending: tab.instance.autoReconnect?.isPending?.() ?? null,
            isActive: manager.activeTabId === id,
            chipDisconnected: !!item?.classList.contains('is-disconnected'),
            statusDisconnected: !!item?.classList.contains('tab-status-disconnected')
        };
    }, tabId);
}

async function navigateTo(page, view) {
    await page.evaluate((target) => {
        window.app.navigate(target, {}, { resetStack: true });
    }, view);
}

async function leaveAndReturnToTerminals(page) {
    await navigateTo(page, 'environments');
    await expect(page.locator('.view[data-view="terminal-focus"]')).toHaveCount(0, { timeout: 15_000 });
    await navigateTo(page, 'terminal-focus');
    await expect(page.locator('.view[data-view="terminal-focus"]')).toBeVisible({ timeout: 15_000 });
}

test.describe('terminal auto-reconnect', () => {
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

    test('navigating away and back reconnects every tab, and the background tab replays once at the visible geometry', async ({ page }) => {
        await openTerminals(page);
        const firstTabId = await launchInActiveTab(page, 'codex');
        const firstLiveText = await settledTerminalText(page, firstTabId);

        await page.locator(selectors.tabAddBtn).click();
        await expect(page.locator(selectors.tabItems)).toHaveCount(2, { timeout: 10_000 });
        const secondTabId = await launchInActiveTab(page, 'claude');
        expect(secondTabId).not.toBe(firstTabId);
        const secondLiveText = await settledTerminalText(page, secondTabId);
        expect(firstLiveText).not.toBe(secondLiveText);

        // The second tab is active; the first is the background tab.
        await leaveAndReturnToTerminals(page);
        await expect(page.locator(selectors.tabItems)).toHaveCount(2, { timeout: 15_000 });

        // Active tab first (as before), then the background tab without any click.
        await expect.poll(async () => (await readTabProbe(page, secondTabId))?.open, { timeout: 20_000 }).toBe(true);
        await expect.poll(async () => (await readTabProbe(page, firstTabId))?.open, { timeout: 20_000 }).toBe(true);

        const active = await readTabProbe(page, secondTabId);
        const background = await readTabProbe(page, firstTabId);
        expect(active?.isActive).toBe(true);
        expect(background?.isActive).toBe(false);
        expect(background?.status).toBe('connected');
        expect(background?.chipDisconnected).toBe(false);
        expect(background?.statusDisconnected).toBe(false);
        // Pinned to the visible tab's grid: same xterm size, same PTY signature.
        expect(background?.cols).toBe(active?.cols);
        expect(background?.rows).toBe(active?.rows);
        expect(background?.resizeSignature).toBe(active?.resizeSignature);
        expect(background?.attempts).toBe(0);

        // The active tab's replay is byte-for-byte what was on screen before leaving.
        await expect.poll(() => readTerminalText(page, secondTabId), { timeout: 15_000 }).toBe(secondLiveText);

        // Activating the background tab shows exactly the buffer it had before
        // navigation (replayed once, at the pinned width) and keeps the socket —
        // a fit at the same geometry is not a reconnect.
        await page.locator(`${selectors.tabItem(firstTabId)} .vb-terminal-tab-button`).click();
        await expect.poll(() => readTerminalText(page, firstTabId), { timeout: 15_000 }).toBe(firstLiveText);
        const activated = await readTabProbe(page, firstTabId);
        expect(activated?.open).toBe(true);
        expect(activated?.isActive).toBe(true);
        expect(activated?.cols).toBe(active?.cols);
        expect(activated?.rows).toBe(active?.rows);
        await expect(page.locator('#terminal-stop-btn')).toBeHidden();
    });

    test('with auto-reconnect off only the active tab reconnects and tab selection stays UI-only', async ({ page }) => {
        await openTerminals(page);
        const firstTabId = await launchInActiveTab(page, 'codex');
        await waitForFakeCliReady(page, firstTabId);

        await page.locator(selectors.tabAddBtn).click();
        await expect(page.locator(selectors.tabItems)).toHaveCount(2, { timeout: 10_000 });
        const secondTabId = await launchInActiveTab(page, 'claude');
        await waitForFakeCliReady(page, secondTabId);

        await page.evaluate((key) => localStorage.setItem(key, 'off'), AUTO_RECONNECT_KEY);
        try {
            await leaveAndReturnToTerminals(page);
            await expect(page.locator(selectors.tabItems)).toHaveCount(2, { timeout: 15_000 });
            await expect.poll(async () => (await readTabProbe(page, secondTabId))?.open, { timeout: 20_000 }).toBe(true);

            // Longer than the background settle + gap: the first tab must still be parked.
            await page.waitForTimeout(3_500);
            const background = await readTabProbe(page, firstTabId);
            expect(background?.open).toBe(false);
            expect(background?.status).toBe('disconnected');
            expect(background?.chipDisconnected).toBe(true);

            // Selecting it is not a reconnect (2026-03-07): the Connect button appears instead.
            await page.locator(`${selectors.tabItem(firstTabId)} .vb-terminal-tab-button`).click();
            await expect(page.locator('#terminal-stop-btn')).toBeVisible({ timeout: 5_000 });
            await page.waitForTimeout(1_500);
            const selected = await readTabProbe(page, firstTabId);
            expect(selected?.isActive).toBe(true);
            expect(selected?.open).toBe(false);
            expect(selected?.status).toBe('disconnected');
        } finally {
            await page.evaluate((key) => localStorage.removeItem(key), AUTO_RECONNECT_KEY);
        }
    });

    test('a socket that drops on its own reconnects without a click', async ({ page }) => {
        await openTerminals(page);
        const tabId = await launchInActiveTab(page, 'codex');
        const liveText = await settledTerminalText(page, tabId);

        // Close the browser side behind the tab's back: the tab still holds this
        // socket, so its onclose runs exactly as it would for a dropped connection.
        await page.evaluate((id) => {
            const tab = window.app.terminalController.manager.tabs.get(id);
            tab.instance.socket.close();
        }, tabId);

        await expect.poll(async () => (await readTabProbe(page, tabId))?.status, { timeout: 5_000 }).toBe('disconnected');
        const parked = await readTabProbe(page, tabId);
        expect(parked?.pending, 'a retry is armed').toBe(true);
        expect(parked?.attempts).toBe(1);

        await expect.poll(async () => (await readTabProbe(page, tabId))?.open, { timeout: 15_000 }).toBe(true);
        await expect.poll(() => readTerminalText(page, tabId), { timeout: 15_000 }).toBe(liveText);
        const healed = await readTabProbe(page, tabId);
        expect(healed?.status).toBe('connected');
        expect(healed?.attempts, 'a successful open resets the budget').toBe(0);
        expect(healed?.pending).toBe(false);
        await expect(page.locator('#terminal-stop-btn')).toBeHidden();
    });

    test('a session taken over by another viewer is never auto-reconnected', async ({ page }) => {
        await openTerminals(page);
        const tabId = await launchInActiveTab(page, 'codex');
        await waitForFakeCliReady(page, tabId);

        // A second local viewer attaching to the same tab makes the child close the
        // first socket with "Session taken over" (single-viewer policy).
        await page.evaluate((id) => {
            const manager = window.app.terminalController.manager;
            const tab = manager.tabs.get(id);
            const token = tab.instance.getSafeTabTokenForWebSocket(sessionStorage.getItem('viberails_tab'));
            const intruder = new WebSocket(manager.getWebSocketUrl(id, 0, 0), token ? [token] : []);
            intruder.binaryType = 'arraybuffer';
            window.__vbIntruder = intruder;
        }, tabId);

        try {
            await expect.poll(async () => (await readTabProbe(page, tabId))?.status, { timeout: 10_000 }).toBe('disconnected');
            await expect.poll(() => readTerminalText(page, tabId), { timeout: 5_000 }).toMatch(/taken over/i);
            const taken = await readTabProbe(page, tabId);
            expect(taken?.pending, 'no retry may be armed after a takeover').toBe(false);
            expect(taken?.attempts).toBe(0);

            // Past the first retry delay: still parked, Connect button offered.
            await page.waitForTimeout(3_500);
            const still = await readTabProbe(page, tabId);
            expect(still?.open).toBe(false);
            expect(still?.status).toBe('disconnected');
            await expect(page.locator('#terminal-stop-btn')).toBeVisible();
        } finally {
            await page.evaluate(() => {
                try { window.__vbIntruder?.close(); } catch { /* no-op */ }
                delete window.__vbIntruder;
            });
        }
    });
});
