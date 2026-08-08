/**
 * LLM / Environment launch-picker facade.
 *
 * Import this (not LlmPickerController directly) wherever a launch target is
 * chosen: terminal header, sandbox CLI, multi-run rows, and the environment
 * modal's provider select. Contexts: 'terminal' | 'sandbox' | 'multi-run' |
 * 'environment-provider'.
 *
 * Automation Workers NEVER appear in these pickers — the server excludes them
 * from the preferences catalog regardless of `hidden`. The automation editor
 * uses `mountWorkerPicker` from ./worker-picker.js instead.
 */
export function mountLlmPicker(app, selectEl, options = {}) {
    return app.llmPickerController.mount(selectEl, options);
}

export function setLlmPickerValue(app, selectEl, value) {
    app.llmPickerController.setValue(selectEl, value);
}

export function getEnabledLlmItems(app, context) {
    return app.llmPickerController.getEnabledItems(context);
}
