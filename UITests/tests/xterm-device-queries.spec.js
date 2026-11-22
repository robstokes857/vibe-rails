// VIBE-354: what the real xterm.js does with terminal queries, with and without the handlers a
// bundled-console-host session installs (VibeRails/wwwroot/js/modules/terminal-device-queries.js).
//
// A session on the bundled Windows console host has its queries answered by the server
// (TerminalQueryResponder.cs), in the order the program asked. xterm.js answers queries too
// unless told not to, and the tab forwards everything xterm.js emits to the PTY. A second
// device-attributes answer reaches the program as the typed characters ESC [ ? 1 ; 2 c; a second
// cursor report is an F3 keypress; any other answer arrives after the server's, as stray input.
//
// Run: cd UITests && npm run test:node   (or: node --test UITests/tests/xterm-device-queries.spec.js)

const { test } = require('node:test');
const assert = require('node:assert');
const fs = require('node:fs');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { Terminal } = require('@xterm/headless');

const MODULES = path.resolve(__dirname, '..', '..', 'VibeRails', 'wwwroot', 'js', 'modules');
const RESPONDER = path.resolve(__dirname, '..', '..', 'VibeRails', 'Services', 'Terminal', 'Pty', 'TerminalQueryResponder.cs');

const loadModule = () => import(pathToFileURL(path.join(MODULES, 'terminal-device-queries.js')).href);

function createTerminal() {
    const term = new Terminal({ cols: 80, rows: 24, allowProposedApi: true });
    const replies = [];
    term.onData((data) => replies.push(data));
    return { term, replies };
}

function feed(term, text) {
    return new Promise((resolve) => term.write(text, resolve));
}

const ESC = '\x1b';

// What the server answers, asked with the cursor at row 5, column 9 and nothing else changed.
// The third column is what xterm.js 6 says, which is what the server says too.
const SERVER_ANSWERED = [
    ['primary device attributes', `${ESC}[c`, `${ESC}[?1;2c`],
    ['secondary device attributes', `${ESC}[>c`, `${ESC}[>0;276;0c`],
    ['operating status', `${ESC}[5n`, `${ESC}[0n`],
    ['cursor position', `${ESC}[6n`, `${ESC}[5;9R`],
    ['extended cursor position', `${ESC}[?6n`, `${ESC}[?5;9R`],
    ['synchronized output mode', `${ESC}[?2026$p`, `${ESC}[?2026;2$y`],
    ['bracketed paste mode', `${ESC}[?2004$p`, `${ESC}[?2004;2$y`],
    ['alternate screen mode', `${ESC}[?1049$p`, `${ESC}[?1049;2$y`],
    ['auto-repeat mode', `${ESC}[?8$p`, `${ESC}[?8;3$y`],
    ['an unknown mode', `${ESC}[?2027$p`, `${ESC}[?2027;0$y`],
    ['keyboard action mode', `${ESC}[2$p`, `${ESC}[2;4$y`]
];

// Every other query a program might send. Some xterm.js answers, some it does not; on a
// bundled-host session it must answer none.
const OTHER_QUERIES = [
    `${ESC}[0c`, `${ESC}[>0c`, `${ESC}[=c`, `${ESC}[6;1n`, `${ESC}[?15n`, `${ESC}[?25n`,
    `${ESC}[4$p`, `${ESC}[20$p`, `${ESC}[?1$p`, `${ESC}[?7$p`, `${ESC}[?25$p`, `${ESC}[?1004$p`,
    `${ESC}P$qm${ESC}\\`, `${ESC}P$q q${ESC}\\`, `${ESC}P+q544e${ESC}\\`,
    `${ESC}]4;1;?${ESC}\\`, `${ESC}]10;?\x07`, `${ESC}]11;?${ESC}\\`, `${ESC}]12;?\x07`, `${ESC}]10;?;?\x07`,
    `${ESC}[>q`, `${ESC}[>0q`, `${ESC}[?u`, `${ESC}[14t`, `${ESC}[16t`, `${ESC}[18t`, `${ESC}[21t`
];

