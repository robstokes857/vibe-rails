# Customizable LLM Tom Select Pickers

## Goal

Give users one global place to control which launch targets appear in the shared LLM Tom Select pickers and the order in which they appear.

Every applicable picker will have a persistent footer button labeled **Customize LLM list**. The button opens a modal containing the visibility and ordering controls.

This feature configures launch-picker presentation only. It must not disable an underlying CLI, stop a running session, remove an Environment, or make historical sessions undiscoverable.

## Current State

The main shared implementation is in [`VibeRails/wwwroot/js/modules/utils.js`](VibeRails/wwwroot/js/modules/utils.js):

- `BASE_LLM_CHOICES` defines the built-in CLI order.
- `buildLlmSelectionOptions()` builds base CLI and custom Environment options.
- `populateLlmSelectionSelect()` populates the native select and enhances it.
- `enhanceLlmSelectWithTomSelect()` owns the common Tom Select configuration and rendering.

The shared option builder is currently used by:

- Terminal header: `terminal-multitab.js`
- Sandbox launch selectors: `sandbox-controller.js`
- Automation Environment selector: `jobs-controller.js`
- Multi Run selectors: `terminal-multirun.js`

Custom Environments already have a persisted `Environments.Hidden` flag. The common option builder filters hidden Environments while preserving a hidden Environment when it is already selected by an existing Automation.

The following controls are related but should not blindly inherit launch-picker visibility:

- The Environment creation CLI picker shares Tom Select styling, but hiding a launch target must not prevent creating an Environment for that provider.
- Chat History builds provider filters from similar data, but hiding a launch target must not hide historical sessions or their filters.
- The terminal tab undo selector is unrelated and must not receive the customization footer.

## Product Decisions

### Preference scope

Preferences are machine-wide, matching the existing global Environment model.

Configurable items are:

- Built-in CLIs: Claude, Codex, GLM 5.2, Kimi K3, OpenCode, Copilot, and Antigravity.
- Plain Terminal, in contexts that currently support it.
- Every custom Environment.

### Groups and ordering

Keep the existing picker groups. Users can reorder items within these sections:

- Base CLIs
- Custom Environments

The group order itself remains fixed. Supporting arbitrary cross-group ordering would conflict with the existing Tom Select optgroup presentation and is outside the first version.

### Meaning of disabled

Disabling an item removes it from new-launch choices. It does not:

- Disable or uninstall the CLI.
- Stop or alter active sessions.
- Delete or disable a custom Environment.
- Hide Chat History.
- Prevent creating or editing an Environment for that CLI.
- Clear an existing Automation or restored terminal-tab reference.

If a disabled item is already selected by a saved entity, reinsert that option into that picker and label it as hidden. This prevents an unrelated edit from silently changing the saved reference.

It is valid to hide every item. The Tom Select dropdown must still open and expose the customization footer so the user can recover.

### Defaults

- Existing installations with no picker preference retain the current rendered order.
- Existing `Environments.Hidden` values remain effective.
- Newly supported built-in CLIs are visible by default and appended in canonical order.
- Newly created Environments are visible by default and appended to the Custom Environments section.
- **Reset to defaults** shows all items and restores canonical/default ordering.

## API and Persistence

Add a dedicated preference API rather than extending the general application settings payload.

### Suggested endpoints

```text
GET    /api/v1/llm-picker/preferences
PUT    /api/v1/llm-picker/preferences
DELETE /api/v1/llm-picker/preferences
```

The GET response should be the fully resolved catalog, not only raw overrides:

```json
{
  "items": [
    {
      "key": "base:claude",
      "kind": "base",
      "group": "Base CLIs",
      "label": "Claude",
      "cli": "claude",
      "environmentId": null,
      "enabled": true,
      "order": 0
    },
    {
      "key": "env:42:codex",
      "kind": "environment",
      "group": "Custom Environments",
      "label": "Work (codex)",
      "cli": "codex",
      "environmentId": 42,
      "enabled": true,
      "order": 0
    }
  ]
}
```

The PUT request sends the complete ordered snapshot. The server validates and normalizes it before saving.

### Stable keys

Continue using the existing selection-value format as the stable preference key:

- `base:{cli}`
- `env:{environmentId}:{cli}`

Include plain Terminal as `base:shell`; picker context rules determine where it can appear.

### Storage

Recommended storage model:

- Store group ordering and disabled built-in keys as versioned JSON in `GlobalCache`, using a key such as `ui.llm-picker.v1`.
- Keep custom Environment visibility in the existing `Environments.Hidden` column.
- Save the preference document and custom Environment visibility in one SQLite transaction so a failed request cannot partially apply.

