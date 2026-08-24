import test from 'node:test';
import assert from 'node:assert/strict';
import {
    translateOpenCodeMouseWheel,
    OPENCODE_RIGHT_PANE_COL_RATIO,
    OPENCODE_INPUT_GUARD_ROWS
} from '../../../VibeRails/wwwroot/js/modules/terminal-opencode-wheel.js';

const COLS = 120;
const ROWS = 30;
const RIGHT_COL = Math.floor(COLS * OPENCODE_RIGHT_PANE_COL_RATIO) + 1;
const LEFT_COL = 20;
const MID_ROW = 12;
const INPUT_ROW = ROWS - OPENCODE_INPUT_GUARD_ROWS + 1;

function wheel(button, col, row) {
    return `\x1b[<${button};${col};${row}M`;
}

function translate(data, extras = {}) {
    return translateOpenCodeMouseWheel(data, { cli: 'opencode', cols: COLS, rows: ROWS, ...extras });
}

test('left-pane wheel up becomes PageUp', () => {
    assert.equal(translate(wheel(64, LEFT_COL, MID_ROW)), '\x1b[5~');
});

test('left-pane wheel down becomes PageDown', () => {
    assert.equal(translate(wheel(65, LEFT_COL, MID_ROW)), '\x1b[6~');
});

test('right-pane wheel keeps SGR coordinates', () => {
    const event = wheel(65, RIGHT_COL, MID_ROW);
    assert.equal(translate(event), event);
});

test('right-pane input-row wheel still becomes PageDown', () => {
    assert.equal(translate(wheel(65, RIGHT_COL, INPUT_ROW)), '\x1b[6~');
});

test('glm-5.2 and glm-5.3 use the same gate', () => {
    const left = wheel(64, LEFT_COL, MID_ROW);
    const right = wheel(65, RIGHT_COL, MID_ROW);
    assert.equal(translate(left, { cli: 'glm-5.2' }), '\x1b[5~');
    assert.equal(translate(right, { cli: 'GLM-5.3' }), right);
});

test('non-OpenCode CLIs are unchanged', () => {
    const event = wheel(64, LEFT_COL, MID_ROW);
    assert.equal(translateOpenCodeMouseWheel(event, { cli: 'claude', cols: COLS, rows: ROWS }), event);
    assert.equal(translateOpenCodeMouseWheel(event, { cli: 'codex', cols: COLS, rows: ROWS }), event);
    assert.equal(translateOpenCodeMouseWheel(event, { cli: 'grok-4.6', cols: COLS, rows: ROWS }), event);
});

test('clicks and keystrokes are unchanged', () => {
    assert.equal(translate('\x1b[<0;80;12M'), '\x1b[<0;80;12M');
    assert.equal(translate('hello'), 'hello');
});

test('missing geometry falls back to translating every wheel', () => {
    assert.equal(translate(wheel(65, RIGHT_COL, MID_ROW), { cols: 0, rows: 0 }), '\x1b[6~');
});
