import { LLM_MODEL_OPTIONS, normalizeLlmModel } from './llm-model-catalog.js';
import { parseCliArguments } from './cli-arguments.js';

/** Display only: preserve unknown/custom model identifiers instead of guessing a name. */
export function friendlyModelName(cli, model) {
    const value = String(model || '').trim();
    const normalized = normalizeLlmModel(cli, value);
    if (!(LLM_MODEL_OPTIONS[cli] || []).some(([id]) => id && id === normalized)) return value;
    let name = value.replace(/^(anthropic|openai|google|zai|zai-coding-plan|deepseek|moonshotai|xai|opencode)\//, '');
    const context = name.endsWith('[1m]') ? ' (1M context)' : '';
    name = name.replace(/\[1m\]$/, '').replace(/(\d)-(\d)/g, '$1.$2');
    if (/[A-Z ]/.test(name)) return name + context; // Antigravity already uses display names.
    return name.split('-').map(word => ({ gpt: 'GPT', glm: 'GLM', mai: 'MAI' })[word]
        || word.charAt(0).toUpperCase() + word.slice(1)).join(' ') + context;
}

export function friendlyEffortName(effort) {
    const value = String(effort || '').trim();
    return ({ none: 'None', minimal: 'Minimal', low: 'Low', medium: 'Medium', high: 'High',
        xhigh: 'Extra high', max: 'Maximum', ultra: 'Ultra' })[value] || value;
}

/** Read the same saved argv the Environment launcher uses. Last explicit option wins. */
export function workerModelSummary(cli, customArgs) {
    const args = parseCliArguments(customArgs);
    let model = '', effort = '';
    for (let i = 0; i < args.length; i++) {
        const arg = args[i];
        if (arg === '--') break;
        const equal = arg.indexOf('=');
        const flag = equal < 0 ? arg : arg.slice(0, equal);
        if (!['--model', '-m', '--effort', '--reasoning-effort', '--variant', '-c', '--config'].includes(flag)) continue;
        const value = equal < 0 ? args[++i] : arg.slice(equal + 1);
        if (!value) continue;
        if (flag === '--model' || flag === '-m') model = value;
        else if (flag === '-c' || flag === '--config') {
            if (cli !== 'codex') continue;
            const match = /^(model|model_reasoning_effort)=(.*)$/.exec(value);
            if (!match) continue;
            const setting = match[2].replace(/^["']|["']$/g, '');
            if (match[1] === 'model') model = setting;
            else effort = setting;
        } else effort = value;
    }
    return [friendlyModelName(cli, model), effort ? `${friendlyEffortName(effort)} effort` : '']
        .filter(Boolean).join(' · ');
}
