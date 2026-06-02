// Shared "Send debug log" flow.
//
// Prompts the user for an optional note, then asks the local vb server to build an
// encrypted bundle of the session's raw terminal data and upload it to viberails.ai
// (POST /api/v1/debug-bundle/{sessionId}/send). Used from both the terminal tab menu
// (active session) and the chat-history list (past sessions).
export function showSendDebugLogModal(app, sessionId) {
    if (!sessionId) {
        app.showToast('Send debug log', 'No session to send yet — start a session first.', 'warning');
        return;
    }

    app.showModal('Send debug log', `
        <form id="send-debug-log-form">
            <div class="mb-3">
                <label class="form-label">What went wrong? <span class="text-muted">(optional)</span></label>
                <textarea class="form-control" id="send-debug-log-message" rows="3" maxlength="2000"
                    placeholder="e.g. the terminal froze after I resized the window"></textarea>
                <small class="form-text text-muted">
                    This uploads an encrypted copy of this session to VibeRails support so the issue
                    can be debugged. It includes the full terminal output <strong>and the commands and
                    prompts you typed</strong> — including text that wasn't shown on screen — which may
                    contain secrets. Requires a valid API key.
                </small>
            </div>
            <div class="mb-3 form-check">
                <input type="checkbox" class="form-check-input" id="send-debug-log-consent">
                <label class="form-check-label" for="send-debug-log-consent">
                    I understand this whole session — including what I typed — will be uploaded.
                </label>
            </div>
            <div class="d-flex gap-2 justify-content-end">
                <button type="button" class="btn btn-secondary" data-action="close-modal">Cancel</button>
                <button type="submit" class="btn btn-primary" id="send-debug-log-submit" disabled>Send</button>
            </div>
        </form>
    `);

    const form = document.getElementById('send-debug-log-form');
    const consent = document.getElementById('send-debug-log-consent');
    const consentSubmitBtn = document.getElementById('send-debug-log-submit');
    consent?.addEventListener('change', () => {
        if (consentSubmitBtn) consentSubmitBtn.disabled = !consent.checked;
    });
    form?.addEventListener('submit', async (e) => {
        e.preventDefault();
        if (!consent?.checked) return; // Send stays gated on explicit consent.
        const message = document.getElementById('send-debug-log-message')?.value?.trim() || '';
        const submitBtn = form.querySelector('button[type="submit"]');
        if (submitBtn) {
            submitBtn.disabled = true;
            submitBtn.textContent = 'Sending…';
        }
        try {
            const res = await app.apiCall(
                `/api/v1/debug-bundle/${encodeURIComponent(sessionId)}/send`,
                'POST',
                { message }
            );
            const type = res?.success ? 'success' : (res?.status === 'no_api_key' ? 'warning' : 'error');
            app.showToast(
                'Send debug log',
                res?.message || (res?.success ? 'Debug log sent.' : 'Failed to send debug log.'),
                type
            );
            if (res?.success) {
                app.closeModal();
                return;
            }
        } catch (error) {
            app.showToast('Send debug log', error?.message || 'Failed to send debug log.', 'error');
        }
        if (submitBtn) {
            submitBtn.disabled = false;
            submitBtn.textContent = 'Send';
        }
    });
}
