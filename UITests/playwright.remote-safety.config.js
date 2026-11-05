const { defineConfig } = require('@playwright/test');
module.exports = defineConfig({ testDir: './tests', testMatch: ['board-remote.spec.js'], workers: 1, reporter: 'list',
    use: { headless: true, channel: 'chrome', baseURL: 'http://127.0.0.1:18788', screenshot: 'only-on-failure' },
    webServer: { command: 'node node_modules/http-server/bin/http-server ../VibeRails/wwwroot -a 127.0.0.1 -p 18788 -c-1', cwd: __dirname,
        url: 'http://127.0.0.1:18788', reuseExistingServer: false, timeout: 15000 } });
