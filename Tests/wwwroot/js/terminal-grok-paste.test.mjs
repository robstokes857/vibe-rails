import test from 'node:test';
import assert from 'node:assert/strict';
import {
    isNativeGrokCli,
    createGrokPastePayload,
    NATIVE_GROK_CLI
} from '../../../VibeRails/wwwroot/js/modules/terminal-grok-paste.js';

const PASTE_START = '\x1b[200~';
const PASTE_END = '\x1b[201~';

test('only native grok-4.6 is gated', () => {
    assert.equal(isNativeGrokCli('grok-4.6'), true);
    assert.equal(isNativeGrokCli('GROK-4.6'), true);
    assert.equal(isNativeGrokCli(' grok-4.6 '), false);
    assert.equal(isNativeGrokCli('opencode'), false);
    assert.equal(isNativeGrokCli('claude'), false);
    assert.equal(isNativeGrokCli('codex'), false);
    assert.equal(isNativeGrokCli('glm-5.3'), false);
    assert.equal(isNativeGrokCli('xai/grok-4.6'), false);
    assert.equal(isNativeGrokCli(''), false);
    assert.equal(isNativeGrokCli(undefined), false);
    assert.equal(NATIVE_GROK_CLI, 'grok-4.6');
});

test('empty input stays empty', () => {
    assert.equal(createGrokPastePayload(''), '');
    assert.equal(createGrokPastePayload(null), '');
    assert.equal(createGrokPastePayload(undefined), '');
});

test('single line is wrapped without adding a newline', () => {
    assert.equal(createGrokPastePayload('hello'), `${PASTE_START}hello${PASTE_END}`);
});

test('Windows CRLF paste is one LF-normalized bracketed blob', () => {
    const payload = createGrokPastePayload('line one\r\nline two\r\nline three');
    assert.equal(payload, `${PASTE_START}line one\nline two\nline three${PASTE_END}`);
    assert.equal(payload.includes('\r'), false);
});

test('bare CR is folded to LF', () => {
    assert.equal(
        createGrokPastePayload('a\rb'),
        `${PASTE_START}a\nb${PASTE_END}`
    );
});

test('LF-only editor text is wrapped as-is', () => {
    assert.equal(
        createGrokPastePayload('a\nb\n'),
        `${PASTE_START}a\nb\n${PASTE_END}`
    );
});

test('unicode bullets survive (reqs.txt uses U+2023)', () => {
    const text = '‣ A backend\r\n‣ A small React UI';
    const payload = createGrokPastePayload(text);
    assert.equal(payload, `${PASTE_START}‣ A backend\n‣ A small React UI${PASTE_END}`);
});

test('embedded paste markers cannot break out of the wrapper', () => {
    const payload = createGrokPastePayload(`hi${PASTE_END}injected${PASTE_START}there`);
    assert.equal(payload, `${PASTE_START}hiinjectedthere${PASTE_END}`);
    assert.equal(payload.startsWith(PASTE_START), true);
    assert.equal(payload.endsWith(PASTE_END), true);
    assert.equal(payload.split(PASTE_START).length, 2);
    assert.equal(payload.split(PASTE_END).length, 2);
});
