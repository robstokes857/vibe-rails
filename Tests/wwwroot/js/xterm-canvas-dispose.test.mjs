import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

// Terminal.dispose() marks its store disposed and then disposes its addons. On the way out a
// renderer addon puts xterm's DOM renderer back, which only works on a live terminal: building
// one on a disposed terminal throws "Cannot read properties of undefined (reading
// 'onShowLinkUnderline')" out of Terminal.dispose(). Upstream's WebGL addon checks
// `_store._isDisposed` first; the vendored Canvas addon did not. With Canvas selected (the Mac in
// VIBE-351), leaving Terminals threw before the next view mounted, left the other tabs' sockets
// open, and the next viewer's connect raised a false "taken over" warning. Re-vendoring an
// unguarded bundle must fail here. Rendered behavior: terminal-board-card.spec.js.
for (const name of ['addon-webgl.js', 'addon-canvas.js']) {
    test(`${name} restores the DOM renderer only while the terminal is alive`, () => {
        const source = readFileSync(new URL(`../../../VibeRails/wwwroot/assets/xterm/${name}`, import.meta.url), 'utf8');
        const restores = [...source.matchAll(/toDisposable\)\(\(\(\)=>\{([^}]*_createRenderer\(\)[^}]*)\}/g)]
            .map(match => match[1]);
        assert.equal(restores.length, 1, `expected one DOM renderer restore in ${name}`);
        assert.match(restores[0], /^(?:if\()?this\._terminal\._core\._store\._isDisposed(?:\)return;|\|\|\()/,
            `${name}: the DOM renderer restore must be skipped once the terminal is disposed: ${restores[0]}`);
    });
}
