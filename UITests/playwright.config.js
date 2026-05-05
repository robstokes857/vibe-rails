// @ts-check
const { defineConfig, devices } = require('@playwright/test');

module.exports = defineConfig({
  testDir: './tests',
  // xterm-scrollback uses node:test, not Playwright. It's run via `npm run test:node`.
  testIgnore: ['**/xterm-scrollback.spec.js'],
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
