const { test, expect } = require('./fixtures');

const CODE_QUALITY_RESPONSE = {
  success: true,
  exitCode: 0,
  status: 'warning',
  title: 'Code analyzer',
  output: '[warning] Review the highest-concern working-tree changes.',
  startedUtc: '2026-07-18T15:30:00Z',
  durationMs: 342,
  healthScore: 42,
  rating: 'AtRisk',
  analyzedFileCount: 2,
  skippedFileCount: 1,
  report: {
    score: 58,
    rating: 'AtRisk',
    analyzedFileCount: 2,
    skippedFileCount: 1,
    files: [
      {
        file: 'VibeRails/Services/ExampleService.cs',
        score: 82,
        rating: 'Critical',
        referencedByCount: 7,
        priority: 164,
        baselineScore: 40,
        introducedScore: 42,
        categories: [
          {
            name: 'Complexity',
            score: 82,
            weight: 0.6,
            weightedScore: 49.2,
            metrics: [{
              name: 'cyclomatic_complexity',
              value: 19,
              score: 82,
              warn: 10,
              critical: 15,
              higherIsBetter: false,
              source: 'ProcessAsync',
              line: 42,
              snippet: 'public async Task ProcessAsync()\n{\n    if (ready) Run();\n}'
            }]
          },
          {
            name: 'Maintainability',
            score: 65,
            weight: 0.4,
            weightedScore: 26,
            metrics: [{
              name: 'maintainability_index',
              value: 45,
              score: 65,
              warn: 60,
              critical: 40,
              higherIsBetter: true,
              source: 'ExampleService',
              line: 1,
              snippet: 'public sealed class ExampleService'
            }]
          }
        ]
      },
      {
        file: 'VibeRails/Routes/ExampleRoutes.cs',
        score: 20,
        rating: 'Healthy',
        referencedByCount: 1,
        priority: 20,
        categories: [{
          name: 'Complexity',
          score: 20,
          weight: 1,
          weightedScore: 20,
          metrics: [{
            name: 'cyclomatic_complexity',
            value: 3,
            score: 20,
            warn: 10,
            critical: 15,
            higherIsBetter: false,
            source: 'MapExample',
            line: 8,
            snippet: 'public static void MapExample() { }'
          }]
        }]
      }
    ],
    worstMetrics: [{
      name: 'cyclomatic_complexity',
      file: 'VibeRails/Services/ExampleService.cs',
      value: 19,
      score: 82,
      warn: 10,
      critical: 15,
      higherIsBetter: false,
      source: 'ProcessAsync',
      line: 42,
      snippet: 'public async Task ProcessAsync()\n{\n    if (ready) Run();\n}'
    }]
  }
};

async function openFirstRuleFileInManager(page) {
  const tree = page.locator('[data-agent-file-tree]');
  const configured = tree.locator('.agent-files-configured .agent-file-tree-item');
  let item = configured.first();

  if (await configured.count() === 0) {
    const withoutRules = tree.locator('[data-agent-empty-group]');
    if (await withoutRules.getAttribute('open') === null) {
      await withoutRules.locator('summary').click();
    }
    item = withoutRules.locator('.agent-file-tree-item').first();
  }

  await expect(item).toBeVisible();
  await item.locator('.agent-file-tree-open').click();
  return item;
}

test('has title', async ({ page }) => {
  await page.goto('/');
  await expect(page).toHaveTitle(/vibe-rails/i);
});

test('opens the terminal workspace by default', async ({ page }) => {
  await page.goto('/');
  await expect(page.locator('.view[data-view="terminal-focus"]')).toBeVisible();
  await expect(page.locator('#vb-terminal-panel')).toHaveCount(1);
  await expect(page.locator('[data-rules-overview-host]')).toHaveCount(0);
});

