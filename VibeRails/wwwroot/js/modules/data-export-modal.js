// Export Data progress modal.
//
// The export is one long POST that snapshots state.db, compresses it, hashes it and uploads it.
// The server publishes where it has got to, so this polls that while the POST is in flight and
// shows real stage-by-stage byte progress rather than a spinner.
//
// The returned promise settles when the *export* finishes, not when the modal closes: the settings
// page re-enables its button at that point while the outcome stays on screen until dismissed.

const EXPORT_ENDPOINT = '/api/v1/settings/export-data';
const PROGRESS_ENDPOINT = '/api/v1/settings/export-data/progress';
const POLL_INTERVAL_MS = 400;

// Server stage names, in the order they happen. Anything earlier than the reported stage is done.
const STAGES = [
    { key: 'snapshot', label: 'Snapshot', hint: 'Copying the database' },
    { key: 'compressing', label: 'Compress', hint: 'Shrinking the copy' },
    { key: 'hashing', label: 'Checksum', hint: 'Verifying the copy' },
    { key: 'uploading', label: 'Upload', hint: 'Sending to the server' }
];

export function showDataExportModal(app, options = {}) {
    const controller = new AbortController();
    const startedAt = Date.now();

    app.showModal('Export Data', buildMarkup(app, options));

    const root = document.getElementById('data-export-modal');
    const elements = {
        root,
        elapsed: document.getElementById('data-export-elapsed'),
        outcome: document.getElementById('data-export-outcome'),
        cancel: document.getElementById('data-export-cancel'),
        close: document.getElementById('data-export-close'),
        retry: document.getElementById('data-export-retry')
    };

    const state = {
        stageDurations: {},
        completed: new Set(),
        lastSample: null,
        throughput: null,
        runId: null,
        sawProgress: false,
        finished: false,
        cancelled: false
    };

    elements.cancel?.addEventListener('click', () => {
        state.cancelled = true;
        controller.abort();
    });
    elements.close?.addEventListener('click', () => app.closeModal());

    setStageState('snapshot', 'active');
    tickElapsed(Date.now() - startedAt);

    const polling = startPolling();

    return runExport();

    async function runExport() {
        try {
            const result = await app.apiCall(
                EXPORT_ENDPOINT,
                'POST',
                null,
                { showLoading: false, signal: controller.signal }
            );
            state.finished = true;
            if (result?.success) {
                renderSuccess(result);
            } else {
                renderFailure(
                    result?.message || 'Failed to export data.',
                    result?.status
                );
            }
            return result;
        } catch (error) {
            state.finished = true;
            if (state.cancelled || error?.name === 'AbortError') {
                app.closeModal();
                app.showToast('Data Export', 'Export cancelled.', 'info');
                return null;
            }

            const message = error?.message || 'Failed to export data.';
            renderFailure(message, null);
            // Returned rather than rethrown so the caller reports it the same way it reports a
            // structured failure from the server.
            return { success: false, status: 'failed', message };
        } finally {
            polling.stop();
        }
    }

    // ── Progress polling ─────────────────────────────────────────────────────────────────────

    function startPolling() {
        let timer = null;
        let stopped = false;

        const poll = async () => {
            // app.closeModal() simply empties the modal container, and navigating away calls it,
            // so the node going missing is how this loop learns it is no longer wanted.
            if (stopped || state.finished || !document.contains(elements.root)) {
                stop();
                return;
            }

            try {
                const snapshot = await app.apiCall(
                    PROGRESS_ENDPOINT,
                    'GET',
                    null,
                    { showLoading: false }
                );
                applySnapshot(snapshot);
            } catch {
                // A dropped poll is not worth surfacing — the POST is the source of truth for
                // whether the export succeeded, and the next tick will catch up.
            }

            if (!stopped) timer = setTimeout(poll, POLL_INTERVAL_MS);
        };

        timer = setTimeout(poll, POLL_INTERVAL_MS);

        function stop() {
            stopped = true;
            if (timer) clearTimeout(timer);
            timer = null;
        }

        return { stop };
    }

    function applySnapshot(snapshot) {
        if (state.finished) return;

        if (!snapshot?.active) {
            tickElapsed(Date.now() - startedAt);
            return;
        }

        // Another export (a second window, or one already running when this modal opened) must
        // not drive this modal's bars.
        if (state.runId === null) state.runId = snapshot.runId;
        if (snapshot.runId !== state.runId) return;

        const stageIndex = STAGES.findIndex(stage => stage.key === snapshot.stage);
        if (stageIndex < 0) return;
        state.sawProgress = true;

        for (let i = 0; i < stageIndex; i++) {
            const key = STAGES[i].key;
            if (!state.completed.has(key)) {
                state.completed.add(key);
                setStageState(key, 'done');
            }
        }

        setStageState(snapshot.stage, 'active');
        updateActiveStage(snapshot);
        tickElapsed(snapshot.elapsedMs ?? (Date.now() - startedAt));
    }

    function updateActiveStage(snapshot) {
        const row = stageRow(snapshot.stage);
        if (!row) return;

        const bar = row.querySelector('.data-export-stage-bar');
        const fill = row.querySelector('.progress-bar');
        const detail = row.querySelector('.data-export-stage-detail');
        const time = row.querySelector('.data-export-stage-time');

        if (time) time.textContent = formatDuration(snapshot.stageElapsedMs || 0);

        const total = Number(snapshot.totalBytes) || 0;
        const processed = Number(snapshot.processedBytes) || 0;
        measureThroughput(processed);

        if (total > 0) {
            const percent = Math.max(0, Math.min(100, Math.round((processed / total) * 100)));
            bar?.classList.remove('d-none');
            if (fill) {
                fill.classList.remove('progress-bar-striped', 'progress-bar-animated');
                fill.style.width = `${percent}%`;
            }
            if (detail) {
                detail.classList.remove('d-none');
                const rate = state.throughput ? ` · ${formatBytes(state.throughput)}/s` : '';
                detail.textContent =
                    `${formatBytes(processed)} of ${formatBytes(total)} · ${percent}%${rate}`;
            }
        } else {
            // No known total for this stage — an animated bar is honest about that, a made-up
            // percentage would not be.
            bar?.classList.remove('d-none');
            if (fill) {
                fill.classList.add('progress-bar-striped', 'progress-bar-animated');
                fill.style.width = '100%';
            }
            if (detail) {
                const stage = STAGES.find(entry => entry.key === snapshot.stage);
                detail.classList.remove('d-none');
                detail.textContent = stage?.hint || 'Working';
            }
        }
    }

    function measureThroughput(processed) {
        const now = Date.now();
        const previous = state.lastSample;
        if (previous && processed > previous.processed) {
            const seconds = (now - previous.at) / 1000;
            if (seconds >= 0.25) {
                const sample = (processed - previous.processed) / seconds;
                // Smoothed so the figure does not jitter between polls.
                state.throughput = state.throughput
                    ? (state.throughput * 0.7) + (sample * 0.3)
                    : sample;
                state.lastSample = { at: now, processed };
            }
            return;
        }

        if (!previous || processed < previous.processed) {
            state.lastSample = { at: now, processed };
            state.throughput = null;
        }
    }

    // ── Rendering ────────────────────────────────────────────────────────────────────────────

    function stageRow(key) {
        return elements.root?.querySelector(`[data-stage="${key}"]`);
    }

    function setStageState(key, next) {
        const row = stageRow(key);
        if (!row || row.dataset.state === next) return;
        if (row.dataset.state === 'done' && next === 'active') return;

        row.dataset.state = next;
        const icon = row.querySelector('.data-export-stage-icon');
        if (icon) icon.innerHTML = stageIcon(next);

        if (next === 'done') {
            const bar = row.querySelector('.data-export-stage-bar');
            const fill = row.querySelector('.progress-bar');
            const detail = row.querySelector('.data-export-stage-detail');
            if (fill) {
                fill.classList.remove('progress-bar-striped', 'progress-bar-animated');
                fill.style.width = '100%';
            }
            bar?.classList.add('d-none');
            detail?.classList.add('d-none');
            state.throughput = null;
            state.lastSample = null;
        }
    }

    function stageIcon(stageState) {
        if (stageState === 'done') {
            return '<i class="fa-solid fa-circle-check text-success"></i>';
        }
        if (stageState === 'active') {
            return '<span class="spinner-border spinner-border-sm text-primary" role="status"></span>';
        }
        if (stageState === 'failed') {
            return '<i class="fa-solid fa-circle-xmark text-danger"></i>';
        }
        return '<i class="fa-regular fa-circle text-muted"></i>';
    }

    function tickElapsed(milliseconds) {
        if (elements.elapsed) {
            elements.elapsed.textContent = `Elapsed ${formatDuration(milliseconds)}`;
        }
    }

    function renderSuccess(result) {
        STAGES.forEach(stage => setStageState(stage.key, 'done'));
        tickElapsed(Date.now() - startedAt);
        showTerminalButtons();

        const sha = typeof result?.sha256 === 'string' ? result.sha256 : '';
        if (elements.outcome) {
            elements.outcome.innerHTML = `
                <div class="alert alert-success mb-0">
                    <div class="fw-semibold">
                        <i class="fa-solid fa-circle-check me-1"></i>${app.escapeHtml(
                            result?.message || 'Data exported successfully.'
                        )}
                    </div>
                    ${sha
                        ? `<div class="small mt-2 mb-0 text-break">
                               <span class="text-muted">SHA-256</span>
                               <code class="ms-1">${app.escapeHtml(sha)}</code>
                           </div>`
                        : ''}
                </div>
            `;
        }
    }

    function renderFailure(message, status) {
        const active = elements.root?.querySelector('[data-state="active"]');
        const failedStage = state.sawProgress
            ? STAGES.find(stage => stage.key === active?.dataset.stage)
            : null;
        if (active?.dataset.stage) {
            // Only blame a stage the server actually reported being in. An export that fails
            // before the first poll lands would otherwise pin the failure on Snapshot, which is
            // usually the one stage that did work.
            setStageState(active.dataset.stage, state.sawProgress ? 'failed' : 'pending');
        }
        tickElapsed(Date.now() - startedAt);
        const retryNeedsSettingsChange = ['invalid_api_key', 'no_api_key', 'not_configured']
            .includes(status);
        showTerminalButtons({ allowRetry: !retryNeedsSettingsChange });

        if (elements.outcome) {
            const title = failureTitle(status, failedStage);
            const guidance = failureGuidance(status);
            elements.outcome.innerHTML = `
                <div class="alert alert-danger mb-0">
                    <div class="fw-semibold">
                        <i class="fa-solid fa-circle-exclamation me-1"></i>${app.escapeHtml(title)}
                    </div>
                    <div class="mt-1">${app.escapeHtml(message)}</div>
                    <div class="small mt-2">${app.escapeHtml(guidance)}</div>
                    ${status
                        ? `<div class="small text-muted mt-2">
                               Error code: <code>${app.escapeHtml(status)}</code>
                           </div>`
                        : ''}
                </div>
            `;
        }
    }

    function showTerminalButtons({ allowRetry = false } = {}) {
        elements.cancel?.classList.add('d-none');
        elements.close?.classList.remove('d-none');
        if (allowRetry) elements.retry?.classList.remove('d-none');

        elements.retry?.addEventListener(
            'click',
            () => {
                app.closeModal();
                // A retry resumes: already-uploaded blocks are skipped server-side.
                showDataExportModal(app, options).then(result => {
                    if (result) {
                        app.showToast(
                            'Data Export',
                            result.message
                                || (result.success
                                    ? 'Data exported successfully.'
                                    : 'Failed to export data.'),
                            result.success ? 'success' : 'error'
                        );
                    }
                });
            },
            { once: true }
        );
    }
}

