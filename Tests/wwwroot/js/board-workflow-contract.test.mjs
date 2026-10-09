import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { BoardStepStatus as Status } from '../../../VibeRails/wwwroot/js/modules/board-step-status.js';
import { laneArrowMarkup, laneStepMarkup } from '../../../VibeRails/wwwroot/js/modules/board-lane-workflow.js';

test('browser workflow statuses match every backend wire constant', async () => {
    const source = await readFile('VibeRails.Data.Abstractions/Board/BoardStepStatus.cs', 'utf8');
    const constants = Object.fromEntries([...source.matchAll(/public const string (\w+) = "([^"]+)";/g)]
        .map(([, key, value]) => [key, value]));
    assert.ok(Object.keys(constants).length > 0);
    assert.deepEqual(Status, constants);
});

test('only passed and skipped steps complete arrows; awaiting verdict and stopping keep waiting', () => {
    for (const status of Object.values(Status)) {
        const flows = [{ cardId: 'card', cardLabel: 'Card', steps: [{ jobId: 7, stepStatus: status, status: 'Succeeded' }] }];
        assert.equal(laneArrowMarkup(7, flows).includes('is-complete'),
            [Status.Passed, Status.Skipped].includes(status), status);
        assert.ok(laneStepMarkup(7, flows).includes(status));
    }
});