test('Quality combines rule management, validation, Git Guard, and Code quality', async ({ page }) => {
  await page.goto('/');

  const qualityNav = page.locator('.app-subnav-link[data-action="navigate-home"]:visible');
  await expect(qualityNav).toHaveText(/quality/i);
  await expect(page.locator('.app-subnav-link[data-view="rule-files"]:visible')).toHaveCount(0);
  await qualityNav.click();

  await expect(page.locator('.view[data-view="agents"]')).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Rules and code quality' })).toBeVisible();
  await expect(page.locator('.project-health-guard')).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Rules', exact: true })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Code quality', exact: true })).toBeVisible();
  await expect(page.locator('#vb-terminal-panel')).toHaveCount(0);

  // Rule CRUD is one deliberate drill-in instead of another top-level page.
  await page.getByRole('button', { name: 'Manage rules' }).click();
  await expect(page.locator('[data-rule-manager-modal]')).toBeVisible();

  const container = page.locator('[data-agent-file-tree]');
  await expect(container).toBeVisible();

  const configuredFiles = container.locator('.agent-files-configured .agent-file-tree-item');
  if (await configuredFiles.count() > 0) {
    await expect(configuredFiles.first()).toBeVisible();
    await expect(configuredFiles.first().locator('.agent-file-tree-name')).toContainText('/Rules');
    await expect(configuredFiles.first().locator('.agent-file-tree-badge')).toHaveText(/[1-9]\d* rules?/);
  } else {
    await expect(container.locator('.agent-files-configured .agent-files-group-empty'))
      .toHaveText(/No rule files have rules yet/i);
  }

  const filesWithoutRules = container.locator('[data-agent-empty-group]');
  if (await filesWithoutRules.count() > 0) {
    await expect(filesWithoutRules).not.toHaveAttribute('open', '');
    await expect(filesWithoutRules.locator('summary')).toContainText('Without rules');
  }

  // Selecting a rule file opens the inline editor beside the list — still no trip to
  // the full-page markdown editor.
  await openFirstRuleFileInManager(page);
  const editor = page.locator('[data-agent-rule-editor]');
  await expect(editor.getByRole('button', { name: 'Add rule' })).toBeVisible();
  await expect(page.locator('[data-view="agent-edit"]')).toHaveCount(0);

  // Child CRUD dialogs layer above the manager. Cancel returns to the same
  // selected file instead of destroying the manager underneath.
  const renameRuleFile = editor.getByRole('button', { name: "Set this rule file's display name" });
  await renameRuleFile.click();
  const ruleCrudDialog = page.locator('.agent-rule-modal-layer');
  await expect(ruleCrudDialog.getByRole('dialog', { name: 'Set Rule File Display Name' })).toBeVisible();
  await ruleCrudDialog.getByRole('button', { name: 'Cancel' }).click();
  await expect(ruleCrudDialog).toHaveCount(0);
  await expect(page.locator('[data-rule-manager-modal]')).toBeVisible();
  await expect(renameRuleFile).toBeFocused();

  await page.locator('#modal-container [data-action="close-modal"]').click();
  await expect(page.locator('[data-rule-manager-modal]')).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Fix rules & code quality' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Fix rules', exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Fix code quality', exact: true })).toBeVisible();
});

