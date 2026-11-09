// Decoration only: token text and offsets always refer to the unmodified prompt.
const TOKENS = /(?<code>```[\s\S]*?(?:```|$)|~~~[\s\S]*?(?:~~~|$)|(?<ticks>`+)[^\r\n]*?(?:\k<ticks>|(?=\r?\n)|$))|(?<variable>\{\{\s*[A-Za-z_][A-Za-z0-9_.-]*[^{}\r\n]*\}\})|(?<card>[@!]\[[^\]\r\n]*\]\(card:[A-Za-z0-9_-]+\))|(?<!\S)(?<file>@(?:"[^"\r\n]+"|[^\s<>"'`&]*[^\s<>"'`&.,;:!?)\]}]))|(?<!\S)(?<session>!(?:[a-f\d]{8}-[a-f\d]{4}-[a-f\d]{4}-[a-f\d]{4}-[a-f\d]{12}|[a-f\d]{32}|[a-z][a-z\d]*(?:-[a-z\d]+)*-\d+)(?![\w-]))|(?<!\S)(?<commit>\#(?:[a-f\d]{7,40}|[a-z][a-z\d]*(?:-[a-z\d]+)*-\d+)(?![\w-]))/gi;

/** Identify complete references without interpreting markup or changing source text. */
export function promptReferenceRanges(value) {
    const text = String(value ?? '');
    const ranges = [];
    for (const match of text.matchAll(TOKENS)) {
        const { code, ticks, ...groups } = match.groups;
        if (code !== undefined) continue;
        const kind = Object.keys(groups).find(key => groups[key] !== undefined);
        if (kind === 'file' && !match[0].startsWith('@"') && !/[./]/.test(match[0])) continue;
        if (kind === 'variable' && /^\{\{\s*step\s*:/i.test(match[0])) continue;
        ranges.push({ start: match.index, end: match.index + match[0].length, kind });
    }
    return ranges;
}

const TEXT_STYLES = ['fontFamily', 'fontSize', 'fontWeight', 'fontStyle', 'fontVariant',
    'fontStretch', 'fontKerning', 'fontFeatureSettings', 'fontVariationSettings',
    'lineHeight', 'letterSpacing', 'wordSpacing', 'textTransform', 'textIndent',
    'textAlign', 'direction', 'tabSize', 'paddingTop', 'paddingRight', 'paddingBottom', 'paddingLeft'];

/** Paint references beneath a native textarea, keeping its caret, selection and undo. */
export function bindPromptReferenceHighlights(input) {
    const doc = input.ownerDocument;
    const abort = new AbortController();
    const { signal } = abort;
    let disposed = false;
    const shell = doc.createElement('div');
    shell.className = 'prompt-reference-field';
    input.before(shell);
    shell.append(input);
    const overlay = doc.createElement('div');
    overlay.className = 'prompt-reference-overlay';
    overlay.setAttribute('aria-hidden', 'true');
    const mirror = doc.createElement('div');
    mirror.className = 'prompt-reference-mirror';
    overlay.append(mirror);
    shell.prepend(overlay);
    input.classList.add('prompt-reference-input');

    function geometry() {
        if (disposed) return;
        const style = getComputedStyle(input);
        for (const key of TEXT_STYLES) mirror.style[key] = style[key];
        mirror.style.color = style.color;
        mirror.style.wordBreak = style.wordBreak;
        mirror.style.width = `${input.clientWidth}px`;
        mirror.style.transform = `translate(${-input.scrollLeft}px, ${-input.scrollTop}px)`;
        const field = input.getBoundingClientRect(), parent = shell.getBoundingClientRect();
        const scaleX = parent.width / shell.offsetWidth || 1;
        const scaleY = parent.height / shell.offsetHeight || 1;
        Object.assign(overlay.style, {
            left: `${(field.left - parent.left) / scaleX + input.clientLeft}px`,
            top: `${(field.top - parent.top) / scaleY + input.clientTop}px`,
            width: `${input.clientWidth}px`, height: `${input.clientHeight}px`, opacity: style.opacity
        });
    }

    function paint() {
        const text = input.value;
        const fragment = doc.createDocumentFragment();
        let end = 0;
        for (const range of promptReferenceRanges(text)) {
            fragment.append(doc.createTextNode(text.slice(end, range.start)));
            const mark = doc.createElement('span');
            mark.className = 'prompt-reference-token';
            mark.dataset.promptReference = range.kind;
            mark.textContent = text.slice(range.start, range.end);
            fragment.append(mark);
            end = range.end;
        }
        fragment.append(doc.createTextNode(text.slice(end) + '\u200b'));
        mirror.replaceChildren(fragment);
        geometry();
    }

    input.addEventListener('input', paint, { signal });
    input.addEventListener('scroll', geometry, { signal });
    input.addEventListener('compositionstart', () => shell.classList.add('is-composing'), { signal });
    input.addEventListener('compositionend', () => { shell.classList.remove('is-composing'); paint(); }, { signal });
    const resize = new ResizeObserver(geometry);
    resize.observe(input);
    const theme = new MutationObserver(geometry);
    for (const element of [doc.body, doc.documentElement, input])
        theme.observe(element, { attributes: true, attributeFilter: ['class', 'style', 'disabled'] });
    doc.fonts?.ready.then(() => { if (!disposed) geometry(); });
    doc.fonts?.addEventListener('loadingdone', geometry, { signal });
    paint();
    return () => {
        disposed = true;
        abort.abort();
        resize.disconnect();
        theme.disconnect();
        input.classList.remove('prompt-reference-input');
        shell.before(input);
        shell.remove();
    };
}
