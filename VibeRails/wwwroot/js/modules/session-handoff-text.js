// Send to (VIBE-363): play a session's whole recording through an off-screen xterm.js and keep
// the text it rendered, as the input for a handoff. This is the terminal's own rendering, not a
// conversation parser: nothing is filtered, and redraw artifacts or repeated history are expected.
//
// Two things a plain "read the buffer at the end" would lose are handled here:
// - Scrollback is bounded, so lines are collected as they scroll out of the viewport, before
//   xterm trims them. A marker on the first uncollected line follows trimming and reflow.
// - Full-screen programs (Codex, Grok, OpenCode) draw on the alternate screen, which has no
//   scrollback. Its text is saved before each user message, before the program leaves the
//   alternate screen, and at the end.

import { prepareFrames } from '../../session-replay/timeline.mjs';

/** Owner decision (2026-11-23): about 800,000 tokens of session text. viberails.ai enforces it too. */
export const HANDOFF_MAX_INPUT_TOKENS = 800_000;

// Lines kept in xterm's scrollback between collections. Writes are sized so one batch cannot
// push more than this past the collection point (see MAX_BATCH_*).
const SCROLLBACK = 10_000;
const MAX_BATCH_BYTES = 64 * 1024;
const MAX_BATCH_LINE_FEEDS = 2_000;
const ALTERNATE_SCREEN_MODES = new Set([47, 1047, 1049]);
const LOST_LINES_NOTE = '[Some earlier output scrolled away before it could be read.]';

export class HandoffTooLargeError extends Error {
    constructor(estimatedTokens, maxTokens = HANDOFF_MAX_INPUT_TOKENS) {
        super(`This session is about ${estimatedTokens.toLocaleString('en-US')} tokens of text. `
            + `Send to accepts up to ${maxTokens.toLocaleString('en-US')}, so nothing was sent.`);
        this.name = 'HandoffTooLargeError';
        this.estimatedTokens = estimatedTokens;
        this.maxTokens = maxTokens;
    }
}

/** Four UTF-8 bytes per token, the estimate viberails.ai and the local route also use. */
export function estimateTokens(text) {
    let bytes = 0;
    for (let i = 0; i < text.length; i++) {
        const code = text.charCodeAt(i);
        if (code < 0x80) bytes += 1;
        else if (code < 0x800) bytes += 2;
        else if (code >= 0xD800 && code <= 0xDBFF && i + 1 < text.length
            && text.charCodeAt(i + 1) >= 0xDC00 && text.charCodeAt(i + 1) <= 0xDFFF) { bytes += 4; i++; }
        else bytes += 3;
    }
    return Math.ceil(bytes / 4);
}

export function decodeFrameData(data) {
    if (data instanceof Uint8Array) return data;
    if (typeof data !== 'string') return new Uint8Array(data ?? []);
    if (typeof Uint8Array.fromBase64 === 'function') return Uint8Array.fromBase64(data);
    const binary = atob(data);
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
    return bytes;
}

/**
 * Reads the recording through the session-replay API, which pages without a total cap.
 * `request(path, signal)` returns parsed JSON.
 */
export async function loadRecording(sessionId, { request, signal, onProgress } = {}) {
    const id = encodeURIComponent(sessionId);
    const manifest = await request(`/api/v1/session-replay/sessions/${id}`, signal);
    const source = manifest.frameSource === 'enriched' ? 'enriched' : 'raw';
    const raw = [];
    let cursor = 0;
    while (cursor < manifest.frameMaxId) {
        const page = await request(`/api/v1/session-replay/sessions/${id}/frames?after=${cursor}&max=${manifest.frameMaxId}&source=${source}`, signal);
        for (const frame of page.items ?? []) raw.push({ ...frame, data: decodeFrameData(frame.data) });
        onProgress?.({ phase: 'loading', done: raw.length, total: manifest.frameCount || raw.length });
        if (page.done) break;
        if (!(page.next > cursor)) throw new Error('The recording did not page forward. Try again.');
        cursor = page.next;
    }
    // Resizes are applied where the recording captured them; one fixed grid garbles the text.
    const frames = prepareFrames(raw, manifest.geometry ?? [], source);
    let previous = manifest.session?.started ?? 0;
    for (const frame of frames) { frame.at = Math.max(previous, frame.at ?? 0); previous = frame.at; }
    const prompts = (manifest.prompts ?? []).map(prompt => prompt.at).filter(Number.isFinite).sort((a, b) => a - b);
    return { frames, prompts };
}

/**
 * Replays the frames and returns the rendered text. `createTerminal(options)` returns an xterm.js
 * Terminal; it is never opened, so nothing is drawn.
 */
