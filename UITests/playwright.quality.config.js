const { defineConfig } = require('@playwright/test');
process.env.VIBERAILS_QUALITY_STATIC = '1';

module.exports = defineConfig({
    testDir: './tests',
    testMatch: 'code-quality-ux.spec.js',
    workers: 1,
    reporter: 'list',
    use: { headless: true, baseURL: 'http://127.0.0.1:18763' },
    webServer: {
        command: 'node node_modules/http-server/bin/http-server ../VibeRails/wwwroot -a 127.0.0.1 -p 18763 -c-1',
        cwd: __dirname,
        url: 'http://127.0.0.1:18763',
        reuseExistingServer: false,
        timeout: 15000
    }
});
