import { escapeHtml } from './utils.js';

// The trackers a dedicated board can mirror (VIBE-165). The server names them in remoteProvider.
const TRACKERS = {
    jira: { label: 'Jira', logo: 'assets/img/jira.svg' },
    ado: { label: 'Azure DevOps', logo: 'assets/img/azure-devops.svg' }
};

/** "Jira", "Azure DevOps", or '' for a local board or an unknown provider. */
export function trackerLabel(provider) {
    return TRACKERS[provider]?.label || '';
}

/** The tracker whose dedicated board this is, or null. isJiraBoard covers older responses. */
export function boardTracker(board) {
    if (!board) return null;
    if (board.remoteProvider) return TRACKERS[board.remoteProvider] ? board.remoteProvider : null;
    return board.isJiraBoard ? 'jira' : null;
}

/** The issue a card is linked to: { provider, key, url }, or null. The Jira fields cover older responses. */
export function cardTracker(card) {
    if (card?.remoteProvider && card.remoteIssueKey) {
        if (!TRACKERS[card.remoteProvider]) return null;
        const url = card.remoteIssueUrl || (card.remoteProvider === 'jira' ? card.jiraIssueUrl : null) || null;
        return { provider: card.remoteProvider, key: card.remoteIssueKey, url };
    }
    return card?.jiraIssueKey ? { provider: 'jira', key: card.jiraIssueKey, url: card.jiraIssueUrl || null } : null;
}

/** Fixed local brand artwork shared by the Board picker, settings and tracker badges. */
export function boardBrandLogo(provider, label = provider && TRACKERS[provider] ? `${TRACKERS[provider].label} board` : 'VibeRails board') {
    const source = TRACKERS[provider]?.logo || 'assets/img/logo.png';
    return `<img class="board-brand-logo" src="${source}" alt="${escapeHtml(label)}" width="18" height="18"${label ? '' : ' aria-hidden="true"'}>`;
}
