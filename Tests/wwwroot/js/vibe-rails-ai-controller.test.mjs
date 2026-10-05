import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import vm from 'node:vm';

const modulePath = path.resolve('VibeRails/wwwroot/js/modules/vibe-rails-ai-controller.js');

test('Vibe AI learning card separates LLM retrieval savings from Token Saver', () => {
    const source = readFileSync(modulePath, 'utf8');

    assert.match(source, /LLM tokens saved/);
    assert.match(source, /excludes Token Saver/);
    assert.match(source, /all-time tokens kept off the LLM/);
    assert.match(source, /\/api\/v1\/token-savings/);
    assert.doesNotMatch(source, /<div class="k">Tokens Saved<\/div>/);
});

test('indexing diagnostics display progress and escape failures as plain text', () => {
    const source = readFileSync(modulePath, 'utf8')
        .replace(/^import .*;\r?\n/m, '')
        .replace('export class VibeRailsAiController', 'class VibeRailsAiController');
    const Controller = vm.runInNewContext(source + '\nVibeRailsAiController;');
    const controller = new Controller({});
    controller.nodes = { statusPills: {}, diagPanel: {} };
    controller.state = { status: { databaseExists: true, indexing: {
        embedded: 7, chunks: 12, documents: 3, pendingSources: 2, failed: 1,
        lastError: '<img src=x onerror=alert(1)>', lastReconciledUtc: '2026-10-05T00:00:00Z',
    } } };
    controller.renderStatus();
    assert.match(controller.nodes.diagPanel.innerHTML, /7 \/ 12 chunks embedded across 3 documents/);
    assert.match(controller.nodes.diagPanel.innerHTML, /2 sources awaiting chunking; 1 failures awaiting retry/);
    assert.match(controller.nodes.diagPanel.innerHTML, /&lt;img src=x onerror=alert\(1\)&gt;/);
    assert.doesNotMatch(controller.nodes.diagPanel.innerHTML, /<img/);
    assert.match(controller.nodes.statusPills.innerHTML, /Search DB/);
    controller.state.status.indexing = null;
    controller.renderStatus();
    assert.doesNotMatch(controller.nodes.diagPanel.innerHTML, /failures awaiting retry/);
});
