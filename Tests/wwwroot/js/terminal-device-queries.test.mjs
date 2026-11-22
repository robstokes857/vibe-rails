import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import {
    COLOR_OSC_IDENTIFIERS,
    SUPPRESSED_CSI_QUERIES,
    SUPPRESSED_DCS_QUERIES,
    installServerAnsweredQueries,
    isColorQuery,
    windowsPtyOptionFor,
    BUNDLED_HOST_WINDOWS_PTY
} from '../../../VibeRails/wwwroot/js/modules/terminal-device-queries.js';
import { TerminalTab } from '../../../VibeRails/wwwroot/js/modules/terminal-tab.js';

// VIBE-354. A session on the bundled Windows console host has its terminal queries answered by
// the server, in the order the program asked them. If the viewer answers too, the second
// device-attributes answer reaches the program as the typed characters ESC [ ? 1 ; 2 c, a second
// cursor report is an F3 keypress, and any other answer arrives late, as stray input.

function fakeParser() {
    const registered = [];
    const register = (kind) => (id, callback) => {
        const entry = { kind, id, callback, disposed: false };
        registered.push(entry);
        return { dispose() { entry.disposed = true; } };
    };
    const key = (value) => typeof value === 'number'
        ? String(value) : `${value.prefix || ''}|${value.intermediates || ''}|${value.final}`;
    return {
        registered,
        registerCsiHandler: register('csi'),
        registerDcsHandler: register('dcs'),
        registerOscHandler: register('osc'),
        // What xterm.js does with a sequence: the newest handler that returns true wins.
        dispatch(kind, id, argument) {
            for (const entry of [...registered].reverse()) {
                if (!entry.disposed && entry.kind === kind && key(entry.id) === key(id)
                    && entry.callback(argument) === true) return 'handled';
            }
            return 'xterm';
        }
    };
}

test('the viewer answers no terminal query on a bundled-host session', () => {
    const parser = fakeParser();
    installServerAnsweredQueries({ parser });

    const csi = [
        [{ final: 'c' }, [0]],                                     // DA1
        [{ prefix: '>', final: 'c' }, [0]],                        // DA2
        [{ final: 'n' }, [5]],                                     // operating status
        [{ final: 'n' }, [6]],                                     // cursor position
        [{ prefix: '?', final: 'n' }, [6]],                        // extended cursor position
        [{ prefix: '?', intermediates: '$', final: 'p' }, [2026]], // private mode state
        [{ intermediates: '$', final: 'p' }, [4]]                  // ANSI mode state
    ];
    for (const [id, params] of csi) {
        assert.equal(parser.dispatch('csi', id, params), 'handled', JSON.stringify(id));
    }
    assert.equal(parser.dispatch('dcs', { intermediates: '$', final: 'q' }, 'm'), 'handled');
    for (const identifier of COLOR_OSC_IDENTIFIERS) {
        assert.equal(parser.dispatch('osc', identifier, identifier === 4 ? '1;?' : '?'), 'handled', `OSC ${identifier}`);
    }
});

test('everything that is not a question still reaches xterm.js', () => {
    const parser = fakeParser();
    installServerAnsweredQueries({ parser });

    // Setting a colour is not asking for one.
    assert.equal(parser.dispatch('osc', 10, 'rgb:ff/00/00'), 'xterm');
    assert.equal(parser.dispatch('osc', 11, '#1e1e1e'), 'xterm');
    assert.equal(parser.dispatch('osc', 4, '1;rgb:00/ff/00'), 'xterm');
    // Functions nobody registered for.
    assert.equal(parser.dispatch('csi', { final: 'H' }, [1, 1]), 'xterm');
    assert.equal(parser.dispatch('csi', { final: 'm' }, [0]), 'xterm');
    assert.equal(parser.dispatch('csi', { prefix: '?', final: 'h' }, [2026]), 'xterm');
    assert.equal(parser.dispatch('osc', 0, 'a title with a ? in it'), 'xterm');
    assert.equal(parser.dispatch('osc', 8, ';https://example.com/?q=1'), 'xterm');
});

test('a colour query is recognised by a question mark standing as a whole value', () => {
    assert.equal(isColorQuery('?'), true);
    assert.equal(isColorQuery('?;?'), true);
    assert.equal(isColorQuery('1;?'), true);
    assert.equal(isColorQuery('rgb:ff/00/00'), false);
    assert.equal(isColorQuery('1;rgb:ff/00/00'), false);
    assert.equal(isColorQuery(''), false);
    assert.equal(isColorQuery(undefined), false);
});

