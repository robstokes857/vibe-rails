import { escapeHtml } from './utils.js';
import { wrapSelectionAsCode } from './board-text.js';
import { bindBoardSpelling } from './board-spelling.js';

/** Shared native input. Owners provide references and attachment persistence; nothing renders beneath the field. */
export function composerMarkup({
    name,
    value = '',
    placeholder = '',
    disabled = false,
    submitLabel = '',
    label = name
}) {
    const off = disabled ? ' disabled' : '';
    return `
        <div class="board-composer" data-board-composer="${escapeHtml(name)}">
            <div class="board-composer-toolbar">
                <button type="button" class="board-composer-btn" data-board-composer-action="code"
                    title="Format the selection as code"${off}>
                    <i class="fa-solid fa-code" aria-hidden="true"></i><span>Code</span>
                </button>
                <button type="button" class="board-composer-btn" data-board-composer-action="image"
                    title="Attach a file (or paste an image)"${off}>
                    <i class="fa-solid fa-paperclip" aria-hidden="true"></i><span>Attach</span>
                </button>
                <span class="board-composer-hint">@ file · ! card/session · # commit</span>
            </div>
                <textarea class="form-control board-composer-input" data-board-composer-input aria-label="${escapeHtml(label)}"
                placeholder="${escapeHtml(placeholder)}" rows="3" spellcheck="true"${off}>${escapeHtml(value)}</textarea>
            <input type="file" hidden data-board-composer-file multiple>
            <div class="board-composer-busy" data-board-composer-busy hidden>Adding files…</div>
            ${
                submitLabel
                    ? `<div class="board-composer-footer">
                <button type="button" class="btn btn-sm btn-outline-primary board-composer-submit"
                    data-board-composer-action="submit"${off}>${escapeHtml(submitLabel)}</button>
            </div>`
                    : ''
            }
        </div>`;
}

export function bindCardComposer(
    composer,
    {
        onSubmit = null,
        onAttach = null,
        bindHighlights = null,
        bindReferences = null
    } = {}
) {
    if (!composer) return () => {};
    const input = composer.querySelector('[data-board-composer-input]');
    const file = composer.querySelector('[data-board-composer-file]');
    if (!input) return () => {};

    const abort = new AbortController();
    const disposers = [];
    const listen = (target, event, handler) =>
        target?.addEventListener(event, handler, { signal: abort.signal });
    const autoGrow = () => {
        input.style.height = 'auto';
        const needed = input.scrollHeight;
        // Drop the inline height first so a flex parent (the create-card
        // description) can size the box. Only lock a pixel height when the
        // text is taller than that allocation.
        input.style.height = '';
        if (needed > input.clientHeight + 1) {
            input.style.height = `${needed}px`;
        }
    };
    listen(input, 'input', autoGrow);
    if (bindHighlights) disposers.push(bindHighlights(input));
    disposers.push(bindBoardSpelling(input));
    if (bindReferences) disposers.push(bindReferences(input));
    // Sized now rather than on the next frame: an occluded page never gets one,
    // and the description box would open at its one-line default.
    autoGrow();

    const submit = () => {
        if (!onSubmit) return;
        const text = input.value.trim();
        if (!text) return;
        onSubmit(text);
    };

    composer
        .querySelectorAll('[data-board-composer-action]')
        .forEach((button) => {
            listen(button, 'click', () => {
                const action = button.dataset.boardComposerAction;
                if (action === 'code') {
                    const next = wrapSelectionAsCode(
                        input.value,
                        input.selectionStart,
                        input.selectionEnd
                    );
                    input.value = next.value;
                    input.setSelectionRange(
                        next.selectionStart,
                        next.selectionEnd
                    );
                    input.focus();
                    input.dispatchEvent(new Event('input', { bubbles: true }));
                } else if (action === 'image') {
                    file?.click();
                } else if (action === 'submit') {
                    submit();
                }
            });
        });

    listen(input, 'keydown', (event) => {
        if (
            (event.ctrlKey || event.metaKey) &&
            event.key === 'Enter' &&
            onSubmit
        ) {
            event.preventDefault();
            event.stopPropagation();
            submit();
        }
    });

    listen(file, 'change', () => {
        onAttach?.(input, Array.from(file.files || []));
        file.value = '';
    });

    // The first paste handler in this codebase: pull image files off the
    // clipboard and let normal text paste through untouched.
    listen(input, 'paste', (event) => {
        const images = Array.from(event.clipboardData?.files || []).filter(
            (item) => item.type.startsWith('image/')
        );
        if (images.length === 0 || !onAttach) return;
        event.preventDefault();
        onAttach?.(input, images);
    });

    listen(input, 'dragover', (event) => {
        if (
            !onAttach ||
            !Array.from(event.dataTransfer?.types || []).includes('Files')
        )
            return;
        event.preventDefault();
        composer.classList.add('is-drop-target');
    });
    listen(input, 'dragleave', () =>
        composer.classList.remove('is-drop-target')
    );
    listen(input, 'drop', (event) => {
        const images = Array.from(event.dataTransfer?.files || []);
        composer.classList.remove('is-drop-target');
        if (images.length === 0 || !onAttach) return;
        event.preventDefault();
        onAttach?.(input, images);
    });
    return () => {
        abort.abort();
        disposers.reverse().forEach((dispose) => dispose?.());
    };
}
