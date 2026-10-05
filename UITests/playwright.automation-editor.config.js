const { defineConfig } = require('@playwright/test');
process.env.VIBERAILS_AUTOMATION_STATIC = '1';
module.exports = defineConfig({
    testDir: './tests',
    testMatch: 'jobs-environment.spec.js',
    workers: 1,
    reporter: 'list',
    use: {
        headless: true,
        baseURL: 'http://127.0.0.1:18772',
        screenshot: 'only-on-failure',
        trace: 'retain-on-failure'
    },
    webServer: {
        command: 'node node_modules/http-server/bin/http-server ../VibeRails/wwwroot -a 127.0.0.1 -p 18772 -c-1',
        cwd: __dirname,
        url: 'http://127.0.0.1:18772',
        reuseExistingServer: false
    }
});
