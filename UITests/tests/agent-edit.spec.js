const { test, expect } = process.env.VIBERAILS_QUALITY_STATIC === '1'
  ? require('@playwright/test')
  : require('./fixtures');

const PROJECT_PATH = 'C:/source/vibe-rails';
const CONFIGURED_RULE_PATH = `${PROJECT_PATH}/src/Module/vc.rules.md`;
const EMPTY_RULE_PATH = `${PROJECT_PATH}/vc.rules.md`;

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

// Exercise the actual UI against stable files, and contain every mutation in this test's
// browser context. Running this spec never writes the developer's rule files or settings.
async function installRuleApi(page) {
  const agents = [
    { path: CONFIGURED_RULE_PATH, name: 'vc.rules.md', customName: 'Module policy', ruleCount: 1,
      rules: [{ text: 'Log all file changes', enforcement: 'WARN' }] },
    { path: EMPTY_RULE_PATH, name: 'vc.rules.md', ruleCount: 0, rules: [] }
  ];
  if (process.env.VIBERAILS_QUALITY_STATIC === '1') {
    await page.addInitScript(() => sessionStorage.setItem('viberails_tab', 'rule-file-fixture'));
  }
  await page.routeWebSocket('**/api/v1/events/ws*', () => {});
  await page.route('**/api/v1/**', async route => {
    const request = route.request();
    const url = new URL(request.url());
    const method = request.method();
    const payload = method === 'GET' ? null : request.postDataJSON();
    const agent = agents.find(item => item.path === (payload?.path || url.searchParams.get('path')));
    if (url.pathname === '/api/v1/agents/name' && method === 'PUT') {
      agent.customName = payload.customName;
      return route.fulfill({ json: { path: agent.path, customName: agent.customName } });
    }
    if (url.pathname === '/api/v1/agents' && method === 'POST') {
      const created = {
        path: payload.path, name: 'vc.rules.md', ruleCount: payload.rules.length,
        rules: payload.rules.map(text => ({ text, enforcement: 'WARN' }))
      };
      agents.push(created);
      return route.fulfill({ json: created });
    }
    if (url.pathname === '/api/v1/agents/rules/enforcement' && method === 'PUT') {
      agent.rules.find(rule => rule.text === payload.ruleText).enforcement = payload.enforcement;
      return route.fulfill({ json: agent });
    }
    if (url.pathname === '/api/v1/agents/rules' && method === 'POST') {
      agent.rules.push({ text: payload.ruleText, enforcement: payload.enforcement });
      agent.ruleCount = agent.rules.length;
      return route.fulfill({ json: agent });
    }
    if (url.pathname === '/api/v1/agents/rules' && method === 'DELETE') {
      agent.rules = agent.rules.filter(rule => !payload.rules.includes(rule.text));
      agent.ruleCount = agent.rules.length;
      return route.fulfill({ json: agent });
    }
    const responses = {
      '/api/v1/context': { isInGit: true, rootPath: PROJECT_PATH, launchDirectory: PROJECT_PATH },
      '/api/v1/settings': {},
      '/api/v1/projects/name': { customName: 'vibe-rails' },
      '/api/v1/environments': { environments: [] },
      '/api/v1/sandboxes': { sandboxes: [] },
      '/api/v1/agents': { agents },
      '/api/v1/rules/details': { rules: [
        { name: 'Log all file changes', description: 'Document changed files.' },
        { name: 'Package file changes', description: 'Review dependency changes.' },
        { name: "Directory Lock('path to directory')", description: 'Protect a directory and its descendants.' },
        { name: 'Check commit message for', description: 'Reject forbidden words or phrases in commit messages.' }
      ] },
      '/api/v1/agents/files': { files: ['app.cs', '.gitignore', 'child/vc.rules.md'], totalCount: 3 },
      '/api/v1/agents/content': { content: '# VibeRails Rules\n\n## Vibe Rails Rules\n- Log all file changes (WARN)\n' },
      '/api/v1/hooks/status': { inGitRepo: true, isInstalled: true, repositoryPath: PROJECT_PATH },
      '/api/v1/hooks/preview': { success: true, status: 'passed', output: 'No rule violations.', violations: [] },
      '/api/v1/code-analyzer': CODE_QUALITY_RESPONSE,
      '/api/v1/terminal/tabs': { tabs: [] },
      '/api/v1/llm-picker/preferences': { items: [
        { key: 'base:claude', kind: 'base', group: 'Base CLIs', label: 'Claude (default)', cli: 'claude', enabled: true, order: 0 }
      ] }
    };
    await route.fulfill({ json: responses[url.pathname] || {} });
  });
}

