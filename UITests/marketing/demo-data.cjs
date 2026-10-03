// Fictional, hand-authored content for public screenshots. Never load application state.
const projectPath = '/demo/trailhead';
const date = '2026-10-02T14:00:00Z';
const columns = ['Backlog', 'In Progress', 'Review', 'Done'].map((name, position) => ({
    id: `lane_${position}`, name, position, boardId: 'demo_board',
    color: ['#64748b', '#06b6d4', '#a855f7', '#10b981'][position]
}));
const cardRows = [
    [0, 'Add keyboard shortcuts', 'Navigate the workspace without leaving the keyboard.', 'feature', 'medium', 'codex'],
    [0, 'Export a weekly activity summary', 'Give the team a clear view of completed work.', 'feature', 'medium', 'claude'],
    [0, 'Write the quick-start guide', 'Help new contributors make their first change.', 'task', 'low', 'copilot'],
    [1, 'Build the project activity feed', 'Group recent changes into a useful timeline.', 'feature', 'high', 'claude'],
    [1, 'Add search to the command palette', 'Find commands by name, category, or shortcut.', 'feature', 'medium', 'codex'],
    [1, 'Polish empty states', 'Explain the next step when a workspace is new.', 'task', 'low', 'opencode'],
    [2, 'Improve notification preferences', 'Keep each channel easy to configure and test.', 'feature', 'high', 'codex'],
    [2, 'Simplify the sync retry policy', 'Make retry timing explicit and cover edge cases.', 'task', 'medium', 'claude'],
    [2, 'Keep focus after closing a dialog', 'Return keyboard focus to the original action.', 'bug', 'medium', 'copilot'],
    [3, 'Ship the workspace switcher', 'Move between projects with context intact.', 'feature', 'medium', 'claude'],
    [3, 'Add accessible status indicators', 'Use text and icons alongside status colors.', 'task', 'medium', 'codex'],
    [3, 'Speed up the task list', 'Reduce repeat lookups and keep scrolling smooth.', 'task', 'low', 'opencode']
];
const cards = cardRows.map(([lane, title, description, type, priority, cli], i) => ({
    id: `demo_card_${i + 1}`, key: `TRAIL-${41 + i}`, displayId: `TRAIL-${41 + i}`,
    boardId: 'demo_board', columnId: `lane_${lane}`, position: i % 3, title, description,
    type, priority, assignee: `base:${cli}`, points: null, tags: [], blocked: false,
    flagged: false, commentCount: [2, 1, 0, 4, 3, 1, 5, 2, 1, 3, 2, 2][i],
    activeSessionId: i === 3 || i === 4 ? `demo_session_${i}` : null,
    activeTabId: i === 3 || i === 4 ? `demo_tab_${i}` : null,
    hasActiveAutomation: i === 6,
    createdAt: date, updatedAt: date, attachments: [], commits: [], sessions: [], comments: [], linkedCards: []
}));
cards[6].description = '## Notification preferences\nLet people choose which updates reach them.\n\n### Acceptance criteria\n- [x] Separate activity and release notifications\n- [x] Save preferences per workspace\n- [x] Add keyboard and screen-reader coverage\n- [ ] Complete the independent code review\n\nImplementation: @src/Notifications/PreferenceService.cs\n\nKeep the defaults quiet and make every setting easy to understand.';
cards[6].comments = [
    { id: 'demo_comment_1', author: { kind: 'user', label: 'You' }, body: 'Please keep the first-run experience simple. Activity updates should be opt-in.', createdAt: date },
    { id: 'demo_comment_2', author: { kind: 'agent', label: 'Codex' }, body: '**Implementation ready.** Preferences persist per workspace. Added tests for default values, keyboard navigation, and save/reload.', createdAt: date },
    { id: 'demo_comment_3', author: { kind: 'agent', label: 'Claude' }, body: 'Reviewing the settings flow and checking that the tests cover switching workspaces.', createdAt: date }
];
cards[6].commentCount = 3;
const environments = [
    { id: 1, name: 'Feature builder', cli: 'claude', customPrompt: 'Read the project conventions. Implement the requested change and run the relevant tests.', workspaceMode: 1, workspaceBranch: 'feature/activity-feed' },
    { id: 2, name: 'Careful refactor', cli: 'codex', customPrompt: 'Make small, reviewable changes. Preserve public behavior and explain the tests you ran.', workspaceMode: 1, workspaceBranch: 'refactor/preferences' },
    { id: 3, name: 'Documentation', cli: 'copilot', customPrompt: 'Write concise documentation with working examples.', workspaceMode: 0 },
    { id: 4, name: 'Code reviewer', cli: 'claude', purpose: 'code_review', customPrompt: 'Review the changed code for correctness and missing tests. Post actionable findings.', automationWorker: true, workspaceMode: 0 },
    { id: 5, name: 'Release notes', cli: 'codex', customPrompt: 'Summarize the changes for users. Group fixes and features.', automationWorker: true, workspaceMode: 0 }
].map(env => ({ customArgs: '', lastUsed: 'Today', createdAt: date, steps: [], ...env }));
const items = ['claude', 'codex', 'copilot', 'opencode'].map((cli, order) => ({
    key: `base:${cli}`, kind: 'base', group: 'Base CLIs', label: { claude: 'Claude', codex: 'Codex', copilot: 'Copilot', opencode: 'OpenCode' }[cli], cli, enabled: true, order
}));
for (const env of environments.filter(env => !env.automationWorker)) items.push({
    key: `env:${env.id}:${env.cli}`, kind: 'environment', group: 'Custom Environments',
    label: env.name, cli: env.cli, environmentId: env.id, enabled: true, order: env.id
});
const jobs = [
    { id: 1, name: 'Independent code review', description: 'Review the card changes and post findings.', actions: [{ id: 'review', kind: 0, environmentId: 4, environmentName: 'Independent reviewer', llm: 1 }], triggers: [{ kind: 3 }] },
    { id: 2, name: 'Code quality check', description: 'Check maintainability before work leaves Review.', actions: [{ id: 'quality', kind: 2, arguments: ['working-tree'] }], triggers: [{ kind: 3 }] },
    { id: 3, name: 'Weekly release notes', description: 'Turn completed work into a concise release summary.', actions: [{ id: 'notes', kind: 0, environmentId: 5, environmentName: 'Release notes', llm: 2 }], triggers: [{ kind: 0, scheduleKind: 2, daysOfWeekMask: 32, localTime: '09:00', timeZoneId: 'America/Chicago', nextRunUtc: '2026-10-09T14:00:00Z' }] }
].map(job => ({ enabled: true, projectPath, llm: 1, timeoutMinutes: 20, launchMinimized: true, prompt: '', createdAt: date, ...job }));
const modules = ['Workspace', 'Tasks', 'Activity', 'Search', 'Notifications', 'Storage', 'Shared', 'Tests'];
const names = [
    ['WorkspaceService', 'WorkspaceSettings', 'WorkspaceController'],
    ['TaskService', 'TaskRepository', 'TaskController'],
    ['ActivityFeed', 'ActivityRecorder', 'TimelineQuery'],
    ['SearchIndex', 'SearchService', 'CommandPalette'],
    ['PreferenceService', 'NotificationRouter', 'ChannelSettings'],
    ['DatabaseContext', 'MigrationRunner', 'QueryCache'],
    ['Clock', 'Result', 'Validation'],
    ['WorkspaceTests', 'PreferenceTests', 'SearchTests']
];
const graph = { schemaVersion: '1.0', repository: { name: 'Demo' }, fileCount: 24,
    truncated: false, capturedUtc: date, description: 'Fictional demo project. Source relationships and metrics are illustrative.', nodes: [], edges: [] };
