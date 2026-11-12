import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const modules = path.resolve('VibeRails/wwwroot/js/modules');
const { cssHexColor, escapeHtml } = await import(pathToFileURL(path.join(modules, 'utils.js')).href);

// VIBE-310: a lane colour is written into a style attribute. A shared board's colour comes from
// its owner and is only length-checked on the way in, so the sink accepts a #rrggbb value only.
test('only a #rrggbb colour reaches a style attribute', () => {
    for (const color of ['#64748b', '#3B82F6', ' #10b981 ']) {
        assert.equal(cssHexColor(color), color.trim());
    }
    for (const color of [
        null, undefined, '', 42, {}, 'red', '#fff', '#12345', '#1234567', '#12345g',
        '1;position:fixed;inset:0', '#64748b;display:none', '#64748b;background:url(https://x.example/a)',
        'var(--x)', 'rgb(0,0,0)', '#64748b"', '#64748b\n;x:y'
    ]) {
        assert.equal(cssHexColor(color), '', String(color));
    }
});

test('escaping alone does not stop extra declarations, which is why the lane uses cssHexColor', () => {
    const hostile = '1;position:fixed;inset:0';
    assert.equal(escapeHtml(hostile), hostile);

    const controller = fs.readFileSync(path.join(modules, 'board-controller.js'), 'utf8');
    assert.match(controller, /style="--lane-color:\$\{cssHexColor\(column\.color\)\}"/);
    assert.doesNotMatch(controller, /--lane-color:\$\{escapeHtml\(/);
});
