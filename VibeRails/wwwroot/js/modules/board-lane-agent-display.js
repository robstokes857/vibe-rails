import { getCliBrand } from './utils.js';
import { getJobCliForLlm } from './jobs-controller.js';
import { workerModelSummary } from './llm-display.js';

export function laneReviewerSummary(environment, environments = []) {
    const label = target => {
        const parts = (target?.selection || '').split(':');
        const env = parts[0] === 'env' ? environments.find(item => Number(item.id) === Number(parts[1])) : null;
        return env?.name || (parts[0] === 'base' ? getCliBrand(parts[1]).label : target?.selection) || 'Unavailable reviewer';
    };
    const routing = environment?.reviewerRouting;
    if (routing?.mode !== 'switch') return `Reviewer: ${getCliBrand(environment?.cli || '').label}`;
    const mappings = (routing.mappings || []).map(mapping => `${getCliBrand(mapping.sourceProvider).label} → ${label(mapping.reviewer)}`);
    return mappings.length ? `Switch reviewer: ${mappings.join('; ')}. Unknown, mixed, human or unmapped source → ${label(routing.fallback)}.`
        : `Reviewer: ${label(routing.fallback)} for every coding source.`;
}

export function workerIdentity(job, environments = []) {
    const actions = Array.isArray(job?.actions) ? job.actions : [];
    const worker = actions.length
        ? actions.find(action => Number(action.kind) === 0)
        : job?.environmentId ? job : null;
    if (!worker) {
        const checks = actions.filter(action => [2, 3].includes(Number(action.kind)));
        const label = checks.length
            ? checks.map(action => `${Number(action.kind) === 2 ? 'Code quality' : 'VCA'} · ${action.arguments?.join(' ') || 'working-tree'}`).join('; ')
            : job ? 'Script workflow' : 'Unavailable Automation';
        return { label, icon: checks.length ? 'list-check' : job ? 'code' : 'question' };
    }
    const environment = environments.find(item => worker.environmentId != null && Number(item.id) === Number(worker.environmentId));
    if (environment?.reviewerRouting?.mode === 'switch')
        return { label: 'Reviewer selection', icon: 'shuffle', workerName: environment.name };
    const cli = environment?.cli || getJobCliForLlm(worker.llm || job.llm);
    return { ...getCliBrand(cli || ''), icon: 'user-gear',
        workerName: environment?.name || worker.environmentName || job.environmentName,
        modelSummary: workerModelSummary(cli, environment?.customArgs) };
}