modules.forEach((name, i) => {
    graph.nodes.push({ id: name, name, kind: 'module', path: `src/${name}` });
    names[i].forEach((name, j) => {
        const fileId = `${name}.cs`;
        graph.nodes.push({ id: fileId, name: fileId, kind: 'file', path: `src/${modules[i]}/${fileId}`, parentId: modules[i], summary: `${name} implements the ${modules[i].toLowerCase()} workflow in this fictional demo project.` });
        graph.edges.push({ id: `contains-${fileId}`, source: modules[i], target: fileId, kind: 'contains' });
        graph.nodes.push({ id: name, name, kind: 'class', path: `src/${modules[i]}/${fileId}`, parentId: fileId, line: 8 });
        graph.edges.push({ id: `class-${fileId}`, source: fileId, target: name, kind: 'contains' });
    });
});
[['Workspace', 'Tasks'], ['Tasks', 'Activity'], ['Activity', 'Notifications'], ['Search', 'Tasks'], ['Workspace', 'Storage'], ['Tasks', 'Storage'], ['Notifications', 'Storage'], ['Search', 'Shared'], ['Storage', 'Shared'], ['Tests', 'Workspace'], ['Tests', 'Notifications'], ['Tests', 'Search']].forEach(([source, target], i) => {
    graph.edges.push({ id: `ref-${i}`, source, target, kind: 'references', evidence: 'Illustrative source reference in the demo project' });
});
const categories = ['Complexity', 'Size', 'Cohesion', 'Coupling', 'Testability', 'Duplication', 'Maintainability'];
const reportFiles = [
    ['Notifications/PreferenceService', 29.4], ['Search/SearchService', 24.8], ['Activity/ActivityFeed', 18.2],
    ['Tasks/TaskService', 13.7], ['Storage/QueryCache', 9.4], ['Workspace/WorkspaceService', 7.1]
];
const scan = { success: true, healthScore: 88.2, rating: 'Clean', analyzedFileCount: 24, skippedFileCount: 0,
    durationMs: 180, output: 'Demo scan complete.', report: { score: 11.8, rating: 'Clean', worstMetrics: [],
        overview: categories.map((category, i) => ({ category, concern: [19.4, 14.2, 5.8, 13.6, 12.1, 0, 17.5][i], worstConcern: 29.4, worstMetricFile: 'src/Notifications/PreferenceService.cs', worstMetricName: 'cognitive_complexity' })),
        files: reportFiles.map(([name, score]) => ({ file: `src/${name}.cs`, score, rating: 'Clean', priority: score,
            referencedByCount: 3, baselineScore: score + 3, introducedScore: 0,
            categories: [{ name: 'Complexity', score, weight: 1, weightedScore: score, metrics: [
                { name: 'cognitive_complexity', score, line: 18, value: 9, warn: 15, critical: 25, higherIsBetter: false, source: 'UpdatePreferences', snippet: 'public Result UpdatePreferences(Preferences preferences)\n{\n    var validation = preferences.Validate();\n    if (!validation.IsValid) return Result.Invalid(validation);\n    repository.Save(preferences);\n    return Result.Success();\n}' },
                { name: 'cyclomatic_complexity', score: 12, line: 18, value: 3, warn: 10, critical: 20, higherIsBetter: false, source: 'UpdatePreferences' }
            ] }] })) } };
