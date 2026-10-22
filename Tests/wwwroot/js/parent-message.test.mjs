import test from 'node:test';
import assert from 'node:assert/strict';
import { isParentMessage } from '../../../VibeRails/wwwroot/js/modules/parent-message.js';

test('only the parent can send commands; self, null, children and siblings are rejected', () => {
    const host = { parent: {} };
    assert.equal(isParentMessage({ source: host.parent }, host), true);
    for (const source of [null, host, {}, { 0: {}, length: 1 }]) {
        assert.equal(isParentMessage({ source }, host), false);
    }
});

test('the real frame hierarchy identifies VS Code host messages when parent is replaced with self', () => {
    const host = {};
    host.parent = host;
    assert.equal(isParentMessage({ source: { 0: {}, 1: host, length: 2 } }, host), true);
    for (const source of [null, host, {}, { 0: {}, length: 1 }, { get length() { throw new Error('closed'); } }]) {
        assert.equal(isParentMessage({ source }, host), false);
    }
});
