import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';

// Views that await data must stand down when the user navigated on in the meantime:
// otherwise the stale continuation paints or binds the old view over the new one.
// Seen live as "click Automation right after boot → the terminal view comes back over it".
//
// The terminal focus view paints its shell BEFORE the refresh (instant view switch) and
// defers the dangerous work — binding the terminal manager — until after the await, so
// its stand-down guard sits between the refresh and the bind. The dashboard still paints
// after the refresh, so its guard sits between the refresh and the paint. Quality paints at
// once, like the terminal view, and does nothing at all after its refresh (VIBE-313).
const terminalSource = readFileSync(path.resolve('VibeRails/wwwroot/js/modules/terminal-multitab.js'), 'utf8');
const dashboardSource = readFileSync(path.resolve('VibeRails/wwwroot/js/modules/dashboard-controller.js'), 'utf8');

test('the terminal focus view checks it is still current after its async refresh, before binding', () => {
    const start = terminalSource.indexOf('async loadTerminalFocusView(');
    const bindAt = terminalSource.indexOf('await this.bindTerminalActions', start);
    assert.ok(start >= 0 && bindAt > start, 'loadTerminalFocusView should await bindTerminalActions');
    const body = terminalSource.slice(start, bindAt);
    const refreshAt = body.search(/await this\.app\.refreshDashboardData\(\);/);
    const guardAt = body.search(/if \(this\.app\.currentView !== 'terminal-focus' \|\| !terminalContent\.isConnected\) return;/);
    assert.ok(refreshAt >= 0, 'the refresh must be awaited before binding');
    assert.ok(guardAt > refreshAt, 'the stand-down guard must run after the refresh, before binding');
});

test('the dashboard view checks it is still current after its async refresh, before painting', () => {
    const start = dashboardSource.indexOf('async loadDashboard(');
    const paintAt = dashboardSource.indexOf('content.innerHTML', start);
    assert.ok(start >= 0 && paintAt > start, 'loadDashboard should paint into #app-content');
    const body = dashboardSource.slice(start, paintAt);
    // The last refresh before the paint is the dashboard's own; Quality's branch returns before it.
    const refreshAt = body.lastIndexOf('await Promise.all([this.app.refreshDashboardData(), this.refreshProjectName()]);');
    const guardAt = body.search(/if \(this\.loadId !== loadId \|\| this\.app\.currentView !== view\) return;/);
    assert.ok(refreshAt >= 0, 'the refresh must be awaited before painting');
    assert.ok(guardAt > refreshAt, 'the stand-down guard must run after the refresh, before painting');
});

test('the Quality view paints before its refresh and does nothing after it', () => {
    const start = dashboardSource.indexOf("if (view === 'code-quality') {");
    const end = dashboardSource.indexOf('return;', start);
    assert.ok(start >= 0 && end > start, 'loadDashboard should branch on the code-quality view');
    const branch = dashboardSource.slice(start, end);
    const mountAt = branch.indexOf('this.mountCodeQuality();');
    const refresh = 'await Promise.all([this.app.refreshDashboardData(), this.refreshProjectName()]);';
    const refreshAt = branch.indexOf(refresh);
    assert.ok(mountAt >= 0 && refreshAt > mountAt, 'the view mounts synchronously, ahead of the refresh');
    // Nothing between the await and the return can paint over a view the user moved on to.
    assert.equal(branch.slice(refreshAt + refresh.length).trim(), '');
});

// Saved Quality links must paint the independent report workspace.
test('the code-quality route paints its report workspace', async () => {
    const { DashboardController } = await import('../../../VibeRails/wwwroot/js/modules/dashboard-controller.js');
    const previousDocument = globalThis.document;
    const previousWindow = globalThis.window;
    const painted = [];
    const content = { innerHTML: 'old view', appendChild: node => painted.push(node) };
    globalThis.document = { getElementById: id => (id === 'app-content' ? content : null) };
    globalThis.window = { scrollTo() {} };
    try {
        const mounted = [];
        const order = [];
        const qualityRoot = {};
        const app = {
            currentView: 'code-quality',
            data: { isInGit: false },
            refreshDashboardData: async () => { order.push('refresh'); },
            cloneTemplate: () => ({ querySelector: selector => (selector === '[data-view="code-quality"]' ? qualityRoot : null) }),
            ruleController: {
                prefetchCodeQuality: () => order.push('map'),
                attachCodeQualityOverview: host => { order.push('mount'); mounted.push(host); }
            }
        };
        await new DashboardController(app).loadDashboard();
        assert.equal(content.innerHTML, '');
        assert.equal(painted.length, 1);
        assert.deepEqual(mounted, [qualityRoot]);
        // The map is asked for first, and the view is up before the data refresh even starts.
        assert.deepEqual(order, ['map', 'mount', 'refresh']);
    } finally {
        globalThis.document = previousDocument;
        globalThis.window = previousWindow;
    }
});