function api(path, method) {
    const simple = {
        '/api/v1/context': { isInGit: true, rootPath: projectPath, launchDirectory: projectPath },
        '/api/v1/projects/name': { name: 'Trailhead Demo' }, '/api/v1/settings': {},
        '/api/v1/environments': { environments }, '/api/v1/agents': { agents: [{ name: 'vc.rules.md', path: 'vc.rules.md', rules: [
            { text: 'Keep public API changes backward compatible.', enforcement: 'STOP' },
            { text: 'Add regression tests for bug fixes.', enforcement: 'COMMIT' },
            { text: 'Use structured logs without credentials or personal data.', enforcement: 'STOP' },
            { text: 'Prefer focused functions with clear names.', enforcement: 'WARN' }
        ] }] },
        '/api/v1/rules/details': { rules: [] }, '/api/v1/sandboxes': { sandboxes: [] },
        '/api/v1/llm-picker/preferences': { items }, '/api/v1/automation-nav/preferences': { items: [] },
        '/api/v1/board/boards': { boards: [{ id: 'demo_board', name: 'Trailhead · Demo', position: 0, cardCount: cards.length, columns }] },
        '/api/v1/board/columns': { columns }, '/api/v1/board/cards': { cards },
        '/api/v1/board/cards/activity': { cards, activeAutomationColumnIds: ['lane_2'] },
        '/api/v1/board/cards/link-candidates': { cards: [] },
        '/api/v1/code-analyzer': scan, '/api/v1/code-analyzer/graph': graph,
        '/api/v1/code-analyzer/ignores': { files: [] },
        '/api/v1/hooks/status': { inGitRepo: true, isInstalled: true, repositoryPath: projectPath },
        '/api/v1/hooks/preview': { success: true, status: 'passed', output: 'All configured checks passed.', violations: [] },
        '/api/v1/jobs': { jobs }, '/api/v1/jobs/runs/summary': { runs: [] },
        '/api/v1/python-scripts': { scriptsDirectory: '/demo/scripts', pinConfigured: true, scripts: [
            { name: 'check_docs.py', status: 'approved', bytes: 2048, updatedUtc: date },
            { name: 'release_summary.py', status: 'approved', bytes: 3072, updatedUtc: date }
        ] },
        '/api/v1/lifecycle/ping': { success: true }, '/api/v1/lifecycle/disconnect': { success: true },
        '/api/v1/token-savings': {}, '/api/v1/board/boards/demo_board/jira': { enabled: false },
        '/api/v1/codex/settings/Careful%20refactor': {},
        '/api/v1/chatHistory': { sessions: [], totalCount: 0, hasMore: false },
        '/api/v1/board/cards/demo_card_7/checks': { latest: [] },
        '/api/v1/board/cards/demo_card_7/automations': { jobs, runs: [{ id: 'demo_run', name: 'Independent code review', status: 1 }] },
        '/api/v1/terminal/tabs': { tabs: [
            { tabId: 'demo_terminal', sessionId: 'demo_recording', cli: 'codex', hasActiveSession: true, createdUTC: date, workingDirectory: projectPath },
            { tabId: 'demo_claude', sessionId: 'demo_claude_recording', cli: 'claude', hasActiveSession: true, createdUTC: date, workingDirectory: projectPath }
        ], maxTabs: 100 },
        '/api/v1/terminal/tabs/demo_terminal/status': { hasActiveSession: true, sessionId: 'demo_recording', cli: 'codex', workingDirectory: projectPath },
        '/api/v1/terminal/tabs/demo_claude/status': { hasActiveSession: true, sessionId: 'demo_claude_recording', cli: 'claude', workingDirectory: projectPath }
    };
    if (Object.hasOwn(simple, path)) return simple[path];
    const card = cards.find(card => path === `/api/v1/board/cards/${card.id}`);
    if (card) return card;
    if (/\/board\/columns\/[^/]+\/automation$/.test(path)) return {
        jobIds: path.includes('lane_2') ? [1, 2] : [], revision: 1, jobs,
        runningAgents: path.includes('lane_2') ? [{ runId: 'demo_run', name: jobs[0].name, cardId: cards[6].id, cardLabel: `TRAIL-47 · ${cards[6].title}`, terminalSessionId: 'demo_review' }] : []
    };
    if (/\/jobs\/\d+$/.test(path)) return jobs.find(job => path.endsWith(`/${job.id}`));
    if (path.endsWith('/runs')) return { runs: [] };
    return undefined;
}
module.exports = { api };