test('whatever the server answers, the viewer does not', () => {
    // TerminalQueryScanner.cs decides what the server answers. A query answered on both sides
    // types into the program.
    const scanner = fs.readFileSync(
        new URL('../../../VibeRails/Services/Terminal/Protocol/TerminalQueryScanner.cs', import.meta.url), 'utf8');
    const cases = [...scanner.matchAll(
        /\(\(byte\)'(\w)', (?:0|\(byte\)'(.)'), (?:0|\(byte\)'(.)'), (?:\d|_)\) => TerminalQuery\.(\w+)/g)]
        .map((match) => ({ final: match[1], prefix: match[2] || '', intermediates: match[3] || '', name: match[4] }));
    assert.equal(cases.length, 7, 'expected the seven server-answered queries in TerminalQueryScanner.cs');

    const parser = fakeParser();
    installServerAnsweredQueries({ parser });
    for (const item of cases) {
        const id = { final: item.final };
        if (item.prefix) id.prefix = item.prefix;
        if (item.intermediates) id.intermediates = item.intermediates;
        assert.equal(parser.dispatch('csi', id, [0]), 'handled', `the viewer would still answer ${item.name}`);
    }
});

test('restoring gives xterm.js its own replies back, once', () => {
    const parser = fakeParser();
    const restore = installServerAnsweredQueries({ parser });
    assert.equal(parser.registered.length,
        SUPPRESSED_CSI_QUERIES.length + SUPPRESSED_DCS_QUERIES.length + COLOR_OSC_IDENTIFIERS.length);

    restore();
    restore();
    assert.ok(parser.registered.every((entry) => entry.disposed));
    assert.equal(parser.dispatch('csi', { final: 'c' }, [0]), 'xterm');
});

test('a terminal without the parser API is left alone', () => {
    assert.doesNotThrow(() => installServerAnsweredQueries(null)());
    assert.doesNotThrow(() => installServerAnsweredQueries({})());
    assert.doesNotThrow(() => installServerAnsweredQueries({ parser: {} })());
});

test('windowsPty turns re-wrapping off for a bundled-host session and is left alone otherwise', () => {
    assert.deepEqual(windowsPtyOptionFor({ bundled: true }), { backend: 'winpty', buildNumber: 1 });
    assert.notEqual(windowsPtyOptionFor({ bundled: true }), BUNDLED_HOST_WINDOWS_PTY, 'a copy: xterm.js keeps the object');
    assert.ok(Object.isFrozen(BUNDLED_HOST_WINDOWS_PTY));
    // xterm.js's default. Every other session resizes exactly as before.
    assert.deepEqual(windowsPtyOptionFor({ bundled: false }), {});
    assert.deepEqual(windowsPtyOptionFor({ bundled: 'bundled' }), {});
    assert.deepEqual(windowsPtyOptionFor(), {});
});

// ---- TerminalTab: the host is settled before the viewer attaches ---------------------------

function createTab(state, { apiCall } = {}) {
    const events = [];
    const sockets = [];
    globalThis.sessionStorage = { getItem: () => null };
    globalThis.WebSocket = class {
        static OPEN = 1;
        constructor(url) {
            this.url = url;
            this.readyState = 0;
            sockets.push(this);
            events.push('socket');
        }
        close() { this.closed = true; }
    };
    const tab = Object.assign(Object.create(TerminalTab.prototype), {
        state: { id: 'tab-1', hasActiveSession: true, sessionId: 'session-1', ...state },
        isActive: false,
        _connectFocusTimeouts: [],
        _pendingResizeSyncTimeoutId: null,
        manager: {
            app: { apiCall: apiCall || (async () => { throw new Error('status was read although the host is known'); }) },
            updateUi() {},
            getWebSocketUrl: (id) => `ws://test/${id}`
        },
        ensureTerminal() {
            this.vibeTerminal ||= {
                cols: 120,
                rows: 30,
                fit() {},
                setConsoleHost: (host) => events.push(['host', host])
            };
        }
    });
    return { tab, events, sockets };
}

test('a session whose host is known attaches without a status read, host first', () => {
    const { tab, events, sockets } = createTab(
        { consoleHost: 'bundled', consoleHostSessionId: 'session-1' });

    void tab.connect();

    // Synchronous up to the socket, as connect() has always been, and the renderer was told
    // before any byte could arrive.
    assert.deepEqual(events, [['host', { bundled: true }], 'socket']);
    assert.equal(sockets.length, 1);
});

