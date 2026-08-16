// @ts-check
//
// E2E coverage for the Multi Run modal (terminal-multirun.js) plus the shared
// TomSelect viewport-flip behavior added to enhanceLlmSelectWithTomSelect in
// utils.js. Backend is spawned with VIBERAILS_TEST_FAKE_CLI=1 so the launches
// run a portable echo+sleep instead of a real CLI.

const { test, expect, selectors, newApiContext } = require('./fixtures');

const PREFERENCES = '/api/v1/llm-picker/preferences';

// The per-test preference resets below hit MACHINE-WIDE state on the developer's real
// backend. Snapshot whatever the developer had before this file runs and put it back
// afterwards, so test isolation cannot destroy their saved ordering/visibility.
let savedPickerItems = null;

test.beforeAll(async ({ playwright }) => {
    const api = await newApiContext(playwright);
    try {
        const response = await api.get(PREFERENCES);
        if (response.ok()) savedPickerItems = (await response.json()).items;
    } finally {
        await api.dispose();
    }
});

test.afterAll(async ({ playwright }) => {
    const api = await newApiContext(playwright);
    try {
        if (savedPickerItems) {
            const restored = await api.put(PREFERENCES, { data: { items: savedPickerItems } });
            if (!restored.ok()) {
                console.warn(`[terminal-multirun spec] preference restore failed: ${restored.status()}`);
            }
        } else {
            await api.delete(PREFERENCES);
        }
    } catch (error) {
        console.warn('[terminal-multirun spec] preference restore failed:', error);
    } finally {
        await api.dispose();
    }
});

async function navigateToDashboard(page) {
    await page.goto('/');
    await expect(page.locator('#terminal-settings-btn')).toBeVisible({ timeout: 15_000 });
    // The terminal manager's init() asynchronously creates a placeholder tab
    // when the server has none — a visible gear doesn't mean init is done.
    // Wait for at least one tab item so initialCount reads consistently.
    await expect(page.locator(selectors.tabItems).first()).toBeVisible({ timeout: 10_000 });
}

async function openMultiRunModal(page) {
    // Multi Run lives in the settings panel's Actions block (the old kebab
    // dropdown was merged into it). Opening the panel animates a 0.3s slide;
    // Playwright's actionability checks wait for the button to be stable.
    await page.locator('#terminal-settings-btn').click();
    await page.locator('#terminal-multirun-btn').click();
    await page.waitForSelector('#vb-multirun-input', { timeout: 5_000 });
}

// Clean tab state before each test. Tabs persist in the backend across test
// boundaries (only the browser context is reset), so a previous test's leftover
// tabs would throw off `toHaveCount(N)` expectations.
test.beforeEach(async ({ context }) => {
    try {
        await context.request.delete(PREFERENCES);
        const res = await context.request.get('/api/v1/terminal/tabs');
        if (res.ok()) {
            const data = await res.json();
            for (const t of (data.tabs || [])) {
                if (t?.tabId) {
                    await context.request.delete(`/api/v1/terminal/tabs/${encodeURIComponent(t.tabId)}`);
                }
            }
        }
    } catch {
        // Best-effort cleanup; tests will fail loudly if state is wrong.
    }
});

test.afterEach(async ({ context }) => {
    try {
        await context.request.delete(PREFERENCES);
    } catch {
        // Best-effort global preference isolation; afterAll restores the developer's state.
    }
});

