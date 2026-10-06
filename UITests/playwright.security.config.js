const { defineConfig } = require('@playwright/test');

module.exports = defineConfig({
    testDir: './tests',
    testMatch: 'loopback-cookie-security.spec.js',
    workers: 1,
    use: { browserName: 'chromium', headless: true },
    reporter: 'list'
});
