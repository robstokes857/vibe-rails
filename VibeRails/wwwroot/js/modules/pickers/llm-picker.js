/**
 * Launch-target picker facade.
 *
 * Import this (not LlmPickerController directly) wherever a launch target is
 * chosen. Contexts:
 *   'terminal'             — built-in CLIs (incl. the plain Terminal shell) and Quick
 *                            Launches; the only picker with the "Manage quick launches…"
 *                            footer.
 *   'assignee'             — built-in CLIs (no shell), Quick Launches and Agents: the Board
 *                            card assignee, card chat, reviewer targets and Fix pickers.
 *   'multi-run'            — built-in CLIs only.
 *   'environment-provider' — built-in CLIs only, plain CLI values (the setup editor).
 *
 * A saved setup with a starting message is an Agent; without one it is a Quick Launch
 * (see launch-kind.js). Automation Workers never enter the server-resolved catalog.
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
