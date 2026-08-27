# Left sidebar (Activity Bar) launch button — research spike

**Date:** 2026-08-26
**Scope:** How to put the VibeRails VS Code extension launch control on the left Activity Bar, and whether that can replace the two launch surfaces we have today.
**Repo surface:** `vscode-viberails/`
**This is research only.** Nothing in the extension was changed.

---

## Short answer

VS Code has **no API for a command-only button on the left Activity Bar**. An Activity Bar icon is always a **View Container**. Clicking it opens the Primary Sidebar and shows one or more Views you contributed. There is no `command` field on `viewsContainers.activitybar`.

That is the whole trick. To get a VibeRails mark on the left rail you must:

1. Contribute a `viewsContainers.activitybar` entry (id, title, **monochrome SVG** icon).
2. Contribute at least one `views` entry whose key matches that container id. **Without a view, the icon does not appear.**
3. Fill that view with either:
   - an empty tree + `viewsWelcome` buttons that run `viberails.open` / `viberails.stop`, or
   - a compact `type: "webview"` sidebar (Open / Stop / status), while the full dashboard stays an editor `WebviewPanel`.

You cannot make the Activity Bar icon itself mean “open the dashboard and do nothing else.” Microsoft’s UX guidelines say not to use an Activity Bar item as a launcher for a Webview Panel.

---

## What we have today (the two launch buttons)

`vscode-viberails` currently contributes **no** Activity Bar item. Launch lives in two other places:

| # | Surface | How it is wired | What the user sees |
|---|---|---|---|
| 1 | **Status bar** (bottom left) | Runtime: `extension.ts` `createStatusBarItem(Left, 1000)` with `command = viberails.open`. A second `$(close)` item (priority 999) is shown while the backend is running. | `$(terminal) VibeRails` in purple (`#c084fc`), plus a close icon when live. |
| 2 | **Editor title bar** (the toolbar on every editor tab) | Manifest: `contributes.menus["editor/title"]` → `viberails.open`, `group: "navigation"`, **`when: "true"`**. Command icon is `media/vs-logo.png`. | The color PNG logo on **every** editor tab, all the time, whether the dashboard is open or not. |

Other existing launch paths (keep these; they are not the “two buttons”):

- Command Palette: `VibeRails: Open Dashboard` / `VibeRails: Stop Dashboard`
- Keybinding: `Ctrl+Alt+V` / `Cmd+Alt+V`

The dashboard itself is an **editor Webview Panel** (`WebviewPanelManager` → `createWebviewPanel` in `ViewColumn.One`). It is a full-width SPA (terminals, history, environments). That is the right host for the dashboard. The sidebar is ~300px and is the wrong host for the same UI.

If the goal is “one obvious place on the left, not two random places,” the clean swap is:

- **Add** an Activity Bar View Container + a small launcher view.
- **Remove** the `editor/title` contribution (`when: "true"` is noisy; it paints our logo on every file tab).
- **Decide** whether the status bar item stays as a secondary shortcut or goes away so launch lives in one place.

---

## What the left bar actually is

VS Code workbench, left to right:

1. **Activity Bar** — vertical icon rail (Explorer, Search, Source Control, Run and Debug, Extensions, plus extension containers). This is what people mean by “left hand bar.”
2. **Primary Sidebar** — the pane that opens when you click an Activity Bar icon. It renders the Views of that container.
3. **Editor groups** — where our dashboard Webview Panel lives today.
4. **Panel** — bottom area (Terminal, Problems). You *can* contribute a View Container here instead (`viewsContainers.panel`), which is not what we want.
5. **Status Bar** — bottom strip. This is where our launch button is today.

The Activity Bar and Primary Sidebar are coupled. Clicking a contributed Activity Bar item **always** focuses that View Container in the sidebar. Users can later drag the container to the Panel or Secondary Sidebar; we cannot contribute directly to the Secondary Sidebar.

VS Code also lets users put the Activity Bar on the **top** (`workbench.activityBar.location`). A `viewsContainers.activitybar` contribution still shows; it just moves with the rail.

---

## Hard constraint: no command-only Activity Bar icon

`contributes.viewsContainers.activitybar[]` only accepts:

| Field | Required | Meaning |
|---|---|---|
| `id` | yes | Container id. Views are nested under this same key in `contributes.views`. VS Code also synthesizes `workbench.view.extension.<id>` to focus it. |
| `title` | yes | Sidebar header / tooltip. |
| `icon` | yes | Path to an image (SVG strongly recommended) **or** a ThemeIcon such as `$(rocket)`. |

