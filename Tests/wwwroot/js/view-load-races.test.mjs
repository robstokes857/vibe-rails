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
// after the refresh, so its guard sits between the refresh and the paint.
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
    const refreshAt = body.search(/await Promise\.all\(\[this\.app\.refreshDashboardData\(\), nameTask\]\);/);
    const guardAt = body.search(/if \(!\['dashboard', 'agents', 'code-quality'\]\.includes\(this\.app\.currentView\)\) return;/);
    assert.ok(refreshAt >= 0, 'the refresh must be awaited before painting');
    assert.ok(guardAt > refreshAt, 'the stand-down guard must run after the refresh, before painting');
});

// app.js routes the legacy `code-quality` view (saved tabs, deep links) to loadDashboard, so the
// stand-down guard must count it as the dashboard or the page loads its data and paints nothing.
test('the legacy code-quality route paints Project health', async () => {
    const { DashboardController } = await import('../../../VibeRails/wwwroot/js/modules/dashboard-controller.js');
    const previousDocument = globalThis.document;
    const previousWindow = globalThis.window;
    const painted = [];
    const content = { innerHTML: 'old view', appendChild: node => painted.push(node) };
    globalThis.document = { getElementById: id => (id === 'app-content' ? content : null) };
    globalThis.window = { scrollTo() {} };
    try {
        const mounted = [];
        const rulesHost = {};
        const dashboard = { querySelector: selector => (selector === '[data-rules-overview-host]' ? rulesHost : null) };
        const app = {
            currentView: 'code-quality',
            data: { isInGit: false },
            refreshDashboardData: async () => {},
            cloneTemplate: () => ({ querySelector: selector => (selector === '[data-dashboard]' ? dashboard : null) }),
            agentController: { mountAgentsOverview: host => mounted.push(host) }
        };
        await new DashboardController(app).loadDashboard();
        assert.equal(content.innerHTML, '');
        assert.equal(painted.length, 1);
        assert.deepEqual(mounted, [rulesHost]);
    } finally {
        globalThis.document = previousDocument;
        globalThis.window = previousWindow;
    }
});
