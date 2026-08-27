export const NATIVE_GROK_CLI = 'grok-4.6';

const PASTE_START = '\x1b[200~';
const PASTE_END = '\x1b[201~';

export function isNativeGrokCli(cli) {
    return (cli || '').toLowerCase() === NATIVE_GROK_CLI;
}

function stripEmbeddedPasteMarkers(text) {
    return text.split(PASTE_START).join('').split(PASTE_END).join('');
}

function normalizeNewlines(text) {
    return text.replace(/\r\n/g, '\n').replace(/\r/g, '\n');
}

/**
 * Grok Build's composer treats CR/LF as submit unless the paste is wrapped in
 * DEC bracketed-paste markers (`CSI 200~` … `CSI 201~`). Other TUIs (Claude,
 * Codex, OpenCode) emit `CSI ?2004h` so xterm.js sets `bracketedPasteMode` and
 * our generic wrapper kicks in. Native Grok still understands those paste
 * markers — grok.exe contains both `?2004h` and `[200~` — but on Windows
 * crossterm's "legacy Windows API" path does not implement EnableBracketedPaste,
 * so the CSI often never reaches xterm.js. Ctrl+V / Open-in-editor then inject
 * raw newlines and Grok submits each line.
 *
 * Force-wrap Grok pastes regardless of xterm's mode bit, and fold Windows CRLF
 * to LF inside the blob. Other CLIs keep the mode-gated path in vibe-terminal.js.
 */
export function createGrokPastePayload(text) {
    if (typeof text !== 'string' || text.length === 0) {
        return '';
    }

    const normalized = normalizeNewlines(stripEmbeddedPasteMarkers(text));
    return `${PASTE_START}${normalized}${PASTE_END}`;
}
