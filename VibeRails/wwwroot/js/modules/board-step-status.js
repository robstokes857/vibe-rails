// Wire values mirror BoardStepStatus; board-workflow-contract.test.mjs checks exact parity.
export const BoardStepStatus = Object.freeze({
    Waiting: 'Waiting',
    Queued: 'Queued',
    Running: 'Running',
    Succeeded: 'Succeeded',
    Failed: 'Failed',
    Cancelled: 'Cancelled',
    Interrupted: 'Interrupted',
    TimedOut: 'TimedOut',
    Skipped: 'Skipped',
    Unknown: 'Unknown',
    AwaitingResult: 'Awaiting result',
    Reviewing: 'Reviewing',
    Fixing: 'Fixing',
    Stopping: 'Stopping',
    Passed: 'Passed'
});