test('xterm.js answers these queries by default (the second answer we must not send)', async () => {
    const { term, replies } = createTerminal();
    try {
        await feed(term, `${ESC}[5;9H`);
        for (const [name, query, reply] of SERVER_ANSWERED) {
            replies.length = 0;
            await feed(term, query);
            assert.deepStrictEqual(replies, [reply], name);
        }
    } finally { term.dispose(); }
});

test('the server identifies itself the way xterm.js does', () => {
    // The program should not be able to tell which side answered. If an xterm.js upgrade changes
    // one of these, the previous test fails first and this one names the file to update.
    const responder = fs.readFileSync(RESPONDER, 'utf8');
    for (const literal of ['"\\u001b[?1;2c"u8', '"\\u001b[>0;276;0c"u8', '"\\u001b[0n"u8']) {
        assert.ok(responder.includes(literal), `TerminalQueryResponder.cs no longer replies with ${literal}`);
    }
});

test('on a bundled-host session xterm.js answers no query at all', async () => {
    const { installServerAnsweredQueries } = await loadModule();
    const { term, replies } = createTerminal();
    try {
        installServerAnsweredQueries(term);
        await feed(term, `${ESC}[5;9H`);
        for (const [, query] of SERVER_ANSWERED) {
            await feed(term, query);
        }
        for (const query of OTHER_QUERIES) {
            await feed(term, query);
        }
        // Split across writes, as a slow program or a small read might deliver it.
        await feed(term, `${ESC}[`);
        await feed(term, '6n');
        assert.deepStrictEqual(replies.map((reply) => JSON.stringify(reply)), [],
            'the viewer answered a terminal query on a session whose server answers them');

        // The query bytes were still consumed as sequences, not printed, and nothing they are
        // not allowed to change was changed: the row is empty and the cursor has not moved.
        assert.strictEqual(term.buffer.active.getLine(4).translateToString(true), '');
        assert.strictEqual(term.buffer.active.cursorX, 8);
        assert.strictEqual(term.buffer.active.cursorY, 4);
    } finally { term.dispose(); }
});

test('everything that is not a question still works on a bundled-host session', async () => {
    const { installServerAnsweredQueries } = await loadModule();
    const { term, replies } = createTerminal();
    try {
        installServerAnsweredQueries(term);
        // Modes still switch, text still prints, and a colour can still be set.
        await feed(term, `${ESC}[?2004h${ESC}[?1049h${ESC}[3;3Hhello${ESC}]10;rgb:ff/00/00\x07${ESC}]0;title ?\x07`);
        assert.strictEqual(term.modes.bracketedPasteMode, true);
        assert.strictEqual(term.buffer.active.type, 'alternate');
        assert.strictEqual(term.buffer.active.getLine(2).translateToString(true), '  hello');
        assert.deepStrictEqual(replies, []);
        // What the user types is not a reply and is never held back.
        term.input('x', true);
        assert.deepStrictEqual(replies, ['x']);
    } finally { term.dispose(); }
});

test('the handlers survive the reset a reconnect does before replaying a snapshot', async () => {
    const { installServerAnsweredQueries } = await loadModule();
    const { term, replies } = createTerminal();
    try {
        installServerAnsweredQueries(term);
        term.reset(); // vibe-terminal.js resetForSnapshotReplay()
        for (const [, query] of SERVER_ANSWERED) {
            await feed(term, query);
        }
        assert.deepStrictEqual(replies, []);
    } finally { term.dispose(); }
});

test('restoring gives the viewer its replies back for a session on another host', async () => {
    const { installServerAnsweredQueries } = await loadModule();
    const { term, replies } = createTerminal();
    try {
        const restore = installServerAnsweredQueries(term);
        restore();
        await feed(term, `${ESC}[c${ESC}[?2026$p`);
        assert.deepStrictEqual(replies, [`${ESC}[?1;2c`, `${ESC}[?2026;2$y`]);
    } finally { term.dispose(); }
});

