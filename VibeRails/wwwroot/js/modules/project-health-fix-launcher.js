import { getEnabledLlmItems, mountLlmPicker, setLlmPickerValue } from './pickers/llm-picker.js';
import { parseLlmSelection } from './utils.js';

const LAST_AGENT_KEY = 'viberails.projectHealth.fixAgent';

function rememberAgent(value) {
    try { localStorage.setItem(LAST_AGENT_KEY, value); } catch { /* storage may be unavailable */ }
}

function initialAgent(app) {
    const enabled = getEnabledLlmItems(app, 'sandbox');
    let remembered = '';
    try { remembered = localStorage.getItem(LAST_AGENT_KEY) || ''; } catch { /* use shared order */ }
    return enabled.find(item => item.key === remembered)?.key || enabled[0]?.key || '';
}

export function mountProjectHealthFixPickers(app, root) {
    const selects = Array.from(root.querySelectorAll('[data-project-health-fix-agent]'));
    if (!selects.length) return () => {};
    const selectedValue = initialAgent(app);
    let syncing = false;
    const disposers = selects.map(select => mountLlmPicker(app, select, {
        context: 'sandbox',
        placeholder: 'Select an agent…',
        selectedValue
    }));
    const onChange = event => {
        if (syncing) return;
        const value = event.currentTarget.value;
        rememberAgent(value);
        syncing = true;
        try {
            for (const select of selects) {
                if (select.value !== value) setLlmPickerValue(app, select, value);
            }
        } finally {
            syncing = false;
        }
    };
    selects.forEach(select => select.addEventListener('change', onChange));
    return () => {
        selects.forEach(select => select.removeEventListener('change', onChange));
        disposers.forEach(dispose => dispose?.());
    };
}

export async function launchProjectHealthFix(app, { title, prompt, workingDirectory, selectionValue }) {
    const selection = parseLlmSelection(selectionValue, app.data?.environments || []);
    if (!selection.cli || selection.cli === 'shell'
        || (selection.kind === 'environment' && !selection.environmentName)) {
        app.showToast('Choose an agent', 'Select an available agent or environment beside the Fix button.', 'warning');
        return false;
    }
    rememberAgent(selectionValue);
    try {
        if (typeof app.terminalController?.launchInFocus !== 'function') {
            throw new Error('The terminal is unavailable. Reload the page and try again.');
        }
        const opened = await app.terminalController.launchInFocus({
            cli: selection.cli,
            environmentName: selection.environmentName,
            workingDirectory,
            title,
            tabLabel: title,
            initialPrompt: prompt,
            forceNewTab: true
        });
        if (opened === false) throw new Error('The terminal could not open. Try again.');
        return true;
    } catch (error) {
        app.showToast('Could not launch agent', error?.message || 'The terminal could not open. Try again.', 'error');
        return false;
    }
}