There is no `command`, no `onClick`, no “just run this.” Stack Overflow threads that try to add `command` on the container confirm it is ignored.

A container with **zero views does not render an icon**. The matching `contributes.views.<containerId>` array is mandatory.

When the user first opens the view, VS Code fires `onView:<viewId>`. We already activate on `onStartupFinished`, so we do not need to add that event, but it is the right event if we ever stop activating at startup.

---

## Microsoft UX rules that apply to this extension

From the official Activity Bar and Views guidelines (current as of 2026-08):

**Do**

- One View Container per extension in almost all cases.
- Icon that matches built-in Activity Bar style (simple, monochrome, currentColor).
- Clear container name (“VibeRails”).
- Prefer a Tree View or a Welcome View for simple actions.
- Put view-scoped actions on `view/title` (they become the sidebar toolbar when the container has a single view).

**Don’t**

- **Use an Activity Bar item to open a Webview Panel.** Stated twice: Activity Bar guidelines and Views guidelines.
- Duplicate an existing product icon.
- Use tree items as fake buttons whose only job is to fire a command.
- Stuff a desktop-width custom webview into the sidebar if a command / welcome button would do.

That last “don’t” is aimed directly at our current architecture. Auto-opening `createWebviewPanel` from `TreeView.onDidChangeVisibility` / `WebviewView.onDidChangeVisibility` is the workaround people reach for, and it is the thing Microsoft tells you not to ship. It also leaves an empty (or duplicate) sidebar open next to the editor panel, which looks broken.

---

## Option A — Empty tree + Welcome buttons (smallest change)

**What it is.** Contribute a container + a default tree view. Register a `TreeDataProvider` that always returns `[]`. Contribute `viewsWelcome` whose contents include markdown command links. A command link on its own line is rendered as a **button**.

**Manifest sketch** (drop into `vscode-viberails/package.json` `contributes`):

```json
"viewsContainers": {
  "activitybar": [
    {
      "id": "viberails",
      "title": "VibeRails",
      "icon": "media/activitybar.svg"
    }
  ]
},
"views": {
  "viberails": [
    {
      "id": "viberails.launcher",
      "name": "VibeRails",
      "icon": "media/activitybar.svg",
      "contextualTitle": "VibeRails"
    }
  ]
},
"viewsWelcome": [
  {
    "view": "viberails.launcher",
    "contents": "Open the VibeRails dashboard in the editor.\n[Open Dashboard](command:viberails.open)",
    "when": "!viberails:running"
  },
  {
    "view": "viberails.launcher",
    "contents": "Dashboard is running.\n[Reveal Dashboard](command:viberails.open)\n[Stop Dashboard](command:viberails.stop)",
    "when": "viberails:running"
  }
],
"menus": {
  "view/title": [
    {
      "command": "viberails.open",
      "when": "view == viberails.launcher",
      "group": "navigation@1"
    },
    {
      "command": "viberails.stop",
      "when": "view == viberails.launcher && viberails:running",
      "group": "navigation@2"
    }
  ]
}
```

**Runtime sketch:**

```ts
await vscode.commands.executeCommand('setContext', 'viberails:running', true);  // after start
await vscode.commands.executeCommand('setContext', 'viberails:running', false); // after stop

vscode.window.registerTreeDataProvider('viberails.launcher', {
  getChildren: () => [],
  getTreeItem: (el) => el
});
```

Welcome content **only** shows when the tree is empty **and** `TreeView.message` is unset. That is why the provider must return no children.

With a **single** view in the container, VS Code collapses the sidebar toolbar: the `view/title` Open / Stop icons sit in the header next to the title (same pattern as Notes in the Sidebars UX doc). That is the closest thing to “a launch button on the left.”

**Click path:** Activity Bar icon → sidebar opens → user clicks **Open Dashboard** (welcome button or header icon) → existing `viberails.open` runs → editor panel as today.

**Pros**

- Tiny: package.json + ~20 lines of TS + one SVG.
- Native look, themeable, accessible, no extra CSP.
- Matches “I want a launch button,” not “I want a second dashboard.”
- Easy to swap welcome copy with `viberails:running`.

**Cons**

- Two clicks to launch (icon, then button). Cannot be one click without violating the Webview-Panel rule.
- Welcome views are intentionally sparse. Do not put marketing copy here.
- Header icons need a command `icon` (codicon, e.g. `$(play)` / `$(debug-stop)`). Our current command icon is a color PNG, which is the wrong asset for a 16×16 toolbar button too.