test('an unknown host is read from the tab status before the socket exists', async () => {
    const calls = [];
    const { tab, events } = createTab({}, {
        apiCall: async (url, method) => {
            calls.push([url, method]);
            return { hasActiveSession: true, sessionId: 'session-1', consoleHost: 'bundled' };
        }
    });

    const connecting = tab.connect();
    assert.deepEqual(events, [], 'nothing attaches until the host is known');
    await new Promise((resolve) => setImmediate(resolve));

    assert.deepEqual(calls, [['/api/v1/terminal/tabs/tab-1/status', 'GET']]);
    assert.deepEqual(events, [['host', { bundled: true }], 'socket']);
    assert.equal(tab.usesBundledConsoleHost(), true);
    void connecting;
});

test('a host remembered for another session is not trusted', async () => {
    // The tab's session was replaced by a Board launch or an Automation. The remembered answer
    // describes the old one.
    const { tab, events } = createTab(
        { consoleHost: 'bundled', consoleHostSessionId: 'earlier-session' },
        { apiCall: async () => ({ hasActiveSession: true, sessionId: 'session-1' }) });

    assert.equal(tab.usesBundledConsoleHost(), false);
    void tab.connect();
    await new Promise((resolve) => setImmediate(resolve));

    assert.deepEqual(events, [['host', { bundled: false }], 'socket']);
});

test('a failed status read still attaches, with xterm.js defaults', async () => {
    const { tab, events } = createTab({}, { apiCall: async () => { throw new Error('host unreachable'); } });

    void tab.connect();
    await new Promise((resolve) => setImmediate(resolve));

    assert.deepEqual(events, [['host', { bundled: false }], 'socket']);
});

test('a second connect during the status read leaves one socket', async () => {
    const pending = [];
    const { tab, sockets } = createTab({}, { apiCall: () => new Promise((resolve) => pending.push(resolve)) });

    const first = tab.connect();
    const second = tab.connect();
    assert.equal(pending.length, 2);
    pending[0]({ hasActiveSession: true, sessionId: 'session-1', consoleHost: 'bundled' });
    assert.equal(await first, false);
    pending[1]({ hasActiveSession: true, sessionId: 'session-1', consoleHost: 'bundled' });
    await new Promise((resolve) => setImmediate(resolve));

    assert.equal(sockets.length, 1);
    void second;
});

test('a session stopped during the status read is not attached', async () => {
    const pending = [];
    const { tab, sockets } = createTab({}, { apiCall: () => new Promise((resolve) => pending.push(resolve)) });

    const connecting = tab.connect();
    tab.state.hasActiveSession = false;
    pending[0]({ hasActiveSession: false });

    assert.equal(await connecting, false);
    assert.equal(sockets.length, 0);
});

test('a shrink does not clear the screen on the bundled host', () => {
    // The clear depends on the built-in host repainting the viewport after a resize. The bundled
    // host sends no repaint, so a cleared shell tab would stay blank.
    const shrink = (state) => {
        const { tab } = createTab(state);
        tab.isActive = true;
        tab.socket = { readyState: 1 };
        tab.lastResizeSignature = '120x30';
        return tab.shouldResetDisplayBeforeResize(100, 30);
    };

    assert.equal(shrink({ consoleHost: null, consoleHostSessionId: 'session-1' }), true);
    assert.equal(shrink({}), true);
    assert.equal(shrink({ consoleHost: 'bundled', consoleHostSessionId: 'session-1' }), false);
    // Remembered for a different session: not this session's host.
    assert.equal(shrink({ consoleHost: 'bundled', consoleHostSessionId: 'earlier-session' }), true);
});

test('a start response sets the host for its session and a stop forgets it', async () => {
    const { tab } = createTab({ hasActiveSession: false, sessionId: null });
    tab.disconnect = () => {};
    tab.connect = async () => true;
    tab.manager.app.apiCall = async (url) => url.endsWith('/start')
        ? { hasActiveSession: true, sessionId: 'session-2', cli: 'Claude', consoleHost: 'bundled' }
        : {};
    tab.manager.app.showError = () => {};

    await tab.startSession({ cli: 'claude' });
    assert.equal(tab.usesBundledConsoleHost(), true);
    assert.equal(tab.state.consoleHostSessionId, 'session-2');

    await tab.stopSession();
    assert.equal(tab.usesBundledConsoleHost(), false);
    assert.equal(tab.state.consoleHost, undefined);
});