test.beforeEach(async ({ page }) => installRuleApi(page));

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
    await expect(configuredFiles.first().locator('.agent-file-tree-name')).toHaveText('Module policy');
    await expect(configuredFiles.first().locator('.agent-file-tree-path')).toHaveText('src/Module');
    await expect(configuredFiles.first().locator('.agent-file-tree-scope')).toHaveText('This folder and its subfolders');
    await expect(configuredFiles.first().locator('.agent-file-tree-badge')).toHaveText(/[1-9]\d* rules?/);
  } else {
    await expect(container.locator('.agent-files-configured .agent-files-group-empty'))
      .toHaveText(/No rule files have rules yet/i);
  }

  const filesWithoutRules = container.locator('[data-agent-empty-group]');
  if (await filesWithoutRules.count() > 0) {
    await expect(filesWithoutRules).toHaveAttribute('open', '');
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
  const renameRuleFile = editor.getByRole('button', { name: 'Edit display name', exact: true });
  await renameRuleFile.click();
  const ruleCrudDialog = page.locator('.agent-rule-modal-layer');
  await expect(ruleCrudDialog.getByRole('dialog', { name: 'Edit display name' })).toBeVisible();
  await ruleCrudDialog.getByRole('button', { name: 'Cancel' }).click();
  await expect(ruleCrudDialog).toHaveCount(0);
  await expect(page.locator('[data-rule-manager-modal]')).toBeVisible();
  await expect(renameRuleFile).toBeFocused();

  await page.locator('#modal-container [data-action="close-modal"]').click();
  await expect(page.locator('[data-rule-manager-modal]')).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Fix rules & code quality' })).toHaveCount(0);
  for (const scope of ['rules', 'code quality']) {
    const controls = page.getByRole('group', { name: `Fix ${scope} with an agent` });
    await expect(controls.getByRole('button', { name: `Fix ${scope} with:`, exact: true })).toBeVisible();
    await expect(controls.locator('[role="combobox"]')).toBeVisible();
  }
});