test.describe('terminal-multirun', () => {

    test('Multi Run modal renders two CLI selects, a textarea, and a Run button', async ({ page }) => {
        await navigateToDashboard(page);
        await openMultiRunModal(page);

        await expect(page.locator('#vb-multirun-cli-1')).toHaveCount(1);
        await expect(page.locator('#vb-multirun-cli-2')).toHaveCount(1);
        await expect(page.locator('#vb-multirun-input')).toBeVisible();
        await expect(page.locator('#vb-multirun-run-btn')).toBeVisible();

        // Both selects are TomSelect-enhanced — the official API attaches a
        // .tomselect property pointing at the controller, with .wrapper for the
        // visible DOM. Checking both is more durable than depending on whether
        // the wrapper lands as a sibling vs an ancestor.
        const enhanced = await page.evaluate(() => {
            const s1 = document.getElementById('vb-multirun-cli-1');
            const s2 = document.getElementById('vb-multirun-cli-2');
            return {
                ts1: !!s1?.tomselect?.wrapper,
                ts2: !!s2?.tomselect?.wrapper,
            };
        });
        expect(enhanced.ts1).toBe(true);
        expect(enhanced.ts2).toBe(true);

        // Defaults match TerminalMultiRun's mount: Claude on the left, Codex on the right.
        const sel1Value = await page.evaluate(() =>
            document.getElementById('vb-multirun-cli-1').tomselect.getValue());
        const sel2Value = await page.evaluate(() =>
            document.getElementById('vb-multirun-cli-2').tomselect.getValue());
        expect(sel1Value).toBe('base:claude');
        expect(sel2Value).toBe('base:codex');
    });

    test('Multi Run dropdowns expose every base LLM and nothing else', async ({ page }) => {
        await navigateToDashboard(page);
        await openMultiRunModal(page);

        const optionValues = await page.evaluate(() => {
            const sel = document.getElementById('vb-multirun-cli-1');
            return Array.from(sel.querySelectorAll('option'))
                .map((o) => o.value)
                .filter((v) => v); // drop the blank placeholder if any
        });

        expect(optionValues.sort()).toEqual(
            [
                'base:claude',
                'base:codex',
                'base:glm-5.2',
                'base:grok-4.6',
                'base:opencode',
                'base:copilot',
                'base:antigravity'
            ].sort()
        );
    });

    test('LLM dropdown has an accessible search box that filters options', async ({ page }) => {
        await navigateToDashboard(page);
        await openMultiRunModal(page);

        const select = page.locator('#vb-multirun-cli-1');
        await select.evaluate((element) => element.tomselect.open());

        const search = page.locator('.ts-dropdown.plugin-dropdown_input .dropdown-input:visible');
        await expect(search).toBeVisible();
        await expect(search).toHaveAttribute('aria-label', 'Search LLMs...');
        await search.fill('copilot');

        const visibleOptions = page.locator('.ts-dropdown.plugin-dropdown_input:visible .option:visible');
        await expect(visibleOptions).toHaveCount(1);
        await expect(visibleOptions.first()).toContainText('Copilot');
    });

    test('Run with empty prompt shows error and does not start sessions', async ({ page }) => {
        await navigateToDashboard(page);
        // Read tab count after panel renders rather than asserting an absolute number —
        // beforeEach cleanup is best-effort and the panel auto-creates a placeholder.
        const initialCount = await page.locator(selectors.tabItems).count();

        let startCallCount = 0;
        page.on('request', (req) => {
            if (req.method() === 'POST' && /\/api\/v1\/terminal\/tabs\/[^/]+\/start$/.test(req.url())) {
                startCallCount++;
            }
        });

        await openMultiRunModal(page);
        await page.locator('#vb-multirun-input').fill('   '); // whitespace only → trim() empty
        await page.locator('#vb-multirun-run-btn').click();

        // Modal stays open. Settling delay so any in-flight POST has time to register.
        await expect(page.locator('#vb-multirun-input')).toBeVisible();
        await page.waitForTimeout(500);

        expect(startCallCount).toBe(0);
        await expect(page.locator(selectors.tabItems)).toHaveCount(initialCount);
    });

    test('Run with two CLIs and a prompt creates two tabs and forwards initialPrompt to /start', async ({ page }) => {
        await navigateToDashboard(page);
        const initialCount = await page.locator(selectors.tabItems).count();

        const startBodies = [];
        page.on('request', (req) => {
            if (req.method() === 'POST' && /\/api\/v1\/terminal\/tabs\/[^/]+\/start$/.test(req.url())) {
                try { startBodies.push(req.postDataJSON()); } catch { /* no-op */ }
            }
        });

        const PROMPT = 'Make me a cat photo';
        await openMultiRunModal(page);
        await page.locator('#vb-multirun-input').fill(PROMPT);
        await page.locator('#vb-multirun-run-btn').click();

        // Modal closes once both launches succeed.
        await expect(page.locator('#vb-multirun-input')).toHaveCount(0, { timeout: 20_000 });

        // Multi Run added two new tabs on top of whatever was already mounted.
        await expect(page.locator(selectors.tabItems)).toHaveCount(initialCount + 2, { timeout: 20_000 });

        expect(startBodies).toHaveLength(2);
        expect(startBodies[0].initialPrompt).toBe(PROMPT);
        expect(startBodies[1].initialPrompt).toBe(PROMPT);
        // CLI order matches the picker order (left → right).
        expect(startBodies[0].cli).toBe('claude');
        expect(startBodies[1].cli).toBe('codex');
    });

    test('Ctrl+Enter in the textarea triggers Run without clicking the button', async ({ page }) => {
        await navigateToDashboard(page);
        const initialCount = await page.locator(selectors.tabItems).count();

        const startBodies = [];
        page.on('request', (req) => {
            if (req.method() === 'POST' && /\/api\/v1\/terminal\/tabs\/[^/]+\/start$/.test(req.url())) {
                try { startBodies.push(req.postDataJSON()); } catch { /* no-op */ }
            }
        });

        await openMultiRunModal(page);
        await page.locator('#vb-multirun-input').fill('test prompt');
        // Dispatch the keydown directly. Playwright's locator.press('Control+Enter')
        // sometimes lands without the ctrlKey modifier flag depending on focus
        // bookkeeping, which causes the handler's `event.ctrlKey` check to miss.
        await page.evaluate(() => {
            const ta = document.getElementById('vb-multirun-input');
            ta.focus();
            ta.dispatchEvent(new KeyboardEvent('keydown', {
                key: 'Enter', code: 'Enter', ctrlKey: true, bubbles: true, cancelable: true,
            }));
        });

        await expect(page.locator(selectors.tabItems)).toHaveCount(initialCount + 2, { timeout: 20_000 });
        expect(startBodies).toHaveLength(2);
    });

});