export async function renderRecordingText({ frames, prompts = [] }, { createTerminal, signal, onProgress } = {}) {
    const first = frames[0];
    const term = createTerminal({
        cols: dimension(first?.cols, 120), rows: dimension(first?.rows, 30),
        scrollback: SCROLLBACK, allowProposedApi: true, disableStdin: true, convertEol: false
    });
    const parts = [];
    // Where reading resumes in the normal buffer. Between resizes a marker sits on the first row
    // of the last line read: xterm moves it as rows are trimmed and disposes it when its row is
    // trimmed or erased, so it is kept above the viewport, where nothing erases. A reflow that also
    // trims a full scrollback leaves markers on the wrong row (xterm 6.0), so a resize carries the
    // position across as a count of unread lines above the cursor, which reflow keeps in place.
    let marker = null;
    // The first unread row while a full-screen program runs: markers can only be placed on the
    // normal buffer while it is active, and nothing writes to it meanwhile.
    let pendingBoundary = null;
    let markerResetExpected = false;
    let lastScreen = '';

    const isAlternate = () => term.buffer.active.type === 'alternate';

    const placeMarker = (line) => {
        marker?.dispose();
        marker = null;
        // Markers are placed relative to the cursor, and only on the normal buffer.
        if (line < 0 || isAlternate()) return;
        const buffer = term.buffer.normal;
        marker = term.registerMarker(line - (buffer.baseY + buffer.cursorY)) ?? null;
    };

    const cursorLineStart = () => {
        const buffer = term.buffer.normal;
        return logicalLineStart(buffer, buffer.baseY + buffer.cursorY);
    };

    const firstUncollectedLine = () => {
        const buffer = term.buffer.normal;
        if (pendingBoundary !== null) return pendingBoundary;
        if (!marker) return 0;
        if (marker.isDisposed || marker.line < 0) {
            // ED3 and RIS trim the marker away on purpose; anything else means rows were trimmed
            // before they were read.
            if (!markerResetExpected) parts.push(LOST_LINES_NOTE);
            marker = null;
            return 0;
        }
        return logicalLineEnd(buffer, marker.line);
    };

    // A marker on the line before `boundary`, or none when reading resumes at row 0.
    const markBoundary = (boundary) => {
        const buffer = term.buffer.normal;
        placeMarker(boundary > 0 ? logicalLineStart(buffer, boundary - 1) : -1);
    };

    // The count is taken and turned back into a row while the cursor is the normal buffer's own.
    // If growing the window pulled read rows back into the viewport, reading resumes at its top:
    // those rows may be read twice, but nothing is lost.
    const resize = (cols, rows) => {
        const buffer = term.buffer.normal;
        const unread = countLines(buffer, firstUncollectedLine(), cursorLineStart());
        marker?.dispose();
        marker = null;
        pendingBoundary = null;
        term.resize(cols, rows);
        const boundary = Math.min(linesBefore(buffer, cursorLineStart(), unread), logicalLineStart(buffer, buffer.baseY));
        if (isAlternate()) pendingBoundary = boundary;
        else markBoundary(boundary);
    };

    // Leaving a full-screen program: the rows have not changed since the boundary was found.
    const anchorAfterAlternate = () => {
        if (pendingBoundary === null || isAlternate()) return;
        const boundary = pendingBoundary;
        pendingBoundary = null;
        markBoundary(boundary);
    };

    // Appends normal-buffer rows [first uncollected, end). `end` is the first row of a line.
    const collect = (end, { mark = true } = {}) => {
        const buffer = term.buffer.normal;
        const start = firstUncollectedLine();
        pendingBoundary = null;
        markerResetExpected = false;
        end = Math.min(end, buffer.length);
        if (end <= start) return;
        let text = readLines(buffer, start, end);
        // Reading to the end of the buffer includes the screen's empty rows below the output.
        if (!mark) text = text.trimEnd();
        if (text) parts.push(text);
        if (mark) placeMarker(logicalLineStart(buffer, end - 1));
        else { marker?.dispose(); marker = null; }
    };

    // Rows above the viewport no longer change. Never split a wrapped line across collections.
    const collectScrollback = () => {
        if (isAlternate()) return;
        const buffer = term.buffer.normal;
        collect(logicalLineStart(buffer, buffer.baseY));
    };

    // Row by row: a full-screen program lays out rows, and one that fills a row to the last column
    // leaves the next marked as a wrap, which would join the whole screen into a few long lines.
    const saveScreen = (label) => {
        const buffer = term.buffer.alternate;
        const rows = [];
        for (let i = 0; i < buffer.length; i++) rows.push(buffer.getLine(i)?.translateToString(true).trimEnd() ?? '');
        const text = trimBlankLines(rows.join('\n'));
        if (!text || text === lastScreen) return;
        lastScreen = text;
        parts.push(`=== Full-screen view (${label}) ===\n${text}\n=== End of full-screen view ===`);
    };

    // Parser hooks run before xterm's own handler (returning false lets it run).
    const hooks = [
        // Entering full-screen mode: keep the scrollback so far ahead of the program's screens.
        term.parser.registerCsiHandler({ prefix: '?', final: 'h' }, params => {
            if (!isAlternate() && hasAlternateScreenMode(params)) collectScrollback();
            return false;
        }),
        term.parser.registerCsiHandler({ prefix: '?', final: 'l' }, params => {
            if (isAlternate() && hasAlternateScreenMode(params)) saveScreen('when the program left full-screen mode');
            return false;
        }),
        // ED3 erases the scrollback: read all of it first (splitting a line that runs into the
        // viewport). xterm then trims the marker away, and the remaining rows start at row 0.
        term.parser.registerCsiHandler({ final: 'J' }, params => {
            if (params[0] === 3 && !isAlternate()) {
                collect(term.buffer.normal.baseY);
                markerResetExpected = true;
            }
            return false;
        }),
        // RIS resets both buffers: keep what was there.
        term.parser.registerEscHandler({ final: 'c' }, () => {
            if (isAlternate()) saveScreen('before the terminal was reset');
            collect(term.buffer.normal.length, { mark: false });
            lastScreen = '';
            return false;
        }),
        // A resize during a full-screen program also reflows the normal buffer, but a marker can
        // only be placed while it is active: anchor as soon as the program leaves.
        term.buffer.onBufferChange(() => anchorAfterAlternate())
    ];

    try {
        const totalBytes = frames.reduce((sum, frame) => sum + frame.data.length, 0);
        let writtenBytes = 0;
        let reportedPercent = -1;
        let promptIndex = 0;
        let index = 0;
        let pieces = null;
        let batchBytes = 0;

        // Progress in whole percent, so the status moves steadily without a timer.
        const reportProgress = () => {
            const percent = totalBytes ? Math.floor(writtenBytes / totalBytes * 100) : 100;
            if (percent <= reportedPercent) return;
            reportedPercent = percent;
            onProgress?.({ phase: 'replaying', done: writtenBytes, total: totalBytes });
        };

        // The next run of frames that share one window size and do not cross a user message,
        // after keeping the full screen from before that message and applying the size.
        const startBatch = () => {
            const frame = frames[index];
            // A user message was sent before this output: keep the full screen the user was looking at.
            if (promptIndex < prompts.length && prompts[promptIndex] < frame.at) {
                if (isAlternate()) saveScreen(`before the user's message at ${clock(prompts[promptIndex])}`);
                while (promptIndex < prompts.length && prompts[promptIndex] < frame.at) promptIndex++;
            }
            const cols = dimension(frame.cols, 120);
            const rows = dimension(frame.rows, 30);
            if (cols !== term.cols || rows !== term.rows) resize(cols, rows);

            const nextPrompt = prompts[promptIndex] ?? Infinity;
            const batch = [];
            let bytes = 0;
            let lineFeeds = 0;
            while (index < frames.length) {
                const next = frames[index];
                if (batch.length && (dimension(next.cols, 120) !== cols || dimension(next.rows, 30) !== rows || next.at > nextPrompt)) break;
                const feeds = countLineFeeds(next.data);
                if (batch.length && (bytes + next.data.length > MAX_BATCH_BYTES || lineFeeds + feeds > MAX_BATCH_LINE_FEEDS)) break;
                batch.push(next.data);
                bytes += next.data.length;
                lineFeeds += feeds;
                index++;
            }
            pieces = splitBatch(batch, bytes);
            batchBytes = bytes;
        };

        // The next piece to write, or null at the end of the recording.
        const nextPiece = () => {
            for (;;) {
                if (pieces) {
                    const step = pieces.next();
                    if (!step.done) return step.value;
                    pieces = null;
                    writtenBytes += batchBytes;
                    reportProgress();
                }
                if (index >= frames.length) return null;
                throwIfAborted(signal);
                startBatch();
            }
        };

        // Each piece is queued from the previous piece's write callback, which xterm calls as soon
        // as it has parsed that piece and before it moves on. Its write buffer never runs dry, so it
        // goes on to the next piece at once instead of starting a setTimeout for every write. Those
        // timers are clamped to 4 ms in a browser, and a VS Code webview whose window is unfocused,
        // covered or minimized holds each one a second or more while still reporting itself visible.
        // xterm still yields to the page after 12 ms of parsing. Only one piece is ever queued, so
        // the scrollback is read after every piece, as when each write was awaited.
        await new Promise((resolve, reject) => {
            const pump = () => {
                try {
                    const piece = nextPiece();
                    if (piece === null) resolve();
                    else term.write(piece, parsed);
                } catch (error) {
                    reject(error);
                }
            };
            const parsed = () => {
                try {
                    collectScrollback();
                } catch (error) {
                    reject(error);
                    return;
                }
                pump();
            };
            pump();
        });

        // Whatever is left on the normal screen, then the full screen if a program is still open.
        collect(term.buffer.normal.length, { mark: false });
        if (isAlternate()) saveScreen('at the end of the recording');
        return parts.join('\n').replace(/\n{3,}/g, '\n\n').trim();
    } finally {
        for (const hook of hooks) hook.dispose();
        marker?.dispose();
        term.dispose();
    }
}

