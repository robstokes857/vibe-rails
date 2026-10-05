import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';
import path from 'node:path';

// The QUALITY page's tones and the code map's palette are host tokens: one block per theme in
// style.css, mapped from the editor theme by the VS Code bridge, with the Atlas bundle's own
// palette as the fallback. These tests pin that contract and the page composition around it.
const styleCss = readFileSync(path.resolve('VibeRails/wwwroot/style.css'), 'utf8');
const indexHtml = readFileSync(path.resolve('VibeRails/wwwroot/index.html'), 'utf8');
const viewerSource = readFileSync(path.resolve('VibeRails/wwwroot/js/modules/code-report/viewer.js'), 'utf8');
const viewerCss = readFileSync(path.resolve('VibeRails/wwwroot/js/modules/code-report/styles.css'), 'utf8');
const bundle = readFileSync(path.resolve('VibeRails/wwwroot/js/modules/code-report/vendor/atlas/code-atlas.mjs'), 'utf8');
const { RuleController, describeCodeAnalyzerScan } = await import(
    pathToFileURL(path.resolve('VibeRails/wwwroot/js/modules/rule-controller.js')).href);

const TONES = ['--quality-pass', '--quality-warn', '--quality-stop'];
const MAP_TOKENS = ['--node-module', '--node-file', '--node-class', '--node-function', '--node-data',
    '--graph-edge', '--graph-cross-edge', '--graph-changed'];

function block(css, selector) {
    const start = css.indexOf(selector);
    assert.ok(start >= 0, `${selector} block exists`);
    return css.slice(start, css.indexOf('}', start));
}

test('the app theme declares every quality tone and map token once on :root', () => {
    const root = block(styleCss, ':root {');
    const declared = {};
    for (const token of [...TONES, ...MAP_TOKENS]) {
        const matches = [...root.matchAll(new RegExp(`${token}: (#[0-9a-f]{6});`, 'g'))];
        assert.equal(matches.length, 1, `${token} declared once on :root`);
        declared[token] = matches[0][1];
    }
    assert.notEqual(declared['--quality-pass'], '#10b981', 'the pass tone is not the global success green');
    assert.notEqual(declared['--node-module'], declared['--node-file'], 'directories and files are told apart by color');
    assert.notEqual(declared['--graph-changed'], declared['--node-file'], 'changed files ring in their own color');
});

