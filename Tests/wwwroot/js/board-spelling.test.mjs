import test from 'node:test';
import assert from 'node:assert/strict';
import { createEnglishSpeller } from '../../../VibeRails/wwwroot/assets/spellcheck/english.js';
import { spellingWords, findMisspellings, normalizeSpellingWord } from '../../../VibeRails/wwwroot/js/modules/board-spelling-words.js';

const speller = createEnglishSpeller();
test('offline English dictionary checks inflections, contractions and useful suggestions', () => {
    assert.deepEqual(findMisspellings("The descriptions aren't saved. Monday Robert English. This is mispelled.", speller).map(x => x.word), ['mispelled']);
    assert.ok(speller.suggest('mispelled').includes('misspelled'));
    assert.equal(normalizeSpellingWord('Isn’t'), "isn't");
});

test('Markdown exclusions preserve UTF-16 offsets and leave link labels as prose', () => {
    const source = '🙂 An errror. [mispelled](https://zzzz.invalid/path)\n'
        + '`inlineerrror` ``another ` errror``\n```js\nfencederrror\n```\n'
        + '~~~\nothererrror\n~~~\n    indentederrror\n\tmoreerrror\n'
        + '@src/zzzz.cs @"src/path with errror.cs" !VB-123 #abcdead\n'
        + 'https://zzzz.invalid user@zzzz.invalid www.zzzz.invalid\n'
        + 'snake_case camelCase PascalCase README VB-123 thing.cs\n'
        + '<img src="zzzz" onerror="zzzz"> lasttt';
    const words = findMisspellings(source, speller);
    assert.deepEqual(words.map(x => x.word), ['errror', 'mispelled', 'lasttt']);
    for (const { word, start, end } of words) assert.equal(source.slice(start, end), word);
});

test('unfinished code fences and inline code are not checked', () => {
    assert.deepEqual(spellingWords('valid\n```js\nunfinishederrro'), [{ word: 'valid', start: 0, end: 5 }]);
    assert.deepEqual(spellingWords('valid `unfinishederrro'), [{ word: 'valid', start: 0, end: 5 }]);
});

test('accepted words are case insensitive and repeated checks are bounded by unique words', () => {
    let checks = 0;
    const fake = { correct: () => { checks++; return false; } };
    const source = 'mispelled Mispelled mispelled customword';
    assert.equal(findMisspellings(source, fake, new Set(['customword'])).length, 3);
    assert.equal(checks, 2); // Two case-sensitive forms, regardless of how often each repeats.
});

test('large descriptions and long code markers do not change source positions', () => {
    const source = 'word '.repeat(19000) + '\n' + '`'.repeat(4000) + '\nerrror';
    const errors = findMisspellings(source, speller);
    assert.deepEqual(errors.map(x => x.word), []); // The final fence is intentionally still open.
});

test('embedded backtick runs at the description limit are checked in bounded time', () => {
    // F1: the former inline regex took about five seconds on this 100,000-char
    // input. Keep a generous budget for CI; the scanner normally takes milliseconds.
    const source = 'a' + '`'.repeat(99998) + 'x';
    const started = performance.now();
    assert.deepEqual(spellingWords(source), []);
    assert.ok(performance.now() - started < 2000, 'Inline-code scan stalled the UI');
});

test('inline delimiters match exact run lengths and preserve prose offsets after code', () => {
    const source = 'before ``code `inside` code`` after\r\nnext `unfinished\r\nlast';
    const words = spellingWords(source);
    assert.deepEqual(words.map(item => item.word), ['before', 'after', 'next', 'last']);
    for (const { word, start, end } of words) assert.equal(source.slice(start, end), word);
    const mismatched = 'before ``code ` still code`` after';
    assert.deepEqual(spellingWords(mismatched).map(item => item.word), ['before', 'after']);
});
