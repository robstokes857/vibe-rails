import { escapeHtml, confirmDialog } from './utils.js';
import { launchPresentation } from './launch-presentation.js';
import {
    LaunchApi,
    attachmentForEditor,
    retiredStepMessage
} from './launch-api.js';
import { cardEditorLayout, configureCardEditorDialog } from './card-editor.js';
import { composerMarkup, bindCardComposer } from './card-editor-composer.js';
import {
    attachmentSection,
    createEditorAttachments
} from './card-editor-attachments.js';
import { bindBoardReferences } from './board-references.js';
import { BoardApi } from './board-api.js';
import {
    openEditorAttachment,
    disposeBoardAttachmentPreview
} from './board-attachments.js';
import {
    renderBoardLaunchOptions,
    readBoardLaunchOptions,
    bindBoardLaunchOptions,
    waitForBoardLaunchDefaults
} from './board-launch-options.js';
import { mountLlmPicker } from './pickers/llm-picker.js';
import { runHistorySection, mountRunHistory } from './launch-history.js';
import { bindingSection, mountBindings } from './launch-bindings.js';
import { PythonScriptsController } from './python-scripts-controller.js';
import { openLaunchTerminal, waitForNativeLaunch } from './launch-terminal.js';
import { resolvePromptTemplateForLaunch } from './prompt-template-modal.js';
import { promptVariableHelp, bindPromptVariableHelp } from './prompt-variable-help.js';
import { bindPromptReferenceHighlights } from './prompt-reference-highlights.js';
import { bindPromptImagePreviews } from './prompt-image-previews.js';

const field = (label, name, value = '', type = 'text') =>
    `<label class="launch-field">${label}<input class="form-control" type="${type}" data-launch-field="${name}" value="${escapeHtml(value)}"></label>`;
const area = (label, name, value = '') =>
    `<label class="launch-field">${label}<textarea class="form-control" rows="3" data-launch-field="${name}">${escapeHtml(value)}</textarea></label>`;
const select = (label, name, values, value) =>
    `<label class="launch-field">${label}<select class="form-select" aria-label="${escapeHtml(label)}" data-launch-field="${name}">${values.map(([key, text]) => `<option value="${key}"${String(key) === String(value) ? ' selected' : ''}>${text}</option>`).join('')}</select></label>`;
const lines = (value) =>
    String(value || '')
        .split('\n')
        .filter((line) => line.length);

