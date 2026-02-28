const { test, expect } = require('@playwright/test');

test('has title', async ({ page }) => {
  await page.goto('/');
  await expect(page).toHaveTitle(/Vibe Rails/);
});

test('can navigate to Agent Files', async ({ page }) => {
  await page.goto('/');

  // Wait for the dashboard to load (checking for a visible element unique to dashboard)
  await expect(page.getByText('Global Context')).toBeVisible();

  // Open Agents view from sub-navigation.
  await page.locator('.app-subnav-link[data-view="agents"]').click();

  // Expect the header to be visible
  await expect(page.getByRole('heading', { name: 'Agent Files & Rules' })).toBeVisible();

  // The 'Agent Files in Project' container should be visible
  // Since we are in global context (default), it should show the "not available" message
  const container = page.locator('[data-agent-file-tree]');
  await expect(container).toBeVisible();
  await expect(container).toContainText('Agent files are only available in local project context');
});
