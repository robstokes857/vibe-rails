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
    const guardAt = body.search(/if \(!\['dashboard', 'agents'\]\.includes\(this\.app\.currentView\)\) return;/);
    assert.ok(refreshAt >= 0, 'the refresh must be awaited before painting');
    assert.ok(guardAt > refreshAt, 'the stand-down guard must run after the refresh, before painting');
});