**Verdict:** Best first implementation if we only want the left-rail presence plus Open/Stop.

---

## Option B — Compact Webview View in the sidebar (what Cline / Continue / Roo do)

**What it is.** Same View Container, but the view is `"type": "webview"`. Register a `WebviewViewProvider`. The sidebar is a **small** HTML surface (status, Open, Stop, maybe environment name). The full dashboard stays `createWebviewPanel`.

**Manifest:**

```json
"views": {
  "viberails": [
    {
      "type": "webview",
      "id": "viberails.sidebar",
      "name": "VibeRails",
      "icon": "media/activitybar.svg",
      "contextualTitle": "VibeRails"
    }
  ]
}
```

`"type": "webview"` is **required**. If you omit it, VS Code treats the view as a tree, never calls `resolveWebviewView`, and you get a dead placeholder.

**Runtime:**

```ts
const provider: vscode.WebviewViewProvider = {
  resolveWebviewView(webviewView) {
    webviewView.webview.options = {
      enableScripts: true,
      localResourceRoots: [context.extensionUri]
    };
    webviewView.webview.html = /* compact launcher HTML, not index.html */;
    webviewView.webview.onDidReceiveMessage((msg) => {
      if (msg.command === 'open') void vscode.commands.executeCommand('viberails.open');
      if (msg.command === 'stop') void vscode.commands.executeCommand('viberails.stop');
    });
    // Optional running badge on the Activity Bar icon:
    // webviewView.badge = { value: 1, tooltip: 'Dashboard running' };
  }
};

context.subscriptions.push(
  vscode.window.registerWebviewViewProvider('viberails.sidebar', provider, {
    webviewOptions: { retainContextWhenHidden: true }
  })
);
```

`retainContextWhenHidden: true` is load-bearing. Without it, switching to Explorer destroys the sidebar webview and the next click rebuilds it from scratch.

Focus the container from code with:

```ts
await vscode.commands.executeCommand('workbench.view.extension.viberails');
// or the synthesized view-focus command:
await vscode.commands.executeCommand('viberails.sidebar.focus');
```

**How peer extensions do this**

| Extension | Container | View | Icon | What the sidebar actually is |
|---|---|---|---|---|
| Continue | `continue` | `continue.continueGUIView` (`type: webview`) | `media/sidebar-icon.png` | The product UI (chat) lives in the sidebar |
| Roo Code / Roo Cline | `roo-cline-ActivityBar` | `roo-cline.SidebarProvider` (`type: webview`, `name: ""`) | `$(rocket)` or a simplified `currentColor` SVG | The product UI lives in the sidebar; “Open in Editor” is a *secondary* action |
| Cline | same pattern | webview | monochrome mark | Same |
| Microsoft sample “Calico Colors” | (contributes to built-in `explorer`) | `calicoColors.colorsView` (`type: webview`) | n/a | Official `registerWebviewViewProvider` sample |

Those products **are** a sidebar. VibeRails is not. Our dashboard is a multi-page app with xterm tabs. Copying Cline’s “put the whole UI in the sidebar” would squeeze terminals into ~300px and fight the existing panel manager (`portMapping`, token injection, `__viberails_*` bridge, CSP that allows Lato).

**Pros**

- Sidebar can show live status, errors, “backend starting…”, workspace folder.
- One Activity Bar click lands on a real Open button immediately (still a second click to launch, but the button can be large and obvious).
- Badge API can mark the icon while the backend is up.

**Cons**

- New HTML/CSS that must follow VS Code webview theming (`var(--vscode-*)`), a11y, and CSP.
- Do not load `wwwroot/index.html` here.
- More test surface (sidebar provider + existing panel).

**Verdict:** Right choice if we want the left rail to feel like a control panel, not a Welcome page. Still keep the full dashboard in the editor.

---

## Option C — Visibility listener auto-opens the editor panel (do not)

```ts
treeView.onDidChangeVisibility((e) => {
  if (e.visible) void vscode.commands.executeCommand('viberails.open');
});
```

This is the Stack Overflow answer to “run a command when the Activity Bar icon is clicked.” It is also:

- Explicitly banned (“Don’t use an Activity Bar item to open a Webview Panel”).
- Fired on collapse/expand of an individual view, not only on icon click.
- Guaranteed to leave the sidebar open *and* spawn the editor panel, so the user now has two VibeRails surfaces.
- Awkward on the second click (icon click hides the sidebar; visibility false does not mean “stop”).