// ---- Resizing -----------------------------------------------------------------------------
// The bundled host repaints nothing after a resize. It asks where the cursor is and the server
// answers from its emulator (TerminalEmulator.TerminalBuffer.Resize), which keeps every cell where
// it is and clamps the cursor: row = min(row, rows), column = min(column, columns). The viewer
// has to end up with the same cursor or the next thing the program draws lands somewhere else.
// The same table is checked against the emulator in TerminalQueryResponderTests.cs
// (TheEmulatorClampsItsCursorOnAResizeTheWayTheViewerDoes); keep the two in step.
const RESIZES = [
    // columns, rows, cursor row, cursor column (1-based) -> new columns, new rows
    { from: [188, 36], cursor: [5, 134], to: [145, 23] },
    { from: [188, 36], cursor: [5, 180], to: [145, 23] },
    { from: [40, 10], cursor: [10, 8], to: [40, 6] },
    { from: [40, 10], cursor: [3, 8], to: [60, 20] },
    { from: [40, 10], cursor: [10, 40], to: [20, 4] },
    { from: [80, 24], cursor: [24, 1], to: [80, 30] }
];

// Paints the way a snapshot on attach does: every row positioned with CUP and written full width.
async function paintFullWidth(term, cols, rows) {
    for (let row = 1; row <= rows; row++) await feed(term, `${ESC}[${row};1H${' '.repeat(cols)}`);
}

function bundledTerminal(module, cols, rows, extra = {}) {
    return new Terminal({
        cols, rows, scrollback: 100, allowProposedApi: true,
        windowsPty: module.windowsPtyOptionFor({ bundled: true }), ...extra
    });
}

test('after a resize the viewer has its cursor where the server emulator has it', async () => {
    const module = await loadModule();
    for (const { from, cursor, to } of RESIZES) {
        const term = bundledTerminal(module, from[0], from[1]);
        try {
            await paintFullWidth(term, from[0], from[1]);
            await feed(term, `${ESC}[${cursor[0]};${cursor[1]}H`);
            term.resize(to[0], to[1]);
            assert.deepStrictEqual(
                [term.buffer.active.cursorY + 1, term.buffer.active.cursorX + 1],
                [Math.min(cursor[0], to[1]), Math.min(cursor[1], to[0])],
                `${from.join('x')} cursor ${cursor.join(';')} -> ${to.join('x')}`);
        } finally { term.dispose(); }
    }
});

test('a narrower terminal re-wraps nothing, where xterm.js with a current ConPTY build moves the prompt 22 rows', async () => {
    // Measured live: 188x36 to 145x23, prompt on row 5. Every row had been painted full width by
    // the attach snapshot, xterm.js wrapped each of them, and the cursor ended on the last row
    // with four rows scrolled away, while the host was told row 5 and drew the next key there.
    const module = await loadModule();
    const shrink = async (windowsPty) => {
        const term = new Terminal({ cols: 188, rows: 36, scrollback: 100, allowProposedApi: true, windowsPty });
        try {
            await paintFullWidth(term, 188, 36);
            await feed(term, `${ESC}[5;1H${'P'.repeat(133)}`);
            term.resize(145, 23);
            const buffer = term.buffer.active;
            return { row: buffer.cursorY + 1, column: buffer.cursorX + 1, scrolled: buffer.baseY,
                promptRow: buffer.getLine(buffer.baseY + buffer.cursorY).translateToString(true, 0, 145).trimEnd() };
        } finally { term.dispose(); }
    };

    const bundled = await shrink(module.windowsPtyOptionFor({ bundled: true }));
    assert.deepStrictEqual(bundled, { row: 5, column: 134, scrolled: 0, promptRow: 'P'.repeat(133) });

    const reflowing = await shrink({ backend: 'conpty', buildNumber: 26200 });
    assert.deepStrictEqual([reflowing.row, reflowing.scrolled], [23, 4],
        'xterm.js no longer re-wraps here with a current ConPTY build; revisit BUNDLED_HOST_WINDOWS_PTY');
});

