import test from 'node:test';
import assert from 'node:assert/strict';
import { publicCardUrl, cardSharingSection, bindCardSharing, describeAudience, parseShareEmails } from '../../../VibeRails/wwwroot/js/modules/board-card-sharing.js';

const turn = () => new Promise(resolve => setImmediate(resolve));
function fixture(apiCall, hasDraft = () => false) {
    const nodes = new Map(['[data-card-share-message]', '[data-card-share-links]', '[data-card-share-name]', '[data-share-action="more"]']
        .map(key => [key, { value: '', textContent: '', innerHTML: '', hidden: true }]));
    const listeners = new Map();
    const host = { isConnected: true, open: true, querySelector: s => nodes.get(s), querySelectorAll: () => [...nodes.values()],
        addEventListener: (type, fn) => listeners.set(type, fn), removeEventListener: type => listeners.delete(type) };
    const editor = { querySelector: () => host };
    const control = bindCardSharing(editor, { id: 'card fixture', title: 'Example' }, { app: { apiCall }, hasDraft });
    return { control, host, nodes, load: async () => { listeners.get('toggle')(); await turn(); },
        click: action => listeners.get('click')({ target: { closest: s => s === '[data-share-action]' ? { dataset: { shareAction: action }, closest: () => null } : null } }) };
}

test('public card URLs permit only the fixed host and fragment capability', () => {
    assert.equal(publicCardUrl('/shared/card#key=' + 'a'.repeat(64)), 'https://viberails.ai/shared/card#key=' + 'a'.repeat(64));
    for (const path of ['https://evil.test', '//evil.test', '/shared/card?key=' + 'a'.repeat(64), '/shared/card#key=' + 'g'.repeat(64), undefined])
        assert.equal(publicCardUrl(path), null);
    assert.match(cardSharingSection(true), /Saved changes/);
    assert.match(cardSharingSection(false), /Save the card/);
});

test('sharing preserves unsaved drafts and performs no publication', async () => {
    const f = fixture(() => { throw new Error('Must not call remote'); }, () => true);
    await f.click('create');
    assert.match(f.nodes.get('[data-card-share-message]').textContent, /drafts are still here/);
    f.control.dispose();
});

test('uncertain creation is followed by one read, never a second POST', async () => {
    const calls = [];
    const f = fixture(async (...args) => { calls.push(args); return args[1] === 'POST'
        ? { success: false, message: 'Check the list; a link may exist.' } : { success: true, links: [] }; });
    await f.click('create');
    assert.deepEqual(calls.map(c => c[1]), ['POST', 'GET']);
    assert.equal(calls[0][0], '/api/v1/board/cards/card%20fixture/sharing-links');
    assert.match(f.nodes.get('[data-card-share-message]').textContent, /may exist/);
    f.control.dispose();
});

test('disposal aborts reads and ignores late responses', async () => {
    let resolve, signal;
    const f = fixture((_, __, ___, options) => { signal = options.signal; return new Promise(done => { resolve = done; }); });
    await f.load(); f.control.dispose();
    assert.equal(signal.aborted, true);
    resolve({ success: true, links: [{ id: 1, displayName: 'Late result' }] });
    await turn();
    assert.equal(f.nodes.get('[data-card-share-links]').innerHTML, '');
});

test('a submitted creation can finish after close without repainting or rereading', async () => {
    let resolve; const calls = [];
    const f = fixture((...args) => { calls.push(args); return new Promise(done => { resolve = done; }); });
    const pending = f.click('create');
    f.control.dispose(); resolve({ success: true, message: 'Created', link: {} }); await pending;
    assert.equal(calls.length, 1);
    assert.equal(f.nodes.get('[data-card-share-links]').innerHTML, '');
});

test('creation forwards the chosen audience and describes link audiences without markup', async () => {
    const calls = [];
    const f = fixture(async (...args) => { calls.push(args); return { success: true, links: [] }; });
    f.nodes.set('[data-card-share-audience-mode]:checked', { value: 'email' });
    f.nodes.set('[data-card-share-audience-emails]', { value: 'a@example.test, A@example.test\nb@example.test' });
    await f.click('create');
    assert.equal(calls[0][1], 'POST');
    assert.deepEqual(calls[0][2], { displayName: 'Example', access: 'email', emails: ['a@example.test', 'A@example.test', 'b@example.test'] });
    f.control.dispose();
    const g = fixture(async (...args) => { calls.push(args); return { success: true, links: [] }; });
    await g.click('create');
    assert.deepEqual(calls.at(-2)[2], { displayName: 'Example', access: 'public', emails: [] });
    g.control.dispose();
    assert.equal(describeAudience({ access: 'public' }), 'Anyone with the link');
    assert.equal(describeAudience({ access: 'email', recipients: ['x@example.test'] }), '1 person: x@example.test');
    assert.deepEqual(parseShareEmails(' one@example.test;two@example.test two@example.test '), ['one@example.test', 'two@example.test']);
    assert.match(cardSharingSection(true), /Who can view/);
});
