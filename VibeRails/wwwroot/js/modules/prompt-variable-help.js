import { escapeHtml } from './utils.js';

const variables = [
    ['{{name}}', 'Ask for a value at launch'],
    ['{{name default="…"}}', 'Ask for a value with a prefilled answer'],
    ['{{datetime}}', 'Current date and time'],
    ['{{date}}', 'Current date'],
    ['{{time}}', 'Current time'],
    ['{{git_branch}}', 'Current Git branch'],
    ['{{env_name}}', 'Saved configuration name'],
    ['{{worktree}}', 'Create a Git worktree and start there; removed after 7 days'],
    ['{{board_card}}', 'Triggering Board card number; empty for other launches']
];

/** Discoverable shortcuts for the supported launch-prompt placeholders. */
export function promptVariableHelp() {
    return `<div class="prompt-variable-help">
        <p id="launch-prompt-variable-help" class="form-text">Click a variable to insert it at the cursor. You can also type <code>{{</code> for suggestions.</p>
        <div class="d-flex flex-wrap gap-2" role="group" aria-label="Insert a prompt variable">
            ${variables.map(([token, hint]) => `<button type="button" class="btn btn-sm btn-secondary" data-prompt-variable="${escapeHtml(token)}" title="${escapeHtml(hint)}" aria-label="Insert ${escapeHtml(token)}: ${escapeHtml(hint)}"><code>${escapeHtml(token)}</code></button>`).join('')}
        </div>
        <p class="form-text">Replace <code>name</code> with your own variable name to ask for a value at launch; a default prefills the answer. Other values fill in automatically. <code>{{board_card}}</code> is empty without a triggering Board card.</p>
        <details class="form-text"><summary>Worktree options</summary>
            <p><code>{{worktree}}</code> creates a checkout from HEAD in your home folder and starts the agent there. Optional: <code>{{worktree path="../my-task" delete_after_days=14}}</code>. Use a new directory outside the source checkout. Retention defaults to 7 days; use 7 or more, or <code>never</code>. Expired worktrees, including uncommitted files, are removed while Vibe Rails is open; active sessions postpone removal. Git branches are kept.</p>
        </details>
    </div>`;
}

/** Inserts into the native textarea so dirty tracking and undo stay intact. */
export function bindPromptVariableHelp(root, input) {
    const abort = new AbortController();
    root.querySelectorAll('[data-prompt-variable]').forEach(button => {
        button.addEventListener('click', () => {
            if (input.disabled || input.readOnly) return;
            const token = button.dataset.promptVariable;
            const start = input.selectionStart;
            const end = input.selectionEnd;
            if (input.maxLength > 0 && input.value.length - (end - start) + token.length > input.maxLength) return;
            input.focus({ preventScroll: true });
            input.setSelectionRange(start, end);
            // Chromium's native edit preserves Undo; other hosts use the textarea API.
            if (!input.ownerDocument.execCommand?.('insertText', false, token)) {
                input.setRangeText(token, start, end, 'end');
                input.dispatchEvent(new Event('input', { bubbles: true }));
            }
        }, { signal: abort.signal });
    });
    return () => abort.abort();
}
