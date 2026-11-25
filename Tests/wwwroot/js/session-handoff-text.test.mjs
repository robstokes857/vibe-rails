// VIBE-363: Send to replays a whole recording through xterm.js and sends the rendered text.
import test from 'node:test';
import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
import {
    HANDOFF_MAX_INPUT_TOKENS, HandoffTooLargeError, decodeFrameData, estimateTokens, loadRecording,
    renderRecordingText, renderSessionHandoffText
} from '../../../VibeRails/wwwroot/js/modules/session-handoff-text.js';

// The browser bundle needs a DOM; @xterm/headless is the same 6.0 core (parser, buffers,
// markers) without the renderer. UITests installs it.
const headlessPath = fileURLToPath(new URL('../../../UITests/node_modules/@xterm/headless/lib-headless/xterm-headless.js', import.meta.url));
const Terminal = existsSync(headlessPath) ? createRequire(import.meta.url)(headlessPath).Terminal : null;
const skip = Terminal ? false : 'run npm ci in UITests to install @xterm/headless';
const createTerminal = options => new Terminal(options);

const encoder = new TextEncoder();
const frame = (text, at = 1000, cols = 80, rows = 24) => ({ at, data: encoder.encode(text), cols, rows });
const render = (frames, prompts = []) => renderRecordingText({ frames, prompts }, { createTerminal });

test('normal-buffer output beyond the scrollback is all kept, in order, once', { skip }, async () => {
    // 30,000 lines is three times the module's scrollback; 500 lines per frame.
    const frames = [];
    for (let start = 0; start < 30_000; start += 500) {
        let chunk = '';
        for (let line = start; line < start + 500; line++) chunk += `line ${line}\r\n`;
        frames.push(frame(chunk, 1000 + start));
    }
    const text = await render(frames);
    const lines = text.split('\n');
    assert.equal(lines.length, 30_000);
    lines.forEach((line, index) => assert.equal(line, `line ${index}`));
    assert.doesNotMatch(text, /scrolled away/);
});

test('each write is queued from the last one\'s callback, not behind a timer of its own', { skip }, async t => {
    // VIBE-370: a prompt between every frame makes 300 separate writes. Awaiting each write left
    // xterm's buffer empty, and xterm starts every write into an empty buffer with a setTimeout:
    // 4 ms each in a browser, about a second each in a throttled VS Code webview.
    const frames = [];
    const prompts = [];
    for (let i = 0; i < 300; i++) {
        frames.push(frame(`line ${i}\r\n`, 1000 + i * 10));
        prompts.push(1005 + i * 10);
    }
    const original = globalThis.setTimeout;
    let timers = 0;
    globalThis.setTimeout = (...args) => { timers++; return original(...args); };
    t.after(() => { globalThis.setTimeout = original; });
    const progress = [];
    const text = await renderRecordingText({ frames, prompts }, {
        createTerminal, onProgress: ({ phase, done, total }) => progress.push({ phase, percent: Math.floor(done / total * 100) })
    });
    globalThis.setTimeout = original;
    assert.deepEqual(text.split('\n'), frames.map((_, i) => `line ${i}`));
    // xterm still yields every 12 ms of parsing; this recording takes a few slices at most.
    assert.ok(timers < 30, `${timers} timers for 300 writes`);
    // Progress moves in whole percents and ends at 100.
    assert.ok(progress.every(entry => entry.phase === 'replaying'));
    assert.ok(progress.length > 50 && progress.length <= 101, `${progress.length} progress updates`);
    assert.ok(progress.every((entry, i) => i === 0 || entry.percent > progress[i - 1].percent));
    assert.equal(progress.at(-1).percent, 100);
});

test('a line that wraps keeps the space at the wrap point and reads as one line', { skip }, async () => {
    const long = 'abcdefghij '.repeat(20).trim(); // 219 characters at 20 columns.
    const text = await render([frame(`${long}\r\nnext\r\n`, 1000, 20, 5)]);
    assert.equal(text, `${long}\nnext`);
});