function buildMarkup(app, options) {
    const size = formatBytes(options?.sizeBytes);
    const rows = STAGES.map(stage => `
        <li class="data-export-stage" data-stage="${stage.key}" data-state="pending">
            <div class="d-flex align-items-center gap-2">
                <span class="data-export-stage-icon" style="width:1rem;text-align:center">
                    <i class="fa-regular fa-circle text-muted"></i>
                </span>
                <span class="flex-grow-1">${app.escapeHtml(stage.label)}</span>
                <span class="text-muted small font-monospace data-export-stage-time"></span>
            </div>
            <div class="progress d-none data-export-stage-bar mt-1 ms-4" style="height:6px">
                <div class="progress-bar" role="progressbar" style="width:0%"></div>
            </div>
            <div class="small text-muted d-none data-export-stage-detail mt-1 ms-4"></div>
        </li>
    `).join('');

    return `
        <div id="data-export-modal" class="d-flex flex-column gap-3">
            <div class="d-flex align-items-center gap-2 small text-muted">
                <i class="fa-solid fa-database"></i>
                <span>state.db${size ? ` (${app.escapeHtml(size)})` : ''}</span>
                <i class="fa-solid fa-arrow-right-long mx-1"></i>
                <i class="fa-solid fa-cloud-arrow-up"></i>
                <span>VibeRails</span>
            </div>

            <ul class="list-unstyled d-flex flex-column gap-2 mb-0">${rows}</ul>

            <div id="data-export-outcome" role="status" aria-live="polite"></div>

            <div class="d-flex justify-content-between align-items-center">
                <small class="text-muted font-monospace" id="data-export-elapsed">Elapsed 0:00</small>
                <div class="d-flex gap-2">
                    <button type="button" class="btn btn-secondary btn-sm" id="data-export-cancel">Cancel</button>
                    <button type="button" class="btn btn-primary btn-sm d-none" id="data-export-retry">Retry</button>
                    <button type="button" class="btn btn-secondary btn-sm d-none" id="data-export-close">Close</button>
                </div>
            </div>
        </div>
    `;
}

