import { escapeHtml } from './utils.js';
import codexCapabilities from './codex-model-capabilities.json' with { type: 'json' };

// Model, speed and effort metadata verified against Codex CLI 0.164.0, 2026-10-25.
// GPT-6.1 Terra: refreshed Codex model cache and official speed docs, 2026-10-26.
// CodexModelCapabilities embeds this same data for backend validation.
Object.values(codexCapabilities).forEach(capability => {
    Object.values(capability).forEach(Object.freeze);
    Object.freeze(capability);
});
Object.freeze(codexCapabilities);

function codexCapability(model) {
    const key = String(model || '').trim().toLowerCase();
    return Object.hasOwn(codexCapabilities, key) ? codexCapabilities[key] : null;
}

export function codexModelSupportsSpeed(model, speed) {
    return codexCapability(model)?.speeds.includes(speed) || false;
}

export function codexModelSupportsEffort(model, effort) {
    if (!effort) return true;
    const levels = codexCapability(model)?.efforts;
    if (levels) return levels.includes(effort);
    return !(String(model || '').trim().toLowerCase() === 'gpt-5.5' && effort === 'max');
}

// Keep unlisted saved effort values; constrain only known, unsupported combinations.
export function normalizeCodexModelEffort(model, effort) {
    const value = String(effort || '').trim();
    const levels = codexCapability(model)?.efforts;
    if (levels && value === 'minimal') return levels[0];
    if (levels && value === 'ultra' && !levels.includes(value)) return levels.at(-1);
    return !codexModelSupportsEffort(model, value) && value === 'max' ? 'xhigh' : value;
}