Do not ship this.

---

## Option D — Full dashboard as a sidebar Webview View (do not)

Technically possible: `type: "webview"` and point `resolveWebviewView` at the same HTML `WebviewPanelManager` builds.

Why not:

- Dashboard layout is desktop-width (nav, terminals, history). Sidebar is a rail.
- `createWebviewPanel` vs `WebviewView` have different lifecycles, reveal, title, and `ViewColumn` behavior. `portMapping` and the VS Code keybinding hacks (`shift+enter`, `escape` unbound for `activeWebviewPanelId == 'viberailsDashboard'`) are **panel**-scoped. A sidebar webview is a different `activeWebviewPanelId` story; those keybindings would not apply.
- Closing the sidebar is not the same as Stop Dashboard. Users would kill the PTY by collapsing Explorer-equivalent UI.
- Microsoft: “Limit the use of custom Webview Views”; “Don’t use a View Container to open a Webview in the Editor” — the inverse (stuffing an editor app into the sidebar) is the same class of mistake.

---

## Icon work (this will bite us if skipped)

Today we only have `media/vs-logo.png` (color marketplace / editor-title icon). That file is the **wrong** asset for the Activity Bar.

Official View Container icon spec (`contributes.viewsContainers`):

- Size: **24×24**, centered (older docs said 28×28 on a 50×40 slot; ship 24×24 SVG with padding, it scales).
- Color: **single color**. Use `fill="currentColor"` on paths. No `#000`, no CSS classes, no `<style>` blocks.
- Format: SVG recommended. PNG is accepted and looks bad (tinted blob at 60% opacity).
- States: default **60%** opacity, hover/active **100%**. VS Code applies this; do not encode it in the file.
- Keep the SVG dumb: no XML declaration, no Adobe namespaces, no `<defs>`. Roo Code had a Windows / VS Code 1.108 bug where a “designed” SVG simply did not show; the fix was a flattened `currentColor` path.

Also add `icon` + `contextualTitle` on the **view** itself. If the user drags the view onto the Activity Bar or Secondary Sidebar, VS Code uses the view icon, not the container icon.

Optional: `"icon": "$(terminal)"` (ThemeIcon) on the container, like Roo’s `$(rocket)`. That skips the SVG file and always matches the product icon theme. Tradeoff: we lose the VibeRails mark.

**Do not** reuse `media/vs-logo.png` on the Activity Bar.

Command icons used in `view/title` are a different spec: **16×16** with 1px padding, also monochrome. Prefer `"icon": "$(play)"` / `"$(debug-stop)"` on `viberails.open` / `viberails.stop` rather than the PNG. Changing the command icon affects every menu that shows it (including Command Palette? no — Palette does not show icons). It *would* change the editor-title button if we kept that menu, which is another reason to delete `editor/title`.

---

## Recommended plan for VibeRails

Ship **Option A first**. It answers “put the launch button on the left” with the least new surface and without fighting the existing panel.

Concrete steps when we implement:

1. **Add** `media/activitybar.svg` — flattened monochrome mark from the logo, `fill="currentColor"`.
2. **Add** `viewsContainers.activitybar`, `views.viberails`, `viewsWelcome`, and `menus.view/title` as in Option A.
3. **Give** `viberails.open` / `viberails.stop` codicon command icons (`$(play)`, `$(debug-stop)` or `$(close)`).
4. **Register** an empty `TreeDataProvider` for `viberails.launcher`.
5. **Set** `viberails:running` from the existing start/stop paths in `extension.ts` (after `stopBarItem.show()` / `hide()`).
6. **Remove** `menus.editor/title`. That is the second of the two current buttons and it is the worse of the two (global tab chrome).
7. **Status bar:** default recommendation is **keep it for one release** (people already click it; README / ABOUT / AGENTS all document it), then drop it if the Activity Bar sticks. If the request is strictly “only on the left rail,” delete the two `StatusBarItem`s in the same change and update those three docs.
8. **Do not** call `viberails.open` from a visibility listener.
9. **Do not** load the dashboard SPA in the sidebar.
10. If Option A feels too thin after using it, upgrade the same container to Option B without changing the Activity Bar id (so user layout / “keep open” state survives).

Upgrade path A → B: keep `viewsContainers.id = "viberails"`, change the view to `type: "webview"` with a new id (or the same id if nothing else references it), delete `viewsWelcome` (welcome is tree-only).

---

## Files that would change (when we implement, not now)

