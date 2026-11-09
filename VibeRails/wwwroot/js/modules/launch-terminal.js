import { isRunFinished } from './launch-api.js';

/** Observe actual native startup; enqueue alone does not mean a process has started. */
export async function waitForNativeLaunch(api, initial) {
    let run = initial;
    for (let attempt = 0; attempt < 30; attempt++) {
        if (run.status === 'running' || isRunFinished(run) || run.status === 'uncertain') return run;
        await new Promise(resolve => setTimeout(resolve, 1000));
        run = await api.call(`runs/${encodeURIComponent(run.id)}`);
    }
    return run;
}

/** Follow a queued launch without keeping a timer or request alive after dismissal. */
export async function openLaunchTerminal(app, api, initial, { signal } = {}) {
    let run = initial;
    for (let attempt = 0; attempt < 30 && !signal?.aborted; attempt++) {
        if (run.tabId && run.sessionId) {
            if (!(await app.terminalController.adoptLaunchedTab(run.tabId)))
                app.navigate('terminal-focus', { tabId: run.tabId });
            return true;
        }
        if (isRunFinished(run) || run.status === 'uncertain') return false;
        await new Promise((resolve) => {
            const done = () => {
                clearTimeout(timer);
                signal?.removeEventListener('abort', done);
                resolve();
            };
            const timer = setTimeout(done, 1000);
            signal?.addEventListener('abort', done, { once: true });
        });
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
