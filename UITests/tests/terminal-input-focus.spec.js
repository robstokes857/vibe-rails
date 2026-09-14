const { test, expect } = process.env.VIBERAILS_TERMINAL_INPUT_STATIC === '1'
    ? require('@playwright/test')
    : require('./fixtures');
const CODEX_ESCAPE = '__cmd__:escape';

// Real DOM, xterm, Monaco and physical key events, with an intercepted WS peer.
// This proves browser input delivery only, not ConPTY or Codex interruption.
async function openTerminal(page, cli = 'codex') {
    const received = [];
    const tab = { tabId: 'escape-fixture', sessionId: 'fixture-session', cli,
        hasActiveSession: true, workingDirectory: 'C:/terminal-fixture' };
    await page.addInitScript(() => sessionStorage.setItem('viberails_tab', 'terminal-fixture'));
    await page.routeWebSocket('**/api/v1/events/ws*', () => {});
    await page.routeWebSocket('**/api/v1/terminal/tabs/*/ws*', socket => {
        socket.onMessage(message => received.push(message));
        socket.send(Buffer.from('Terminal input fixture\r\n'));
    });
    await page.route('**/api/v1/**', route => {
        const path = new URL(route.request().url()).pathname;
        const responses = {
            '/api/v1/context': { isInGit: true, rootPath: 'C:/terminal-fixture', launchDirectory: 'C:/terminal-fixture' },
            '/api/v1/settings': {},
            '/api/v1/environments': { environments: [] },
            '/api/v1/agents': { agents: [] },
            '/api/v1/sandboxes': { sandboxes: [] },
            '/api/v1/llm-picker/preferences': { items: [] },
            '/api/v1/terminal/tabs': { tabs: [tab], maxTabs: 8 },
            '/api/v1/terminal/tabs/escape-fixture/status': tab
        };
        return route.fulfill({ json: responses[path] || {} });
    });
    await page.goto('/');
    await expect(page.locator('.xterm-helper-textarea')).toBeAttached();
    await expect.poll(() => page.evaluate(() =>
        window.app?.terminalController?.manager?.getActiveTab()?.instance?.hasOpenSocket()
    )).toBe(true);
    await expect(page.locator('#loading-overlay')).toHaveClass(/\bd-none\b/);
    // Let the connection's documented 180ms retry complete before testing a later action.
    await expect.poll(() => page.evaluate(() =>
        window.app.terminalController.manager.getActiveTab().instance._initialConnectActive
    )).toBe(false);
    await page.waitForTimeout(220);
    await page.locator('.xterm-screen').click();
    return received;
}

async function openEditor(page) {
    await page.getByRole('button', { name: 'Open in text editor', exact: true }).click();
    await expect(page.locator('.vb-terminal-editor-modal .monaco-editor')).toBeVisible();
    await expect.poll(() => page.evaluate(() =>
        !!document.activeElement?.closest('.monaco-editor')
    )).toBe(true);
}

async function expectEscapeDelivered(page, received) {
    const before = received.filter(message => message === CODEX_ESCAPE).length;
    await page.keyboard.press('Escape');
    await expect.poll(() => received.filter(message => message === CODEX_ESCAPE).length).toBe(before + 1);
}

test('physical Escape reaches the WebSocket peer from xterm', async ({ page }) => {
    const received = await openTerminal(page);
    await expectEscapeDelivered(page, received);
});

test('Codex Shift+Enter then Escape then typing keeps ordered, distinct input messages', async ({ page }) => {
    const received = await openTerminal(page);
    received.length = 0;
    await page.keyboard.press('Shift+Enter');
    await page.keyboard.press('Escape');
    await page.keyboard.type('x');
    await expect.poll(() => received.filter(message => !message.startsWith('__resize__:')))
        .toEqual(['\n', CODEX_ESCAPE, 'x']);
});

test('each physical Codex Escape press sends exactly one command', async ({ page }) => {
    const received = await openTerminal(page);
    received.length = 0;
    await page.keyboard.press('Escape');
    await page.keyboard.press('Escape');
    await page.keyboard.type('x');
    await expect.poll(() => received.filter(message => !message.startsWith('__resize__:')))
        .toEqual([CODEX_ESCAPE, CODEX_ESCAPE, 'x']);
});

