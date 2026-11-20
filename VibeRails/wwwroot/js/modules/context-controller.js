import { BoardApi } from './board-api.js';
import { getSelectedBoardId } from './board-selection.js';
import { contextEditorSection, mountContextEditor } from './board-settings.js';
import { confirmDialog } from './utils.js';

/**
 * The Context tab of App settings: App, Project and board launch context plus the Vibe Board
 * Nudge. It registers no navigation guard; App settings asks once for the whole page using
 * {@link ContextController#anyDirty} and {@link ContextController#isBusy}.
 */
export class ContextController {
    constructor(app) {
        this.app = app;
        this.dispose = null;
        this.anyDirty = () => false;
        this.isBusy = () => false;
    }

    /**
     * Renders into `root`, which holds `[data-context-project]` and `[data-context-workspace]`.
     * `boardId` preselects a board (Board settings opens Context for its board); `onBoardChange`
     * hears every board the picker shows.
     */
    mount(root, { boardId, onBoardChange } = {}) {
        this.unload();
        const hasProject = this.app.data.isInGit;
        const project = root.querySelector('[data-context-project]');
        project.textContent = hasProject ? this.app.data.projectDisplayName : 'No Git repository open';
        if (hasProject) project.title = this.app.data.configs.rootPath;
        const editorHost = root.querySelector('[data-context-workspace]');
        editorHost.innerHTML = contextEditorSection({ project: hasProject });

        const editor = mountContextEditor(this.app, editorHost.firstElementChild);
        const picker = root.querySelector('[data-context-board-picker]');
        const status = root.querySelector('[data-context-boards-status]');
        const retry = root.querySelector('[data-context-boards-retry]');
        const lifetime = new AbortController();
        let selected = '';
        let confirming = false;
        const choose = id => {
            selected = picker.value = id;
            editor.setBoard(id);
            onBoardChange?.(id);
        };
        const loadBoards = async () => {
            status.textContent = 'Loading boards…';
            retry.hidden = true;
            picker.disabled = true;
            try {
                const boards = (await BoardApi.getBoardsAsync({ existingOnly: true, signal: lifetime.signal }))
                    .filter(board => !board.shared);
                if (lifetime.signal.aborted) return;
                picker.replaceChildren(...(boards.length
                    ? boards.map(board => new Option(board.name, board.id))
                    : [new Option('No editable boards', '')]));
                status.textContent = '';
                picker.disabled = boards.length < 2;
                const preferred = boardId || getSelectedBoardId(this.app);
                choose(boards.find(board => board.id === preferred)?.id || boards[0]?.id || '');
            } catch (error) {
                if (lifetime.signal.aborted) return;
                picker.replaceChildren(new Option('Boards unavailable', ''));
                status.textContent = error.message || 'Boards could not be loaded.';
                retry.hidden = false;
            }
        };

        const confirmDiscard = async scope => {
            confirming = true;
            try {
                return await confirmDialog({
                    title: 'Discard unsaved context?',
                    message: `Your unsaved ${scope} changes will be lost.`,
                    confirmLabel: 'Discard changes',
                    cancelLabel: 'Keep editing'
                });
            } finally {
                confirming = false;
            }
        };
        picker.addEventListener('change', async () => {
            const next = picker.value;
            picker.value = selected;
            if (next === selected || confirming || editor.isSaving()) return;
            if (editor.boardDirty() && !await confirmDiscard('board and card context')) return;
            if (!lifetime.signal.aborted) choose(next);
        });
        retry.addEventListener('click', loadBoards);
        this.anyDirty = () => editor.anyDirty();
        this.isBusy = () => editor.isSaving() || confirming;
        this.dispose = () => {
            lifetime.abort();
            editor.dispose();
        };
        void loadBoards();
    }

    unload() {
        this.dispose?.();
        this.dispose = null;
        this.anyDirty = () => false;
        this.isBusy = () => false;
    }
}
