import test from 'node:test';
import assert from 'node:assert/strict';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const moduleUrl = pathToFileURL(path.resolve('VibeRails/wwwroot/js/modules/board-text.js')).href;
const { renderCommentHtml, toPlainPreview, wrapSelectionAsCode } = await import(moduleUrl);

// Board comments render with NO sanitizer, which is only safe because the renderer
// escapes its input before any transform runs and never reintroduces markup from
// user text. These tests pin that property. They mirror the invariants
// UITests/tests/frontend-security.spec.js already enforces for CLI output: injected
// markup must not become an element, and no handler may survive.
//
// This matters more than usual here: the app's CSP sets script-src 'unsafe-inline'
// with no nonce, so the browser WOULD run an injected inline handler. There is no
// second line of defence behind this file.

test('injected markup renders as text, never as an element', () => {
    const html = renderCommentHtml('Nice! <img src=x onerror="window.__x=true"> <script>alert(1)</script>');

    assert.doesNotMatch(html, /<img(?![^>]*class="board-image")/, 'no raw <img> element');
    assert.doesNotMatch(html, /<script/i, 'no <script> element');
    assert.match(html, /&lt;img src=x/, 'the tag is shown as literal text');
    assert.match(html, /&lt;script&gt;/, 'the script tag is shown as literal text');
});

// Every tag this renderer is allowed to emit. Anything else appearing as a real
// tag means user input escaped the pipeline.
const EMITTED_TAG = /<\/?(?:pre|code|div|span|img|a)\b[^>]*>/g;

test('no tag survives except the ones the renderer writes itself', () => {
    for (const payload of [
        '<div onclick="steal()">x</div>',
        '<svg/onload=alert(1)>',
        '<iframe src="javascript:alert(1)">',
        '<a href="javascript:alert(1)">click</a>',
        '<img src=x onerror=alert(1)>',
        '<style>body{display:none}</style>',
        '<form action=/><button formaction="javascript:alert(1)">'
    ]) {
        const html = renderCommentHtml(payload);

        // Whatever tags remain must be ones this file emitted, and none of those
        // carry an event handler or a javascript: URL.
        for (const tag of html.match(EMITTED_TAG) || []) {
            assert.doesNotMatch(tag, /\son[a-z]+\s*=/i, `handler in emitted tag for: ${payload}`);
            assert.doesNotMatch(tag, /javascript:/i, `javascript: URL in emitted tag for: ${payload}`);
        }

        // And nothing tag-shaped is left over once those are removed: the payload
        // is sitting there as escaped text, which is the whole point.
        const residue = html.replace(EMITTED_TAG, '');
        assert.doesNotMatch(residue, /</, `an unexpected tag survived for: ${payload}`);
        assert.match(html, /&lt;/, `the payload should be visible as text for: ${payload}`);
    }
});

test('a javascript: or data: URL is never turned into a link', () => {
    const html = renderCommentHtml('javascript:alert(1) and data:text/html,<script>alert(1)</script>');
    assert.doesNotMatch(html, /<a /, 'only http(s) is autolinked');
    // It is still shown to the reader, just as inert text.
    assert.match(html, /javascript:alert\(1\)/);
});

test('http(s) URLs autolink without swallowing trailing punctuation', () => {
    const html = renderCommentHtml('see https://example.com/a. done');
    assert.match(html, /href="https:\/\/example\.com\/a"/);
    assert.match(html, /<\/a>\. done/);
});

test('printable characters and whitespace survive intact', () => {
    // A mangled control-character class once threatened to strip spaces and
    // punctuation out of every comment; this pins that it does not.
    const source = 'a b!c"d#e$f%g&h(i)j*k+l,m-n.o/p0q9r:s;t?u@v[w]x_y~z';
    const html = renderCommentHtml(source);
    assert.match(html, /a b!c/);
    assert.match(html, /p0q9r/);
    assert.match(html, /l,m-n\.o\/?/);
    assert.ok(!html.includes('  '), 'no collapsed/duplicated whitespace');
});

test('newlines are preserved as content, not converted to markup', () => {
    const html = renderCommentHtml('one\ntwo');
    assert.doesNotMatch(html, /<br/, 'display relies on white-space: pre-wrap');
    assert.match(html, /one\ntwo/);
});

test('fenced blocks become a pre/code block with an escaped body', () => {
    const html = renderCommentHtml('before\n```js\nif (a < b) return "<x>";\n```\nafter');
    assert.match(html, /<pre class="board-code"><code>/);
    assert.match(html, /board-code-lang">js</);
    assert.match(html, /a &lt; b/, 'code content stays escaped');
    assert.match(html, /&quot;&lt;x&gt;&quot;/);
});

test('inline transforms never reach inside a fenced block', () => {
    const html = renderCommentHtml('```\nsee https://x.com and `tick`\n```');
    assert.doesNotMatch(html, /<a /, 'no autolink inside code');
    assert.doesNotMatch(html, /board-inline-code/, 'no inline code inside code');
});

test('inline code is wrapped and stays escaped', () => {
    const html = renderCommentHtml('call `rotate<T>()` now');
    assert.match(html, /<code class="board-inline-code">rotate&lt;T&gt;\(\)<\/code>/);
});

test('images resolve through the attachment record, never through the text', () => {
    const attachments = [{ id: 'img_1', url: 'data:image/png;base64,AAAA' }];

    const good = renderCommentHtml('shot ![cap](attachment:img_1)', { attachments });
    assert.match(good, /<img class="board-image" src="data:image\/png;base64,AAAA"/);
    assert.match(good, /alt="cap"/);

    // An id that is not on the record cannot produce an <img> at all, so a comment
    // can never point an image at something we did not store.
    const unknown = renderCommentHtml('![x](attachment:not_a_real_id)', { attachments });
    assert.doesNotMatch(unknown, /<img/);
    assert.match(unknown, /attachment:not_a_real_id/);

    // And no attachment list at all is the same story.
    assert.doesNotMatch(renderCommentHtml('![x](attachment:img_1)'), /<img/);
});

test('empty and nullish bodies render as nothing', () => {
    assert.equal(renderCommentHtml(''), '');
    assert.equal(renderCommentHtml('   \n  '), '');
    assert.equal(renderCommentHtml(null), '');
    assert.equal(renderCommentHtml(undefined), '');
});

test('toPlainPreview flattens code and images for a one-line summary', () => {
    assert.equal(toPlainPreview('a\n```\ncode\n```\nb'), 'a [code] b');
    assert.equal(toPlainPreview('x ![s](attachment:img_1) y'), 'x [image] y');
    assert.equal(toPlainPreview('use `npm i`'), 'use npm i');
    assert.ok(toPlainPreview('z'.repeat(200)).length <= 140);
});

test('wrapSelectionAsCode fences a selection and parks the caret for an empty one', () => {
    const wrapped = wrapSelectionAsCode('look at this', 8, 12);
    assert.equal(wrapped.value, 'look at \n```\nthis\n```');

    // With no selection the caret lands inside the new fence, ready to type.
    const empty = wrapSelectionAsCode('', 0, 0);
    assert.equal(empty.value, '```\n\n```');
    assert.equal(empty.selectionStart, 4);
});
