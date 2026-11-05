import test from 'node:test';
import assert from 'node:assert/strict';
import { renderAttachmentMarkdown } from '../../../VibeRails/wwwroot/js/modules/board-attachment-markdown.js';

test('Markdown attachments render headings, nested lists, tables, emphasis and fenced code', () => {
    const html = renderAttachmentMarkdown('# Scope\n\n**Strong** and *emphasis*\n\n- one\n  - two\n\n'
        + '| Name | Value |\n| --- | --- |\n| Feature | Ready |\n\n```js\nconst a = "<img>";\n```');
    for (const fragment of ['<h1>Scope</h1>', '<strong>Strong</strong>', '<em>emphasis</em>',
        '<table>', '<th>Name</th>', '<td>Ready</td>', '<code class="language-js">const a = &quot;&lt;img&gt;&quot;;']) {
        assert.ok(html.includes(fragment), fragment);
    }
    assert.equal((html.match(/<ul>/g) || []).length, 2);
});

test('HTML, image URLs, relative links and executable schemes never become active content', () => {
    const html = renderAttachmentMarkdown('<img src="x" onerror="alert(1)">\n\n<svg onload="alert(1)"></svg>\n\n'
        + '![tracker](https://attacker.example/pixel)\n\n'
        + '![nested **alt**](https://example.test/img.png)\n\n'
        + '[js](javascript:alert(1)) [entity](jav&#x61;script:alert(1)) [data](data:text/html,hi) '
        + '[command](command:workbench.action.terminal.new) [file](file:///secret) [relative](/api/v1/shutdown) '
        + '[protocol](//attacker.example/path) [credential](https://user:pass@example.com) [fragment](#id)');
    assert.doesNotMatch(html, /<(?:img|svg|script|iframe|a)\b/i);
    assert.match(html, /&lt;img src=/);
    assert.match(html, /Image: tracker — not loaded/);
    assert.match(html, /Image: nested alt — not loaded/);
});

test('only explicit HTTP(S) links open without opener/referrer access', () => {
    const html = renderAttachmentMarkdown('[guide](https://example.com/?a=1&b=2 "read me")\n\n<http://example.test/>');
    assert.equal((html.match(/<a /g) || []).length, 2);
    assert.match(html, /href="https:\/\/example.com\/\?a=1&amp;b=2"/);
    assert.match(html, /title="read me"/);
    assert.equal((html.match(/target="_blank" rel="noopener noreferrer" referrerpolicy="no-referrer"/g) || []).length, 2);
});

test('hostile fences, attributes and entity-encoded markup stay escaped', () => {
    const html = renderAttachmentMarkdown('```x" onmouseover="alert(1)\n<script>alert(1)</script>\n```\n\n'
        + '&lt;img src=x onerror=alert(1)&gt;\n\n[quote](https://example.com/ "\" onmouseover=\"alert(1)")');
    assert.doesNotMatch(html, /<script|<img|" onmouseover="/i);
    assert.match(html, /&lt;script&gt;/);
});