function failureTitle(status, failedStage) {
    if (status === 'upload_failed') return 'Upload failed';
    if (status === 'invalid_api_key' || status === 'no_api_key') return 'Upload blocked';
    if (status === 'busy') return 'Export already running';
    return failedStage?.label ? `${failedStage.label} failed` : 'Export failed';
}

function failureGuidance(status) {
    if (status === 'upload_failed') {
        return 'Your local database was not changed. Retry will reuse any blocks the server already received.';
    }
    if (status === 'invalid_api_key' || status === 'no_api_key') {
        return 'Close this dialog, update and save the API key in Settings, then try again.';
    }
    if (status === 'busy') {
        return 'Wait for the other export to finish, or close any VibeRails window still exporting, then retry.';
    }
    if (status === 'not_configured') {
        return 'Install a build with a configured export endpoint before trying again.';
    }
    return 'Your local database was not changed. Correct the problem above and retry.';
}

function formatDuration(milliseconds) {
    const totalSeconds = Math.max(0, Math.floor((Number(milliseconds) || 0) / 1000));
    const minutes = Math.floor(totalSeconds / 60);
    const seconds = totalSeconds % 60;
    if (minutes < 60) return `${minutes}:${String(seconds).padStart(2, '0')}`;

    const hours = Math.floor(minutes / 60);
    return `${hours}:${String(minutes % 60).padStart(2, '0')}:${String(seconds).padStart(2, '0')}`;
}

function formatBytes(bytes) {
    // Number(null) is 0, so an unknown size would otherwise render as a confident "0 B".
    if (bytes === null || bytes === undefined || bytes === '') return '';

    const value = Number(bytes);
    if (!Number.isFinite(value) || value < 0) return '';

    const units = ['B', 'KB', 'MB', 'GB', 'TB'];
    let scaled = value;
    let unitIndex = 0;
    while (scaled >= 1024 && unitIndex < units.length - 1) {
        scaled /= 1024;
        unitIndex++;
    }

    const decimals = scaled >= 100 || unitIndex === 0 ? 0 : scaled >= 10 ? 1 : 2;
    return `${scaled.toFixed(decimals)} ${units[unitIndex]}`;
}
