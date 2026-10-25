import test from 'node:test';
import assert from 'node:assert/strict';
import { reviewRequestChange, setupVSCodeReviewBridge } from '../../../VibeRails/wwwroot/js/modules/vscode-review-bridge.js';

test('only well-formed review request changes are forwarded, typed and bounded', () => {
    assert.deepEqual(reviewRequestChange({ id: ' abc ', status: 'approved', cardKey: 'VB-1', sent: true }), { id: 'abc', status: 'approved', cardKey: 'VB-1', sent: true });
    assert.deepEqual(reviewRequestChange({ id: 'abc', status: 'deleted', cardKey: '', sent: 'yes' }), { id: 'abc', status: 'deleted', cardKey: null, sent: false });
    for (const bad of [null, 'x', {}, { id: 'abc' }, { id: '', status: 'open' }, { id: 'a'.repeat(65), status: 'open' }, { id: 'abc', status: 'o'.repeat(33) }]) {
        assert.equal(reviewRequestChange(bad), null);
    }
    assert.equal(reviewRequestChange({ id: 'abc', status: 'open', cardKey: 'k'.repeat(80) }).cardKey.length, 64);
});

test('the bridge listens only inside VS Code and relays each event to the host global', () => {
    const forwarded = [];
    const handlers = new Map();
    const app = { appEventClient: { on: (type, fn) => { handlers.set(type, fn); return () => handlers.delete(type); } } };
    assert.equal(setupVSCodeReviewBridge(app, { __viberails_VSCODE__: false }), null);
    assert.equal(handlers.size, 0);

    const host = { __viberails_VSCODE__: true, __viberails_reviewRequestChanged__: change => forwarded.push(change) };
    const unsubscribe = setupVSCodeReviewBridge(app, host);
    handlers.get('review_request_updated')({ id: 'r1', status: 'rejected', cardKey: null, sent: true });
    handlers.get('review_request_updated')('junk');
    assert.deepEqual(forwarded, [{ id: 'r1', status: 'rejected', cardKey: null, sent: true }]);
    unsubscribe();
    assert.equal(handlers.size, 0);

    // A host without the global (an older extension) is simply not told.
    const quiet = setupVSCodeReviewBridge(app, { __viberails_VSCODE__: true });
    handlers.get('review_request_updated')({ id: 'r1', status: 'approved' });
    quiet();
    assert.equal(forwarded.length, 1);
});
