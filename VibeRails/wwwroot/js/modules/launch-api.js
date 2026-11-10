/** The authenticated, project-scoped client for global definitions and immutable runs. */
export class LaunchApi {
    constructor(app) {
        this.app = app;
    }
    call(path, method = 'GET', body = null, options = {}) {
        return this.app.apiCall(`/api/v1/${path}`, method, body, {
            showLoading: false,
            preferErrorResponseMessage: true,
            ...options
        });
    }
    catalog({ includeAll = false, ...options } = {}) {
        return this.call(`launch-catalog${includeAll ? '?includeAll=true' : ''}`, 'GET', null, options);
    }
    projects(options) {
        return this.call('launch-projects', 'GET', null, options);
    }
    definition(type, id, options) {
        return this.call(
            `${type === 'action' ? 'actions' : 'presets'}/${encodeURIComponent(id)}`,
            'GET',
            null,
            options
        );
    }
    save(type, id, body) {
        return this.call(
            `${type === 'action' ? 'actions' : 'presets'}${id ? `/${encodeURIComponent(id)}` : ''}`,
            id ? 'PUT' : 'POST',
            body
        );
    }
    retire(type, id, revision) {
        return this.call(
            `${type === 'action' ? 'actions' : 'presets'}/${encodeURIComponent(id)}/retire`,
            'POST',
            { expectedRevision: revision }
        );
    }
    bindings(id, options) {
        return this.call(
            `trigger-bindings?definitionId=${encodeURIComponent(id)}`,
            'GET',
            null,
            options
        );
    }
    saveBinding(id, body) {
        return this.call(
            `trigger-bindings${id ? `/${encodeURIComponent(id)}` : ''}`,
            id ? 'PUT' : 'POST',
            body
        );
    }
    run(
        id,
        type = 'preset',
        {
            occurrenceId = crypto.randomUUID(),
            promptPrefix = null,
            initialPrompt = null,
            expectedRevision = null,
            nativeTerminal = false
        } = {}
    ) {
        return this.call('runs', 'POST', {
            definitionId: id,
            definitionType: type,
            occurrenceId,
            promptPrefix,
            initialPrompt,
            expectedRevision,
            nativeTerminal
        });
    }
    runs(id, before, options) {
        return this.call(
            `runs?definitionId=${encodeURIComponent(id)}&limit=20${before == null ? '' : `&before=${encodeURIComponent(before)}`}`,
            'GET',
            null,
            options
        );
    }
    runAction(id, action) {
        return this.call(`runs/${encodeURIComponent(id)}/${action}`, 'POST');
    }
    attachments(id, options) {
        return this.call(
            `presets/${encodeURIComponent(id)}/attachments`,
            'GET',
            null,
            options
        );
    }
    upload(id, body) {
        return this.call(
            `presets/${encodeURIComponent(id)}/attachments`,
            'POST',
            body
        );
    }
    remove(id, attachmentId) {
        return this.call(
            `presets/${encodeURIComponent(id)}/attachments/${encodeURIComponent(attachmentId)}`,
            'DELETE'
        );
    }
    content(id, attachmentId, options) {
        return this.call(
            `presets/${encodeURIComponent(id)}/attachments/${encodeURIComponent(attachmentId)}/content`,
            'GET',
            null,
            { ...options, responseType: 'blob' }
        );
    }
}

export const attachmentForEditor = (item) => ({
    ...item,
    bytes: item.size ?? item.bytes
});
export const retiredStepMessage = (text) =>
    /\{\{\s*step\s*:/i.test(String(text ?? ''))
        ? 'Step placeholders are retired. Remove {{step:...}} before saving.'
        : '';
export const isRunFinished = (run) =>
    ['succeeded', 'failed', 'cancelled', 'timed-out', 'interrupted'].includes(
        run.status
    );
