// @ts-check

const { test, expect } = require('./fixtures');

test.describe('frontend security boundaries', () => {
    test('CLI launcher renders API and error output as text', async ({ page }) => {
        await page.goto('/');
        await expect.poll(() => page.evaluate(() => Boolean(window.app?.cliLauncher)))
            .toBe(true);

        const result = await page.evaluate(async () => {
            const app = window.app;
            const launcher = app.cliLauncher;
            const originalApiCall = app.apiCall;
            const originalShowToast = app.showToast;
            const originalShowError = app.showError;
            const terminal = document.createElement('div');
            terminal.id = 'cli-terminal';
            document.body.appendChild(terminal);
            const attack = (marker) =>
                `<img data-xss="${marker}" src="invalid:${marker}" onerror="window.__cliXss = true">`;

            window.__cliXss = false;
            app.showToast = () => {};
            app.showError = () => {};

            try {
                const responseValues = [
                    attack('message'),
                    attack('stdout'),
                    attack('stderr')
                ];
                app.apiCall = async () => ({
                    success: true,
                    message: responseValues[0],
                    standardOutput: responseValues[1],
                    standardError: responseValues[2]
                });

                await launcher.launchCLI('codex');
                await new Promise(resolve => setTimeout(resolve, 25));
                const responseResult = {
                    lines: Array.from(terminal.querySelectorAll('.vb-terminal-line'), line => line.textContent),
                    injectedElements: terminal.querySelectorAll('[data-xss]').length,
                    executed: window.__cliXss
                };

                const maliciousCli = attack('cli');
                const maliciousError = attack('error');
                app.apiCall = async () => {
                    throw new Error(maliciousError);
                };

                await launcher.launchCLI(maliciousCli);
                await new Promise(resolve => setTimeout(resolve, 25));
                const errorResult = {
                    lines: Array.from(terminal.querySelectorAll('.vb-terminal-line'), line => line.textContent),
                    injectedElements: terminal.querySelectorAll('[data-xss]').length,
                    executed: window.__cliXss
                };

                return { responseValues, maliciousCli, maliciousError, responseResult, errorResult };
            } finally {
                app.apiCall = originalApiCall;
                app.showToast = originalShowToast;
                app.showError = originalShowError;
                terminal.remove();
                delete window.__cliXss;
            }
        });

        expect(result.responseResult.injectedElements).toBe(0);
        expect(result.responseResult.executed).toBe(false);
        expect(result.responseResult.lines).toEqual([
            'Launching CODEX CLI...',
            ...result.responseValues
        ]);

        expect(result.errorResult.injectedElements).toBe(0);
        expect(result.errorResult.executed).toBe(false);
        expect(result.errorResult.lines).toEqual([
            `Launching ${result.maliciousCli.toUpperCase()} CLI...`,
            `Error: ${result.maliciousError}`
        ]);
    });

    test('PTY OSC 52 output cannot write to the browser clipboard', async ({ page }) => {
        await page.goto('/');

        const result = await page.evaluate(async () => {
            const { VibeTerminal } = await import('/js/modules/vibe-terminal.js');
            const output = document.createElement('div');
            output.style.width = '800px';
            output.style.height = '300px';
            document.body.appendChild(output);

            const originalClipboard = Object.getOwnPropertyDescriptor(navigator, 'clipboard');
            const writes = [];
            Object.defineProperty(navigator, 'clipboard', {
                configurable: true,
                value: {
                    readText: async () => '',
                    writeText: async (text) => writes.push(text)
                }
            });

            // Keep this parser-level check on xterm's DOM renderer. Canvas/WebGL
            // disposal is unrelated to OSC 52 and can race app-level renderer setup.
            const originalCanvasAddon = window.CanvasAddon;
            const originalWebglAddon = window.WebglAddon;
            window.CanvasAddon = undefined;
            window.WebglAddon = undefined;
            const terminal = new VibeTerminal({ outputEl: output, cols: 80, rows: 10 });
            try {
                const clipboardText = 'terminal-controlled clipboard content';
                const encoded = btoa(clipboardText);
                await terminal.writeAsync(`\u001b]52;c;${encoded}\u0007`);
                await new Promise(resolve => setTimeout(resolve, 25));

                return {
                    writes,
                    clipboardScriptLoaded: Boolean(
                        document.querySelector('script[src$="/addon-clipboard.js"]')
                        || document.querySelector('script[src$="assets/xterm/addon-clipboard.js"]')
                    )
                };
            } finally {
                terminal.dispose();
                output.remove();
                if (originalClipboard) {
                    Object.defineProperty(navigator, 'clipboard', originalClipboard);
                } else {
                    delete navigator.clipboard;
                }
                window.CanvasAddon = originalCanvasAddon;
                window.WebglAddon = originalWebglAddon;
            }
        });

        expect(result.clipboardScriptLoaded).toBe(false);
        expect(result.writes).toEqual([]);
    });
});
