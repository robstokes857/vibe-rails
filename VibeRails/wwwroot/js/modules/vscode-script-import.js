/** Wire the extension's file command only after the dashboard has initialized. */
export function setupVSCodeScriptImport(app, host = window) {
    if (!host.__viberails_VSCODE__ || typeof host.__viberails_scriptImportReady__ !== 'function'
        || typeof host.__viberails_scriptImportReceived__ !== 'function') return;

    let importing = false;
    host.addEventListener('message', async ({ data }) => {
        if (data?.command !== 'importScript' || typeof data.requestId !== 'string'
            || typeof data.path !== 'string') return;
        if (importing) {
            host.__viberails_scriptImportReceived__(data.requestId, 'Finish the open script dialog before adding another file.');
            return;
        }
        importing = true;
        host.__viberails_scriptImportReceived__(data.requestId);
        try {
            await app.jobController.pythonScripts.importScript(data.path);
        } catch (error) {
            app.showError(error?.message || 'Could not add that script.');
        } finally {
            importing = false;
        }
    });
    host.__viberails_scriptImportReady__();
}
