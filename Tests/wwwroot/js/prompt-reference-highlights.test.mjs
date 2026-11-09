import test from 'node:test';
import assert from 'node:assert/strict';
import { promptReferenceRanges } from '../../../VibeRails/wwwroot/js/modules/prompt-reference-highlights.js';

const tokens = text => promptReferenceRanges(text).map(range => ({
    kind: range.kind, text: text.slice(range.start, range.end)
}));

test('prompt highlights preserve source and Unicode offsets for every reference type', () => {
    const text = '🙂 Use @deploy/deploy.ps1, @"my folder/file.png" and {{time}} '
        + '{{name default="hello world"}} !VIBE-123 ![a card](card:card_1) '
        + '!12345678-1234-1234-1234-123456789abc #abcdef012345.';
    assert.deepEqual(tokens(text), [
        { kind: 'file', text: '@deploy/deploy.ps1' },
        { kind: 'file', text: '@"my folder/file.png"' },
        { kind: 'variable', text: '{{time}}' },
        { kind: 'variable', text: '{{name default="hello world"}}' },
        { kind: 'session', text: '!VIBE-123' },
        { kind: 'card', text: '![a card](card:card_1)' },
        { kind: 'session', text: '!12345678-1234-1234-1234-123456789abc' },
        { kind: 'commit', text: '#abcdef012345' }
    ]);
});

test('code, email, ordinary punctuation and incomplete or retired tokens stay plain', () => {
    const text = '`@src/a.cs {{time}}` ``#abcdef0``\n```js\n!VIBE-123\n```\n'
        + '~~~\n@a.cs\n~~~\nrob@example.com @agent # Heading !hello {{ti {{step:123}}';
    assert.deepEqual(tokens(text), []);
    assert.deepEqual(tokens('```\n@a.cs {{time}}'), []);
});

test('HTML-like text remains source and cannot turn into markup', () => {
    const text = '<img onerror="bad()"> @"<script>.png" {{name default="<b>"}}';
    assert.deepEqual(tokens(text), [
        { kind: 'file', text: '@"<script>.png"' },
        { kind: 'variable', text: '{{name default="<b>"}}' }
    ]);
});