test('the VS Code theme bridge maps every token from the editor theme', () => {
    const bridge = block(styleCss, 'html[data-viberails-host="vscode"][data-viberails-vscode-theme="enabled"] {');
    for (const token of [...TONES, ...MAP_TOKENS]) {
        const match = bridge.match(new RegExp(`${token}: var\\((--vscode-[A-Za-z-]+|--vb-vscode-[a-z-]+)`));
        assert.ok(match, `${token} reads an editor color in the bridge`);
    }
    assert.match(bridge, /--quality-pass: var\(--vscode-charts-blue/, 'a passing check is never the editor green');
    assert.match(bridge, /--graph-changed: var\(--vscode-gitDecoration-modifiedResourceForeground/);
});

test('the Atlas bundle falls back to the same palette the app declares', () => {
    const root = block(styleCss, ':root {');
    const expected = Object.fromEntries(MAP_TOKENS.map(token => [token, root.match(new RegExp(`${token}: (#[0-9a-f]{6});`))[1]]));
    const host = bundle.split('\n').filter((_, index) => index !== 1).join('\n');
    const template = JSON.parse(bundle.match(/^const rendererTemplate = (.*);$/m)[1]);
    for (const source of [host, template]) {
        const dark = source.match(/dark: \{[\s\S]*?nodes: \{ module: '(#[0-9a-f]{6})', class: '(#[0-9a-f]{6})', file: '(#[0-9a-f]{6})', function: '(#[0-9a-f]{6})', data: '(#[0-9a-f]{6})' \},\s*graph: \{ edge: '(#[0-9a-f]{6})', crossEdge: '(#[0-9a-f]{6})', changed: '(#[0-9a-f]{6})' \}/);
        assert.ok(dark, 'the dark palette is parsable');
        assert.deepEqual(dark.slice(1), [expected['--node-module'], expected['--node-class'], expected['--node-file'], expected['--node-function'],
            expected['--node-data'], expected['--graph-edge'], expected['--graph-cross-edge'], expected['--graph-changed']]);
    }
    // The renderer's readability tiers ship in the template.
    assert.match(template, /const FILE_FLOOR = \.35;/);
    assert.match(template, /function fileDetail\(\)/);
    assert.match(template, /function domainLink\(edge\)/);
    assert.doesNotMatch(template, /Drawing \$\{count\(view\.edges\)\}/, 'the link budget is no longer a notice');
});

test('the report viewer and the grade read the page tones, open with Git changes and light them', () => {
    assert.match(viewerSource, /class="code-report code-report-compact"/);
    assert.match(viewerSource, /this\.activeList = 'changes'/);
    assert.match(viewerSource, /highlightChanges: true/);
    assert.match(viewerSource, /toggleDiagnostics\(\)/);
    assert.doesNotMatch(viewerSource, /data-graph-note|updateGraphNote/);
    assert.match(viewerCss, /\.code-report \.qr\{--qr-good:var\(--quality-pass,#[0-9a-f]{6}\)/);
    assert.match(viewerCss, /\.code-report\.code-report-compact \.qr-stats/);
    assert.match(viewerCss, /\.code-report\.code-report-compact \.qr-metrics-section/);
});

test('the QUALITY page is one header row, chips for the rule files and a menu for the log and coverage', () => {
    const template = indexHtml.match(/<template id="agents-template">([\s\S]*?)<\/template>/)[1];
    const header = template.match(/<header class="project-health-header">([\s\S]*?)<\/header>/)[1];
    assert.match(header, /<h1>Rules and code quality<\/h1>/);
    assert.match(header, /<section class="project-health-guard"/, 'Git Guard is a pill in the header');
    assert.doesNotMatch(template, /project-health-eyebrow|project-health-card-kicker|project-health-card-toolbar/);
    assert.doesNotMatch(template, /See what needs attention/);
    const rules = template.match(/<section class="card project-health-card project-health-rules[\s\S]*?<\/section>\s*<section class="card project-health-card project-health-quality/)[0];
    const rulesHeader = rules.match(/<header class="project-health-card-header">([\s\S]*?)<\/header>/)[1];
    assert.match(rulesHeader, /data-action="run-hook-preview"[\s\S]*?data-action="manage-rules"/, 'Check again sits in the card header');
    assert.match(rules, /data-rules-card-status[\s\S]*?data-action="toggle-health-details"/, 'the Details toggle sits in the verdict row');
    assert.match(rules, /<div class="project-health-rule-files" data-rule-files/);
    assert.match(rules, /visually-hidden">\s*<span class="rules-check-running"[\s\S]*?data-vca-console-state/, 'the console state badge is kept for the console, never shown');
    const quality = template.slice(template.indexOf('project-health-quality vca-console-card'));
    assert.match(quality, /data-code-analyzer-meta/);
    assert.match(quality, /data-action="toggle-map-coverage"/);
    assert.match(quality, /data-action="toggle-code-analyzer-log"/);
    assert.match(quality, /<details class="rules-console-transcript project-health-scan-transcript" data-code-analyzer-log hidden>/);
    assert.match(styleCss, /\.main-container:has\(\.project-health-page\) \{\s*padding-top: 14px;/);
});

test('the scan line says when, how many and how long', () => {
    const now = Date.parse('2026-10-05T12:00:00Z');
    const scan = (startedUtc, extra = {}) => describeCodeAnalyzerScan({ success: true, startedUtc, analyzedFileCount: 10, durationMs: 4180, ...extra }, now);
    assert.equal(scan('2026-10-05T11:57:00Z'), 'Scanned 3 min ago · 10 files · 4.2 s');
    assert.equal(scan('2026-10-05T11:59:50Z'), 'Scanned just now · 10 files · 4.2 s');
    assert.equal(scan('2026-10-05T09:00:00Z', { analyzedFileCount: 1, durationMs: 640 }), 'Scanned 3 h ago · 1 file · 640 ms');
    assert.equal(scan('2026-10-01T12:00:00Z'), 'Scanned 4 d ago · 10 files · 4.2 s');
    assert.equal(scan(undefined, { durationMs: undefined }), 'Scanned · 10 files');
    assert.equal(describeCodeAnalyzerScan({ success: false }), '');
    assert.equal(describeCodeAnalyzerScan(null), '');
});

test('rule files render as chips with their relative path, rule count and strictest level', () => {
    const host = { innerHTML: '' };
    const agents = [
        { path: 'C:/repo/AGENTS.md', rules: [{ enforcement: 'STOP' }, { enforcement: 'WARN' }] },
        { path: 'C:/repo/Services/Mcp/AGENTS.md', rules: [{ enforcement: 'commit' }] },
        { path: 'C:/repo/wwwroot/AGENTS.md', rules: [] }
    ];
    const controller = new RuleController({
        data: { agents },
        escapeHtml: value => String(value).replace(/[&<>"']/g, char => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[char]),
        getAgentFileViewModel: agent => ({ relativePath: agent.path.replace('C:/repo/', '') })
    });
    const nodes = new Map([['[data-rule-files]', host],
        ['[data-rule-file-count]', {}], ['[data-rule-count]', {}], ['[data-stop-rule-count]', {}]]);
    controller.viewRoot = { querySelector: selector => nodes.get(selector) || null };
    controller.renderRuleInventorySummary();
    assert.equal(nodes.get('[data-rule-file-count]').textContent, 3);
    assert.equal(nodes.get('[data-stop-rule-count]').textContent, 1);
    const chips = [...host.innerHTML.matchAll(/<button type="button" class="project-health-rule-file[^"]*"[^>]*>([\s\S]*?)<\/button>/g)].map(match => match[1]);
    assert.equal(chips.length, 4, 'three files and the add chip');
    assert.match(chips[0], /<b>AGENTS\.md<\/b><small>2 rules<\/small><em data-level="STOP">STOP<\/em>/);
    assert.match(chips[1], /<b>Services\/Mcp\/AGENTS\.md<\/b><small>1 rule<\/small><em data-level="COMMIT">COMMIT<\/em>/);
    assert.match(chips[2], /<b>wwwroot\/AGENTS\.md<\/b><small>0 rules<\/small>$/);
    assert.equal(chips[3], '+ New rule file');
    assert.match(host.innerHTML, /data-rule-file="C:\/repo\/Services\/Mcp\/AGENTS\.md"/);
    // A root without the chip host (the Git Guard views) still renders the counts.
    controller.viewRoot = { querySelector: selector => selector === '[data-rule-files]' ? null : nodes.get(selector) || null };
    controller.renderRuleInventorySummary();
    assert.equal(nodes.get('[data-rule-count]').textContent, 3);
});