/** Loads, replays and size-checks a session. Throws HandoffTooLargeError over the allowance. */
export async function renderSessionHandoffText(sessionId, { request, createTerminal, signal, onProgress } = {}) {
    const recording = await loadRecording(sessionId, { request, signal, onProgress });
    const text = await renderRecordingText(recording, { createTerminal, signal, onProgress });
    const estimatedTokens = estimateTokens(text);
    if (estimatedTokens > HANDOFF_MAX_INPUT_TOKENS) throw new HandoffTooLargeError(estimatedTokens);
    return text;
}

// Joins wrapped rows into their logical line. A row that continues on the next keeps its
// trailing spaces (the wrap may fall on a space); the end of a line is trimmed. xterm's own trim
// keeps spaces a program wrote, such as a TUI's padding, so the trim here does the rest.
function readLines(buffer, start, end) {
    const lines = [];
    let current = '';
    for (let i = start; i < end; i++) {
        const line = buffer.getLine(i);
        if (!line) continue;
        const continues = i + 1 < end && buffer.getLine(i + 1)?.isWrapped === true;
        current += line.translateToString(!continues);
        if (!continues) { lines.push(current.trimEnd()); current = ''; }
    }
    if (current) lines.push(current.trimEnd());
    return lines.join('\n');
}

