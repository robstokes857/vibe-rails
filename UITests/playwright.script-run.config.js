const { defineConfig } = require('@playwright/test');
const port = Number(process.env.VIBERAILS_SCRIPT_TEST_PORT) || 18774;
module.exports = defineConfig({
    testDir: './tests', testMatch: 'script-run-window.spec.js', workers: 1, reporter: 'list',
    use: { headless: true, channel: 'chrome', baseURL: `http://127.0.0.1:${port}`, screenshot: 'only-on-failure' },
    webServer: {
        command: `node node_modules/http-server/bin/http-server .. -a 127.0.0.1 -p ${port} -c-1`,
        cwd: __dirname, url: `http://127.0.0.1:${port}/UITests/fixtures/script-run.html`,
        reuseExistingServer: process.env.VIBERAILS_SCRIPT_TEST_REUSE === '1', timeout: 15000
    }
});
