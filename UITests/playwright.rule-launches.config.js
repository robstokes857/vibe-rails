const { defineConfig } = require('@playwright/test');
module.exports = defineConfig({
    testDir: './tests', testMatch: 'rule-launches.spec.js', workers: 1, reporter: 'list',
    outputDir: './test-results-rule-launches',
    use: { headless: true, baseURL: 'http://127.0.0.1:18786', screenshot: 'only-on-failure', trace: 'retain-on-failure' },
    webServer: { command: 'node node_modules/http-server/bin/http-server ../VibeRails/wwwroot -a 127.0.0.1 -p 18786 -c-1',
        cwd: __dirname, url: 'http://127.0.0.1:18786', reuseExistingServer: false }
});
