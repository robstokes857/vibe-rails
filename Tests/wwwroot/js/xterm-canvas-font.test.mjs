import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

// Chromium 156 (Electron 45, the VS Code webview) ignores a canvas font with leading whitespace
// and keeps its 10px sans-serif default. The vendored WebGL and Canvas addons built
// `${italic} ${weight} …` for every glyph, so upright text became " normal 26px Menlo, …" and the
// terminal drew tiny, spaced-out glyphs in full-size cells (xterm.js #6297, fix PR #6312).
// The vendored bundles carry that fix; re-vendoring an unfixed xterm must fail here.
// The rendered behavior is covered by terminal-automations.spec.js ("strict canvas font parser").
const bundles = ['addon-webgl.js', 'addon-canvas.js'];

function canvasFontTemplates(source) {
    const templates = [];
    for (const match of source.matchAll(/\.font=`([^`]*)`/g)) templates.push(match[1]);
    for (const match of source.matchAll(/_getFont\([^)]*\)\{return`([^`]*)`/g)) templates.push(match[1]);
    return templates;
}

for (const name of bundles) {
    test(`${name} never builds a canvas font that starts with an optional style and a space`, () => {
        const source = readFileSync(new URL(`../../../VibeRails/wwwroot/assets/xterm/${name}`, import.meta.url), 'utf8');
        const templates = canvasFontTemplates(source);
        // One glyph-atlas font and one text-layer font per renderer.
        assert.equal(templates.length, 2, `expected the atlas and _getFont templates in ${name}`);
        for (const template of templates) {
            assert.match(template, /px \$\{/, `not a canvas font shorthand: ${template}`);
            // `${style} ${weight}` is " normal …" whenever style is empty (upright text).
            assert.doesNotMatch(template, /^\$\{[^}]*\} /,
                `${name}: the first interpolation can be empty and leave a leading space: ${template}`);
        }
    });
}
