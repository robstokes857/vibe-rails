// @ts-check
const { defineConfig, devices } = require('@playwright/test');

module.exports = defineConfig({
  testDir: './tests',
  // These use node:test, not Playwright. They run via `npm run test:node`.
  testIgnore: ['**/xterm-scrollback.spec.js', '**/xterm-device-queries.spec.js'],
  fullyParallel: false,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 2 : 0,
  // One backend instance per run; one bootstrap consumption. Workers share storageState
  // via the saved auth file (see global-setup.js + tests/fixtures.js).
  workers: 1,
  reporter: process.env.CI ? [['list'], ['html', { open: 'never' }]] : 'list',
  globalSetup: require.resolve('./global-setup.js'),
  globalTeardown: require.resolve('./global-teardown.js'),
  use: {
    trace: 'on-first-retry',
    // baseURL + storageState are applied per-context by tests/fixtures.js using values
    // written by global-setup.js. We can't set them here because config loads before
    // globalSetup runs and the backend's port isn't known yet.
  },
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
  ],
});