test.describe('TomSelect dropdown viewport-flip', () => {

    // Re-parent the wrapper to <body> before pinning. Bootstrap's modal-dialog has a
    // transform that turns descendant `position: fixed` into "fixed within the modal"
    // — we need viewport-relative positioning for the helper's measurements to reflect
    // a real edge-of-screen scenario.
    test('adds .ts-dropdown-flipped when wrapper sits at the bottom edge of the viewport', async ({ page }) => {
        await navigateToDashboard(page);
        await openMultiRunModal(page);

        const result = await page.evaluate(async () => {
            const sel = document.getElementById('vb-multirun-cli-2');
            const ts = sel.tomselect;
            const wrapper = ts.wrapper;

            document.body.appendChild(wrapper);
            wrapper.style.position = 'fixed';
            wrapper.style.left = '20px';
            wrapper.style.bottom = '0';
            wrapper.style.width = '300px';
            wrapper.style.zIndex = '99999';

            ts.close();
            ts.open();
            await new Promise((r) => requestAnimationFrame(r));

            const flipped = ts.dropdown?.classList.contains('ts-dropdown-flipped');
            const rect = wrapper.getBoundingClientRect();
            return { flipped, spaceBelow: window.innerHeight - rect.bottom };
        });

        expect(result.spaceBelow, 'wrapper should reach the viewport bottom').toBeLessThanOrEqual(1);
        expect(result.flipped, 'helper should add .ts-dropdown-flipped').toBe(true);
    });

    test('does not flip when there is plenty of room below', async ({ page }) => {
        await navigateToDashboard(page);
        await openMultiRunModal(page);

        const flipped = await page.evaluate(async () => {
            const sel = document.getElementById('vb-multirun-cli-1');
            const ts = sel.tomselect;
            const wrapper = ts.wrapper;

            document.body.appendChild(wrapper);
            wrapper.style.position = 'fixed';
            wrapper.style.left = '20px';
            wrapper.style.top = '0';
            wrapper.style.width = '300px';
            wrapper.style.zIndex = '99999';

            ts.close();
            ts.open();
            await new Promise((r) => requestAnimationFrame(r));

            return ts.dropdown?.classList.contains('ts-dropdown-flipped');
        });

        expect(flipped).toBe(false);
    });
});