The resolver should:

1. Build the current server-side catalog.
2. Read the saved ordering/base visibility document.
3. Read custom Environment visibility from `Environments.Hidden`.
4. Ignore stale keys for deleted Environments.
5. Append catalog items absent from the saved order using their defaults.
6. Return contiguous normalized order values within each group.

The PUT endpoint should reject duplicate keys, duplicate positions, wrong-group moves, malformed keys, and arbitrary unknown items. Place a reasonable upper bound on request item count.

The DELETE endpoint clears the versioned preference document, makes custom Environments visible, and returns the newly resolved defaults.

### Backend files

Expected additions and changes:

- Add `VibeRails/Routes/LlmPickerRoutes.cs`.
- Add `VibeRails/Services/LlmPickerPreferenceService.cs` and its interface if one is useful for testing.
- Register the service in `VibeRails/MapRegisterServices.cs`.
- Map the routes in `VibeRails/Routes/Routes.cs`.
- Add request/response records and AOT JSON registrations in `VibeRails/DTOs/ResponseRecords.cs`.
- Add an atomic repository operation in `VibeRails/DB/IRepository.cs` and `VibeRails/DB/Repository.cs`.
- Reuse the existing `GlobalCache` table; no new table is required.

## Shared Frontend Architecture

Add `VibeRails/wwwroot/js/modules/llm-picker-controller.js` as the single owner of customizable launch pickers.

It should:

- Load and cache the resolved preference catalog.
- Mount a native select and Tom Select using a declared picker context.
- Track mounted pickers and their configuration.
- Return a disposer for modal/view lifecycle cleanup.
- Refresh all connected pickers after a successful save or reset.
- Preserve each picker's current value while refreshing.
- Open, bind, save, reset, and dispose the customization modal.

Keep `utils.js` responsible for pure value parsing, option building, grouping, brand rendering, and low-level Tom Select enhancement. Move orchestration and persistence out of `utils.js`.

### Picker contexts

| Context | Visible catalog types |
|---|---|
| `terminal` | Base CLIs, custom Environments, and Terminal |
| `sandbox` | Base CLIs and custom Environments |
| `automation` | Custom Environments only |
| `multi-run` | Base CLIs only |
| `environment-provider` | Supported providers excluding Terminal; ignore visibility preferences |

Filtering happens after applying the global order. This gives every context the same relative ordering for the items it supports.

### Startup and live refresh

Instantiate the picker controller from `app.js` and load its preferences before rendering the initial view.

After Save or Reset, the controller refreshes every mounted picker through its registry. It must preserve:

- Current native/Tom Select value.
- Existing selected-but-disabled references.
- Picker-specific placeholder and search text.
- Context-specific inclusion rules.

Disconnected DOM elements should be pruned from the registry. Modal and view owners should still use returned disposers so stale Tom Select instances and listeners do not accumulate.

## Tom Select Footer

Extend `enhanceLlmSelectWithTomSelect()` with an opt-in footer callback. Do not add the footer to every Tom Select control by default.

The footer should:

- Be a real `<button type="button">`, not a fake selectable option.
- Be appended to the Tom Select dropdown below `ts.dropdown_content`.
- Stay fixed while the options area scrolls.
- Display a gear icon and **Customize LLM list**.
- Remain present during search and when there are no visible results.
- Close Tom Select before opening the modal.
- Be reachable by keyboard and have an explicit accessible label.
- Handle `mousedown`/focus behavior so Tom Select does not close and swallow the click before the modal callback runs.

Add narrowly scoped styles to `VibeRails/wwwroot/style.css` for the footer, empty state, modal rows, drag state, visibility state, and compact/mobile layout.

## Customization Modal

Use the existing application modal infrastructure.

The modal contains separate Base CLI and Custom Environment sections. Each row contains:

- CLI logo.
- Display label.
- Visibility switch or eye control.
- Drag handle.
- Move up and move down buttons.

Drag-and-drop is the pointer interaction. Move buttons provide the accessible keyboard equivalent and are the authoritative fallback on touch or browsers where dragging is unreliable.

Modal actions:

- **Cancel** closes without changing live picker state.
- **Reset to defaults** requires confirmation if the form is dirty, calls the reset endpoint, and refreshes all pickers.
- **Save** sends the complete snapshot, disables controls while pending, keeps the modal open on failure, and refreshes all pickers only after a successful response.

