// Regression for the "scrollback lost at end of long live session" bug
// against the xterm.js integration in vibe-terminal.js (terminal-multitab).
//
// Symptom (Rob, session 8dd5fe21-2eaf-4622-a7ba-a070416ffa7d): after a long
// Claude Code live session in the VS Code extension's web terminal, scrolling
// up shows nothing — not even the early conversation. Server-side scrollback
// (C# emulator) is fine; the loss is on the xterm.js client side.
//
// Root cause: vibe-terminal.js `clearDisplay()` used to call xterm.js
// `terminal.clear()`, which by xterm.js v6 contract clears BOTH the visible
// buffer AND scrollback. `clearDisplay()` is reached on every shrink resize
// (via `resetDisplayOnly()` from `sendResizeToPty()`), so a font-size bump
// or container shrink mid-session wiped scrollback. Claude Code redraws its
// TUI in place with absolute CUP positioning and never re-scrolls history,
// so the scrollback stayed empty until end of session.
//
// Run: cd UITests && node --test tests/xterm-scrollback.spec.js

const { test } = require('node:test');
const assert = require('node:assert');
const { Terminal } = require('@xterm/headless');
const fs = require('node:fs');
const path = require('node:path');

const FIXTURE_PATH = path.resolve(
    __dirname, '..', '..', 'TerminalEmulator.Tests', 'fixtures',
    'session_8dd5fe21_full.bin');

const VIBE_TERMINAL_PATH = path.resolve(
    __dirname, '..', '..', 'VibeRails', 'wwwroot', 'js', 'modules',
    'vibe-terminal.js');

function feed(term, bytes) {
    return new Promise((resolve) => term.write(bytes, resolve));
}

async function replaySession(term) {
    const bytes = fs.readFileSync(FIXTURE_PATH);
    const chunkSize = 64 * 1024;
    for (let off = 0; off < bytes.length; off += chunkSize) {
        await feed(term, bytes.subarray(off, Math.min(off + chunkSize, bytes.length)));
    }
}

function extractMethodBody(source, methodName) {
    const re = new RegExp(`\\b${methodName}\\s*\\([^)]*\\)\\s*\\{`, 'g');
    const m = re.exec(source);
    if (!m) return null;
    let i = m.index + m[0].length;
    let depth = 1;
    while (i < source.length && depth > 0) {
        const ch = source[i++];
        if (ch === '{') depth++;
        else if (ch === '}') depth--;
    }
    return source.slice(m.index, i);
}

const fixtureExists = fs.existsSync(FIXTURE_PATH);
const skipNoFixture = fixtureExists ? false : `Fixture missing: ${FIXTURE_PATH}`;

// --- Source-level regression: clearDisplay must not use the API that wipes scrollback ---

test('vibe-terminal.js clearDisplay() must not call this._terminal.clear()', () => {
    const source = fs.readFileSync(VIBE_TERMINAL_PATH, 'utf8');
    const body = extractMethodBody(source, 'clearDisplay');
    assert.ok(body, 'clearDisplay method body not found in vibe-terminal.js');

    // xterm.js v6 Terminal.clear() clears scrollback. Long Claude sessions get
    // wiped to zero history when this fires from a shrink-resize. Use ED2 +
    // CUP home (`\\x1b[2J\\x1b[H`) — same visible cleanup, scrollback intact.
    assert.doesNotMatch(body, /this\._terminal\.clear\s*\(\s*\)/,
        'clearDisplay() must not call this._terminal.clear() — use term.write("\\x1b[2J\\x1b[H") instead. ' +
        'See UITests/tests/xterm-scrollback.spec.js for the full bug context.');
});

// --- Behavioral pinning: confirm the two ANSI / API contracts on real session bytes ---

test('xterm.js Terminal.clear() wipes scrollback (the API hazard we are avoiding)',
    { skip: skipNoFixture },
    async () => {
        const term = new Terminal({ cols: 165, rows: 34, scrollback: 20000, allowProposedApi: true });
        await replaySession(term);

        const beforeBaseY = term.buffer.active.baseY;
        assert.ok(beforeBaseY > 100,
            `session should produce scrollback; got baseY=${beforeBaseY}`);

        term.clear();

        const afterBaseY = term.buffer.active.baseY;
        // Pinning the upstream xterm.js v6 contract: Terminal.clear() collapses
        // the buffer to a single row. If this ever changes upstream we want a
        // failing test pointing us back to the rationale here.
        assert.strictEqual(afterBaseY, 0,
            `expected xterm.js Terminal.clear() to wipe scrollback, but baseY=${afterBaseY}`);
    });

test('CSI 2J + CUP home preserves scrollback (what clearDisplay does after the fix)',
    { skip: skipNoFixture },
    async () => {
        const term = new Terminal({ cols: 165, rows: 34, scrollback: 20000, allowProposedApi: true });
        await replaySession(term);

        const beforeBaseY = term.buffer.active.baseY;
        assert.ok(beforeBaseY > 100);

        await feed(term, '\x1b[2J\x1b[H');

        const afterBaseY = term.buffer.active.baseY;
        assert.strictEqual(afterBaseY, beforeBaseY,
            `ED2 + CUP home should keep scrollback intact; before=${beforeBaseY} after=${afterBaseY}`);
    });