| File | Change |
|---|---|
| `vscode-viberails/package.json` | `viewsContainers`, `views`, `viewsWelcome`, `view/title`; delete `editor/title`; optional command `icon` updates |
| `vscode-viberails/src/extension.ts` | empty tree provider, `setContext('viberails:running')`; optionally drop status bar items |
| `vscode-viberails/media/activitybar.svg` | new |
| `vscode-viberails/src/test/suite/smoke.test.ts` | still drives `viberails.open`; add a check that the view contribution exists if we ever inspect the manifest |
| `vscode-viberails/README.md`, `ABOUT.md`, `AGENTS.md` | “click the VibeRails icon in the Activity Bar (left rail)” |

No backend / `wwwroot` changes for Option A. Option B would add a small sidebar HTML module next to `webview-panel.ts`, still not the dashboard.

---

## Other mechanics worth knowing

**Activation.** A view contribution implies `onView:<viewId>`. We already use `onStartupFinished` so the status bar exists before the first click. If we drop the status bar, we could activate later (`onView:viberails.launcher` + `onCommand:viberails.open`) and save a process until the user actually wants us. That is a separate product decision: today we pay startup cost to have a persistent button.

**Workspace trust.** `untrustedWorkspaces.supported: false` already. The Activity Bar icon is hidden in untrusted windows, same as the rest of the extension. No extra work.

**Missing icon after install.** User right-click on the Activity Bar → re-check “VibeRails”, or Command Palette → `View: Reset View Locations`. Same as MongoDB / any other container users accidentally hide.

**Badges.** `TreeView.badge` / `WebviewView.badge` (`{ value, tooltip }`) draw the blue number on the Activity Bar icon. Optional “1” while the backend is running. Users can hide all badges with `workbench.activityBar.showBadges`.

**Progress.** `vscode.window.withProgress({ location: { viewId: 'viberails.launcher' } })` can put the spinner in the sidebar instead of (or as well as) a Notification while the backend starts. We currently use `ProgressLocation.Notification` (“Starting VibeRails…”).

**Keybindings.** The empty-command `shift+enter` / `escape` entries in `package.json` are load-bearing for the **editor** webview terminal. They key off `activeWebviewPanelId == 'viberailsDashboard'`. A sidebar Webview View is not that panel. Leave those entries alone.

**Order on the rail.** There is no supported “put us under Explorer” field on the contribution point. New containers land with the other extension containers; users drag.

---

## Open questions for whoever implements this

1. **One place or two?** Activity Bar only, or Activity Bar + status bar? This spike’s recommendation: Activity Bar + drop editor-title immediately; keep status bar until we see whether people still want a bottom-left click target.
2. **A or B?** Welcome buttons vs compact webview. A is the spike-sized answer; B is the control-panel answer.
3. **Custom SVG vs `$(terminal)` / `$(rocket)`?** Custom mark is nicer branding if we flatten it correctly; ThemeIcon is zero-risk rendering.
4. **Should clicking Open also `focus` the sidebar?** Not required. `viberails.open` already reveals the editor panel.

---

## References

Official:

- Contribution points: `views`, `viewsContainers`, `viewsWelcome` — https://code.visualstudio.com/api/references/contribution-points
- Activity Bar UX — https://code.visualstudio.com/api/ux-guidelines/activity-bar (“Don’t use an Activity Bar item to open a Webview Panel”)
- Views UX — https://code.visualstudio.com/api/ux-guidelines/views (same don’t, plus welcome-view and webview-view guidance)
- Sidebars UX — https://code.visualstudio.com/api/ux-guidelines/sidebars (single-view toolbar consolidation)
- Tree View guide (container + `view/title`) — https://code.visualstudio.com/api/extension-guides/tree-view
- Webview API (panel vs view) — https://code.visualstudio.com/api/extension-guides/webview
- Extending the workbench — https://code.visualstudio.com/api/extension-capabilities/extending-workbench

Samples:

- Tree View — https://github.com/microsoft/vscode-extension-samples/tree/main/tree-view-sample
- Welcome View — https://github.com/microsoft/vscode-extension-samples/tree/main/welcome-view-content-sample
- Webview View — https://github.com/microsoft/vscode-extension-samples/tree/main/webview-view-sample

In this repo today:

- `vscode-viberails/package.json` — `editor/title` launch; no `viewsContainers`
- `vscode-viberails/src/extension.ts` — status bar items + `viberails.open` / `viberails.stop`
- `vscode-viberails/src/webview-panel.ts` — editor `WebviewPanel` (keep)