test('a shorter terminal keeps the line the cursor is on', async () => {
    const module = await loadModule();
    const term = bundledTerminal(module, 40, 10);
    try {
        for (let line = 1; line <= 9; line++) await feed(term, `line ${line}\r\n`);
        await feed(term, 'prompt>');
        term.resize(40, 6);
        const buffer = term.buffer.active;
        assert.deepStrictEqual([buffer.cursorY + 1, buffer.cursorX + 1], [6, 8]);
        assert.strictEqual(buffer.getLine(buffer.baseY + buffer.cursorY).translateToString(true), 'prompt>');
        assert.strictEqual(buffer.getLine(buffer.baseY).translateToString(true), 'line 5', 'the top rows scrolled away');
    } finally { term.dispose(); }
});

test('a taller terminal gets its new rows at the bottom', async () => {
    // Without the option xterm.js pulls scrollback back into view and moves its cursor down; the
    // console host and the server emulator both leave the cursor where it was.
    const module = await loadModule();
    const grow = async (windowsPty) => {
        const term = new Terminal({ cols: 40, rows: 5, scrollback: 100, allowProposedApi: true, windowsPty });
        try {
            for (let line = 1; line <= 12; line++) await feed(term, `line ${line}\r\n`);
            const before = term.buffer.active.cursorY;
            term.resize(40, 8);
            return { before, after: term.buffer.active.cursorY };
        } finally { term.dispose(); }
    };

    const bundled = await grow(module.windowsPtyOptionFor({ bundled: true }));
    assert.strictEqual(bundled.after, bundled.before, 'the cursor row must not move when rows are added');

    const xtermDefault = await grow(module.windowsPtyOptionFor({ bundled: false }));
    assert.ok(xtermDefault.after > xtermDefault.before,
        'expected the default growth to pull scrollback down; if xterm.js changed this, revisit windowsPty');
});

test('text beyond the right edge is hidden, not lost, and comes back when the terminal is widened', async () => {
    const module = await loadModule();
    const term = bundledTerminal(module, 20, 5);
    try {
        await feed(term, 'ABCDEFGHIJKLMNOPQRS\r\nsecond');
        term.resize(10, 5);
        assert.deepStrictEqual([term.buffer.active.cursorY + 1, term.buffer.active.cursorX + 1], [2, 7]);
        assert.strictEqual(term.buffer.active.getLine(0).translateToString(true, 0, 10), 'ABCDEFGHIJ');
        assert.strictEqual(term.buffer.active.getLine(1).isWrapped, false, 'nothing was wrapped onto the next row');
        term.resize(20, 5);
        assert.strictEqual(term.buffer.active.getLine(0).translateToString(true), 'ABCDEFGHIJKLMNOPQRS');
        assert.strictEqual(term.buffer.active.getLine(1).translateToString(true), 'second');
    } finally { term.dispose(); }
});

test('line wraps are the ones the program produced, not guessed from the last column', async () => {
    // Output reaches the viewer unmodified on this host, so a line that fills the row and then
    // ends is not a wrapped line. With an old ConPTY build xterm.js guesses that it is, and a
    // copy of a framed TUI comes out as one long line.
    const module = await loadModule();
    const wraps = async (windowsPty) => {
        const term = new Terminal({ cols: 10, rows: 6, scrollback: 100, allowProposedApi: true, windowsPty });
        try {
            await feed(term, 'ABCDEFGHIJ\r\nnext\r\nKLMNOPQRSTUVWXY');
            const line = (index) => term.buffer.active.getLine(index).isWrapped;
            return { afterFullRow: line(1), realWrap: line(3) };
        } finally { term.dispose(); }
    };

    assert.deepStrictEqual(await wraps(module.windowsPtyOptionFor({ bundled: true })), { afterFullRow: false, realWrap: true });
    assert.deepStrictEqual(await wraps({ backend: 'conpty', buildNumber: 19045 }), { afterFullRow: true, realWrap: true },
        'xterm.js no longer guesses wraps for an old ConPTY build; BUNDLED_HOST_WINDOWS_PTY could then name conpty');
});
