import { escapeHtml } from './utils.js';

/** Fixed local brand artwork shared by the Board picker, settings and Jira badges. */
export function boardBrandLogo(isJiraBoard, label = isJiraBoard ? 'Jira board' : 'VibeRails board') {
    const source = isJiraBoard ? 'assets/img/jira.svg' : 'assets/img/logo.png';
    return `<img class="board-brand-logo" src="${source}" alt="${escapeHtml(label)}" width="18" height="18"${label ? '' : ' aria-hidden="true"'}>`;
}
