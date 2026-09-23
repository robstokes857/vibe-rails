const { defineConfig } = require('@playwright/test');
process.env.VIBERAILS_BOARD_STATIC = '1';
const port = Number(process.env.VIBERAILS_BOARD_TEST_PORT) || 18764;

module.exports = defineConfig({
    testDir: './tests',
    testMatch: ['board-ux.spec.js', 'board-attachment-security.spec.js', 'board-pagination.spec.js', 'toast-appearance.spec.js'],
    workers: 1,
    reporter: 'list',
    use: {
        headless: true,
        baseURL: `http://127.0.0.1:${port}`,
        screenshot: 'only-on-failure',
        trace: 'retain-on-failure'
    },
    webServer: {
        command: `node node_modules/http-server/bin/http-server ../VibeRails/wwwroot -a 127.0.0.1 -p ${port} -c-1`,
        cwd: __dirname,
        url: `http://127.0.0.1:${port}`,
        reuseExistingServer: false,
        timeout: 15000
    }
});
