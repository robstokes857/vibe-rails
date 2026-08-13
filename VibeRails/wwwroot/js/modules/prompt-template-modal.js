import { escapeHtml } from './utils.js';
import { stepDisplayName } from './environment-steps.js';

const TOKEN_PATTERN = /\{\{\s*([A-Za-z_][A-Za-z0-9_.-]*)([^{}]*)\s*\}\}/g;
const DEFAULT_PATTERN = /\bdefault\s*=\s*(?:"((?:\\.|[^"\\])*)"|'((?:\\.|[^'\\])*)'|([^\s}]+))/i;

// Reserved names that never become fill-in fields. Mirrors PromptPlaceholderService in the
// backend, which owns the authoritative resolution pass at launch. datetime/date/time/env_name
// resolve here too (for the preview and the submitted text); git_branch and step need the server
// (a git call / a shell run), so their tokens are deliberately left literal for it.
// "step" is on this list because {{step:<id>}} parses under TOKEN_PATTERN as name "step" with
// ":<id>" falling into the argument capture.
const BUILTIN_TOKEN_NAMES = Object.freeze(['datetime', 'date', 'time', 'git_branch', 'env_name']);
const STEP_ID_PATTERN = /step\s*:\s*([0-9a-fA-F-]+)/i;

function lower(value) {
    return (value || '').toString().trim().toLowerCase();
}

function isReservedToken(name) {
    const key = lower(name);
    return key === 'step' || BUILTIN_TOKEN_NAMES.includes(key);
}

function pad2(value) {
    return String(value).padStart(2, '0');
}

function formatLocalDate(now) {
    return `${now.getFullYear()}-${pad2(now.getMonth() + 1)}-${pad2(now.getDate())}`;
}

function formatLocalTime(now) {
    return `${pad2(now.getHours())}:${pad2(now.getMinutes())}`;
}

/**
 * Client-side value for a built-in token, or null for the ones only the server can resolve
 * (git_branch, step) — null means "leave the token literal and let the launch pass finish it".
 */
function builtinTokenValue(name, context = {}, now = new Date()) {
    switch (lower(name)) {
        case 'datetime': return `${formatLocalDate(now)} ${formatLocalTime(now)}`;
        case 'date': return formatLocalDate(now);
        case 'time': return formatLocalTime(now);
        case 'env_name': return String(context.environmentName ?? '');
        default: return null;
    }
}

