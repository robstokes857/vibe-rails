const { defineConfig } = require('@playwright/test');
process.env.VIBERAILS_TERMINAL_INPUT_STATIC = '1';

// Static frontend only: no global backend setup, database, profile, or installed CLI.
module.exports = defineConfig({
    testDir: './tests',
    testMatch: 'terminal-input-focus.spec.js',
    workers: 1,
    reporter: 'list',
    use: {
        headless: true,
        baseURL: 'http://127.0.0.1:18765',
        screenshot: 'only-on-failure'
    },
    webServer: {
        command: 'node node_modules/http-server/bin/http-server ../VibeRails/wwwroot -a 127.0.0.1 -p 18765 -c-1',
        cwd: __dirname,
        url: 'http://127.0.0.1:18765',
        reuseExistingServer: false,
        timeout: 15000
    }
});
