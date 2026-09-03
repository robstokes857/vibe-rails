import test from 'node:test';
import assert from 'node:assert/strict';
import {
    TerminalTab,
    normalizeCodexPasteNewlines
} from '../../../VibeRails/wwwroot/js/modules/terminal-tab.js';

const PASTE_START = '\x1b[200~';
const PASTE_END = '\x1b[201~';

function createPayload(cli, text) {
    const tab = Object.create(TerminalTab.prototype);
    tab.state = { cli };
    tab.vibeTerminal = {
        createBracketedPastePayload: (value) => `${PASTE_START}${value}${PASTE_END}`
    };
    return tab._createPastePayload(text);
}

test('Codex CRLF and bare CR become LF before bracketed-paste wrapping', () => {
    const clipboardText = 'first line\r\nsecond line\rthird line\nfourth line';

    assert.equal(
        createPayload('Codex', clipboardText),
        `${PASTE_START}first line\nsecond line\nthird line\nfourth line${PASTE_END}`
    );
    assert.equal(normalizeCodexPasteNewlines(clipboardText).includes('\r'), false);
});

test('Codex leading clipboard newlines cannot become submit-capable CR bytes', () => {
    assert.equal(
        createPayload('codex', '\r\n\r\nfollow-up'),
        `${PASTE_START}\n\nfollow-up${PASTE_END}`
    );
});

test('non-Codex generic paste payloads retain their established byte shape', () => {
    const clipboardText = 'first line\r\nsecond line\rthird line';

    assert.equal(
        createPayload('Claude', clipboardText),
        `${PASTE_START}${clipboardText}${PASTE_END}`
    );
});
