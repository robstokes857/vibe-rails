import { escapeHtml } from './utils.js';

export const AGENT_PURPOSES = [
    ['work', 'Not specified'], ['code_review', 'Code review'], ['testing', 'Testing'],
    ['building', 'Building'], ['deploying', 'Deploying'], ['documentation', 'Documentation'], ['other', 'Other']
];

export const agentPurposeLabel = purpose => AGENT_PURPOSES.find(([value]) => value === purpose)?.[1] || 'Not specified';
export const agentPurposeOptions = (selected = 'work') => AGENT_PURPOSES.map(([value, label]) =>
    `<option value="${value}"${value === selected ? ' selected' : ''}>${escapeHtml(label)}</option>`).join('');

export function matchesCommentFilter(entry, filter) {
    if (entry.isAttention === true || filter === 'all') return true;
    if (filter === 'agent') return entry.author?.kind === 'agent';
    if (filter === 'human') return entry.author?.kind !== 'agent';
    return entry.author?.kind === 'agent' && (entry.purpose || 'work') === filter;
}