test('resizes are replayed: reflow and a taller window neither lose nor scramble lines', { skip }, async () => {
    const frames = [];
    const expected = [];
    let at = 1000;
    const write = (count, cols, rows) => {
        let chunk = '';
        for (let i = 0; i < count; i++) {
            const line = `row ${expected.length} ${'x'.repeat(expected.length % 150)}`.trimEnd();
            expected.push(line);
            chunk += `${line}\r\n`;
        }
        frames.push(frame(chunk, at++, cols, rows));
    };
    write(4000, 120, 30);
    write(4000, 60, 30);  // Narrower: every long line rewraps.
    write(4000, 160, 70); // Wider and taller: rows come back from the scrollback.
    write(4000, 100, 20);
    const lines = (await render(frames)).split('\n');
    // Growing the window may read a few pulled-in rows twice; every line is there, in order.
    let next = 0;
    for (const line of lines) if (line === expected[next]) next++;
    assert.equal(next, expected.length);
    assert.ok(lines.length - expected.length <= 70, `${lines.length - expected.length} repeated lines`);
});

test('ED3 clears the scrollback only after it was read', { skip }, async () => {
    let first = '';
    for (let i = 0; i < 200; i++) first += `before ${i}\r\n`;
    let second = '';
    for (let i = 0; i < 50; i++) second += `after ${i}\r\n`;
    const text = await render([frame(first, 1000, 80, 10), frame('\x1b[3J', 1001, 80, 10), frame(second, 1002, 80, 10)]);
    for (let i = 0; i < 200; i++) assert.match(text, new RegExp(`^before ${i}$`, 'm'));
    for (let i = 0; i < 50; i++) assert.match(text, new RegExp(`^after ${i}$`, 'm'));
    assert.equal(text.match(/^before 0$/gm).length, 1);
    assert.doesNotMatch(text, /scrolled away/);
});