Opening and closing behavior should move focus into the modal and restore focus to the originating Tom Select control afterward.

Remove the existing **Hide from Environment select boxes** switch from the Environment create/edit form so the customization modal becomes the single UI for visibility. Keep the backend `Hidden` property for compatibility, and keep the hidden badge on the Environments page.

## Picker Integration

Convert these launch selectors to the new controller:

1. `terminal-multitab.js`
   - Context: `terminal`
   - Includes plain Terminal.
   - Restored tab selections must survive if subsequently disabled.

2. `sandbox-controller.js`
   - Context: `sandbox`
   - Excludes plain Terminal as it does today.

3. `jobs-controller.js`
   - Context: `automation`
   - Custom Environments only.
   - Preserve hidden or deleted Environment references with the existing missing-reference handling.

4. `terminal-multirun.js`
   - Context: `multi-run`
   - Base CLIs only.
   - Replace hard-coded Claude/Codex defaults with the first and second enabled base items. If only one is enabled, using it on both sides is valid. If none are enabled, leave both empty and keep the customization footer available.

5. `environment-controller.js`
   - Context: `environment-provider` for the create form.
   - Reuse the centralized provider catalog, labels, and brand rendering.
   - Ignore visibility preferences and do not show the customization footer.

Chat History should continue using an unfiltered provider catalog. Preference ordering may be reused for display consistency, but disabled launch targets must remain available as history filters.

## Compatibility and Edge Cases

- Existing custom `Hidden` values are honored without a migration.
- Older clients that update `Environment.Hidden` remain compatible because that column stays authoritative for custom Environment visibility.
- A saved selection that becomes disabled is retained only where it is already referenced; it is not offered for new choices.
- A deleted Environment preference key is ignored and pruned on the next successful save.
- Active sessions and terminal tabs are never stopped or rewritten when preferences change.
- If preference loading fails, render the current default picker rather than blocking the application. Show customization Save errors normally and retain the user's unsaved modal state.
- Tom Select's viewport-flip behavior must account for the added footer height.
- A picker with no applicable visible items still renders its placeholder/empty state and opens the footer.

## Test Plan

### Backend tests

Add route/service/repository coverage for:

- Default catalog when no preference document exists.
- Existing custom `Hidden` values in the resolved response.
- Base visibility and both group orders round-trip through PUT and GET.
- Atomic update of GlobalCache and `Environments.Hidden`.
- New built-in and Environment items append visibly.
- Deleted Environment keys are ignored.
- Duplicate, malformed, unknown, and cross-group keys are rejected.
- Reset restores all visibility and default order.
- A legacy Environment update to `Hidden` is reflected on the next preference GET.
- DTOs work with the source-generated/AOT JSON configuration.

### Frontend and Playwright tests

Add `UITests/tests/llm-picker-preferences.spec.js` covering:

- Footer presence in terminal, sandbox, Automation, and Multi Run pickers.
- Footer absence in the Environment provider picker and unrelated Tom Select controls.
- Opening the modal from each supported picker.
- Reordering and hiding entries, saving, and reopening the picker.
- Propagation to other currently mounted pickers.
- Persistence across navigation and full reload.
- Correct context filtering, including Terminal only appearing in the terminal context.
- Preservation of an existing selected hidden Automation Environment.
- Multi Run defaults choosing enabled items rather than hard-coded hidden entries.
- All-items-hidden recovery path.
- Cancel and failed Save leaving live picker state unchanged.
- Reset behavior.
- Keyboard move controls and focus restoration.
- Dropdown viewport flipping with the new footer.

Existing tests with exact option-list assertions, especially `UITests/tests/terminal-multirun.spec.js`, must reset the global picker preference before and after the test so shared state cannot leak between cases.

## Documentation Updates

After implementation, update:

- `VibeRails/wwwroot/AGENTS.md` with the picker controller, contexts, and customization flow.
- `VibeRails/DB/AGENTS.md` with the `GlobalCache` preference key and the continued role of `Environments.Hidden`.
- User-facing README documentation if the Environment/terminal picker behavior is described there.

## Acceptance Criteria

The feature is complete when:

1. Every supported launch picker exposes the same customization footer and modal.
2. A user can show, hide, and reorder applicable entries and the result is consistent across contexts.
3. Preferences survive application restart.
4. Existing references to hidden entries are never silently cleared.
5. Hiding entries does not affect history, active sessions, CLI availability, or Environment creation.
6. Users can recover even after hiding every item.
7. Backend, frontend, accessibility, persistence, and viewport behavior are covered by automated tests.