for (const modifier of ['Shift', 'Control', 'Alt', 'Meta']) {
    test(`Codex ${modifier}+Escape retains xterm's ordinary key path`, async ({ page }) => {
        const received = await openTerminal(page);
        received.length = 0;
        await page.keyboard.press(`${modifier}+Escape`);
        await page.keyboard.type('x');
        await expect.poll(() => received.filter(message => !message.startsWith('__resize__:')))
            .toEqual([modifier === 'Alt' ? '\x1b\x1b' : '\x1b', 'x']);
    });
}

test('a non-Codex terminal retains the ordinary physical Escape byte', async ({ page }) => {
    const received = await openTerminal(page, 'claude');
    received.length = 0;
    await page.keyboard.press('Escape');
    await page.keyboard.type('x');
    await expect.poll(() => received.filter(message => !message.startsWith('__resize__:')))
        .toEqual(['\x1b', 'x']);
});

test('IME-marked Escape events never become a Codex Escape command', async ({ page }) => {
    const received = await openTerminal(page);
    // IME metadata cannot be expressed by keyboard.press. Only this guard case
    // uses constructed events; all ordinary key/focus regressions use real keys.
    await page.evaluate(() => {
        const textarea = document.querySelector('.xterm-helper-textarea');
        for (const metadata of [{ isComposing: true, keyCode: 27 }, { isComposing: false, keyCode: 229 }]) {
            textarea.dispatchEvent(new KeyboardEvent('keydown', {
                key: 'Escape', code: 'Escape', bubbles: true, cancelable: true, ...metadata
            }));
        }
    });
    await page.keyboard.type('x');
    await expect.poll(() => received.includes('x')).toBe(true);
    expect(received).not.toContain(CODEX_ESCAPE);
    await expectEscapeDelivered(page, received);
});

test('programmatic terminal input and pasted Escape bytes stay literal', async ({ page }) => {
    const received = await openTerminal(page);
    received.length = 0;
    await page.evaluate(async () => {
        const instance = window.app.terminalController.manager.getActiveTab().instance;
        instance.vibeTerminal.input('\x1b', true);
        await instance.vibeTerminal.writeAsync('\x1b[?2004h');
        instance.injectText('first\x1b[31msecond');
    });
    await expect.poll(() => received.filter(message => !message.startsWith('__resize__:')))
        .toEqual(['\x1b', '\x1b[200~first\x1b[31msecond\x1b[201~']);
});

test('canceling the editor returns input to the terminal', async ({ page }) => {
    const received = await openTerminal(page);
    await openEditor(page);
    await page.getByRole('button', { name: 'Cancel', exact: true }).click();
    await expect(page.locator('.vb-terminal-editor-modal')).toHaveCount(0);
    await expectEscapeDelivered(page, received);
});

test('plain editor Escape closes once and the next Escape reaches the terminal', async ({ page }) => {
    const received = await openTerminal(page);
    await openEditor(page);
    await page.keyboard.press('Escape');
    await expect(page.locator('.vb-terminal-editor-modal')).toHaveCount(0);
    expect(received.filter(message => message === CODEX_ESCAPE)).toHaveLength(0);
    await expectEscapeDelivered(page, received);
});

test('replacing the editor cleans it up and leaves the replacement in control', async ({ page }) => {
    const received = await openTerminal(page);
    await openEditor(page);
    await page.evaluate(() => window.app.showModal('Replacement', '<input aria-label="Replacement value" />'));
    await expect(page.getByRole('textbox', { name: 'Replacement value' })).toBeFocused();
    await expect.poll(() => page.evaluate(() => window.monaco.editor.getModels().length)).toBe(0);
    await page.keyboard.press('Escape');
    await expect(page.getByRole('dialog')).toHaveCount(0);
    await page.locator('.xterm-screen').click();
    await expectEscapeDelivered(page, received);
});

test('editor drafts survive cancellation and Send pastes without submitting', async ({ page }) => {
    const received = await openTerminal(page);
    await openEditor(page);
    const host = page.locator('#vb-terminal-editor-host');
    expect((await host.boundingBox()).height).toBeGreaterThan(250);
    await page.keyboard.type('first line');
    await page.keyboard.press('Enter');
    await page.keyboard.type('second line');
    const draft = await page.evaluate(() => window.monaco.editor.getModels()[0].getValue());
    await page.getByRole('button', { name: 'Cancel', exact: true }).click();
    await openEditor(page);
    await expect.poll(() => page.evaluate(() => window.monaco.editor.getModels()[0].getValue()))
        .toBe(draft);
    await page.getByRole('button', { name: 'Send to terminal', exact: true }).click();
    await expect(page.locator('.vb-terminal-editor-modal')).toHaveCount(0);
    expect(received).toContain('first line\nsecond line');
    expect(received).not.toContain('\r');
    await expectEscapeDelivered(page, received);
    await expect.poll(() => page.evaluate(() => window.monaco.editor.getModels().length)).toBe(0);
});