test('rule-file workflows use policy terminology', async ({ page }) => {
  await page.goto('/');

  await page.locator('.app-subnav-link[data-action="navigate-home"]:visible').click();
  await page.getByRole('button', { name: 'Manage rules' }).click();
  await page.getByRole('button', { name: 'New rule file' }).click();

  await expect(page.getByRole('heading', { name: 'Create New Rule File' })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Create New Agent' })).toHaveCount(0);

  await page.getByRole('button', { name: 'Back' }).click();
  await page.getByRole('button', { name: 'Manage rules' }).click();
  await openFirstRuleFileInManager(page);
  await page.locator('[data-agent-rule-editor]').getByRole('button', { name: 'Full editor' }).click();

  await expect(page.getByText('Files covered by this rule file', { exact: true })).toBeVisible();
  await expect(page.getByText('Full Rule File Content', { exact: true })).toBeVisible();
  await expect(page.getByText('Full Agent File Content', { exact: true })).toHaveCount(0);
});

test('Project health is a simple scrollable card stack with no embedded terminal', async ({ page }) => {
  await page.goto('/');
  await page.locator('.app-subnav-link[data-action="navigate-home"]:visible').click();

  const layout = await page.locator('.project-health-stack').evaluate((stack) => {
    const [rules, quality] = stack.querySelectorAll(':scope > .project-health-card');
    return {
      rulesTop: rules.getBoundingClientRect().top,
      qualityTop: quality.getBoundingClientRect().top,
      bodyOverflowY: getComputedStyle(document.body).overflowY
    };
  });
  expect(layout.qualityTop).toBeGreaterThan(layout.rulesTop);
  expect(layout.bodyOverflowY).not.toBe('hidden');
  await expect(page.locator('[data-terminal-section], [data-terminal-content]')).toHaveCount(0);
});

test('the quality brief opens metric details in a modal and returns to the same summary', async ({ page }) => {
  await page.route('**/api/v1/code-analyzer**', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(CODE_QUALITY_RESPONSE) });
  });
  // Registered after the generic mock so it wins for the source-pane fetch.
  await page.route('**/api/v1/code-analyzer/source**', async (route) => {
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        path: 'VibeRails/Services/ExampleService.cs',
        content: 'namespace VibeRails.Services;\n\npublic sealed class ExampleService\n{\n    public async Task ProcessAsync()\n    {\n        if (ready) Run();\n    }\n}\n',
        exists: true,
        isBinary: false,
        truncated: false
      })
    });
  });
  await page.goto('/');
  await page.locator('.app-subnav-link[data-action="navigate-home"]:visible').click();

  // The scan summary lands on the Code quality page as a compact brief.
  const brief = page.locator('[data-vca-quality-brief]');
  await expect(brief).toBeVisible();
  await expect(brief.getByRole('heading', { name: 'Change required' })).toBeVisible();
  await expect(brief.locator('.code-analyzer-brief-ring')).toBeVisible();
  // The full report is not on the summary page.
  await expect(page.locator('[data-code-analyzer-report]')).toHaveCount(0);

  // Its button opens the report without leaving the unified page.
  await brief.getByRole('button', { name: /View metrics/ }).click();
  await expect(page.locator('.view[data-view="agents"]')).toBeVisible();
  await expect(page.locator('[data-project-health-quality-report]')).toBeVisible();

  const card = page.locator('[data-project-health-quality-report]');
  // No internal tabs — the files workspace is the only surface.
  await expect(card.getByRole('tablist', { name: 'Code quality report sections' })).toHaveCount(0);
  await expect(card.getByRole('heading', { name: 'Changed files' })).toBeVisible();
  await expect(card.getByRole('heading', { name: 'Health metrics' })).toBeVisible();
  // Changed files are grouped under full directory headers in the rail.
  await expect(card.locator('.code-analyzer-dir-head').first()).toContainText('VibeRails/Services');
  // The code rides in the third pane, headed by the selected metric.
  await expect(card.getByRole('heading', { name: 'Cyclomatic complexity', exact: true })).toBeVisible();
  await expect(card.locator('.code-analyzer-source-column .code-analyzer-editor-readonly')).toBeVisible();

  // Directory groups collapse and reopen on header click.
  const serviceRow = card.locator('.code-analyzer-file-item', { hasText: 'ExampleService.cs' });
  await expect(serviceRow).toHaveCount(1);
  await card.locator('.code-analyzer-dir-head').first().click();
  await expect(serviceRow).toHaveCount(0);
  await card.locator('.code-analyzer-dir-head').first().click();
  await expect(serviceRow).toHaveCount(1);

  // Closing the modal leaves both health cards and the brief in place.
  await page.locator('#modal-container [data-action="close-modal"]').click();
  await expect(page.locator('[data-project-health-quality-report]')).toHaveCount(0);
  await expect(page.getByRole('heading', { name: 'Rules and code quality' })).toBeVisible();
  await expect(brief).toBeVisible();
});

test('project naming is available from Settings', async ({ page }) => {
  await page.goto('/');
  await page.getByRole('button', { name: 'Settings', exact: true }).click();

  const projectCard = page.locator('[data-project-identity-card]');
  await expect(projectCard).toBeVisible();
  await expect(projectCard.getByText('Project identity', { exact: true })).toBeVisible();
  await expect(projectCard.locator('[data-project-display-name]')).not.toBeEmpty();
  await expect(projectCard.locator('[data-project-root-path]')).toContainText(/vibe-rails/i);

  await projectCard.getByRole('button', { name: 'Change name' }).click();
  await expect(page.locator('#custom-name-form')).toBeVisible();
  await expect(page.locator('#project-custom-name')).not.toHaveValue('');
});
