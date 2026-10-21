import test from 'node:test';
import assert from 'node:assert/strict';
import { codeLinksSection, renderCodeLinks, safeCodeLinkUrl } from '../../../VibeRails/wwwroot/js/modules/board-code-links.js';

test('only GitHub PR and commit URLs become links', () => {
    assert.equal(safeCodeLinkUrl('https://github.com/org/repo/pull/42'), 'https://github.com/org/repo/pull/42');
    assert.equal(safeCodeLinkUrl('https://github.com/org/repo/commit/abc1234'), 'https://github.com/org/repo/commit/abc1234');
    for (const url of ['javascript:alert(1)', 'https://github.com.evil/org/repo/pull/42', 'https://github.com@evil.org/org/repo/pull/42', 'https://github.com/org/repo/issues/42', null])
        assert.equal(safeCodeLinkUrl(url), null);
});

test('card editor offers PR and commit URL input only after saving', () => {
    assert.match(codeLinksSection({ id: 'card' }), /type="url"/);
    assert.match(codeLinksSection({ id: 'card' }), /included in shared card and session data/);
    assert.doesNotMatch(codeLinksSection(null), /<form/);
});

test('renders escaped labels and unlink controls, with safe external navigation', () => {
    const list = {}, count = {};
    const editor = { querySelector: selector => selector.includes('count') ? count : list };
    renderCodeLinks(editor, { codeLinks: [
        { id: 'one', url: 'https://github.com/org/repo/pull/42', label: '<script>alert(1)</script>' },
        { id: 'two', url: 'javascript:alert(1)', label: 'unsafe' }
    ] });
    assert.equal(count.textContent, '2');
    assert.match(list.innerHTML, /rel="noopener noreferrer"/);
    assert.match(list.innerHTML, /&lt;script&gt;/);
    assert.doesNotMatch(list.innerHTML, /href="javascript:/);
    assert.match(list.innerHTML, /data-board-remove-code-link="two"/);
});