function unescapeQuotedValue(value) {
    return String(value ?? '').replace(/\\(["'\\])/g, '$1');
}

function parseToken(match) {
    const name = match?.[1] || '';
    const args = match?.[2] || '';
    const defaultMatch = args.match(DEFAULT_PATTERN);
    const rawDefault = defaultMatch
        ? (defaultMatch[1] ?? defaultMatch[2] ?? defaultMatch[3] ?? '')
        : '';

    return {
        token: match?.[0] || '',
        name,
        defaultValue: defaultMatch ? unescapeQuotedValue(rawDefault) : '',
        hasDefault: Boolean(defaultMatch)
    };
}

export function extractPromptTemplateVariables(template) {
    const text = String(template ?? '');
    const variables = [];
    const byName = new Map();

    for (const match of text.matchAll(TOKEN_PATTERN)) {
        const token = parseToken(match);
        if (!token.name || isReservedToken(token.name)) {
            continue;
        }

        const existing = byName.get(token.name);
        if (existing) {
            if (!existing.hasDefault && token.hasDefault) {
                existing.defaultValue = token.defaultValue;
                existing.hasDefault = true;
            }
            existing.count += 1;
            continue;
        }

        const variable = {
            name: token.name,
            label: labelizeVariableName(token.name),
            defaultValue: token.defaultValue,
            hasDefault: token.hasDefault,
            count: 1
        };
        byName.set(token.name, variable);
        variables.push(variable);
    }

    return variables;
}

export function hasPromptTemplateVariables(template) {
    return extractPromptTemplateVariables(template).length > 0;
}

export function renderPromptTemplate(template, values = {}, context = {}) {
    return String(template ?? '').replace(TOKEN_PATTERN, (...args) => {
        const token = parseToken(args);
        if (isReservedToken(token.name)) {
            // git_branch and {{step:...}} come back as the literal token — the server's
            // launch-time pass (PromptPlaceholderService) resolves them.
            return builtinTokenValue(token.name, context) ?? token.token;
        }
        return values[token.name] ?? '';
    });
}

export function findEnvironmentForPrompt(environments = [], cli, environmentName) {
    if (!environmentName) {
        return null;
    }

    const cliKey = lower(cli);
    const envKey = lower(environmentName);
    return (Array.isArray(environments) ? environments : []).find((environment) =>
        lower(environment?.name) === envKey
        && (!cliKey || lower(environment?.cli) === cliKey)
    ) || null;
}

export async function resolvePromptTemplateForLaunch(app, options = {}) {
    const explicitPrompt = typeof options.initialPrompt === 'string'
        ? options.initialPrompt
        : null;
    const environment = findEnvironmentForPrompt(
        app?.data?.environments || [],
        options.cli,
        options.environmentName
    );
    const sourcePrompt = explicitPrompt ?? environment?.customPrompt ?? '';
    const variables = extractPromptTemplateVariables(sourcePrompt);

    if (variables.length === 0) {
        return {
            canceled: false,
            initialPrompt: explicitPrompt
        };
    }

    const resolved = await showPromptTemplateModal(app, {
        template: sourcePrompt,
        variables,
        environmentName: options.environmentName || environment?.name || '',
        // For the preview's {{step:<id>}} chips — EnvironmentResponse already carries steps.
        steps: environment?.steps || [],
        title: options.title || environment?.name || ''
    });

    return {
        canceled: resolved == null,
        initialPrompt: resolved
    };
}

export function labelizeVariableName(name) {
    const words = String(name || '')
        .replace(/[_.-]+/g, ' ')
        .trim()
        .split(/\s+/)
        .filter(Boolean);

    if (words.length === 0) {
        return 'Value';
    }

    return words
        .map((word) => word.charAt(0).toUpperCase() + word.slice(1))
        .join(' ');
}

function buildValues(variables, container) {
    const values = {};
    variables.forEach((variable) => {
        const input = container.querySelector(`[data-prompt-template-input="${cssEscape(variable.name)}"]`);
        values[variable.name] = input?.value ?? '';
    });
    return values;
}

/** Preview text for a reserved token: the resolved value, or a described stand-in for the
 *  server-resolved ones. */
function reservedTokenPreview(token, context = {}) {
    if (lower(token.name) === 'step') {
        const stepId = STEP_ID_PATTERN.exec(token.token)?.[1]?.toLowerCase() ?? '';
        const steps = Array.isArray(context.steps) ? context.steps : [];
        const step = steps.find(candidate => String(candidate?.id ?? '').toLowerCase() === stepId);
        return step ? `(output of step: ${stepDisplayName(step)})` : '(output of a deleted step)';
    }
    if (lower(token.name) === 'git_branch') {
        return '(current git branch)';
    }
    return builtinTokenValue(token.name, context) ?? token.token;
}

function buildPreviewHtml(template, values = {}, context = {}) {
    const text = String(template ?? '');
    let html = '';
    let lastIndex = 0;

    for (const match of text.matchAll(TOKEN_PATTERN)) {
        const token = parseToken(match);
        html += escapeHtml(text.slice(lastIndex, match.index));

        if (isReservedToken(token.name)) {
            // Auto-filled — rendered as a chip but not clickable: there is no input to focus.
            html += `<span class="vb-prompt-template-token vb-prompt-template-token-builtin">${escapeHtml(reservedTokenPreview(token, context))}</span>`;
        } else {
            const value = values[token.name] ?? '';
            const display = value.length > 0 ? value : token.token;
            const emptyClass = value.length > 0 ? '' : ' vb-prompt-template-token-empty';
            html += `<span class="vb-prompt-template-token${emptyClass}" role="button" tabindex="0" data-prompt-template-focus="${escapeHtml(token.name)}">${escapeHtml(display)}</span>`;
        }

        lastIndex = match.index + token.token.length;
    }

    html += escapeHtml(text.slice(lastIndex));
    return html;
}

function renderModalHtml({ template, variables, environmentName, context }) {
    const envSuffix = environmentName
        ? `<span class="text-muted"> for </span><code>${escapeHtml(environmentName)}</code>`
        : '';

    return `
        <form class="vb-prompt-template-modal" id="vb-prompt-template-form" data-prompt-template-modal>
            <p class="text-muted small mb-3">
                Fill the template values${envSuffix}. The resolved message below is what will be sent when the terminal starts.
            </p>
            <div class="row g-3">
                <div class="col-lg-5">
                    <div class="vb-prompt-template-fields">
                        ${variables.map((variable, index) => `
                            <div class="mb-3">
                                <label class="form-label" for="vb-prompt-template-input-${index}">${escapeHtml(variable.label)}</label>
                                <textarea class="form-control" id="vb-prompt-template-input-${index}" rows="2"
                                    data-prompt-template-input="${escapeHtml(variable.name)}"
                                    spellcheck="false"
                                    ${variable.hasDefault ? '' : 'required'}>${escapeHtml(variable.defaultValue)}</textarea>
                                <small class="form-text text-muted">
                                    <code>{{${escapeHtml(variable.name)}}}</code>${variable.hasDefault ? ' default value loaded' : ' required for this launch'}
                                </small>
                            </div>
                        `).join('')}
                    </div>
                </div>
                <div class="col-lg-7">
                    <div class="d-flex align-items-center justify-content-between gap-2 mb-2">
                        <label class="form-label mb-0">Resolved initial message</label>
                        <span class="text-muted x-small">Click highlighted text to edit it</span>
                    </div>
                    <pre class="vb-prompt-template-preview" data-prompt-template-preview>${buildPreviewHtml(template, Object.fromEntries(variables.map(v => [v.name, v.defaultValue])), context)}</pre>
                </div>
            </div>
            <div class="d-flex justify-content-end gap-2 mt-3">
                <button type="button" class="btn btn-secondary" data-action="close-modal">Cancel</button>
                <button type="submit" class="btn btn-primary">Start Terminal</button>
            </div>
        </form>
    `;
}

function showPromptTemplateModal(app, options) {
    return new Promise((resolve) => {
        if (!app?.showModal) {
            resolve(null);
            return;
        }

        const title = options.title
            ? `Fill Prompt Values: ${options.title}`
            : 'Fill Prompt Values';
        // Context for auto-filled tokens: env_name resolves client-side; steps give the
        // {{step:<id>}} preview chips their names.
        const context = { environmentName: options.environmentName, steps: options.steps };
        app.showModal(title, renderModalHtml({ ...options, context }));

        const modalContainer = document.getElementById('modal-container');
        const form = modalContainer?.querySelector('#vb-prompt-template-form');
        const preview = modalContainer?.querySelector('[data-prompt-template-preview]');
        const variables = options.variables || [];
        let settled = false;
        let dismissObserver = null;

        const settle = (value) => {
            if (settled) {
                return;
            }
            settled = true;
            dismissObserver?.disconnect();
            modalContainer?.removeEventListener('click', onCloseClick, true);
            preview?.removeEventListener('click', onPreviewClick);
            preview?.removeEventListener('keydown', onPreviewKeydown);
            form?.removeEventListener('input', updatePreview);
            form?.removeEventListener('submit', onSubmit);
            app.closeModal?.();
            resolve(value);
        };

        const updatePreview = () => {
            if (!preview || !form) {
                return;
            }
            preview.innerHTML = buildPreviewHtml(options.template, buildValues(variables, form), context);
        };

        const focusInput = (name) => {
            const input = form?.querySelector(`[data-prompt-template-input="${cssEscape(name)}"]`);
            input?.focus();
            input?.select?.();
        };

        function onCloseClick(event) {
            if (event.target?.closest?.('[data-action="close-modal"]')) {
                settle(null);
            }
        }

        function onPreviewClick(event) {
            const target = event.target?.closest?.('[data-prompt-template-focus]');
            if (target) {
                focusInput(target.dataset.promptTemplateFocus);
            }
        }

        function onPreviewKeydown(event) {
            if (event.key !== 'Enter' && event.key !== ' ') {
                return;
            }
            const target = event.target?.closest?.('[data-prompt-template-focus]');
            if (target) {
                event.preventDefault();
                focusInput(target.dataset.promptTemplateFocus);
            }
        }

        function onSubmit(event) {
            event.preventDefault();
            if (!form?.reportValidity?.()) {
                return;
            }

            const resolved = renderPromptTemplate(options.template, buildValues(variables, form), context);
            if (!resolved.trim()) {
                app.showError?.('Resolved initial message is empty.');
                return;
            }

            settle(resolved);
        }

        modalContainer?.addEventListener('click', onCloseClick, true);
        preview?.addEventListener('click', onPreviewClick);
        preview?.addEventListener('keydown', onPreviewKeydown);
        form?.addEventListener('input', updatePreview);
        form?.addEventListener('submit', onSubmit);

        // Any dismissal other than our Cancel button / submit (Escape, the modal ✕, a
        // backdrop click) wipes #modal-container without calling settle(). Watch for the
        // form leaving the DOM and resolve as canceled, so the awaiting launch never hangs
        // and the capture-phase listeners above don't leak.
        if (!form) {
            settle(null);
            return;
        }
        dismissObserver = new MutationObserver(() => {
            if (!form.isConnected) {
                settle(null);
            }
        });
        dismissObserver.observe(modalContainer, { childList: true, subtree: true });

        updatePreview();

        const firstEmpty = variables.find(v => !v.hasDefault);
        const firstName = firstEmpty?.name || variables[0]?.name;
        setTimeout(() => focusInput(firstName), 0);
    });
}

function cssEscape(value) {
    if (window.CSS && typeof window.CSS.escape === 'function') {
        return window.CSS.escape(value);
    }

    return String(value).replace(/["\\]/g, '\\$&');
}
