import test from 'node:test';
import assert from 'node:assert/strict';
import { remotePasswordError } from '../../../VibeRails/wwwroot/js/modules/remote-access-password.js';

test('creating a password requires exactly the agreed six-character minimum, letter and number', () => {
    for (const value of ['', '1234', '123456', 'abcdef', 'abcd1', '😀😀a1']) {
        assert.ok(remotePasswordError(value), `should reject ${JSON.stringify(value)}`);
    }
    for (const value of ['abcde1', ' pass1 ', '密码口令长1', 'a'.repeat(127) + '1']) {
        assert.equal(remotePasswordError(value), '');
    }
});

test('passwords use the same 128 UTF-16-unit bound as the API and presence check', () => {
    assert.ok(remotePasswordError('a'.repeat(128) + '1'));
    assert.ok(remotePasswordError('😀'.repeat(64) + 'a1'));
});
