const { defineConfig } = require('@playwright/test');
const qualityConfig = require('./playwright.quality.config');

// Reuse the existing static frontend server; picker launches and APIs are mocked.
module.exports = defineConfig({
    ...qualityConfig,
    testMatch: 'llm-picker-launch.spec.js'
});