test('rule-file workflows use policy terminology', async ({ page }) => {
  await page.goto('/');

  await page.locator('.app-subnav-link[data-action="navigate-home"]:visible').click();
  await page.getByRole('button', { name: 'Manage rules' }).click();
  await page.getByRole('button', { name: 'New rule file' }).click();

  await expect(page.getByRole('heading', { name: 'Create New Rule File' })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Create New Agent' })).toHaveCount(0);

  await page.getByRole('button', { name: 'Back to Manage rules' }).click();
  await expect(page.locator('[data-rule-manager-modal]')).toBeVisible();
  const selected = await openFirstRuleFileInManager(page);
  const selectedPath = await selected.locator('.agent-file-tree-open').getAttribute('title');
  await page.locator('[data-agent-rule-editor]').getByRole('button', { name: 'Full editor' }).click();

  await expect(page.getByText('Files in this scope', { exact: false })).toBeVisible();
  await expect(page.getByRole('button', { name: 'View rule file content' })).toBeVisible();
  await expect(page.getByText('Full Agent File Content', { exact: true })).toHaveCount(0);
  await page.getByRole('button', { name: 'View rule file content' }).click();
  await expect(page.locator('[data-agent-full-content]')).toContainText('## Vibe Rails Rules');
  await page.getByRole('button', { name: 'Back to Manage rules' }).click();
  await expect(page.locator('[data-rule-manager-modal]')).toBeVisible();
  await expect(page.locator('.agent-file-tree-open[aria-current="true"]')).toHaveAttribute('title', selectedPath);
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

test('wizard Back preserves parameter drafts and creates one vc.rules.md path', async ({ page }) => {
  const directory = `${PROJECT_PATH}/wizard-locks`;
  const expectedPath = `${directory}/vc.rules.md`;
  const csvDraft = 'WIP,fix later, temporary';
  await page.goto('/?view=agents');
  await page.getByRole('button', { name: 'Manage rules' }).click();
  await page.getByRole('button', { name: 'New rule file' }).click();
  const wizard = page.locator('#wizard-content');
  await wizard.getByLabel('Directory for vc.rules.md').fill(directory);
  await wizard.locator('#wizard-next-btn').click();

  await wizard.locator('[data-path-lock-index]').fill('build/output');
  await wizard.getByRole('textbox', { name: 'Forbidden words or phrases', exact: true }).fill(csvDraft);
  await wizard.locator('#wizard-prev-btn').click();
  // Re-entering Step 1 must restore the folder, not the already-constructed file path.
  await expect(wizard.getByLabel('Directory for vc.rules.md')).toHaveValue(directory);
  await wizard.locator('#wizard-next-btn').click();
  await expect(wizard.locator('[data-path-lock-index]')).toHaveValue('build/output');
  await expect(wizard.getByRole('textbox', { name: 'Forbidden words or phrases', exact: true })).toHaveValue(csvDraft);
  await expect(wizard.locator('input[data-rule="Check commit message for"]')).toBeChecked();
  await wizard.locator('#wizard-next-btn').click();

  await expect(wizard.getByText("Directory Lock('build/output')", { exact: true })).toBeVisible();
  await expect(wizard.getByText('Check commit message for: WIP, fix later, temporary', { exact: true })).toBeVisible();
  await wizard.locator('select[data-rule-index="0"]').selectOption('STOP');
  await wizard.getByRole('button', { name: 'Review', exact: true }).click();
  await expect(wizard.locator('dd code')).toHaveText(expectedPath);

  const createRequest = page.waitForRequest(request =>
    new URL(request.url()).pathname === '/api/v1/agents' && request.method() === 'POST');
  await wizard.getByRole('button', { name: 'Create Rule File' }).click();
  const payload = (await createRequest).postDataJSON();
  expect(payload.path.replace(/\\/g, '/')).toBe(expectedPath);
  expect(payload.path.match(/vc\.rules\.md/g)).toHaveLength(1);
  expect(payload.rules).toEqual([
    "Directory Lock('build/output')", 'Check commit message for: WIP, fix later, temporary'
  ]);
  const manager = page.locator('[data-rule-manager-modal]');
  await expect(manager).toBeVisible();
  await expect(manager.locator('.agent-file-tree-open[aria-current="true"]')).toHaveAttribute('title', expectedPath);
  await expect(manager.locator('[data-rule-enforcement="0"]')).toHaveValue('STOP');
});

test('full editor edits and removes the rule directly from its row', async ({ page }) => {
  await page.goto('/?view=agents');
  await page.getByRole('button', { name: 'Manage rules' }).click();
  await openFirstRuleFileInManager(page);
  await page.locator('[data-agent-rule-editor]').getByRole('button', { name: 'Full editor' }).click();

  const editor = page.locator('[data-view="agent-edit"]');
  await editor.getByRole('button', { name: 'Edit Log all file changes', exact: true }).click();
  const form = page.locator('#rule-enforcement-form');
  await expect(form).toBeVisible();
  await expect(page.getByText('Please select a rule first', { exact: true })).toHaveCount(0);
  await form.locator('input[name="rule-enforcement"][value="STOP"]').check();
  const updateRequest = page.waitForRequest(request =>
    request.url().endsWith('/api/v1/agents/rules/enforcement') && request.method() === 'PUT');
  await form.getByRole('button', { name: 'Save enforcement' }).click();
  expect((await updateRequest).postDataJSON()).toEqual({
    path: CONFIGURED_RULE_PATH, ruleText: 'Log all file changes', enforcement: 'STOP'
  });
  await expect(editor.locator('.rules-editor-rule-row .badge')).toHaveText('STOP');

  await editor.getByRole('button', { name: 'Remove Log all file changes', exact: true }).click();
  await page.locator('#inline-remove-rule-confirm').click();
  await expect(editor.getByText('No rules yet', { exact: true })).toBeVisible();
  await expect(editor.locator('[data-rule-edit], [data-rule-delete]')).toHaveCount(0);
  await expect(editor.getByRole('button', { name: 'Add rule', exact: true })).toBeEnabled();
});

test('full editor keeps individual file cards visible above Rules with compact actions', async ({ page }) => {
  await page.goto('/?view=agents');
  await page.getByRole('button', { name: 'Manage rules' }).click();
  await openFirstRuleFileInManager(page);
  await page.locator('[data-agent-rule-editor]').getByRole('button', { name: 'Full editor' }).click();
  const editor = page.locator('[data-view="agent-edit"]');
  const cards = editor.locator('[data-agent-files-list] .file-item');
  await expect(cards).toHaveCount(3);
  await expect(cards.filter({ hasText: 'app.cs' })).toBeVisible();
  await expect(cards.filter({ hasText: 'app.cs' })).toContainText('C#');
  await expect(cards.filter({ hasText: 'app.cs' }).locator('img')).toHaveAttribute('src', /csharp\.svg$/);
  await expect(cards.filter({ hasText: 'vc.rules.md' })).toContainText('child/');
  await expect(editor.locator('details:has([data-agent-files-list])')).toHaveCount(0);
  const filesBox = await editor.locator('.rules-editor-files-section').boundingBox();
  const rulesBox = await editor.locator('.rules-editor-section').filter({ has: page.locator('[data-agent-rules]') }).boundingBox();
  expect(filesBox.y).toBeLessThan(rulesBox.y);
  await expect(editor.getByRole('button', { name: 'Add rule', exact: true })).toBeInViewport();
});

test('display-name actions sit beside the name and save only a friendly searchable label', async ({ page }) => {
  await page.goto('/?view=agents');
  await page.getByRole('button', { name: 'Manage rules' }).click();
  await openFirstRuleFileInManager(page);
  const detail = page.locator('[data-agent-rule-editor]');
  await expect(detail.locator('.rules-editor-name-row').getByRole('button', { name: 'Edit display name', exact: true })).toBeVisible();
  await expect(page.locator('[data-agent-file-tree]').getByRole('button', { name: 'Set display name', exact: true })).toBeVisible();
  await detail.getByRole('button', { name: 'Full editor' }).click();
  const editor = page.locator('[data-view="agent-edit"]');
  const nameRow = editor.locator('.rules-editor-name-row');
  const nameButton = nameRow.getByRole('button', { name: 'Edit display name', exact: true });
  await expect(nameButton).toHaveAttribute('title', /searchable label.*filename remains vc\.rules\.md/);
  const headingBox = await nameRow.locator('[data-agent-display-name]').boundingBox();
  const buttonBox = await nameButton.boundingBox();
  expect(buttonBox.x - (headingBox.x + headingBox.width)).toBeLessThanOrEqual(12);
  await nameButton.click();
  const form = page.locator('#agent-custom-name-form');
  await expect(form).toContainText('friendly, searchable label');
  await expect(form).toContainText('The file stays vc.rules.md');
  await form.getByLabel('Display name', { exact: true }).fill('DB Rules for NoSQL DB 1');
  const requestPromise = page.waitForRequest(request => new URL(request.url()).pathname === '/api/v1/agents/name' && request.method() === 'PUT');
  await form.getByRole('button', { name: 'Save display name', exact: true }).click();
  expect((await requestPromise).postDataJSON()).toEqual({ path: CONFIGURED_RULE_PATH, customName: 'DB Rules for NoSQL DB 1' });
  await expect(editor.locator('[data-agent-display-name]')).toHaveText('DB Rules for NoSQL DB 1');
  await expect(editor.locator('[data-agent-path]')).toHaveText(CONFIGURED_RULE_PATH);
  await editor.getByRole('button', { name: 'Back to Manage rules' }).click();
  const manager = page.locator('[data-rule-manager-modal]');
  await manager.locator('[data-rule-file-search]').fill('NoSQL DB 1');
  await expect(manager.locator('.agent-file-tree-item:visible')).toHaveCount(1);
  await expect(manager.locator('.agent-file-tree-item:visible .agent-file-tree-name')).toHaveText('DB Rules for NoSQL DB 1');
});

test('empty full editor explains how to add a first rule without dead edit actions', async ({ page }) => {
  await page.goto('/?view=agents');
  await page.getByRole('button', { name: 'Manage rules' }).click();
  await page.getByRole('button', { name: 'Open vc.rules.md', exact: true }).click();
  await page.locator('[data-agent-rule-editor]').getByRole('button', { name: 'Full editor' }).click();
  const editor = page.locator('[data-view="agent-edit"]');
  await expect(editor.getByText('No rules yet', { exact: true })).toBeVisible();
  await expect(editor.getByText(/Use Add rule above to choose your first rule/)).toBeVisible();
  await expect(editor.locator('[data-rule-edit], [data-rule-delete], [data-agent-action="edit-rule"], [data-agent-action="remove-rule"]')).toHaveCount(0);
  await editor.getByRole('button', { name: 'Add rule', exact: true }).click();
  const form = page.locator('#inline-add-rule-form');
  await form.locator('input[name="inline-rule-pick"][value="Log all file changes"]').check();
  await form.getByRole('button', { name: 'Add rule', exact: true }).click();
  await expect(editor.getByRole('button', { name: 'Edit Log all file changes', exact: true })).toBeVisible();
  await expect(editor.getByText('No rules yet', { exact: true })).toHaveCount(0);
  await editor.getByRole('button', { name: 'Back to Manage rules' }).click();
  await expect(page.locator('[data-rule-manager-modal]')).toBeVisible();
  await expect(page.locator('.agent-file-tree-open[aria-current="true"]')).toHaveAttribute('title', EMPTY_RULE_PATH);
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
