import test from 'node:test';
import assert from 'node:assert/strict';
import { BoardController } from '../../../VibeRails/wwwroot/js/modules/board-controller.js';

// VIBE-322: clamping each comment and then measuring it forced one layout per comment. Every clamp
// now goes on before the first measurement, so a thread costs one layout.

function comment(log, id, { tall, expanded = false }) {
    const classes = new Set(expanded ? ['is-expanded'] : []);
    const more = { hidden: true, onclick: null, textContent: 'Show more' };
    const body = {
        classList: {
            add(name) { log.push(`write ${id}`); classes.add(name); },
            remove(name) { log.push(`write ${id}`); classes.delete(name); },
            contains: name => classes.has(name),
            toggle(name, force) { const on = force ?? !classes.has(name); if (on) classes.add(name); else classes.delete(name); return on; }
        },
        get scrollHeight() { log.push(`read ${id}`); return tall ? 900 : 100; },
        get clientHeight() { return classes.has('is-clamped') ? Math.min(300, tall ? 900 : 100) : (tall ? 900 : 100); },
        parentElement: { querySelector: () => more }
    };
    return { body, more, classes };
}

test('a thread is clamped with all writes before the first measurement', () => {
    const log = [];
    const comments = [comment(log, 'a', { tall: true }), comment(log, 'b', { tall: false }),
        comment(log, 'c', { tall: true }), comment(log, 'd', { tall: true, expanded: true })];
    const host = { isConnected: true, querySelectorAll: () => comments.map(item => item.body) };
    BoardController.prototype.applyCommentClamps.call({}, host);
    const firstRead = log.findIndex(entry => entry.startsWith('read'));
    assert.deepEqual(log.slice(0, firstRead), ['write a', 'write b', 'write c']);
    assert.deepEqual(log.slice(firstRead, firstRead + 3), ['read a', 'read b', 'read c']);
    assert.ok(log.slice(firstRead + 3).every(entry => entry.startsWith('write')), 'no measurement after a write');
    assert.deepEqual(comments.map(item => item.classes.has('is-clamped')), [true, false, true, false]);
    assert.deepEqual(comments.map(item => item.more.hidden), [false, true, false, true]);
    comments[0].more.onclick();
    assert.equal(comments[0].classes.has('is-expanded'), true);
    assert.equal(comments[0].classes.has('is-clamped'), false);
    assert.equal(comments[0].more.textContent, 'Show less');
});
