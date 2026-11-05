/** English spelling tokens with source offsets. Never rewrite the author's Markdown. */
export function spellingWords(value) {
    let text = String(value ?? '');
    const blank = value => value.replace(/[^\r\n]/g, ' ');
    // Fenced (including unfinished) and indented code, then inline code of any tick length.
    text = text.replace(/^ {0,3}(`{3,}|~{3,})[^\n]*\n[\s\S]*?(?:^ {0,3}\1[^\n]*(?:\n|$)|(?![\s\S]))/gm, blank)
        .replace(/^(?: {4}|\t).*$/gm, blank)
        .replace(/(`+)([^`]|(?!\1)`)*?\1/g, blank)
        .replace(/`[^\n]*$/gm, blank);
    // Keep link labels as prose; destinations, raw HTML and reference definitions are technical.
    text = text.replace(/\]\([^\n)]*\)/g, blank)
        .replace(/^ {0,3}\[[^\]\n]+\]:[^\n]*/gm, blank)
        .replace(/<[^>\n]*>/g, blank)
        .replace(/(?:https?:\/\/|www\.)[^\s<>]+/gi, blank)
        .replace(/[\w.+-]+@[\w.-]+\.[a-z]{2,}/gi, blank)
        .replace(/(?:^|(?<=\s))[@!#](?:"[^"\n]*(?:"|$)|[^\s]+)/gm, blank)
        .replace(/(?:[a-z]:[\\/]|(?:\.{0,2})[\/\\])[\w.\\/-]+/gi, blank);
    const words = [];
    // Consume whole technical tokens so snake_case, camelCase, file.ext and IDs aren't split.
    for (const match of text.matchAll(/[\p{L}\p{N}_]+(?:['’.-][\p{L}\p{N}_]+)*/gu)) {
        const word = match[0];
        if (word.length < 2 || word.length > 48 || /[^a-zA-Z'’]/.test(word)
            || /[a-z][A-Z]/.test(word) || /^[A-Z]{2,}$/.test(word)) continue;
        words.push({ word, start: match.index, end: match.index + word.length });
    }
    return words;
}

export const normalizeSpellingWord = word => String(word).replaceAll('’', "'").toLowerCase();

/** Bound repeated work by unique word; offsets still refer to every original occurrence. */
export function findMisspellings(text, speller, accepted = new Set()) {
    const checked = new Map();
    return spellingWords(text).filter(token => {
        const word = normalizeSpellingWord(token.word);
        if (accepted.has(word)) return false;
        // Hunspell distinguishes proper nouns (Monday, English) from their lowercase forms.
        const source = token.word.replaceAll('’', "'");
        if (!checked.has(source)) checked.set(source, !speller.correct(source));
        return checked.get(source);
    });
}
