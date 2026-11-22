// Terminal queries on a session hosted by the bundled Windows console host (VIBE-354).
//
// The console host built into Windows answers device, cursor and mode queries itself and never
// forwards them, so a viewer of such a session never sees one. The bundled host (conpty.dll +
// OpenConsole.exe) passes the program's output through unchanged and forwards every query to the
// terminal. For those sessions the SERVER is the terminal that answers, from its headless
// emulator (TerminalQueryResponder.cs): the host asks its own questions before any viewer exists
// and after every resize, and a tab may have no viewer or a hidden, throttled one.
//
// xterm.js answers queries by default, through onData, which terminal-tab.js forwards to the
// PTY. For a bundled-host session it must answer none of them:
//  - A second answer is not ignored. A second device-attributes answer reaches the program as
//    the typed characters ESC [ ? 1 ; 2 c, and a cursor report nobody is waiting for is an F3
//    keypress.
//  - An answer to a query the server does not answer would still arrive after the server's
//    answers, out of order. Programs send a device-attributes or cursor query last, as the marker
//    that ends a batch of optional queries, and treat anything after its answer as typing.
// So there is one responder, the server, and a query it does not answer is simply unsupported,
// as it was under the built-in host (which swallowed colour queries, for one).
//
// Nothing is removed from the byte stream in either direction. Each query still reaches xterm.js
// like every other byte the program wrote; a parser handler that returns true marks it handled,
// which is the documented way to replace a built-in sequence handler.

/** CSI functions whose built-in handler does nothing but reply. */
export const SUPPRESSED_CSI_QUERIES = Object.freeze([
    Object.freeze({ name: 'primary device attributes (DA1)', id: Object.freeze({ final: 'c' }) }),
    Object.freeze({ name: 'secondary device attributes (DA2)', id: Object.freeze({ prefix: '>', final: 'c' }) }),
    Object.freeze({ name: 'device status and cursor position (DSR, CPR)', id: Object.freeze({ final: 'n' }) }),
    Object.freeze({ name: 'private device status (DECXCPR)', id: Object.freeze({ prefix: '?', final: 'n' }) }),
    Object.freeze({ name: 'ANSI mode state (DECRQM)', id: Object.freeze({ intermediates: '$', final: 'p' }) }),
    Object.freeze({ name: 'private mode state (DECRQM)', id: Object.freeze({ prefix: '?', intermediates: '$', final: 'p' }) })
]);

/** DCS functions whose built-in handler does nothing but reply. */
export const SUPPRESSED_DCS_QUERIES = Object.freeze([
    Object.freeze({ name: 'setting state (DECRQSS)', id: Object.freeze({ intermediates: '$', final: 'q' }) })
]);

/** OSC colour functions. They set a colour or, given `?`, report one; only the report is suppressed. */
export const COLOR_OSC_IDENTIFIERS = Object.freeze([4, 10, 11, 12]);

/** True when an OSC colour payload asks for a colour instead of setting one. */
export function isColorQuery(data) {
    return typeof data === 'string' && data.split(';').some((part) => part === '?');
}

/**
 * Stops an xterm.js terminal from answering terminal queries. Returns a function that restores
 * xterm.js's own replies. Safe to call on a terminal without a parser API (returns a no-op); the
 * handlers survive `terminal.reset()`.
 */
export function installServerAnsweredQueries(terminal) {
    const parser = terminal?.parser;
    if (!parser || typeof parser.registerCsiHandler !== 'function') {
        return () => {};
    }

    const disposables = SUPPRESSED_CSI_QUERIES.map((query) =>
        parser.registerCsiHandler({ ...query.id }, () => true));
    if (typeof parser.registerDcsHandler === 'function') {
        for (const query of SUPPRESSED_DCS_QUERIES) {
            disposables.push(parser.registerDcsHandler({ ...query.id }, () => true));
        }
    }
    if (typeof parser.registerOscHandler === 'function') {
        for (const identifier of COLOR_OSC_IDENTIFIERS) {
            // A colour being set falls through to xterm.js; only a question is handled here.
            disposables.push(parser.registerOscHandler(identifier, (data) => isColorQuery(data)));
        }
    }

    let disposed = false;
    return () => {
        if (disposed) return;
        disposed = true;
        for (const disposable of disposables) {
            try { disposable?.dispose?.(); } catch { /* no-op */ }
        }
    };
}

/**
 * The xterm.js `windowsPty` option for a session on the bundled host.
 *
 * That host repaints nothing after a resize, and it asks the terminal where the cursor ended up.
 * The server answers from its own emulator, which never re-wraps a line: on a resize every cell
 * stays where it is, what no longer fits is dropped and the cursor is clamped to the new size.
 * This viewer has to lay the screen out the same way. When xterm.js re-wrapped instead, a shrink
 * left a shell's prompt 22 rows away from where the host, and so the shell, drew the next key.
 *
 * xterm.js 6.0.0 has no reflow switch of its own. `windowsPty` is the only lever, and it reads:
 *  - any `backend`: new rows appear at the bottom, scrollback is not pulled back into view;
 *  - a `buildNumber` with `backend: 'conpty'` and a build of 21376 or later: lines re-wrap;
 *  - a `buildNumber` with `backend: 'conpty'` and an older build: no re-wrap, but line wraps are
 *    then guessed from the last column, which is wrong for output that arrives unmodified;
 *  - a `buildNumber` with any other backend: no re-wrap, wrap flags as the program produced them.
 * The last is the layout the emulator has. xterm.js only tests that the build number is there
 * and compares the backend with 'conpty', so the two values below mean nothing beyond that.
 * UITests/tests/xterm-device-queries.spec.js holds xterm.js to all of this.
 */
export const BUNDLED_HOST_WINDOWS_PTY = Object.freeze({ backend: 'winpty', buildNumber: 1 });

/** `{}` is xterm.js's default and leaves every other session exactly as it was. */
export function windowsPtyOptionFor({ bundled = false } = {}) {
    return bundled === true ? { ...BUNDLED_HOST_WINDOWS_PTY } : {};
}
