import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';

// Views that await data before painting must stand down when the user navigated on in
// the meantime: otherwise the stale continuation paints the old view over the new one.
// Seen live as "click Automation right after boot → the terminal view comes back over it".
const terminalSource = readFileSync(path.resolve('VibeRails/wwwroot/js/modules/terminal-multitab.js'), 'utf8');
const dashboardSource = readFileSync(path.resolve('VibeRails/wwwroot/js/modules/dashboard-controller.js'), 'utf8');

test('the terminal focus view checks it is still current after its async refresh, before painting', () => {
    const start = terminalSource.indexOf('async loadTerminalFocusView(');
    const body = terminalSource.slice(start, terminalSource.indexOf('content.innerHTML', start));
    assert.match(body, /await this\.app\.refreshDashboardData\(\);/);
    assert.match(body, /if \(this\.app\.currentView !== 'terminal-focus'\) return;/);
});

test('the dashboard view checks it is still current after its async refresh, before painting', () => {
    const start = dashboardSource.indexOf('async loadDashboard(');
    const body = dashboardSource.slice(start, dashboardSource.indexOf('content.innerHTML', start));
    assert.match(body, /await this\.app\.refreshDashboardData\(\);/);
    assert.match(body, /if \(!\['dashboard', 'agents'\]\.includes\(this\.app\.currentView\)\) return;/);
});
