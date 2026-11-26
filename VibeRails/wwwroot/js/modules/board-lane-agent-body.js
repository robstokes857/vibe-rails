import { mountLaunchLane } from './launch-lane-editor.js';

/**
 * The lane panel's body: the lane's on-entry Agents and Actions in run order.
 *
 * It lists no runs. A run belongs to the card it ran for, and each card's Automations rail shows
 * its lane steps with their state, report, Skip and Retry (board-card-automations.js). The panel
 * used to repeat that for every card in the lane under "Card runs", a list that grew with each
 * card that stayed in the lane (VIBE-372).
 */
export function mountLaneAgentBody({
    app,
    panel,
    column,
    signal,
    updateCount,
    position,
    close
}) {
    const host = panel.querySelector('[data-lane-agents-content]');
    const disposeLane = mountLaunchLane(app, host, column.id, (settings) => {
        updateCount(settings);
        position();
    });
    const abort = new AbortController();
    panel.addEventListener(
        'click',
        (event) => {
            if (event.target.closest('[data-agent-action="close"]')) close?.();
        },
        { signal: abort.signal }
    );
    const dispose = () => {
        abort.abort();
        disposeLane();
    };
    dispose.beforeClose = disposeLane.beforeClose;
    signal.addEventListener('abort', dispose, { once: true });
    return dispose;
}
