import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const modulePath = path.resolve('VibeRails/wwwroot/js/modules/mcp-controller.js');
const { McpController } = await import(pathToFileURL(modulePath).href);

test('Advertised behavior hints become badges, and unspecified ones stay silent', () => {
    const controller = new McpController({});

    assert.equal(controller.toolBadges({ name: 'x' }), '', 'no annotations, no claims');
    assert.equal(
        controller.toolBadges({ annotations: { readOnly: null, destructive: null } }), '',
        'unspecified hints are not filled in with the protocol defaults');

    const readOnly = controller.toolBadges({
        annotations: { readOnly: true, destructive: false, idempotent: true, openWorld: false }
    });
    assert.match(readOnly, /mcp-badge ok">Read-only/);
    assert.match(readOnly, /Repeat-safe/);
    assert.doesNotMatch(readOnly, /Reaches outside/);

    const destructive = controller.toolBadges({
        annotations: { readOnly: false, destructive: true, idempotent: false, openWorld: true }
    });
    assert.match(destructive, /mcp-badge bad">Destructive/);
    assert.match(destructive, /Reaches outside/);
    assert.doesNotMatch(destructive, /Read-only|Repeat-safe/);

    assert.match(
        controller.toolBadges({ annotations: { readOnly: false, destructive: false } }),
        /Writes/);
});

test('Local MCP tools render Python scripts in their own section', () => {
    const controller = new McpController({});
    let html = '';
    controller.state = {
        localConnected: true,
        localSelected: 'python_report',
        localFilter: '',
        localTools: [
            { name: 'search_history', description: 'Search history.', category: 'built-in' },
            {
                name: 'python_report',
                description: 'Create a report.',
                category: 'python-script',
                sourceName: 'report.py'
            }
        ]
    };
    controller.nodes = {
        toolList: { set innerHTML(value) { html = value; } }
    };

    controller.renderLocalToolList();

    assert.match(html, /Built-in tools[\s\S]*search_history/);
    assert.match(html, /Python script tools[\s\S]*python_report[\s\S]*report\.py/);
    assert.ok(html.indexOf('Built-in tools') < html.indexOf('Python script tools'));
});
