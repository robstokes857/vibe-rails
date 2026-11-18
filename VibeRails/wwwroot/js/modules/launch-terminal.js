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

/** Follow a queued launch without keeping a timer or request alive after dismissal. */
export async function openLaunchTerminal(app, api, initial, { signal, metadata } = {}) {
    let run = initial;
    for (let attempt = 0; attempt < 30 && !signal?.aborted; attempt++) {
        const ended = isRunFinished(run) || run.status === 'uncertain';
        // The run records its session before the tab host spawns and publishes it
        // (LaunchExecution.BeforeSpawnAsync). Adopting on the run row alone added the tab
        // with no session: it showed the empty picker and never connected.
        if (run.tabId && run.sessionId && (ended || await tabShowsSession(app, run, signal))) {
            if (metadata) app.terminalController.rememberTabLaunch(run.tabId, metadata);
            if (!(await app.terminalController.adoptLaunchedTab(run.tabId)))
                app.navigate('terminal-focus', { tabId: run.tabId });
            return true;
        }
        if (ended) return false;
        await waitForPoll(signal);
        if (signal?.aborted) return false;
        run = await api.call(
            `runs/${encodeURIComponent(run.id)}`,
            'GET',
            null,
            { signal }
        );
    }
    return false;
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
