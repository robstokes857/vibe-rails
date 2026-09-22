import { escapeHtml } from './utils.js';

// Shared pinned catalog for Environments and board launch options.
export const LLM_MODEL_OPTIONS = Object.freeze({
    'codex': [
        ['', 'Default (Codex recommended)'],
        ['gpt-6-astra', 'gpt-6-astra'],
        ['gpt-5.6-sol', 'gpt-5.6-sol'],
        ['gpt-5.6-terra', 'gpt-5.6-terra'],
        ['gpt-5.6-luna', 'gpt-5.6-luna'],
        ['gpt-5.5', 'gpt-5.5']
    ],
    'claude': [
        ['', 'Default (Claude recommended)'],
        ['claude-fable-5-1', 'claude-fable-5-1'],
        ['claude-fable-5', 'claude-fable-5'],
        ['claude-opus-5', 'claude-opus-5'],
        ['claude-opus-4-8', 'claude-opus-4-8'],
        ['claude-opus-4-7', 'claude-opus-4-7'],
        ['claude-sonnet-5', 'claude-sonnet-5'],
        ['claude-sonnet-4-6', 'claude-sonnet-4-6'],
        ['claude-haiku-4-5', 'claude-haiku-4-5']
    ],
    'copilot': [
        ['', 'Default (auto)'],
        ['claude-fable-5', 'claude-fable-5'],
        ['claude-opus-5', 'claude-opus-5'],
        ['claude-sonnet-5', 'claude-sonnet-5'],
        ['claude-sonnet-4.6', 'claude-sonnet-4.6'],
        ['claude-sonnet-4.5', 'claude-sonnet-4.5'],
        ['claude-haiku-4.5', 'claude-haiku-4.5'],
        ['claude-opus-4.8', 'claude-opus-4.8'],
        ['claude-opus-4.8-fast', 'claude-opus-4.8-fast'],
        ['claude-opus-4.7', 'claude-opus-4.7'],
        ['claude-opus-4.6', 'claude-opus-4.6'],
        ['claude-opus-4.5', 'claude-opus-4.5'],
        ['gpt-5.6-sol', 'gpt-5.6-sol'],
        ['gpt-5.6-terra', 'gpt-5.6-terra'],
        ['gpt-5.6-luna', 'gpt-5.6-luna'],
        ['gpt-5.5', 'gpt-5.5'],
        ['gpt-5.4', 'gpt-5.4'],
        ['gpt-5.4-mini', 'gpt-5.4-mini'],
        ['gpt-5.4-nano', 'gpt-5.4-nano'],
        ['gpt-5.3-codex', 'gpt-5.3-codex'],
        ['gpt-5-mini', 'gpt-5-mini'],
        ['gemini-3.7-flash', 'gemini-3.7-flash'],
        ['gemini-3.6-flash', 'gemini-3.6-flash'],
        ['gemini-3.5-flash', 'gemini-3.5-flash'],
        ['gemini-3.1-pro', 'gemini-3.1-pro'],
        ['mai-code-1.1-flash', 'mai-code-1.1-flash'],
        ['mai-code-1-flash', 'mai-code-1-flash'],
        ['raptor-mini', 'raptor-mini'],
        ['kimi-k2.7-code', 'kimi-k2.7-code'],
        ['kimi-k3', 'kimi-k3'],
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
        ['anthropic/claude-opus-5', 'anthropic/claude-opus-5'],
        ['anthropic/claude-sonnet-5', 'anthropic/claude-sonnet-5'],
        ['anthropic/claude-opus-4-5', 'anthropic/claude-opus-4-5'],
        ['anthropic/claude-sonnet-4-5', 'anthropic/claude-sonnet-4-5'],
        ['openai/gpt-5.6', 'openai/gpt-5.6'],
        ['openai/gpt-5.5', 'openai/gpt-5.5'],
        ['openai/gpt-5.2', 'openai/gpt-5.2'],
        ['openai/gpt-5.1-codex', 'openai/gpt-5.1-codex'],
        ['google/gemini-3-pro', 'google/gemini-3-pro'],
        ['zai/glm-5.2', 'zai/glm-5.2'],
        ['zai-coding-plan/glm-5.3', 'zai-coding-plan/glm-5.3'],
        ['deepseek/deepseek-v4-pro', 'deepseek/deepseek-v4-pro'],
        ['moonshotai/kimi-k3', 'moonshotai/kimi-k3'],
        ['xai/grok-4.6', 'xai/grok-4.6'],
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