test('replacing the editor during Monaco loading cannot reclaim focus or mount it later', async ({ page }) => {
    await openTerminal(page);
    let releaseLoader;
    const loaderGate = new Promise(resolve => { releaseLoader = resolve; });
    await page.route('**/assets/monaco/vs/loader.js', async route => {
        await loaderGate;
        await route.continue();
    });
    await page.getByRole('button', { name: 'Open in text editor', exact: true }).click();
    await expect(page.locator('.vb-terminal-editor-modal')).toBeVisible();
    await page.evaluate(() => window.app.showModal('Replacement', '<input aria-label="Replacement value" />'));
    releaseLoader();
    await expect.poll(() => page.evaluate(() => !!window.monaco?.editor), { timeout: 20000 }).toBe(true);
    await expect(page.getByRole('textbox', { name: 'Replacement value' })).toBeFocused();
    expect(await page.evaluate(() => window.monaco.editor.getModels().length)).toBe(0);
    await expect(page.locator('.vb-terminal-editor-modal')).toHaveCount(0);
});

test('navigation closes the editor without a stale Escape listener', async ({ page }) => {
    const received = await openTerminal(page);
    await openEditor(page);
    await page.evaluate(() => window.app.navigate('settings'));
    await expect(page.locator('.vb-terminal-editor-modal')).toHaveCount(0);
    await expect.poll(() => page.evaluate(() => window.monaco.editor.getModels().length)).toBe(0);
    await page.evaluate(() => window.app.navigate('terminal-focus'));
    await expect(page.locator('.xterm-helper-textarea')).toBeAttached();
    await expect.poll(() => page.evaluate(() =>
        window.app.terminalController.manager?.getActiveTab()?.instance?.hasOpenSocket()
    )).toBe(true);
    await page.locator('.xterm-screen').click();
    await expectEscapeDelivered(page, received);
});

test('Escape dismisses Monaco Find without closing the editor', async ({ page }) => {
    await openTerminal(page);
    await openEditor(page);
    await page.keyboard.press('Control+f');
    await expect(page.locator('.monaco-editor .find-widget')).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(page.locator('.vb-terminal-editor-modal')).toBeVisible();
    await expect(page.locator('.monaco-editor .find-widget')).toHaveAttribute('aria-hidden', 'true');
});

test('Escape dismisses Monaco suggestions before closing the editor', async ({ page }) => {
    await openTerminal(page);
    await openEditor(page);
    await page.evaluate(() => window.monaco.languages.registerCompletionItemProvider('plaintext', {
        provideCompletionItems(model, position) {
            const word = model.getWordUntilPosition(position);
            return { suggestions: [{ label: 'hello', insertText: 'hello',
                kind: window.monaco.languages.CompletionItemKind.Text,
                range: { startLineNumber: position.lineNumber, endLineNumber: position.lineNumber,
                    startColumn: word.startColumn, endColumn: word.endColumn } }] };
        }
    }));
    await page.keyboard.type('hel');
    await page.keyboard.press('Control+Space');
    await expect(page.locator('.monaco-editor .suggest-widget.visible')).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(page.locator('.vb-terminal-editor-modal')).toBeVisible();
    await expect(page.locator('.monaco-editor .suggest-widget.visible')).toHaveCount(0);
    await page.keyboard.press('Escape');
    await expect(page.locator('.vb-terminal-editor-modal')).toHaveCount(0);
});

test('canceling an active tab rename returns input to that terminal', async ({ page }) => {
    const received = await openTerminal(page);
    await page.locator('.vb-terminal-tab-item').hover();
    await page.getByRole('button', { name: 'Rename tab', exact: true }).click();
    await expect(page.getByRole('textbox', { name: 'Tab name' })).toBeFocused();
    await page.keyboard.press('Escape');
    await expect(page.getByRole('textbox', { name: 'Tab name' })).toHaveCount(0);
    await expectEscapeDelivered(page, received);
});
