import { escapeHtml, enhanceSelectWithTomSelect } from './utils.js';

export const launchProjectField = () => `<div class="launch-field"><label for="launch-projects">Projects</label><select id="launch-projects" class="form-select" multiple aria-describedby="launch-projects-help"></select><p id="launch-projects-help" class="board-editor-muted">No selection means All projects. Select projects to limit where this definition can run.</p></div>`;

/** Missing selections remain explicit choices; discovery never silently broadens a saved scope. */
export function mountLaunchProjects(editor, projects, selected = []) {
    const select = editor.querySelector('#launch-projects');
    const choices = new Map(projects.map(project => [project.id, project]));
    for (const id of selected) {
        if (!choices.has(id)) choices.set(id, { id, name: 'Previously selected project', path: id });
    }
    select.innerHTML = [...choices.values()].map(project =>
        `<option value="${escapeHtml(project.id)}"${selected.includes(project.id) ? ' selected' : ''}>${escapeHtml(project.name)} · ${escapeHtml(project.path)}</option>`).join('');
    const render = (data, escape) => {
        const project = choices.get(data.value);
        return `<div class="launch-project-option"><span class="launch-project-copy"><span>${escape(project?.name || data.text)}</span><small class="text-muted">${escape(project?.path || data.value)}</small></span></div>`;
    };
    const picker = enhanceSelectWithTomSelect(select, {
        placeholder: 'All projects', searchPlaceholder: 'Search projects or paths…',
        emptyMessage: 'No matching known projects.', render: { option: render, item: render }
    });
    picker?.control.setAttribute('aria-label', 'Projects');
    picker?.wrapper.classList.add('launch-project-picker');
    picker?.control.setAttribute('aria-describedby', 'launch-projects-help');
    // Selected chips take focus without priming dropdown_input's search. Reopen the
    // choices on click/ArrowDown as well, including when the control already has items.
    const open = event => {
        if (!picker || picker.isDisabled || event.target.closest('.remove')) return;
        if (event.type === 'keydown' && event.key !== 'ArrowDown') return;
        picker.refreshOptions(false);
        picker.open();
    };
    picker?.control.addEventListener('click', open);
    picker?.control.addEventListener('keydown', open);
    const read = () => [...select.selectedOptions].map(option => option.value);
    return {
        read,
        includesCurrent: () => !read().length || read().includes(projects.find(project => project.isCurrent)?.id),
        dispose: () => {
            picker?.control.removeEventListener('click', open);
            picker?.control.removeEventListener('keydown', open);
            picker?.destroy();
        }
    };
}