function logicalLineStart(buffer, line) {
    let start = Math.min(line, buffer.length);
    while (start > 0 && buffer.getLine(start)?.isWrapped) start--;
    return start;
}

// The row after the line that starts at `line`.
function logicalLineEnd(buffer, line) {
    let end = line + 1;
    while (end < buffer.length && buffer.getLine(end)?.isWrapped) end++;
    return end;
}

// Lines that start in rows [start, end).
function countLines(buffer, start, end) {
    let count = 0;
    for (let row = Math.max(0, start); row < end; row++) if (!buffer.getLine(row)?.isWrapped) count++;
    return count;
}

// The first row of the line `count` lines above the line starting at `line`.
function linesBefore(buffer, line, count) {
    let row = line;
    for (let i = 0; i < count && row > 0; i++) row = logicalLineStart(buffer, row - 1);
    return row;
}

function trimBlankLines(text) {
    return text.replace(/^(?:[ \t]*\n)+/, '').trimEnd();
}

function hasAlternateScreenMode(params) {
    return params.some(param => ALTERNATE_SCREEN_MODES.has(Array.isArray(param) ? param[0] : param));
}

function countLineFeeds(data) {
    let count = 0;
    for (let i = 0; i < data.length; i++) if (data[i] === 0x0A) count++;
    return count;
}

// One frame can exceed the batch budget on its own; write it in budget-sized pieces.
function* splitBatch(parts, length) {
    if (parts.length === 1 && length <= MAX_BATCH_BYTES) { yield parts[0]; return; }
    const bytes = new Uint8Array(length);
    let offset = 0;
    for (const part of parts) { bytes.set(part, offset); offset += part.length; }
    for (let start = 0; start < bytes.length; start += MAX_BATCH_BYTES) yield bytes.subarray(start, start + MAX_BATCH_BYTES);
}

function dimension(value, fallback) {
    return Number.isFinite(value) && value >= 2 ? Math.floor(value) : fallback;
}

function clock(at) {
    return `${new Date(at).toISOString().slice(11, 19)} UTC`;
}

function throwIfAborted(signal) {
    if (!signal?.aborted) return;
    throw signal.reason instanceof Error ? signal.reason : new DOMException('The operation was aborted.', 'AbortError');
}
