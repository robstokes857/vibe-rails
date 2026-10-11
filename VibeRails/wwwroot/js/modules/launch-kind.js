// How the UI groups saved launch configurations. The backend keeps Environments and Jobs
// exactly as they are; this is display classification only.
//
//   Agent        — an environment with a starting message (every Automation Worker, plus any
//                  regular environment whose Initial Message is filled in), and the Jobs whose
//                  Worker action points at it.
//   Automation   — a Job with no Worker action: scripts, VCA and Code quality checks.
//   Quick Launch — an environment with no starting message. Lives in the terminal picker only.
//
// `automationWorker` and `hidden` never decide the kind. A Worker always has a message (the
// server refuses a Job without one), so "Agents" is a superset of the Workers list.

export const WORKER_ACTION_KIND = 0;

export const LAUNCH_KIND_LABELS = Object.freeze({
    base: 'Built-in CLIs',
    'quick-launch': 'Quick Launches',
    agent: 'Agents'
});

export function hasStartingMessage(environment) {
    return String(environment?.customPrompt ?? '').trim() !== '';
}

/** 'agent' when the environment carries a starting message, otherwise 'quick-launch'. */
export function launchKind(environment) {
    return hasStartingMessage(environment) ? 'agent' : 'quick-launch';
}

/**
 * The Worker action of a Job, or null. Jobs saved before actions existed carry only a top-level
 * environmentId; they are read the same way the editor already reads them.
 */
export function workerActionOf(job) {
    const actions = Array.isArray(job?.actions) ? job.actions : [];
    const worker = actions.find(action => Number(action?.kind) === WORKER_ACTION_KIND);
    if (worker) return worker;
    if (actions.length === 0 && job?.environmentId != null) {
        return { kind: WORKER_ACTION_KIND, environmentId: job.environmentId, environmentName: job.environmentName ?? null, llm: job.llm };
    }
    return null;
}

/**
 * A setup saved before setups belonged to a project (2026-08-08) has no projectPath, and the
 * server lists it in every project. Only an explicit empty value counts: an older API that
 * omits the field changes nothing.
 */
export function isSharedWithEveryProject(environment) {
    const value = environment?.projectPath;
    return value === null || (typeof value === 'string' && value.trim() === '');
}

/** 'agent' when the Job has a Worker action, otherwise 'automation'. */
export function jobLaunchKind(job) {
    return workerActionOf(job) ? 'agent' : 'automation';
}

/**
 * Split the two lists the pages already fetch into what the user sees.
 *
 * agents: one entry per environment with a message, carrying the Jobs that reference it in the
 * order the server returned them (jobs[0] is the one a card shows). A Job whose Worker
 * environment is not in the list still appears, with environment: null, so its schedule is
 * never silently hidden. An environment without a message that a Job references (legacy data;
 * the server no longer allows saving that) is listed as an Agent for the same reason.
 *
 * `shared` marks an entry every project sees (no projectPath) that nothing here runs; the Agents
 * page keeps those behind a collapsed group so a project's own list stays its own.
 */
export function classifyCatalog(environments = [], jobs = []) {
    const environmentList = Array.isArray(environments) ? environments : [];
    const jobList = Array.isArray(jobs) ? jobs : [];
    const byEnvironmentId = new Map();
    const unresolved = [];
    const automations = [];

    for (const job of jobList) {
        const worker = workerActionOf(job);
        if (!worker) {
            automations.push(job);
            continue;
        }
        const id = Number(worker.environmentId);
        if (!Number.isFinite(id) || !environmentList.some(environment => Number(environment?.id) === id)) {
            unresolved.push(job);
            continue;
        }
        if (!byEnvironmentId.has(id)) byEnvironmentId.set(id, []);
        byEnvironmentId.get(id).push(job);
    }

    const agents = [];
    const quickLaunches = [];
    for (const environment of environmentList) {
        const referencing = byEnvironmentId.get(Number(environment?.id)) || [];
        if (hasStartingMessage(environment) || referencing.length > 0) {
            agents.push({ environment, jobs: referencing, shared: referencing.length === 0 && isSharedWithEveryProject(environment) });
        } else {
            quickLaunches.push(environment);
        }
    }
    for (const job of unresolved) {
        agents.push({ environment: null, jobs: [job], shared: false });
    }

    return { agents, automations, quickLaunches };
}

/** What a card calls an Agent entry: the Job's name when one exists, else the environment's. */
export function agentDisplayName(entry) {
    return entry?.jobs?.[0]?.name || entry?.environment?.name || '';
}
