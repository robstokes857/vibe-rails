import test from 'node:test';
import assert from 'node:assert/strict';
import { once } from 'node:events';
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

test('Local MCP tools render the available tools and selected item', () => {
    const controller = new McpController({});
    let html = '';
    controller.state = {
        localConnected: true,
        localSelected: 'get_board_card',
        localFilter: '',
        localTools: [
            { name: 'search_history', description: 'Search history.' },
            { name: 'get_board_card', description: 'Read a board card.' }
        ]
    };
    controller.nodes = {
        toolList: { set innerHTML(value) { html = value; } }
    };

    controller.renderLocalToolList();

    assert.match(html, /search_history/);
    assert.match(html, /mcp-tool-item selected" data-tool="get_board_card"/);
    assert.match(html, /Read a board card/);
    assert.doesNotMatch(html, /Python script tools/);
});

test('Remote frame data travels only over the port transferred in the ready handshake', async () => {
    const windowPosts = [];
    const frameWindow = { postMessage: (...args) => windowPosts.push(args) };
    const fetches = [];
    const controller = new McpController({});
    controller.state = {
        remoteTools: [{ name: 'echo' }],
        remoteConnected: true,
        remoteEndpoint: 'https://mcp.example/mcp',
        remoteHeaders: { Authorization: 'Bearer fixture' },
        remotePort: null,
        remotePending: null
    };
    controller.nodes = { frame: { contentWindow: frameWindow } };
    controller._fetchJson = async (url, init) => {
        fetches.push({ url, body: JSON.parse(init.body) });
        return { success: true, result: 'pong' };
    };
    const nextMessage = port => new Promise(resolve => { port.onmessage = event => resolve(event.data); });
    const ready = { source: frameWindow, data: { type: 'mcp-remote-ready' } };

    controller._sendToolsToFrame();
    controller._postToFrame({ type: 'mcp-remote-status', message: 'before ready' });
    controller._handleRemoteMessage({ source: {}, data: { type: 'mcp-remote-ready' } });
    assert.equal(windowPosts.length, 0, 'nothing is sent before the frame announces itself');

    controller._handleRemoteMessage(ready);
    assert.equal(windowPosts.length, 1);
    const [handshake, target, transfer] = windowPosts[0];
    assert.deepEqual(handshake, { type: 'mcp-remote-port' }, 'the wildcard-origin message carries no data');
    assert.equal(target, '*');
    assert.equal(transfer.length, 1);
    const framePort = transfer[0];

    assert.deepEqual(await nextMessage(framePort),
        { type: 'mcp-remote-tools', tools: [{ name: 'echo' }], message: '1 tool(s)' },
        'tools stashed before the handshake are flushed through the port');

    const result = nextMessage(framePort);
    framePort.postMessage({ type: 'mcp-remote-call', name: 'echo', args: { text: 'hi' } });
    assert.deepEqual(await result, { type: 'mcp-remote-result', name: 'echo', ok: true, output: 'pong' });
    assert.deepEqual(fetches, [{
        url: '/api/v1/mcp/tools/echo',
        body: {
            endpoint: 'https://mcp.example/mcp',
            headers: { Authorization: 'Bearer fixture' },
            arguments: { text: 'hi' }
        }
    }]);

    // Calls are accepted only over the port, never as window messages.
    await controller._handleRemoteMessage({ source: frameWindow, data: { type: 'mcp-remote-call', name: 'echo' } });
    assert.equal(fetches.length, 1);
    assert.equal(windowPosts.length, 1, 'data never goes through the window with target "*"');

    // A reloaded frame announces itself again: it gets a fresh port and the old one closes.
    const oldPortClosed = once(framePort, 'close');
    controller._handleRemoteMessage(ready);
    await oldPortClosed;
    assert.equal(windowPosts.length, 2);
    const reloadedPort = windowPosts[1][2][0];
    const status = nextMessage(reloadedPort);
    controller._postToFrame({ type: 'mcp-remote-status', message: 'after reload' });
    assert.deepEqual(await status, { type: 'mcp-remote-status', message: 'after reload' });

    const reloadedPortClosed = once(reloadedPort, 'close');
    controller.unload();
    await reloadedPortClosed;
    assert.equal(controller.state.remotePort, null);
    framePort.close();
    reloadedPort.close();
});
