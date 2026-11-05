import { findMisspellings, normalizeSpellingWord } from './board-spelling-words.js';

const STORAGE_KEY = 'viberails.board.spelling.words.v1';
const ignored = new Set();
const subscribers = new Set();
let loading;
let loadAttempt = 0;
let nextId = 0;
let spellingEnabled = false;
const fieldBindings = new Set();

/** Application feature flag; off removes existing bindings without changing field values. */
export function setBoardSpellingEnabled(value) {
    const enabled = value === true;
    if (enabled === spellingEnabled) return;
    spellingEnabled = enabled;
    for (const refresh of fieldBindings) refresh();
}

/** Register a field without touching its DOM or loading the dictionary while disabled. */
export function bindBoardSpelling(input) {
    if (!input) return () => {};
    let dispose;
    const refresh = () => {
        dispose?.();
        dispose = spellingEnabled ? mountBoardSpelling(input) : null;
    };
    fieldBindings.add(refresh);
    refresh();
    return () => { fieldBindings.delete(refresh); dispose?.(); dispose = null; };
}

function savedWords() {
    try {
        const words = JSON.parse(localStorage.getItem(STORAGE_KEY) || '[]');
        return new Set((Array.isArray(words) ? words : []).filter(word =>
            typeof word === 'string' && /^[a-z']{2,48}$/.test(word)).slice(0, 2000));
    } catch { return new Set(); }
}

async function english() {
    if (!loading) {
        // A failed module fetch needs a fresh URL on retry in Chromium's module map.
        const url = new URL('../../assets/spellcheck/english.js', import.meta.url);
        if (++loadAttempt > 1) url.searchParams.set('retry', String(loadAttempt));
        loading = import(url.href).then(module => module.createEnglishSpeller())
            .catch(error => { loading = null; throw error; });
    }
    return loading;
}

const MIRROR_STYLES = ['fontFamily', 'fontSize', 'fontWeight', 'fontStyle', 'fontVariant',
    'fontStretch', 'fontKerning', 'fontFeatureSettings', 'fontVariationSettings',
    'lineHeight', 'letterSpacing', 'wordSpacing', 'textTransform', 'textIndent',
    'textAlign', 'direction', 'tabSize', 'paddingTop', 'paddingRight', 'paddingBottom', 'paddingLeft'];

/** Native field + decoration only: the overlay never receives focus or edits text. */
function mountBoardSpelling(input) {
    if (!input || input.disabled || input.readOnly) return () => {};
    const doc = input.ownerDocument;
    const lifetime = new AbortController();
    const { signal } = lifetime;
    const originalSpellcheck = input.getAttribute('spellcheck');
    const originalDescribedBy = input.getAttribute('aria-describedby');
    const shell = doc.createElement('div');
    shell.className = 'board-spelling';
    input.before(shell);
    shell.append(input);
    const overlay = doc.createElement('div');
    overlay.className = 'board-spelling-overlay';
    overlay.setAttribute('aria-hidden', 'true');
    const mirror = doc.createElement('div');
    mirror.className = 'board-spelling-mirror';
    overlay.append(mirror);
    shell.append(overlay);
    const tools = doc.createElement('div');
    tools.className = 'board-spelling-tools';
    const button = doc.createElement('button');
    button.type = 'button';
    button.className = 'board-spelling-check';
    button.textContent = 'Check spelling';
    button.setAttribute('aria-label', `Check spelling: ${input.getAttribute('aria-label') || input.labels?.[0]?.textContent?.trim() || input.closest('[data-board-composer]')?.dataset.boardComposer || 'text'}`);
    button.setAttribute('aria-haspopup', 'dialog');
    button.setAttribute('aria-expanded', 'false');
    const status = doc.createElement('span');
    status.className = 'board-spelling-status';
    status.id = `board-spelling-status-${++nextId}`;
    status.setAttribute('role', 'status');
    tools.append(button, status);
    shell.after(tools);
    input.setAttribute('aria-describedby', [originalDescribedBy, status.id].filter(Boolean).join(' '));
    // Avoid a second set of native/browser suggestions on top of the local checker.
    input.spellcheck = false;
    let disposed = false, composing = false, generation = 0, timer, issues = [], checkedText = null;
    let popup = null, selected = null, speller = null;

    function close(restoreFocus = false) {
        popup?.remove();
        popup = null;
        selected = null;
        button.setAttribute('aria-expanded', 'false');
        button.removeAttribute('aria-controls');
        if (restoreFocus && input.isConnected) input.focus({ preventScroll: true });
    }

    function geometry() {
        if (disposed) return;
        const style = getComputedStyle(input);
        for (const key of MIRROR_STYLES) mirror.style[key] = style[key];
        const single = input.tagName === 'INPUT';
        mirror.style.whiteSpace = single ? 'pre' : 'pre-wrap';
        mirror.style.overflowWrap = single ? 'normal' : 'break-word';
        mirror.style.wordBreak = style.wordBreak;
        mirror.style.width = `${input.clientWidth}px`;
        // Input line boxes are vertically centred by the browser when its minimum height grows.
        if (single) {
            const height = parseFloat(style.lineHeight) || parseFloat(style.fontSize) * 1.2;
            mirror.style.paddingTop = `${Math.max(0, (input.clientHeight - height) / 2)}px`;
        }
        mirror.style.left = `${parseFloat(style.borderLeftWidth) || 0}px`;
        mirror.style.top = `${parseFloat(style.borderTopWidth) || 0}px`;
        mirror.style.transform = `translate(${-input.scrollLeft}px, ${-input.scrollTop}px)`;
    }

    function paint() {
        mirror.replaceChildren();
        let end = 0;
        for (const issue of issues) {
            mirror.append(doc.createTextNode(checkedText.slice(end, issue.start)));
            const mark = doc.createElement('span');
            mark.className = 'board-spelling-error';
            mark.dataset.spellingStart = String(issue.start);
            mark.textContent = issue.word;
            mirror.append(mark);
            end = issue.end;
        }
        mirror.append(doc.createTextNode((checkedText ?? input.value).slice(end) + '\u200b'));
        geometry();
    }

    async function check(explicit = false) {
        clearTimeout(timer);
        if (disposed || composing || input.disabled || input.readOnly) return [];
        const version = ++generation;
        const text = input.value;
        if (explicit) status.textContent = 'Checking spelling…';
        try {
            speller = await english();
            if (disposed || version !== generation || input.value !== text) return [];
            const accepted = new Set([...savedWords(), ...ignored, 'viberails']);
            issues = findMisspellings(text, speller, accepted);
            checkedText = text;
            paint();
            if (explicit) status.textContent = issues.length
                ? `${issues.length} possible spelling ${issues.length === 1 ? 'issue' : 'issues'}.`
                : 'No spelling issues found.';
            return issues;
        } catch {
            if (!disposed && version === generation) status.textContent = 'Spelling unavailable. Use Check spelling to retry.';
            return [];
        }
    }

    function schedule() {
        ++generation;
        clearTimeout(timer);
        close();
        issues = [];
        checkedText = null;
        status.textContent = '';
        paint();
        if (!composing) timer = setTimeout(() => void check(), 350);
    }

    function acceptWord(persist) {
        if (!selected) return;
        const word = normalizeSpellingWord(selected.word);
        let message = 'Word ignored for this session.';
        if (persist) {
            try {
                const words = savedWords();
                words.add(word);
                if (words.size > 2000) throw new Error('Dictionary full');
                localStorage.setItem(STORAGE_KEY, JSON.stringify([...words]));
                message = 'Word added to your dictionary.';
            } catch {
                ignored.add(word);
                message = 'Word ignored for this session. Your dictionary could not be saved.';
            }
        } else ignored.add(word);
        close(true);
        for (const refresh of subscribers) refresh();
        status.textContent = message;
    }

    function replace(replacement) {
        if (!selected || input.disabled || input.readOnly || input.value !== checkedText) return close();
        const issue = selected;
        close(true);
        input.setSelectionRange(issue.start, issue.end);
        // insertText is intentional: Chromium keeps this replacement in its native undo stack.
        // setRangeText is the fallback for hosts without execCommand; never replace the field.
        const inserted = doc.execCommand?.('insertText', false, replacement);
        if (!inserted) {
            input.setRangeText(replacement, issue.start, issue.end, 'end');
            input.dispatchEvent(new Event('input', { bubbles: true }));
        }
        input.dispatchEvent(new Event('change', { bubbles: true }));
    }

    function positionPopup(issue) {
        const mark = mirror.querySelector(`[data-spelling-start="${issue.start}"]`);
        const box = mark?.getBoundingClientRect() || input.getBoundingClientRect();
        const field = input.getBoundingClientRect();
        const width = Math.min(280, doc.documentElement.clientWidth - 16);
        popup.style.width = `${width}px`;
        popup.style.left = `${Math.max(8, Math.min(Math.max(field.left, box.left), doc.documentElement.clientWidth - width - 8))}px`;
        const height = popup.getBoundingClientRect().height;
        const below = Math.max(field.top, Math.min(box.bottom, field.bottom)) + 4;
        popup.style.top = `${Math.max(8, Math.min(below, doc.documentElement.clientHeight - height - 8))}px`;
        popup.style.maxHeight = `${doc.documentElement.clientHeight - 16}px`;
    }

    function open(issue) {
        if (!issue || !speller || input.disabled || input.readOnly || input.value !== checkedText) return;
        close();
        selected = issue;
        popup = doc.createElement('div');
        popup.className = 'board-spelling-popup';
        popup.id = `board-spelling-popup-${nextId++}`;
        popup.setAttribute('role', 'dialog');
        popup.setAttribute('aria-label', `Spelling suggestions for ${issue.word}`);
        const heading = doc.createElement('div');
        heading.className = 'board-spelling-heading';
        heading.textContent = `Spelling: ${issue.word}`;
        popup.append(heading);
        const suggestions = speller.suggest(normalizeSpellingWord(issue.word)).slice(0, 5);
        for (let suggestion of suggestions) {
            if (/^[A-Z]/.test(issue.word)) suggestion = suggestion[0].toUpperCase() + suggestion.slice(1);
            addAction(suggestion, () => replace(suggestion));
        }
        if (!suggestions.length) {
            const note = doc.createElement('p');
            note.className = 'board-spelling-heading';
            note.textContent = 'No suggestions available.';
            popup.append(note);
        }
        addAction('Ignore word', () => acceptWord(false));
        addAction('Add to dictionary', () => acceptWord(true));
        // Keep the popover inside the card modal's focus boundary and stacking context.
        (input.closest('.modal') || shell).append(popup);
        button.setAttribute('aria-expanded', 'true');
        button.setAttribute('aria-controls', popup.id);
        positionPopup(issue);
        popup.querySelector('button')?.focus({ preventScroll: true });
    }

    function addAction(label, action) {
        const item = doc.createElement('button');
        item.type = 'button';
        item.textContent = label;
        // The detached popover and its listeners can be collected after every close.
        item.addEventListener('click', action);
        popup.append(item);
    }

    function atCaret() {
        const caret = input.selectionStart;
        return issues.find(issue => issue.start <= caret && issue.end >= caret);
    }

    input.addEventListener('input', schedule, { signal });
    input.addEventListener('compositionstart', () => { composing = true; schedule(); }, { signal });
    input.addEventListener('compositionend', () => { composing = false; schedule(); }, { signal });
    input.addEventListener('scroll', () => {
        geometry();
        // Blurring a horizontally scrolled input resets scrollLeft in Chromium.
        // Focusing a suggestion must not immediately dismiss its own popover.
        if (!popup?.contains(doc.activeElement)) close();
    }, { signal });
    input.addEventListener('keyup', geometry, { signal });
    input.addEventListener('click', event => {
        if (event.detail !== 1 || input.selectionStart !== input.selectionEnd || input.value !== checkedText) return;
        const issue = atCaret();
        const mark = issue && mirror.querySelector(`[data-spelling-start="${issue.start}"]`);
        // Hit-test the painted word, not a caret clamped to the end of a different line.
        if (mark && [...mark.getClientRects()].some(box => event.clientX >= box.left && event.clientX <= box.right
            && event.clientY >= box.top && event.clientY <= box.bottom)) open(issue);
    }, { signal });
    input.addEventListener('contextmenu', event => {
        // Chromium puts the caret at the clicked word before dispatching contextmenu.
        const issue = atCaret();
        if (issue && input.value === checkedText) { event.preventDefault(); open(issue); }
    }, { signal });
    input.addEventListener('keydown', event => {
        if (event.altKey && event.key === 'ArrowDown' && !composing) {
            event.preventDefault();
            event.stopImmediatePropagation();
            if (input.value === checkedText) open(atCaret() || issues[0]);
            else void check(true).then(found => open(found.find(issue => issue.start <= input.selectionStart && issue.end >= input.selectionStart) || found[0]));
        }
    }, { signal, capture: true });
    button.addEventListener('click', () => void check(true).then(found => open(found[0])), { signal });
    doc.addEventListener('keydown', event => {
        if (!popup) return;
        if (event.key === 'Escape') {
            event.preventDefault(); event.stopPropagation(); close(true);
        } else if (popup.contains(event.target) && ['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) {
            const items = [...popup.querySelectorAll('button')];
            const index = items.indexOf(doc.activeElement);
            const next = event.key === 'Home' ? 0 : event.key === 'End' ? items.length - 1
                : (index + (event.key === 'ArrowDown' ? 1 : -1) + items.length) % items.length;
            event.preventDefault(); items[next].focus();
        }
    }, { signal, capture: true });
    doc.addEventListener('pointerdown', event => { if (popup && !popup.contains(event.target)) close(); }, { signal, capture: true });
    doc.addEventListener('focusin', event => { if (popup && !popup.contains(event.target)) close(); }, { signal });
    doc.addEventListener('scroll', event => {
        if (popup && event.target !== input && !popup.contains(event.target)) close();
    }, { signal, capture: true });
    window.addEventListener('resize', () => { geometry(); close(); }, { signal });
    window.addEventListener('storage', event => { if (event.key === STORAGE_KEY) schedule(); }, { signal });
    const resize = new ResizeObserver(geometry);
    resize.observe(input);
    // Host-theme/font changes must not leave squiggles at the old glyph positions.
    const theme = new MutationObserver(geometry);
    theme.observe(doc.body, { attributes: true, attributeFilter: ['class', 'style'] });
    theme.observe(doc.documentElement, { attributes: true, attributeFilter: ['class', 'style'] });
    doc.fonts?.ready.then(() => { if (!disposed) geometry(); });
    doc.fonts?.addEventListener('loadingdone', geometry, { signal });
    const refresh = () => { schedule(); void check(); };
    subscribers.add(refresh);
    schedule();
    return () => {
        disposed = true;
        ++generation;
        clearTimeout(timer);
        close();
        lifetime.abort();
        resize.disconnect();
        theme.disconnect();
        subscribers.delete(refresh);
        if (originalSpellcheck === null) input.removeAttribute('spellcheck');
        else input.setAttribute('spellcheck', originalSpellcheck);
        if (originalDescribedBy === null) input.removeAttribute('aria-describedby');
        else input.setAttribute('aria-describedby', originalDescribedBy);
        shell.before(input);
        shell.remove();
        tools.remove();
    };
}
