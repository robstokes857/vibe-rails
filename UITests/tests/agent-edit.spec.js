const { test, expect } = require('./fixtures');

test('has title', async ({ page }) => {
  await page.goto('/');
  await expect(page).toHaveTitle(/vibe-rails/i);
});

test('opens the terminal workspace by default', async ({ page }) => {
  await page.goto('/');
  await expect(page.locator('[data-view="terminal-focus"]')).toBeVisible();
  await expect(page.locator('#vb-terminal-panel')).toHaveCount(1);
  await expect(page.locator('[data-rules-overview-host]')).toHaveCount(0);
});

test('can navigate to Agent Files', async ({ page }) => {
  await page.goto('/');

  const rulesNav = page.locator('.app-subnav-link[data-action="navigate-home"]');
  await expect(rulesNav).toBeVisible();
  await expect(rulesNav).toHaveText(/rules/i);
  await expect(page.getByRole('button', { name: 'Dashboard' })).toHaveCount(0);
  await rulesNav.click();

  await expect(page.getByRole('heading', { name: 'Validation results' })).toBeVisible();
  await expect(page.locator('[data-vca-console]').getByRole('button', { name: 'Run again' })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Code metrics' })).toBeVisible();
  await expect(page.locator('[data-code-analyzer-console]').getByRole('button', { name: 'Run again' })).toBeVisible();
  await expect(page.locator('#vb-terminal-panel')).toHaveCount(1);

  const rulesPrecedeTerminal = await page.evaluate(() => {
    const rules = document.querySelector('[data-rules-overview-host]');
    const terminal = document.querySelector('[data-terminal-section]');
    return Boolean(rules && terminal && (rules.compareDocumentPosition(terminal) & Node.DOCUMENT_POSITION_FOLLOWING));
  });
  expect(rulesPrecedeTerminal).toBe(true);

  const container = page.locator('[data-agent-file-tree]');
  await expect(container).toBeVisible();

  const configuredFiles = container.locator('.agent-files-configured .agent-file-tree-item');
  await expect(configuredFiles.first()).toBeVisible();
  await expect(configuredFiles.first().locator('.agent-file-tree-name')).toContainText('/Agent');
  await expect(configuredFiles.first().locator('.agent-file-tree-badge')).toHaveText(/[1-9]\d* rules?/);

  const filesWithoutRules = container.locator('[data-agent-empty-group]');
  await expect(filesWithoutRules).not.toHaveAttribute('open', '');
  await expect(filesWithoutRules.locator('summary')).toContainText('Without rules');
});

test('project naming is available from Settings', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: 'Settings' }).click();

  const projectCard = page.locator('[data-project-identity-card]');
  await expect(projectCard).toBeVisible();
  await expect(projectCard.getByText('Project identity', { exact: true })).toBeVisible();
  await expect(projectCard.locator('[data-project-display-name]')).not.toBeEmpty();
  await expect(projectCard.locator('[data-project-root-path]')).toContainText(/vibe-rails/i);

  await projectCard.getByRole('button', { name: 'Change name' }).click();
  await expect(page.locator('#custom-name-form')).toBeVisible();
  await expect(page.locator('#project-custom-name')).not.toHaveValue('');
});
