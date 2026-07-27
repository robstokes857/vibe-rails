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

// The Rules page is a local-nav workspace: one section is mounted in the top pane at a
// time, the terminal permanently owns the bottom pane. Every section stays in the DOM —
// switching only toggles `hidden` — so a tab click can never kill a running terminal.
test('can navigate to Agent Files', async ({ page }) => {
  await page.goto('/');

  const rulesNav = page.locator('.app-subnav-link[data-action="navigate-home"]:visible');
  await expect(rulesNav).toBeVisible();
  await expect(rulesNav).toHaveText(/rules/i);
  await expect(page.getByRole('button', { name: 'Dashboard' })).toHaveCount(0);
  await rulesNav.click();

  const localNav = page.getByRole('tablist', { name: 'Rules sections' });
  await expect(localNav.getByRole('tab')).toHaveCount(3);
  await expect(localNav.locator('xpath=..')).toHaveClass(/rules-topbar/);

  const headerCenterlines = await page.locator('.rules-topbar').evaluate((header) => {
    const gitGuard = header.querySelector('.rules-git-setting').getBoundingClientRect();
    const navigation = header.querySelector('.rules-localnav').getBoundingClientRect();
    return [gitGuard.top + gitGuard.height / 2, navigation.top + navigation.height / 2];
  });
  expect(Math.abs(headerCenterlines[0] - headerCenterlines[1])).toBeLessThanOrEqual(1);

  // Validation is the default section; Code quality is mounted but hidden.
  await expect(page.getByRole('heading', { name: 'Validation results' })).toBeVisible();
  const validationPanel = page.locator('[data-vca-console]');
  await expect(validationPanel.getByRole('button', { name: 'Check changes' })).toBeVisible();
  await expect(validationPanel.getByRole('button', { name: 'Copy transcript' })).not.toBeVisible();
  await validationPanel.locator('summary[aria-label="More validation actions"]').click();
  await expect(validationPanel.getByRole('button', { name: 'Copy transcript' })).toBeVisible();
  await validationPanel.locator('summary[aria-label="More validation actions"]').click();
  await expect(page.getByRole('heading', { name: 'Code quality' })).not.toBeVisible();
  await expect(page.locator('#vb-terminal-panel')).toHaveCount(1);

  const rulesPrecedeTerminal = await page.evaluate(() => {
    const rules = document.querySelector('[data-rules-overview-host]');
    const terminal = document.querySelector('[data-terminal-section]');
    return Boolean(rules && terminal && (rules.compareDocumentPosition(terminal) & Node.DOCUMENT_POSITION_FOLLOWING));
  });
  expect(rulesPrecedeTerminal).toBe(true);

  await localNav.getByRole('tab', { name: /Code quality/ }).click();
  await expect(page.getByRole('heading', { name: 'Code quality' })).toBeVisible();
  const qualityPanel = page.locator('[data-code-analyzer-console]');
  await expect(qualityPanel.getByRole('button', { name: 'Scan changes' })).toBeVisible();
  await qualityPanel.locator('summary[aria-label="More code quality actions"]').click();
  await expect(qualityPanel.getByRole('button', { name: 'Scan unpushed commits' })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Validation results' })).not.toBeVisible();
  // The terminal survived the switch.
  await expect(page.locator('#vb-terminal-panel')).toHaveCount(1);

  await localNav.getByRole('tab', { name: /Rule files/ }).click();
  const container = page.locator('[data-agent-file-tree]');
  await expect(container).toBeVisible();

  const configuredFiles = container.locator('.agent-files-configured .agent-file-tree-item');
  await expect(configuredFiles.first()).toBeVisible();
  await expect(configuredFiles.first().locator('.agent-file-tree-name')).toContainText('/Agent');
  await expect(configuredFiles.first().locator('.agent-file-tree-badge')).toHaveText(/[1-9]\d* rules?/);

  const filesWithoutRules = container.locator('[data-agent-empty-group]');
  await expect(filesWithoutRules).not.toHaveAttribute('open', '');
  await expect(filesWithoutRules.locator('summary')).toContainText('Without rules');

  // Selecting a rule file opens the inline editor beside the list — no navigation away
  // from the workspace, so the terminal below is still there.
  await configuredFiles.first().locator('.agent-file-tree-open').click();
  const editor = page.locator('[data-agent-rule-editor]');
  await expect(editor.getByRole('button', { name: 'Add rule' })).toBeVisible();
  await expect(page.locator('[data-view="agent-edit"]')).toHaveCount(0);
  await expect(page.locator('#vb-terminal-panel')).toHaveCount(1);
});

test('the rules workspace docks a substantial console at the bottom and lets the page scroll', async ({ page }) => {
  await page.goto('/');
  await page.locator('.app-subnav-link[data-action="navigate-home"]:visible').click();

  await expect(page.locator('[data-rules-splitter]')).toHaveCount(0);

  const layout = await page.locator('[data-rules-panes]').evaluate((panes) => {
    const [sections, terminal] = panes.querySelectorAll(':scope > .rules-pane');
    const panesRect = panes.getBoundingClientRect();
    const terminalRect = terminal.getBoundingClientRect();
    return {
      terminalHeight: terminalRect.height,
      terminalBottom: terminalRect.bottom,
      workspaceBottom: panesRect.bottom,
      bodyOverflowY: getComputedStyle(document.body).overflowY
    };
  });
  // The console is a real working surface now, not the old ~30dvh sliver.
  expect(layout.terminalHeight).toBeGreaterThanOrEqual(339);
  expect(layout.terminalHeight).toBeLessThanOrEqual(621);
  // It still rides at the bottom of the workspace.
  expect(Math.abs(layout.terminalBottom - layout.workspaceBottom)).toBeLessThanOrEqual(1);
  // The page is no longer viewport-locked — long sections can scroll.
  expect(layout.bodyOverflowY).not.toBe('hidden');
});

test('renders code quality as one workspace with a compact brief on the Validation screen', async ({ page }) => {
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

  // The scan summary lands on the default Validation section as a compact brief.
  const brief = page.locator('[data-vca-quality-brief]');
  await expect(brief).toBeVisible();
  await expect(brief.getByRole('heading', { name: 'Change required' })).toBeVisible();
  await expect(brief.locator('.code-analyzer-brief-ring')).toBeVisible();

  const localNav = page.getByRole('tablist', { name: 'Rules sections' });
  await localNav.getByRole('tab', { name: /Code quality/ }).click();

  const card = page.locator('[data-code-analyzer-console]');
  // No internal tabs any more — the files workspace is the only surface.
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

  // The brief deep-links back into the full report.
  await localNav.getByRole('tab', { name: /Validation/ }).click();
  await brief.getByRole('button', { name: /Open Code quality/ }).click();
  await expect(page.getByRole('heading', { name: 'Code quality' })).toBeVisible();
  await expect(card.getByRole('heading', { name: 'Changed files' })).toBeVisible();
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
