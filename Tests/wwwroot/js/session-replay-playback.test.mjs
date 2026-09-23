import test from 'node:test';
import assert from 'node:assert/strict';
import { SessionReplayPlayback } from '../../../VibeRails/wwwroot/js/modules/session-replay-playback.js';

// Browsers clamp nested setTimeout calls to at least 4ms. Exercise that behavior
// explicitly so a one-timer-per-output-chunk implementation cannot pass.
function replayHarness(delays, speed = 5) {
    let time = 0;
    let nextTimerId = 0;
    let finishedAt = null;
    const pending = new Map();
    const written = [];
    const player = new SessionReplayPlayback(delays.map(delayMs => ({ delayMs })), {
        speed,
        onFrame: index => written.push(index),
        onFinish: () => { finishedAt = time; },
        now: () => time,
        setTimer(callback, delay) {
            const id = nextTimerId++;
            pending.set(id, { at: time + Math.max(4, delay), callback });
            return id;
        },
        clearTimer: id => pending.delete(id)
    });
    return {
        player,
        written,
        pending,
        get finishedAt() { return finishedAt; },
        advance(ms) {
            const target = time + ms;
            while (true) {
                const next = [...pending.entries()].sort((a, b) => a[1].at - b[1].at)[0];
                if (!next || next[1].at > target) break;
                time = next[1].at;
                pending.delete(next[0]);
                next[1].callback();
            }
            time = target;
        },
        wakeLate(ms) {
            time += ms;
            const due = [...pending.entries()].filter(([, timer]) => timer.at <= time);
            for (const [id, timer] of due) {
                pending.delete(id);
                timer.callback();
            }
        }
    };
}

test('dense output replays at 5x and 10x despite the browser timer floor', () => {
    const delays = Array.from({ length: 1001 }, (_, index) => index);
    for (const speed of [5, 10]) {
        const replay = replayHarness(delays, speed);
        replay.player.play();
        replay.advance(1000 / speed);
        assert.equal(replay.finishedAt, 1000 / speed);
        assert.deepEqual(replay.written, delays, 'every frame is written exactly once, in order');
        assert.equal(replay.player.playing, false);
        assert.equal(replay.pending.size, 0);
    }
});

test('changing speed reschedules the remaining gap immediately', () => {
    const replay = replayHarness([0, 400], 1);
    replay.player.play();
    replay.advance(100);
    replay.player.setSpeed(10);
    replay.advance(29);
    assert.deepEqual(replay.written, [0]);
    replay.advance(1);
    assert.deepEqual(replay.written, [0, 1]);
    assert.equal(replay.finishedAt, 130);
});

test('pausing preserves the current gap and does not count paused time', () => {
    const replay = replayHarness([0, 400], 1);
    replay.player.play();
    replay.advance(100);
    replay.player.pause();
    assert.equal(replay.pending.size, 0, 'timer id zero is cancelled too');
    replay.advance(1000);
    replay.player.setSpeed(10);
    assert.deepEqual(replay.written, [0], 'changing speed does not resume paused playback');
    replay.player.play();
    replay.advance(29);
    assert.deepEqual(replay.written, [0]);
    replay.advance(1);
    assert.equal(replay.finishedAt, 1130);
});

test('late timers drain all due frames instead of slowing the recording', () => {
    const replay = replayHarness([0, 10, 20, 30, 40, 500], 1);
    replay.player.play();
    replay.wakeLate(45);
    assert.deepEqual(replay.written, [0, 1, 2, 3, 4]);
    replay.advance(455);
    assert.equal(replay.finishedAt, 500);
});

test('selecting Max flushes every remaining frame and cancels the old timer', () => {
    const replay = replayHarness([0, 400, 800], 1);
    replay.player.play();
    replay.advance(100);
    replay.player.setSpeed(0);
    assert.deepEqual(replay.written, [0, 1, 2]);
    assert.equal(replay.finishedAt, 100);
    assert.equal(replay.pending.size, 0);
    replay.advance(1000);
    assert.deepEqual(replay.written, [0, 1, 2]);
});

test('idle gaps stay capped and equal timestamps do not add artificial delays', () => {
    const replay = replayHarness([0, 0, 0, 60_000, 60_000], 5);
    replay.player.play();
    assert.deepEqual(replay.written, [0, 1, 2]);
    replay.advance(100);
    assert.deepEqual(replay.written, [0, 1, 2, 3, 4]);
    assert.equal(replay.finishedAt, 100);
});

test('seek starts from the chosen frame and a completed seek remains finished', () => {
    const replay = replayHarness([0, 100, 200], 1);
    replay.player.seek(1);
    replay.player.play();
    assert.deepEqual(replay.written, [1]);
    replay.advance(100);
    assert.deepEqual(replay.written, [1, 2]);
    replay.player.seek(3);
    replay.player.play();
    assert.equal(replay.player.playing, false);
    assert.deepEqual(replay.written, [1, 2]);
    replay.player.seek(0);
    replay.player.play();
    assert.deepEqual(replay.written, [1, 2, 0]);
});
