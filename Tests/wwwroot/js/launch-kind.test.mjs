import test from 'node:test';
import assert from 'node:assert/strict';
import {
    agentDisplayName, classifyCatalog, jobLaunchKind, launchKind, workerActionOf
} from '../../../VibeRails/wwwroot/js/modules/launch-kind.js';

const env = (id, name, customPrompt = '', extra = {}) => ({ id, name, cli: 'codex', customPrompt, ...extra });
const worker = environmentId => ({ id: `w${environmentId}`, kind: 0, environmentId });
const script = () => ({ id: 's', kind: 1, scriptPath: 'tools/check.py' });

test('an environment is an Agent only when its starting message has text', () => {
    assert.equal(launchKind(env(1, 'Reviewer', 'Review the diff.')), 'agent');
    assert.equal(launchKind(env(2, 'Fast Opus', '')), 'quick-launch');
    assert.equal(launchKind(env(3, 'Blank', '   \n ')), 'quick-launch');
    assert.equal(launchKind(env(4, 'Worker with message', 'Go', { automationWorker: true })), 'agent');
    assert.equal(launchKind(env(5, 'Hidden preset', '', { hidden: true })), 'quick-launch');
    assert.equal(launchKind(null), 'quick-launch');
});

test('a Job is an Agent run when it has a Worker action, including the legacy top-level shape', () => {
    assert.equal(jobLaunchKind({ actions: [worker(7), script()] }), 'agent');
    assert.equal(jobLaunchKind({ actions: [script(), { kind: 3, arguments: ['unpushed'] }] }), 'automation');
    assert.equal(jobLaunchKind({ actions: [], environmentId: 9 }), 'agent');
    assert.equal(jobLaunchKind({ actions: [], environmentId: null }), 'automation');
    assert.equal(jobLaunchKind({ actions: null, environmentId: null }), 'automation');
    assert.deepEqual(workerActionOf({ actions: [], environmentId: 9, environmentName: 'Old', llm: 1 }),
        { kind: 0, environmentId: 9, environmentName: 'Old', llm: 1 });
});

test('classifyCatalog joins Jobs onto the environments they reference and keeps the rest apart', () => {
    const environments = [
        env(1, 'code review', 'Review the branch.'),
        env(2, 'Fast Opus'),
        env(3, 'Switch reviewer', 'Review.', { automationWorker: true }),
        env(4, 'Blank but scheduled')
    ];
    const jobs = [
        { id: 10, name: 'Switch reviewer', actions: [{ kind: 2, arguments: ['unpushed'] }, { kind: 3, arguments: ['unpushed'] }, worker(3)] },
        { id: 11, name: 'VCA · Done · working-tree', actions: [{ kind: 3, arguments: ['working-tree'] }] },
        { id: 12, name: 'Nightly', actions: [worker(4)] },
        { id: 13, name: 'Orphan', actions: [worker(99)] },
        { id: 14, name: 'Legacy', actions: [], environmentId: 1 }
    ];

    const catalog = classifyCatalog(environments, jobs);

    assert.deepEqual(catalog.agents.map(entry => [entry.environment?.name ?? null, entry.jobs.map(job => job.id)]), [
        ['code review', [14]],
        ['Switch reviewer', [10]],
        ['Blank but scheduled', [12]],
        [null, [13]]
    ]);
    assert.deepEqual(catalog.automations.map(job => job.id), [11]);
    assert.deepEqual(catalog.quickLaunches.map(environment => environment.name), ['Fast Opus']);
});

test('several Jobs on one Agent stay in server order; empty inputs are safe', () => {
    const environments = [env(1, 'Reviewer', 'Go')];
    const jobs = [{ id: 2, name: 'Second', actions: [worker(1)] }, { id: 1, name: 'First', actions: [worker(1)] }];
    const { agents } = classifyCatalog(environments, jobs);
    assert.deepEqual(agents[0].jobs.map(job => job.id), [2, 1]);
    assert.equal(agentDisplayName(agents[0]), 'Second');
    assert.equal(agentDisplayName({ environment: env(1, 'Only env', 'Go'), jobs: [] }), 'Only env');
    assert.deepEqual(classifyCatalog(undefined, undefined), { agents: [], automations: [], quickLaunches: [] });
});

test('unscoped setups are omitted while project jobs retain their missing-worker entry', () => {
    const legacy = env(7, 'Old helper', 'Help.', { projectPath: null });
    const owned = env(8, 'Here', 'Help.', { projectPath: 'C:\repo' });
    const olderApi = env(9, 'No field', 'Help.');
    const blank = env(10, 'Blank', 'Help.', { projectPath: '  ' });
    const quickLaunch = env(11, 'Unscoped quick launch', '', { projectPath: null });

    const { agents, quickLaunches } = classifyCatalog([legacy, owned, olderApi, blank, quickLaunch],
        [{ id: 1, name: 'Runs old', actions: [worker(7)] }]);
    assert.deepEqual(agents.map(entry => entry.environment?.name ?? null), ['Here', 'No field', null]);
    assert.deepEqual(agents[2].jobs.map(job => job.id), [1]);
    assert.deepEqual(quickLaunches, []);
    assert.deepEqual(classifyCatalog([legacy, blank], []).agents, []);
});
