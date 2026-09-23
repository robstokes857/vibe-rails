/**
 * Replay against elapsed time, draining every due frame in one tick. A timer per
 * frame makes dense recordings run at the browser's minimum timer delay instead
 * of the selected speed (nested timeouts are commonly clamped to 4ms).
 */
export class SessionReplayPlayback {
    constructor(frames, {
        onFrame,
        onProgress = () => {},
        onFinish = () => {},
        speed = 5,
        maxIdleMs = 500,
        now = () => performance.now(),
        setTimer = (callback, delay) => setTimeout(callback, delay),
        clearTimer = timer => clearTimeout(timer)
    }) {
        this.onFrame = onFrame;
        this.onProgress = onProgress;
        this.onFinish = onFinish;
        this.now = now;
        this.setTimer = setTimer;
        this.clearTimer = clearTimer;
        this.speed = speed;
        this.frameIndex = 0;
        this.playing = false;
        this.positionMs = 0;
        this.lastTick = 0;
        this.timer = null;

        let elapsed = 0;
        this.timeline = frames.map((frame, index) => {
            if (index > 0) {
                const gap = Number(frame.delayMs) - Number(frames[index - 1].delayMs);
                elapsed += Number.isFinite(gap) ? Math.min(Math.max(gap, 0), maxIdleMs) : 0;
            }
            return elapsed;
        });
    }

    seek(frameIndex) {
        this.pause();
        this.frameIndex = Math.min(Math.max(frameIndex, 0), this.timeline.length);
        this.positionMs = this.timeline[this.frameIndex] ?? this.timeline.at(-1) ?? 0;
    }

    play() {
        if (this.playing || this.frameIndex >= this.timeline.length) return;
        this.playing = true;
        this.lastTick = this.now();
        this.tick();
    }

    pause() {
        this.advanceClock();
        this.playing = false;
        this.cancelTimer();
    }

    setSpeed(speed) {
        if (!Number.isFinite(speed) || speed < 0) return;
        // Account for time already spent at the old speed before changing it.
        this.advanceClock();
        this.speed = speed;
        this.cancelTimer();
        if (this.playing) this.tick();
    }

    advanceClock() {
        if (!this.playing) return;
        const now = this.now();
        this.positionMs += Math.max(0, now - this.lastTick) * this.speed;
        this.lastTick = now;
    }

    cancelTimer() {
        if (this.timer !== null) this.clearTimer(this.timer);
        this.timer = null;
    }

    tick() {
        this.timer = null;
        if (!this.playing) return;
        this.advanceClock();
        while (this.frameIndex < this.timeline.length
            && (this.speed === 0 || this.timeline[this.frameIndex] <= this.positionMs)) {
            this.onFrame(this.frameIndex++);
        }
        this.onProgress(this.frameIndex);
        if (this.frameIndex >= this.timeline.length) {
            this.playing = false;
            this.onFinish();
            return;
        }
        const delay = (this.timeline[this.frameIndex] - this.positionMs) / this.speed;
        this.timer = this.setTimer(() => this.tick(), Math.max(1, delay));
    }
}