export class AiAgentsController {
    constructor(app) {
        this.app = app;
        this.api = new LaunchApi(app);
        this.pythonScripts = new PythonScriptsController(app);
        this.items = [];
        this.launching = new Set();
    }
    unload() {
        this.loadAbort?.abort();
        this.editorLoad?.abort();
        this.root = null;
    }
    async loadView(data = {}) {
        this.unload();
        this.root = document.getElementById('app-content');
        this.root.innerHTML = `<div class="ai-agents-view"><header class="vb-page-header"><span class="launch-section-icon" aria-hidden="true"><i class="fa-solid fa-robot"></i></span><div class="vb-page-heading"><h1 class="vb-page-title">AI Agents</h1><p class="vb-page-description">Save AI setups and scripts to run on demand or from Board lanes.</p></div><div class="vb-page-actions"><button class="btn btn-primary" data-agent-new="preset"><i class="fa-solid fa-plus" aria-hidden="true"></i> Create agent</button><button class="btn btn-secondary" data-agent-new="action"><i class="fa-solid fa-code" aria-hidden="true"></i> Create script</button><button class="btn btn-secondary" data-agent-refresh><i class="fa-solid fa-rotate-right" aria-hidden="true"></i> Refresh</button></div></header><div class="launch-catalog" data-agent-catalog></div><p class="launch-save-error" data-agent-page-error role="alert"></p></div>`;
        this.root.querySelector('.ai-agents-view').addEventListener('click', (event) => {
            const button = event.target.closest('[data-agent-new]');
            if (button) void this.openEditor(button.dataset.agentNew);
        });
        this.root
            .querySelector('[data-agent-refresh]')
            .addEventListener('click', () => void this.refresh());
        this.root
            .querySelector('[data-agent-catalog]')
            .addEventListener('click', (event) => {
                const button = event.target.closest(
                    '[data-agent-edit], [data-agent-run]'
                );
                if (!button) return;
                const item = this.items.find(
                    (item) =>
                        item.id ===
                        (button.dataset.agentEdit || button.dataset.agentRun)
                );
                if (item)
                    void (button.hasAttribute('data-agent-edit')
                        ? this.openEditor(item.definitionType, item.id)
                        : this.launch(item.id, item.definitionType));
            });
        await this.refresh();
        if (data.id) await this.openEditor(data.type || 'preset', data.id);
        else if (data.newJob || data.create)
            await this.openEditor(data.type || 'preset');
    }
    async refresh() {
        this.loadAbort?.abort();
        const abort = (this.loadAbort = new AbortController());
        try {
            const catalog = await this.api.catalog({ signal: abort.signal });
            if (abort.signal.aborted) return;
            const signature = JSON.stringify(catalog.items);
            if (this.catalogSignature !== signature)
                window.__viberails_launcherChanged__?.();
            this.catalogSignature = signature;
            this.items = catalog.items;
            this.app.data.launchCatalog = this.items;
            await this.app.llmPickerController.loadPreferences({ force: true });
            if (!this.root || abort.signal.aborted) return;
            const groups = [
                ['Agents', 'robot', 'Save a prompt and AI settings for a repeatable task.', 'Create agent', 'preset', (item) => item.kind === 'agent'],
                ['Quick Launches', 'bolt', 'Save AI settings without a prompt for a ready-to-use terminal.', 'Create quick launch', 'preset', (item) => item.kind === 'quick-launch'],
                ['Scripts', 'code', 'Run a script yourself or when a card enters a Board lane.', 'Create script', 'action', (item) => item.kind === 'script']
            ];
            // Existing checks remain editable; new checks are added from a Board lane.
            if (this.items.some((item) => item.definitionType === 'action' && item.kind !== 'script'))
                groups.push(['Board checks', 'list-check', 'Checks available to add to Board lanes.', '', '', (item) => item.definitionType === 'action' && item.kind !== 'script']);
            this.root.querySelector('[data-agent-catalog]').innerHTML = groups
                .map(
                    ([title, icon, description, action, type, filter], index) => {
                        const entries = this.items.filter(filter);
                        return `<section class="launch-catalog-section" aria-labelledby="launch-section-${index}"><header class="launch-section-heading"><span class="launch-section-icon" aria-hidden="true"><i class="fa-solid fa-${icon}"></i></span><div><h2 id="launch-section-${index}">${title} <span class="launch-section-count">${entries.length}</span></h2><p>${description}</p></div></header>${
                            entries
                                .map((item) => {
                                    const presentation = launchPresentation(item);
                                    return `<article class="launch-catalog-row">${presentation.logoHtml}<div class="launch-catalog-copy"><button class="btn btn-link" data-agent-edit="${escapeHtml(item.id)}">${escapeHtml(item.name)}</button><p>${escapeHtml(presentation.subtitle)}</p>${item.ineligibleReason ? `<p class="text-warning">Unavailable: ${escapeHtml(item.ineligibleReason)}</p>` : ''}</div><button class="btn btn-secondary" data-agent-run="${escapeHtml(item.id)}" aria-label="Run ${escapeHtml(item.name)}"${item.canLaunch ? '' : ' disabled'}><i class="fa-solid fa-play" aria-hidden="true"></i> Run</button></article>`;
                                })
                                .join('') ||
                            `<div class="launch-catalog-empty"><i class="fa-solid fa-${icon}" aria-hidden="true"></i><h3>No ${title.toLowerCase()} yet</h3><button class="btn btn-secondary" data-agent-new="${type}"><i class="fa-solid fa-plus" aria-hidden="true"></i> ${action}</button></div>`
                        }</section>`;
                    }
                )
                .join('');
            this.root.querySelector('[data-agent-page-error]').textContent = '';
        } catch (error) {
            if (this.root && !abort.signal.aborted)
                this.root.querySelector('[data-agent-page-error]').textContent =
                    error.message;
        }
    }
    async launch(
        id,
        type = 'preset',
        { focusTerminal = false, promptPrefix = null, nativeTerminal = !focusTerminal } = {}
    ) {
        if (this.launching.has(id)) return;
        this.launching.add(id);
        try {
            let initialPrompt = null,
                expectedRevision = null;
            if (type === 'preset') {
                const preset = await this.api.definition(type, id);
                const resolved = await resolvePromptTemplateForLaunch(
                    this.app,
                    {
                        initialPrompt: preset.initialPrompt,
                        environmentName: preset.name
                    }
                );
                if (!resolved || resolved.canceled) return;
                initialPrompt = resolved.initialPrompt;
                expectedRevision = preset.revision;
            }
            const run = await this.api.run(id, type, {
                promptPrefix,
                initialPrompt,
                expectedRevision,
                nativeTerminal
            });
            if (nativeTerminal) {
                this.app.showToast('AI Agents', 'Starting in a native terminal…', 'info');
                let started;
                try {
                    started = await waitForNativeLaunch(this.api, run);
                } catch {
                    this.app.showToast('AI Agents', 'Run queued, but its status could not be checked. Check Run history before retrying.', 'warning');
                    return run;
                }
                if (started.status === 'running' || started.status === 'succeeded')
                    this.app.showToast('AI Agents', started.status === 'running' ? 'Started in a native terminal.' : 'Run finished.', 'success');
                else if (started.status === 'queued' || started.status === 'claimed')
                    this.app.showToast('AI Agents', 'Still waiting to start. Check Run history before running again.', 'warning');
                else
                    this.app.showToast('AI Agents', started.error || `Run ${started.status}. Check Run history for details.`, 'error');
                return started;
            }
            this.app.showToast(
                'AI Agents',
                'Run queued. Follow its status in Run history.',
                'success'
            );
            await this.openEditor(type, id);
            if (focusTerminal && this.activeEditor) {
                const owner = this.activeEditor;
                if (
                    await openLaunchTerminal(this.app, this.api, run, {
                        signal: owner.signal
                    })
                )
                    owner.close();
            }
            return run;
        } catch (error) {
            if (error.name !== 'AbortError')
                this.app.showToast('AI Agents', error.message, 'error');
        } finally {
            this.launching.delete(id);
        }
    }
    launchFromNav(id) {
        const item = this.items.find((item) => item.id === id);
        return this.launch(id, item?.definitionType || 'preset');
    }
    async openEditor(type = 'preset', id = null) {
        this.editorLoad?.abort();
        const abort = (this.editorLoad = new AbortController());
        let saved, initialBindings, attachments;
        try {
            [saved, initialBindings, attachments] = await Promise.all([
                id
                    ? this.api.definition(type, id, { signal: abort.signal })
                    : null,
                id ? this.api.bindings(id, { signal: abort.signal }) : [],
                id && type === 'preset'
                    ? this.api.attachments(id, { signal: abort.signal })
                    : []
            ]);
        } catch (error) {
            if (!abort.signal.aborted)
                this.app.showToast('AI Agents', error.message, 'error');
            return;
        }
        if (abort.signal.aborted) return;
        const definition = {
            ...(saved || {}),
            id,
            attachments: attachments.map(attachmentForEditor),
            pendingAttachments: []
        };
        const item =
            this.items.find((item) => item.id === id) ||
            this.app.data.launchCatalog?.find((item) => item.id === id);
        const cli =
            item?.cli ||
            (typeof saved?.cli === 'string'
                ? saved.cli.toLowerCase()
                : {
                      1: 'codex',
                      2: 'claude',
                      3: 'antigravity',
                      4: 'copilot',
                      6: 'opencode',
                      7: 'glm-5.2',
                      8: 'grok',
                      9: 'glm-5.3',
                      10: 'deepseek-v4-pro',
                      11: 'kimi-k3'
                  }[saved?.cli]) ||
            'codex';
        const isPreset = type === 'preset';
        let dirty = false,
            busy = false,
            disposed = false,
            allowClose = false,
            confirming = false;
        const disposers = [];
        const project = this.app.data.configs?.rootPath || '';
        this.app.cloneTemplate('board-template');
        const beforeClose = (retry = null) => {
            if (allowClose || (!dirty && !busy)) return true;
            if (busy) {
                this.app.showToast(
                    'AI Agents',
                    'Wait for the current save or upload to finish.',
                    'info'
                );
                return false;
            }
            if (!confirming) {
                confirming = true;
                void confirmDialog({
                    title: 'Discard changes?',
                    message: 'Your unsaved changes will be lost.',
                    confirmLabel: 'Discard',
                    danger: true
                }).then((ok) => {
                    confirming = false;
                    if (ok && !disposed) {
                        allowClose = true;
                        this.app.closeModal();
                        retry?.();
                    }
                });
            }
            return false;
        };
        const shown = this.app.showModal(
            saved?.name || (isPreset ? 'Create agent' : 'Create script'),
            `<div class="board-card-editor" data-launch-editor>${cardEditorLayout(
                {
                    main: `${field('Name', 'name', saved?.name || '')}${isPreset ? `<section class="board-block"><h3 class="board-block-label">Initial prompt <span class="text-muted">(optional)</span></h3>${composerMarkup({ name: 'prompt', label: 'Initial prompt (optional)', value: saved?.initialPrompt || '', placeholder: 'Describe the task. Leave blank for a Quick Launch.', livePreview: false })}<p class="board-editor-muted" data-launch-kind></p>${promptVariableHelp()}</section>` : area('Description (optional)', 'description', saved?.description || '')}`,
                    attachments: isPreset
                        ? attachmentSection(attachments.length)
                        : '',
                    activity: runHistorySection(),
                    sidebar: `<div class="board-side-fields">${isPreset ? `<label>Provider<select class="form-select" data-launch-provider aria-label="Provider"></select></label><div data-definition-launch-options></div>` : this.actionFields(saved)}<label class="card-editor-switch-row"><span>Enabled</span><input class="card-editor-switch" type="checkbox" role="switch" data-launch-enabled${saved?.enabled !== false ? ' checked' : ''}></label>${
                        isPreset
                            ? `<details><summary>Additional settings</summary>${area('Description (optional)', 'description', saved?.description || '')}${area('Additional arguments (one argument per line)', 'argv', (saved?.additionalArgv || []).join('\n'))}${field('Timeout (minutes, optional)', 'timeout', saved?.timeoutMinutes || '', 'number')}<div data-provider-extra></div></details>`
                            : ''
                    }</div>${bindingSection(project)}`,
                    footer: `${id ? '<button class="btn btn-sm btn-outline-danger" data-launch-retire>Retire</button>' : '<span></span>'}<div class="board-editor-actions-main"><button class="btn btn-sm btn-outline-secondary" data-launch-cancel>Cancel</button><button class="btn btn-sm btn-primary" data-launch-save>${id ? 'Save' : isPreset ? 'Create agent' : 'Create script'}</button><button class="btn btn-sm btn-outline-secondary" data-launch-run${id ? '' : ' hidden'}>Save and run</button></div><p class="launch-save-error" data-launch-error role="alert"></p>`
                }
            )}</div>`,
            {
                beforeClose,
                onClose: () => {
                    disposed = true;
                    abort.abort();
                    disposers.reverse().forEach((dispose) => dispose?.());
                    disposeBoardAttachmentPreview();
                }
            }
        );
        if (shown === false) {
            abort.abort();
            return;
        }
        // Loading a replacement must not abort an open editor whose dirty guard
        // can still refuse that replacement. Its onClose owns this lifetime now.
        if (this.editorLoad === abort) this.editorLoad = null;
        this.activeEditor = {
            signal: abort.signal,
            close: () => {
                if (!disposed && !dirty && !busy) this.app.closeModal();
            }
        };
        disposers.push(
            this.app.registerNavigationGuard?.(({ retry }) => {
                if (!beforeClose(retry)) return false;
                this.app.closeModal();
                return true;
            })
        );
        const beforeUnload = (event) => {
            if (dirty || busy) {
                event.preventDefault();
                event.returnValue = '';
            }
        };
        window.addEventListener('beforeunload', beforeUnload);
        disposers.push(() =>
            window.removeEventListener('beforeunload', beforeUnload)
        );
        const container = document.getElementById('modal-container');
        configureCardEditorDialog(container);
        const editor = container.querySelector('[data-launch-editor]');
        const get = (name) =>
            editor.querySelector(`[data-launch-field="${name}"]`);
        const prompt = editor.querySelector('[data-board-composer-input]');
        const error = editor.querySelector('[data-launch-error]');
        const report = (message) => {
            error.textContent = message;
        };
        const current = () => !disposed && editor.isConnected;
        const enabled = () =>
            editor.querySelector('[data-launch-enabled]').checked;
        const eligible = () =>
            enabled() && (!isPreset || Boolean(prompt.value.trim()));
        const binding = mountBindings(editor, initialBindings, {
            project,
            eligible
        });
        disposers.push(binding.dispose);
        let bindingBaseline = new Map(
            initialBindings.map((value) => [value.id, JSON.stringify(value)])
        );
        const history = mountRunHistory(
            this.app,
            editor,
            this.api,
            () => definition.id
        );
        disposers.push(() => history.dispose());
        const content = (attachmentId, options) =>
            this.api.content(definition.id, attachmentId, options);
        const panel = isPreset
            ? createEditorAttachments({
                  editor,
                  getOwner: () => definition,
                  isBusy: () => busy,
                  setBusy: (value) => {
                      busy = value;
                  },
                  upload: async (payload) =>
                      attachmentForEditor(
                          await this.api.upload(definition.id, payload)
                      ),
                  remove: (attachment) =>
                      this.api.remove(definition.id, attachment.id),
                  preview: (attachment) =>
                      openEditorAttachment(this.app, attachment, (options) =>
                          content(attachment.id, options)
                      ),
                  confirmRemove: (attachment) =>
                      confirmDialog({
                          title: 'Remove attachment',
                          message: `Remove ${attachment.name}? Previous runs keep their copy.`,
                          confirmLabel: 'Remove',
                          danger: true
                      }),
                  onChanged: () => {
                      dirty = true;
                  },
                  showError: report
              })
            : null;
        if (panel) {
            panel.render();
            disposers.push(() => panel.dispose());
        }
        let selection = `base:${cli}`,
            launchDispose,
            extraCli = cli;
        const optionsByCli = new Map([[cli, saved?.options?.base || null]]),
            extrasByCli = new Map();
        if (isPreset) {
            prompt.setAttribute('aria-describedby', 'launch-prompt-variable-help');
            disposers.push(bindPromptVariableHelp(editor, prompt));
            const provider = editor.querySelector('[data-launch-provider]');
            const optionHost = editor.querySelector(
                '[data-definition-launch-options]'
            );
            const extraHost = editor.querySelector('[data-provider-extra]');
            const renderOptions = () => {
                launchDispose?.();
                const code = selection.slice(5);
                optionHost.innerHTML = renderBoardLaunchOptions(
                    selection,
                    optionsByCli.get(code) || {},
                    { idPrefix: 'definition-launch' }
                );
                launchDispose = bindBoardLaunchOptions(optionHost, selection, {
                    app: this.app,
                    useDefaults:
                        !optionsByCli.has(code) ||
                        (!id && !optionsByCli.get(code))
                });
                const options =
                    extrasByCli.get(code) ||
                    saved?.options?.[
                        code === 'opencode' ||
                        code.startsWith('glm-') ||
                        ['deepseek-v4-pro', 'kimi-k3'].includes(code)
                            ? 'openCode'
                            : code
                    ] ||
                    {};
                extraCli = code;
                const checks =
                    code === 'claude'
                        ? [
                              ['fastMode', 'Fast mode'],
                              [
                                  'noSessionPersistence',
                                  'Do not retain CLI session'
                              ],
                              ['bare', 'Bare mode'],
                              ['debug', 'Debug logging']
                          ]
                        : code === 'codex'
                          ? [['noAltScreen', 'Disable alternate screen']]
                          : [
                                  'opencode',
                                  'glm-5.2',
                                  'glm-5.3',
                                  'deepseek-v4-pro',
                                  'kimi-k3'
                              ].includes(code)
                            ? [['disablePlugins', 'Disable plugins']]
                            : [];
                extraHost.innerHTML =
                    checks
                        .map(
                            ([key, label]) =>
                                `<label class="card-editor-switch-row"><span>${label}</span><input class="card-editor-switch" role="switch" type="checkbox" data-provider-setting="${key}"${options[key] ? ' checked' : ''}></label>`
                        )
                        .join('') +
                    (code === 'claude'
                        ? area(
                              'System prompt (optional)',
                              'systemPrompt',
                              options.systemPrompt || ''
                          )
                        : '');
            };
            const readExtra = () =>
                Object.fromEntries(
                    [...extraHost.querySelectorAll('[data-provider-setting]')]
                        .map((input) => [
                            input.dataset.providerSetting,
                            input.checked
                        ])
                        .concat(
                            get('systemPrompt')
                                ? [['systemPrompt', get('systemPrompt').value]]
                                : []
                        )
                );
            disposers.push(
                mountLlmPicker(this.app, provider, {
                    context: 'multi-run',
                    selectedValue: selection
                })
            );
            renderOptions();
            provider.addEventListener('change', () => {
                optionsByCli.set(
                    selection.slice(5),
                    readBoardLaunchOptions(optionHost, selection)
                );
                extrasByCli.set(extraCli, readExtra());
                selection = provider.value;
                renderOptions();
            });
            disposers.push(() => launchDispose?.());
            editor._readLaunchOptions = () => {
                const code = selection.slice(5);
                return {
                    base: readBoardLaunchOptions(optionHost, selection),
                    ...(code === 'claude'
                        ? { claude: readExtra() }
                        : code === 'codex'
                          ? { codex: readExtra() }
                          : [
                                  'opencode',
                                  'glm-5.2',
                                  'glm-5.3',
                                  'deepseek-v4-pro',
                                  'kimi-k3'
                              ].includes(code)
                            ? { openCode: readExtra() }
                            : {})
                };
            };
            const composer = editor.querySelector('[data-board-composer]');
            BoardApi.attach(this.app);
            disposers.push(
                bindCardComposer(composer, {
                    bindHighlights: bindPromptReferenceHighlights,
                    bindPreview: () => bindPromptImagePreviews(this.app, composer, definition, content),
                    onAttach: (input, files) =>
                        panel.addFiles(composer, input, files),
                    bindReferences: (input) =>
                        bindBoardReferences(input, {
                            app: this.app,
                            host: composer
                        })
                })
            );
            editor
                .querySelector('[data-board-add-files]')
                .addEventListener('click', () =>
                    editor.querySelector('[data-board-files]').click()
                );
            editor
                .querySelector('[data-board-files]')
                .addEventListener('change', (event) => {
                    void panel.addFiles(
                        composer,
                        prompt,
                        [...event.target.files],
                        { inline: false }
                    );
                    event.target.value = '';
                });
            editor.addEventListener('click', (event) => {
                const image = event.target.closest('[data-board-image]');
                const attachment = definition.attachments.find(
                    (item) => item.id === image?.dataset.boardImage
                );
                if (attachment)
                    void openEditorAttachment(this.app, attachment, (options) =>
                        content(attachment.id, options)
                    );
            });
        }
        const updateKind = () => {
            if (prompt)
                editor.querySelector('[data-launch-kind]').textContent =
                    prompt.value.trim()
                        ? 'Agent · starts with this prompt.'
                        : 'Quick Launch · manual use only. Automatic bindings will not run.';
            binding.updateEligibility();
        };
        const updateAction = () => {
            if (isPreset) return;
            const script = get('kind').value === 'script';
            editor.querySelector('[data-action-script]').hidden = !script;
            editor.querySelector('[data-action-check]').hidden = script;
            get('grade').closest('label').hidden =
                get('kind').value !== 'code-quality';
            get('base').closest('label').hidden =
                get('scope').value !== 'range';
            get('head').closest('label').hidden = !['range', 'commit'].includes(
                get('scope').value
            );
        };
        editor.addEventListener('change', updateAction);
        updateAction();
        updateKind();
        editor.addEventListener('input', (event) => {
            if (event.target.closest('[data-run-list]')) return;
            dirty = true;
            updateKind();
        });
        editor.addEventListener('change', () => {
            dirty = true;
            updateKind();
        });
        const lockedControls = new Map();
        const lock = (value) => {
            busy = value;
            if (value) {
                editor.querySelectorAll('input,textarea,select,button').forEach((node) => {
                    if (!lockedControls.has(node)) lockedControls.set(node, {
                        disabled: node.disabled,
                        pickerDisabled: node.tomselect?.isDisabled
                    });
                    node.disabled = true;
                    node.tomselect?.disable();
                });
            } else {
                // Save-and-run releases the form before opening a template dialog;
                // its finally block may release it again after cancellation/failure.
                for (const [node, previous] of lockedControls) {
                    node.disabled = previous.disabled;
                    if (node.tomselect) {
                        if (previous.disabled || previous.pickerDisabled) node.tomselect.disable();
                        else node.tomselect.enable();
                    }
                }
                lockedControls.clear();
            }
        };
        const save = async (run) => {
            if (busy || !current()) return;
            report('');
            const retired =
                retiredStepMessage(prompt?.value) ||
                retiredStepMessage(get('systemPrompt')?.value);
            if (retired) {
                report(retired);
                return;
            }
            if (!get('name').value.trim()) {
                report('Enter a name.');
                get('name').focus();
                return;
            }
            lock(true);
            try {
                await waitForBoardLaunchDefaults(editor);
                const body = {
                    name: get('name').value.trim(),
                    description: get('description')?.value || '',
                    enabled: enabled(),
                    expectedRevision: definition.revision || 0
                };
                if (isPreset)
                    Object.assign(body, {
                        cli: selection.slice(5),
                        initialPrompt: prompt.value,
                        options: editor._readLaunchOptions(),
                        additionalArgv: lines(get('argv').value),
                        timeoutMinutes: Number(get('timeout').value) || null,
                        purpose: saved?.purpose || 'work'
                    });
                else Object.assign(body, this.readAction(get));
                // An owner ID is needed for staged uploads. Keep a new owner
                // disabled until every upload and the complete prompt are saved.
                if (isPreset && !definition.id && definition.pendingAttachments.length) {
                    const staged = await this.api.save(type, null, {
                        ...body, enabled: false, initialPrompt: null
                    });
                    Object.assign(definition, staged.preset);
                }
                if (isPreset) {
                    for (const pending of [...definition.pendingAttachments]) {
                        const attached = attachmentForEditor(
                            await this.api.upload(definition.id, pending)
                        );
                        definition.attachments.push(attached);
                        prompt.value = prompt.value.replaceAll(
                            `attachment:${pending.id}`,
                            `attachment:${attached.id}`
                        );
                        definition.pendingAttachments.splice(
                            definition.pendingAttachments.indexOf(pending),
                            1
                        );
                        panel.render();
                        prompt.dispatchEvent(new Event('input', { bubbles: true }));
                        editor.querySelector('[data-board-composer]')._refreshPreview?.();
                    }
                    body.initialPrompt = prompt.value;
                    body.expectedRevision = definition.revision || 0;
                }
                const result = await this.api.save(type, definition.id, body);
                Object.assign(definition, isPreset ? result.preset : result);
                if (isPreset) {
                    panel.render();
                    editor
                        .querySelector('[data-board-composer]')
                        ._refreshPreview?.();
                    if (result.affectedBindings?.length)
                        report(
                            `${result.affectedBindings.length} automatic binding(s) are now ineligible. They remain saved.`
                        );
                }
                for (const draft of binding.bindings) {
                    if (
                        draft.id &&
                        bindingBaseline.get(draft.id) === JSON.stringify(draft)
                    )
                        continue;
                    const returned = await this.api.saveBinding(draft.id, {
                        definitionId: definition.id,
                        definitionType: type,
                        trigger: draft.trigger,
                        enabled: draft.enabled,
                        expectedRevision: draft.revision || 0
                    });
                    Object.assign(draft, returned);
                    bindingBaseline.set(draft.id, JSON.stringify(draft));
                }
                dirty = false;
                editor.querySelector('[data-launch-save]').textContent = 'Save';
                editor.querySelector('[data-launch-run]').hidden = false;
                await this.refresh();
                if (run) {
                    lock(false);
                    await this.launch(definition.id, type);
                } else this.app.showToast('AI Agents', 'Saved.', 'success');
                await history.refresh();
            } catch (failure) {
                report(
                    `${failure.message || 'Could not save.'} Your draft is kept.`
                );
            } finally {
                if (current()) lock(false);
            }
        };
        editor
            .querySelector('[data-launch-save]')
            .addEventListener('click', () => void save(false));
        editor
            .querySelector('[data-launch-run]')
            .addEventListener('click', () => void save(true));
        editor
            .querySelector('[data-launch-cancel]')
            .addEventListener('click', () => this.app.closeModal());
        editor
            .querySelector('[data-launch-retire]')
            ?.addEventListener('click', async () => {
                if (busy) return;
                if (
                    !(await confirmDialog({
                        title: 'Retire definition?',
                        message:
                            'It becomes unavailable for future runs. Its history and attachments are retained.',
                        confirmLabel: 'Retire',
                        danger: true
                    }))
                )
                    return;
                lock(true);
                try {
                    await this.api.retire(
                        type,
                        definition.id,
                        definition.revision
                    );
                    allowClose = true;
                    this.app.closeModal();
                    await this.refresh();
                } catch (failure) {
                    report(failure.message);
                } finally {
                    if (current()) lock(false);
                }
            });
        editor.addEventListener('keydown', (event) => {
            if ((event.ctrlKey || event.metaKey) && event.key === 'Enter') {
                event.preventDefault();
                void save(false);
            }
        });
        get('name').focus();
    }
    actionFields(saved) {
        return `<input type="hidden" data-launch-field="kind" value="${escapeHtml(saved?.kind || 'script')}"><div data-action-script>${field('Script path (relative to execution project)', 'path', saved?.script?.path)}${select(
            'Interpreter',
            'runtime',
            [
                [0, 'Python'],
                [1, 'PowerShell'],
                [2, 'Bash']
            ],
            saved?.script?.runtime || 0
        )}${area('Arguments (one argument per line)', 'arguments', (saved?.script?.arguments || []).join('\n'))}${field('Working directory (optional)', 'directory', saved?.script?.workingDirectory)}${field('Timeout (seconds, optional)', 'seconds', saved?.script?.timeoutSeconds || '', 'number')}<p class="board-editor-muted">Saving approves this script’s current contents for this project.</p></div><div data-action-check>${select(
            'Scope',
            'scope',
            [
                ['working-tree', 'Working tree'],
                ['unpushed', 'Unpushed commits'],
                ['repository', 'Repository'],
                ['card', 'Card'],
                ['commit', 'One commit'],
                ['range', 'Commit range']
            ],
            saved?.check?.scope || 'working-tree'
        )}${field('Minimum grade (optional)', 'grade', saved?.check?.failGrade)}${field('Base commit (optional)', 'base', saved?.check?.baseCommit)}${field('Head commit (optional)', 'head', saved?.check?.headCommit)}</div>`;
    }
    readAction(get) {
        const kind = get('kind').value;
        return {
            kind,
            script:
                kind === 'script'
                    ? {
                          path: get('path').value,
                          runtime: Number(get('runtime').value),
                          arguments: lines(get('arguments').value),
                          workingDirectory: get('directory').value || null,
                          timeoutSeconds: Number(get('seconds').value) || null
                      }
                    : null,
            check:
                kind === 'script'
                    ? null
                    : {
                          scope: get('scope').value,
                          failGrade:
                              kind === 'code-quality'
                                  ? get('grade').value || null
                                  : null,
                          baseCommit: get('base').value || null,
                          headCommit: get('head').value || null
                      }
        };
    }
}
