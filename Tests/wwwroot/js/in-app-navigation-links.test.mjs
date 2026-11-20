import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync, readdirSync } from 'node:fs';
import path from 'node:path';

const wwwroot = path.resolve('VibeRails/wwwroot');

function sourceFiles() {
    const files = [path.join(wwwroot, 'index.html'), path.join(wwwroot, 'app.js')];
    const walk = dir => {
        for (const entry of readdirSync(dir, { withFileTypes: true })) {
            const full = path.join(dir, entry.name);
            if (entry.isDirectory()) walk(full);
            else if (/\.(?:m?js|html)$/.test(entry.name)) files.push(full);
        }
    };
    walk(path.join(wwwroot, 'js'));
    return files;
}

// The VS Code webview host opens every clicked <a href> as a URI even after the page calls
// preventDefault(). Against the webview's <base>, "?view=context" resolves to the extension's
// wwwroot folder, so VS Code shows a file+.vscode-resource URL instead of switching views.
// In-app navigation must be a <button> that calls app.navigate().
test('in-app view navigation never uses an anchor href', () => {
    const offenders = [];
    for (const file of sourceFiles()) {
        const lines = readFileSync(file, 'utf8').split(/\r?\n/);
        lines.forEach((line, index) => {
            if (/href\s*=\s*["'`]\/?\?|\.href\s*=\s*[`'"]\/?\?/.test(line)) {
                offenders.push(`${path.relative(wwwroot, file)}:${index + 1}: ${line.trim()}`);
            }
        });
    }
    assert.deepEqual(offenders, []);
});

test('the top bar has no Context link; App settings opens from a gear button', () => {
    const html = readFileSync(path.join(wwwroot, 'index.html'), 'utf8');
    const nav = html.slice(html.indexOf('<nav class="app-subnav'), html.indexOf('</nav>'));
    assert.doesNotMatch(nav, /data-view="context"/);
    assert.match(nav, /<button type="button" class="app-settings-button" data-action="navigate" data-view="settings"/);
});