test('a full-screen program is saved before each user message and when it exits', { skip }, async () => {
    const screen = text => `\x1b[2J\x1b[H${text}`;
    const frames = [
        frame('PS> codex\r\n', 1000),
        frame('\x1b[?1049h', 1001),
        frame(screen('• first answer\r\n› fix the tests'), 1002),
        // The user sent "fix the tests" at 1500.
        frame(screen('• tests fixed\r\n› commit'), 2000),
        frame(screen('• committed'), 3000),
        frame('\x1b[?1049l', 3001),
        frame('PS> exit\r\n', 3002)
    ];
    const text = await render(frames, [1500, 2500]);
    assert.match(text, /=== Full-screen view \(before the user's message at 00:00:01 UTC\) ===\n• first answer\n› fix the tests\n=== End of full-screen view ===/);
    assert.match(text, /=== Full-screen view \(before the user's message at 00:00:02 UTC\) ===\n• tests fixed\n› commit\n/);
    assert.match(text, /=== Full-screen view \(when the program left full-screen mode\) ===\n• committed\n/);
    assert.ok(text.indexOf('first answer') < text.indexOf('tests fixed'));
    assert.ok(text.indexOf('tests fixed') < text.indexOf('committed'));
    assert.match(text, /PS> codex/);
    assert.match(text, /PS> exit/);
});

test('a session still on the full screen ends with that screen, and repeats are kept once', { skip }, async () => {
    const frames = [frame('\x1b[?1049h\x1b[Hworking on it', 1000), frame('', 2000)];
    const text = await render(frames, [1500]);
    assert.equal(text.match(/working on it/g).length, 1);
    assert.match(text, /\(before the user's message at 00:00:01 UTC\)/);
});

test('a growing window during a full-screen program does not lose the shell scrollback', { skip }, async () => {
    let shell = '';
    for (let i = 0; i < 300; i++) shell += `shell ${i}\r\n`;
    const frames = [
        frame(shell, 1000, 80, 10),
        frame('\x1b[?1049h\x1b[Hscreen', 1001, 80, 10),
        frame('', 1002, 80, 40),                      // Taller while the program runs.
        frame('\x1b[?1049l\x1b[2J\x1b[Hcleared', 1003, 80, 40)
    ];
    const text = await render(frames);
    // Rows that had scrolled out are kept once. The last few never left the screen, and the
    // ED2 after the program exits erases them, as it does in a real terminal.
    for (let i = 0; i <= 290; i++) assert.equal(text.match(new RegExp(`^shell ${i}$`, 'gm'))?.length, 1, `shell ${i}`);
    assert.doesNotMatch(text, /scrolled away/);
    assert.match(text, /cleared$/);
});

test('the recording is paged from the replay API and resizes are aligned to their bytes', async () => {
    const requests = [];
    const base64 = text => Buffer.from(text).toString('base64');
    const request = async path => {
        requests.push(path);
        if (path === '/api/v1/session-replay/sessions/s%201') return {
            frameSource: 'raw', frameMaxId: 3, frameCount: 3, session: { started: 900 },
            prompts: [{ at: 2000 }, { at: 1500 }],
            geometry: [{ at: 1000, cols: 100, rows: 20, bytes: 4 }, { at: 1001, cols: 50, rows: 10, bytes: 4 }]
        };
        if (path.includes('after=0')) return { items: [{ id: 1, at: 1000, data: base64('abcd'), cols: 0, rows: 0 }], next: 1, done: false };
        return { items: [{ id: 2, at: 999, data: base64('efgh'), cols: 0, rows: 0 }], next: 3, done: true };
    };
    const { frames, prompts } = await loadRecording('s 1', { request });
    assert.deepEqual(requests, [
        '/api/v1/session-replay/sessions/s%201',
        '/api/v1/session-replay/sessions/s%201/frames?after=0&max=3&source=raw',
        '/api/v1/session-replay/sessions/s%201/frames?after=1&max=3&source=raw'
    ]);
    assert.deepEqual(frames.map(f => [new TextDecoder().decode(f.data), f.cols, f.rows, f.at]), [['abcd', 100, 20, 1000], ['efgh', 50, 10, 1000]]);
    assert.deepEqual(prompts, [1500, 2000]);
});

test('a recording that does not page forward is an error, not a loop', async () => {
    const request = async path => path.endsWith('/frames?after=0&max=9&source=raw')
        ? { items: [], next: 0, done: false }
        : { frameSource: 'raw', frameMaxId: 9, frameCount: 1, geometry: [], prompts: [] };
    await assert.rejects(loadRecording('s', { request }), /did not page forward/);
});

test('the token estimate is four UTF-8 bytes per token, like the servers', () => {
    assert.equal(estimateTokens(''), 0);
    assert.equal(estimateTokens('abcd'), 1);
    assert.equal(estimateTokens('abcde'), 2);
    assert.equal(estimateTokens('────'), 3);
    assert.equal(estimateTokens('😀'), 1);
    assert.equal(HANDOFF_MAX_INPUT_TOKENS, 800_000);
    assert.match(new HandoffTooLargeError(912_345).message, /about 912,345 tokens.*up to 800,000/);
});

test('frame data decodes from base64 or bytes', () => {
    assert.deepEqual([...decodeFrameData(Buffer.from('hi').toString('base64'))], [104, 105]);
    const bytes = new Uint8Array([1, 2]);
    assert.equal(decodeFrameData(bytes), bytes);
});

test('a session over the allowance is refused after the replay, before anything is sent', { skip }, async () => {
    // One frame of 3.2 MB plus a little: just over 800,000 estimated tokens.
    const line = `${'a'.repeat(1000)}\r\n`;
    const text = line.repeat(Math.ceil((HANDOFF_MAX_INPUT_TOKENS * 4 + 4000) / 1001));
    const request = async path => path.includes('/frames')
        ? { items: [{ id: 1, at: 1000, data: Buffer.from(text).toString('base64') }], next: 1, done: true }
        : { frameSource: 'raw', frameMaxId: 1, frameCount: 1, geometry: [{ at: 1000, cols: 1000, rows: 5, bytes: text.length }], prompts: [] };
    await assert.rejects(renderSessionHandoffText('s', { request, createTerminal }), error => {
        assert.ok(error instanceof HandoffTooLargeError);
        assert.ok(error.estimatedTokens > HANDOFF_MAX_INPUT_TOKENS);
        return true;
    });
});

test('an aborted replay stops', { skip }, async () => {
    const controller = new AbortController();
    controller.abort();
    await assert.rejects(renderRecordingText({ frames: [frame('x')] }, { createTerminal, signal: controller.signal }), /abort/i);
});
