import { isRunFinished } from './launch-api.js';

/** Observe actual native startup; enqueue alone does not mean a process has started. */
export async function waitForNativeLaunch(api, initial, { signal } = {}) {
    let run = initial;
    for (let attempt = 0; attempt < 30 && !signal?.aborted; attempt++) {
        if (run.status === 'running' || isRunFinished(run) || run.status === 'uncertain') return run;
        await waitForPoll(signal);
        if (signal?.aborted) return run;
        run = await api.call(`runs/${encodeURIComponent(run.id)}`, 'GET', null, { signal });
    }
    return run;
}

/** One-second polls a run may spend queued, and may spend starting once a root has claimed it. */
export const QUEUED_POLLS = 30;
export const STARTING_POLLS = 120;

/**
 * Follow a queued launch until its tab is on screen, without keeping a timer or request alive
 * after dismissal. Resolves `{ opened, run }`: `run` is the last state read, so a caller can say
 * why nothing opened. A run nobody has claimed is given up on after QUEUED_POLLS. A claimed one
 * is already starting its tab, so it is followed for STARTING_POLLS: giving up sooner left a
 * live agent in a tab the page never showed.
 */
export async function followLaunchTerminal(app, api, initial, { signal, metadata } = {}) {
    let run = initial;
    for (let polls = 0; !signal?.aborted; polls++) {
        const ended = isRunFinished(run) || run.status === 'uncertain';
        // The run records its session before the tab host spawns and publishes it
        // (LaunchExecution.BeforeSpawnAsync). Adopting on the run row alone added the tab
        // with no session: it showed the empty picker and never connected.
        if (run.tabId && run.sessionId && (ended || await tabShowsSession(app, run, signal))) {
            if (metadata) app.terminalController.rememberTabLaunch(run.tabId, metadata);
            if (!(await app.terminalController.adoptLaunchedTab(run.tabId)))
                app.navigate('terminal-focus', { tabId: run.tabId });
            return { opened: true, run };
        }
        if (ended || polls >= (run.status === 'queued' ? QUEUED_POLLS : STARTING_POLLS)) break;
        await waitForPoll(signal);
        if (signal?.aborted) break;
        run = await api.call(
            `runs/${encodeURIComponent(run.id)}`,
            'GET',
            null,
            { signal }
        );
    }
    return { opened: false, run };
}

/** Whether followLaunchTerminal put the run's tab on screen. */
export async function openLaunchTerminal(app, api, initial, options) {
    return (await followLaunchTerminal(app, api, initial, options)).opened;
}

/** Whether the run's tab host already reports the run's own session as live. */
async function tabShowsSession(app, run, signal) {
    try {
        const status = await app.apiCall(
            `/api/v1/terminal/tabs/${encodeURIComponent(run.tabId)}/status`,
            'GET',
            null,
            { showLoading: false, signal }
        );
        return status?.hasActiveSession === true && status.sessionId === run.sessionId;
    } catch {
        return false; // A host still starting can briefly fail its status read; poll again.
    }
}

function waitForPoll(signal) {
    return new Promise((resolve) => {
        const done = () => {
            clearTimeout(timer);
            signal?.removeEventListener('abort', done);
            resolve();
        };
        const timer = setTimeout(done, 1000);
        signal?.addEventListener('abort', done, { once: true });
    });
}