// Shared pinned catalog for Environments and board launch options.
export const LLM_MODEL_OPTIONS = Object.freeze({
    'codex': [
        ['', 'Default (Codex recommended)'],
        ['gpt-6-astra', 'gpt-6-astra'],
        ['gpt-6.1-sol', 'gpt-6.1-sol'],
        ['gpt-6.1-terra', 'gpt-6.1-terra'],
        ['gpt-6-sol', 'gpt-6-sol'],
        ['gpt-6-luna', 'gpt-6-luna'],
        ['gpt-5.6-sol', 'gpt-5.6-sol'],
        ['gpt-5.6-terra', 'gpt-5.6-terra'],
        ['gpt-5.6-luna', 'gpt-5.6-luna']
    ],
    // "[1m]" is Claude Code's 1M-context marker on a full model ID. Behind the VibeRails LLM
    // proxy (a custom ANTHROPIC_BASE_URL) Claude Code budgeted a 200K window for the bare ID
    // (measured 2026-09-27); the [1m] form keeps the 1M window. Direct launches treat the suffix
    // as a no-op on natively-1M models, so it is never worse. The value keeps the suffix because
    // it is the --model argument; the label is the model's name, so nobody has to read "[1m]".
    // Only generation-5 models are pinned (owner request 2026-10-02); older saves show "(custom)".
    'claude': [
        ['', 'Default (Claude recommended)'],
        ['claude-fable-5-1[1m]', 'Fable 5.1'],
        ['claude-fable-5[1m]', 'Fable 5'],
        ['claude-opus-5-5[1m]', 'Opus 5.5'],
        ['claude-opus-5[1m]', 'Opus 5'],
        ['claude-sonnet-5-5[1m]', 'Sonnet 5.5'],
        ['claude-sonnet-5[1m]', 'Sonnet 5'],
        ['claude-haiku-5-5[1m]', 'Haiku 5.5']
    ],
    'copilot': [
        ['', 'Default (auto)'],
        ['claude-fable-5.1', 'claude-fable-5.1'],
        ['claude-fable-5', 'claude-fable-5'],
        ['claude-opus-5.5', 'claude-opus-5.5'],
        ['claude-opus-5', 'claude-opus-5'],
        ['claude-sonnet-5.5', 'claude-sonnet-5.5'],
        ['claude-sonnet-5', 'claude-sonnet-5'],
        ['claude-haiku-5.5', 'claude-haiku-5.5'],
        ['claude-haiku-4.5', 'claude-haiku-4.5'],
        ['claude-opus-4.8', 'claude-opus-4.8'],
        ['claude-opus-4.8-fast', 'claude-opus-4.8-fast'],
        ['claude-sonnet-4.6', 'claude-sonnet-4.6'],
        ['gpt-6-astra', 'gpt-6-astra'],
        ['gpt-6.1-sol', 'gpt-6.1-sol'],
        ['gpt-6-sol', 'gpt-6-sol'],
        ['gpt-6-luna', 'gpt-6-luna'],
        ['gpt-5.6-sol', 'gpt-5.6-sol'],
        ['gpt-5.6-terra', 'gpt-5.6-terra'],
        ['gpt-5.6-luna', 'gpt-5.6-luna'],
        ['gpt-5.3-codex', 'gpt-5.3-codex'],
        ['gemini-4-argon', 'gemini-4-argon'],
        ['gemini-3.8-flash', 'gemini-3.8-flash'],
        ['mai-code-1.1-flash', 'mai-code-1.1-flash'],
        ['kimi-k3', 'kimi-k3'],
        ['grok-4.7', 'grok-4.7'],
        ['grok-4.6', 'grok-4.6'],
        ['grok-4.5', 'grok-4.5'],
    ],
    'antigravity': [
        ['', 'Default (Antigravity recommended)'],
        ['Gemini 3.5 Flash (Medium)', 'Gemini 3.5 Flash (Medium)'],
        ['Gemini 3.5 Flash (High)', 'Gemini 3.5 Flash (High)'],
        ['Gemini 3.5 Flash (Low)', 'Gemini 3.5 Flash (Low)'],
        ['Gemini 3.1 Pro (Low)', 'Gemini 3.1 Pro (Low)'],
        ['Gemini 3.1 Pro (High)', 'Gemini 3.1 Pro (High)'],
        ['Claude Sonnet 4.6 (Thinking)', 'Claude Sonnet 4.6 (Thinking)'],
        ['Claude Opus 4.6 (Thinking)', 'Claude Opus 4.6 (Thinking)'],
        ['GPT-OSS 120B (Medium)', 'GPT-OSS 120B (Medium)']
    ],
    'opencode': [
        ['', 'Default (OpenCode recommended)'],
        ['anthropic/claude-fable-5-1', 'anthropic/claude-fable-5-1'],
        ['anthropic/claude-fable-5', 'anthropic/claude-fable-5'],
        ['anthropic/claude-opus-5-5', 'anthropic/claude-opus-5-5'],
        ['anthropic/claude-opus-5', 'anthropic/claude-opus-5'],
        ['anthropic/claude-sonnet-5-5', 'anthropic/claude-sonnet-5-5'],
        ['anthropic/claude-sonnet-5', 'anthropic/claude-sonnet-5'],
        ['anthropic/claude-haiku-5-5', 'anthropic/claude-haiku-5-5'],
        ['anthropic/claude-opus-4-5', 'anthropic/claude-opus-4-5'],
        ['anthropic/claude-sonnet-4-5', 'anthropic/claude-sonnet-4-5'],
        ['openai/gpt-6-astra', 'openai/gpt-6-astra'],
        ['openai/gpt-6.1-sol', 'openai/gpt-6.1-sol'],
        ['openai/gpt-6-sol', 'openai/gpt-6-sol'],
        ['openai/gpt-6-luna', 'openai/gpt-6-luna'],
        ['openai/gpt-5.6-sol', 'openai/gpt-5.6-sol'],
        ['openai/gpt-5.6-terra', 'openai/gpt-5.6-terra'],
        ['openai/gpt-5.6-luna', 'openai/gpt-5.6-luna'],
        ['openai/gpt-5.6', 'openai/gpt-5.6'],
        ['openai/gpt-5.5', 'openai/gpt-5.5'],
        ['openai/gpt-5.2', 'openai/gpt-5.2'],
        ['google/gemini-4-argon', 'google/gemini-4-argon'],
        ['google/gemini-3.8-flash', 'google/gemini-3.8-flash'],
        ['zai/glm-5.3', 'zai/glm-5.3'],
        ['zai/glm-5.2', 'zai/glm-5.2'],
        ['zai-coding-plan/glm-5.3', 'zai-coding-plan/glm-5.3'],
        ['deepseek/deepseek-v4-pro', 'deepseek/deepseek-v4-pro'],
        ['moonshotai/kimi-k3.1-pro', 'moonshotai/kimi-k3.1-pro'],
        ['moonshotai/kimi-k3.1-pro-highspeed', 'moonshotai/kimi-k3.1-pro-highspeed'],
        ['moonshotai/kimi-k3', 'moonshotai/kimi-k3'],
        ['xai/grok-4.7', 'xai/grok-4.7'],
        ['xai/grok-4.6', 'xai/grok-4.6'],
        ['opencode/gpt-6-astra', 'opencode/gpt-6-astra (Zen)'],
        ['opencode/gpt-6.1-sol', 'opencode/gpt-6.1-sol (Zen)'],
        ['opencode/gpt-6-luna', 'opencode/gpt-6-luna (Zen)'],
        ['opencode/gpt-5.1-codex', 'opencode/gpt-5.1-codex (Zen)'],
    ],
    'grok': [
        ['', 'Default (Grok recommended)'],
        ['grok-4.7', 'grok-4.7'],
        ['grok-4.6', 'grok-4.6']
    ]
});

export function renderLlmModelOptions(cli, selectedModel = '') {
    const selected = String(selectedModel || '').trim();
    const options = LLM_MODEL_OPTIONS[cli] || [['', 'Default']];
    const rendered = options.map(([value, label]) => `<option value="${escapeHtml(value)}" ${selected === value ? 'selected' : ''}>${escapeHtml(label)}</option>`);
    if (selected && !options.some(([value]) => value === selected)) rendered.push(`<option value="${escapeHtml(selected)}" selected>${escapeHtml(selected)} (custom)</option>`);
    return rendered.join('');
}

// Trims a saved model value and, for Claude, upgrades a bare ID whose [1m] form is pinned
// (`claude-fable-5-1` -> `claude-fable-5-1[1m]`). Bare 1M-capable IDs only exist in
// environments and Board cards saved before 2026-09-28, when the dropdown still offered both
// forms; reopening such a save now selects the pinned [1m] entry instead of showing "(custom)".
// Anything else (unpinned IDs, other CLIs) passes through unchanged.
export function normalizeLlmModel(cli, model) {
    const value = String(model || '').trim();
    if (cli !== 'claude' || !value || value.endsWith('[1m]')) return value;
    const oneMillion = `${value}[1m]`;
    return (LLM_MODEL_OPTIONS.claude || []).some(([pinned]) => pinned === oneMillion) ? oneMillion : value;
}
