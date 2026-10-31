# Vibe Rails design system

This is the single source of truth for customer-facing Vibe Rails UI: the public site at [viberails.ai](https://viberails.ai/) and the signed-in app. It combines the approved design target with the current implementation. Where the target sets a rule, follow it. Where the target is silent, the current implementation recorded here is the rule. Preserve the existing near-black, purple and teal identity, and follow the component standards below for buttons, forms and feedback.

To change how the UI looks, change this file first, then update the code to match. Every component section names the classes and files that implement it today, so one decision here can be applied everywhere.

Baseline checked against [viberails.ai](https://viberails.ai/) and the repository on 29 October 2026 (`master` at e4ee2ef). Publishing this reference does not apply its styles to the live site; existing screens may still differ until they are updated.

Open [DESIGN_SYSTEM.html](DESIGN_SYSTEM.html) in a browser to see and try the components. It shows the target buttons, dropdowns, validation, toggles, cards, status feedback and dialogs with the approved colors, plus the app shell, status badges, brand patterns and the full token reference in the same style. It can be opened directly from a local checkout without starting the application; example actions stay local. Fonts and icon helpers load from CDNs. With no network, text controls still work and system fonts are used, but icons may be absent. When this file and the HTML examples differ, this file wins.

## How to read this file

| Order | Source | Applies |
| --- | --- | --- |
| 1 | [Approved target choices](#approved-target-choices) | Always. They supersede older values anywhere in this file or the code. |
| 2 | Standards (the target) | New and updated UI. |
| 3 | Current implementation, codified here | Wherever 1 and 2 say nothing. Copy what ships today. |
| 4 | [Known gaps](#known-gaps-in-todays-ui) | Never copy these. Fix them in the code you touch. |

Component sections use up to three headings: **Standard** (what to build), **In the code today** (the classes and files that implement it) and **When you change it** (what to migrate as you go).

## Approved target choices

| Role | Target value | Use |
| --- | --- | --- |
| Warning (`--color-warning`) | `#F66210` | Orange text and icons on dark surfaces; attention or pending action |
| Milestone (`--color-milestone`) | `#CAD00C` | Lime accent for token-savings milestones and celebrations |
| Control corner radius | `4px` | Buttons, inputs and selects |

These choices supersede the older warning, milestone and control-radius values in the current-foundations inventory below. Apply them when implementing this design system, within the requested scope.

Keep the selected colors exact. Orange on `#33251F` has approximately 4.68:1 contrast, and lime on `#282A13` approximately 8.76:1. Use dark text when either color is a solid fill; do not assume white labels remain readable.

## Instructions for agents

- Read this file and the applicable `AGENTS.md` before UI work. Keep changes within the requested feature; do not redesign unrelated screens.
- Reuse existing tokens, Bootstrap primitives and shared components. Consolidate conflicting styles instead of adding another page-specific variant.
- The standards below guide future implementation; proposed token and class names are not APIs that already exist. Inspect the current CSS before using them.
- Use the tokens in [Tokens](#tokens) instead of literal colors, radii and sizes. If a value you need has no token, add it to this file first, then to the `:root` block of `viberails.css`.
- When you change a screen, migrate the components you touch to their standard, using their **When you change it** notes. Leave screens you were not asked to change alone.
- Preserve working form submissions, validation, accessibility and security contracts. Visual changes do not authorize changes to authentication, permissions or data access.
- Keep the app customer-facing. Do not add administration features or expose new GUIDs in routes, labels or links.
- Preview through the feature harness named in `VibeRails-Front/AGENTS.md`; do not start the configured application solely to check styling.

## Design principles

- Make the next action clear. Give each task one primary action and keep supporting actions quieter.
- Keep work readable. Use opaque dark surfaces behind forms, menus and long text; reserve atmospheric gradients for the public homepage and restrained highlights.
- Let color carry a stable meaning. Purple advances a task, teal identifies links and accents, green confirms success, orange warns, lime celebrates milestones, and red marks errors or destructive actions.
- Use familiar controls and direct language. Prefer “Create board,” “Save changes,” “Copy command” and “Disconnect” to ambiguous labels.
- Support customer tasks such as installation, boards, sessions and sharing. Administrative interfaces are outside this system's scope.

## Tokens

The canonical variables live in the `:root` block of [`viberails.css`](VibeRails-Front/wwwroot/css/viberails.css#L6). **Status** says whether a token exists there today (`current`), exists with a value that is changing (`approved change`), or still has to be added (`proposed`). Add a proposed token to that block, with the value given here, the first time you need it. Page stylesheets use these tokens instead of literals.

### Color

| Token | Value | Status | Use |
| --- | --- | --- | --- |
| `--color-bg-base` | `#0a0a0f` | current | Page canvas and recessed inputs |
| `--color-bg-surface` | `#14141d` | current | Cards, dialogs and elevated content |
| `--color-bg-surface-hover` | `#1e1e2a` | current | Raised fills: secondary buttons, menus and hovered neutral surfaces |
| `--portal-surface` | `#191b25` | current (declared on `body.portal-page` in `vibe-portal.css`) | Cards, list rows and page headers on signed-in pages |
| `--portal-surface-hover` | `#222532` | current (as above) | Hovered list rows on signed-in pages |
| `--header-bg` | `rgba(30, 30, 36, 0.95)` | proposed (literal in `.navbar` today) | The sticky header, blurred 12px |
| `--subnav-bg` | `rgba(22, 22, 28, 0.97)` | proposed (literal in `.app-subnav` today) | The sticky section bar under the header |
| `--color-primary` | `#8b5cf6` | current | Brand purple and primary gradient start; purple text and radio accents. Not a fill under white text (4.23:1) |
| `--color-primary-dark` | `#7c3aed` | current | Deeper purple and primary gradient end; the action fill |
| `--action-primary-bg` | `var(--color-primary-dark)` | proposed | Filled primary button |
| `--action-primary-fg` | `#ffffff` | proposed | Text on filled primary button |
| `--action-primary-hover` | `#6d28d9` | proposed | Primary hover state |
| `--action-primary-active` | `#5b21b6` | proposed | Primary pressed state |
| `--color-secondary` | `#64748b` | current | Secondary neutral emphasis |
| `--control-border` | `#64748b` | proposed | Essential input and control boundaries |
| `--color-accent` | `#14b8a6` | current | Teal links and accents; selected and "you are here" states |
| `--action-accent-fg` | `var(--color-accent)` | proposed | Links on dark surfaces |
| `--focus-ring` | `var(--color-accent)` | proposed | Visible keyboard focus |
| `--color-text` | `#f8fafc` | current | Main text |
| `--color-text-muted` | `#94a3b8` | current | Supporting text, labels, resting navigation |
| `--color-border` | `#2d2d3f` | current | Quiet separators and surface edges. Not a control boundary (1.5:1); use `--control-border` |
| `--color-success` | `#10b981` | current | Positive status |
| `--color-warning` | `#F66210` | approved change (today `#f59e0b`) | Warning status; orange text and icons |
| `--color-danger` | `#ef4444` | current | Errors and destructive intent |
| `--feedback-error-fg` | `#fca5a5` | proposed | Error text on dark surfaces |
| `--color-milestone` | `#CAD00C` | approved change (today `#f2c14e`) | Token-savings milestones and celebrations only |
| `--color-wordmark` | `#a77bf3` | proposed (literal today) | Start of the brand gradient |
| `--color-mint` | `#80e7a5` | proposed (literal today) | End of the hero lead's three-stop gradient |
| `--brand-gradient` | `linear-gradient(135deg, #a77bf3 0%, #14b8a6 100%)` | proposed (literal today) | The wordmark, the hero title and nothing else |

White text on solid `#8b5cf6` is approximately **4.23:1**; on solid `#7c3aed` it is **5.70:1**. Use the deeper purple for ordinary-size filled button labels. The current gradient includes the lighter endpoint, so assess its weakest text-background pairing when implementing a shared replacement.

White on teal `#14b8a6` is approximately **2.49:1**. Teal text on the base canvas is approximately **7.93:1**. Use teal as an accent on dark backgrounds, or use dark text if a control has a filled teal background. These ratios use opaque sRGB colors; transparency, gradients and disabled styling need separate checks.

Milestone accents stay reserved for savings celebrations; do not turn them into a general primary button color. Status colors need a readable text pairing and a word or icon identifying their meaning.

### Status tones

Badges, alerts and chips take their colors from these pairs, taken from the examples. Proposed token names: `--tone-<name>-fg`, `--tone-<name>-bg`.

| Tone | Text | Fill | Border | Use |
| --- | --- | --- | --- | --- |
| Success | `#6ee7b7` | `#102c25` | `#2d6155` (alerts) | Connected, saved, valid, live |
| Saved notice | `#a5f3e5` | `#102c25` | `#2d6155` | Inline confirmation ("Your changes have been saved.") |
| Warning | `--color-warning` | `#33251f` | — | Pending, needs attention, legacy, high priority |
| Danger | `--feedback-error-fg` | `#341c26` | `#a45261` | Failed, invalid, deleted, critical |
| Active | `#c4b5fd` | `#2e2146` | — | In progress, selected agent, medium priority |
| Milestone | `--color-milestone` | `#282a13` | — | Token-savings milestones, always with a celebration icon and label |
| Neutral | `--color-text-muted` | `--color-bg-surface-hover` | — | Offline, low priority, revoked, default |

### Legacy colors to replace

These literals are still in today's CSS. Do not add new uses. When you change a component that uses one, replace it as shown.

| Literal | Where it appears | Replace with |
| --- | --- | --- |
| `#9ac6c5`, `rgba(154, 198, 197, α)` (the earlier accent) | Install cards, page headers, board inputs and badges, Signing Keys, the notifications panel, confirm dialog focus | `--color-accent` for text, icons and focus; `--color-border` or `--control-border` for lines |
| `#5b2a86`, `rgba(91, 42, 134, α)` (the earlier primary) | Install-card headers, feature icon boxes, `badge-cli`, input focus glow, primary hover glow, confirm dialog, sharing pages' buttons | `--action-primary-bg` for fills; the Active tone for tints |
| `#7785ac`, `rgba(119, 133, 172, α)` (the earlier secondary) | Notifications bell and panel borders, read-only chip, `badge-context-global` | `--control-border` or `--color-border` |
| `#2b2b36` (the earlier surface) | Confirm dialog, contact panel fallback | `--color-bg-surface` |
| `#252530`, `#33303e` | Notifications panel and row hover | `--color-bg-surface-hover`; menu hover `#343447` |
| `#242431`, `#393149` | Round header buttons and hover | `--color-bg-surface-hover`; hover `#303040` |
| `#f0f0f5`, `#b0b0c3`, `#bdbdce`, `#9494a8` | Panel and dialog text, board field labels | `--color-text`, `--color-text-muted` |
| `rgba(6, 182, 212, α)` (cyan) | Sign-in pill, inline code, savings chip | `--color-accent` tints |
| `#facc15` | Savings chip icon | `--color-milestone` |
| `#f59e0b`, `#f2c14e` | Warning and milestone today | The approved `#F66210` and `#CAD00C` |
| Page palettes: API Keys (`#111722`, `#edf2f7`, `#b3c0d2`, `#364358`, `#50627b`); Runbooks and Recipes (mint primaries); session and card sharing (`#5b2a86` buttons); Reviews and shared cards (`#213b2c` buttons) | Page stylesheets | The shared tokens and components |

### Typography

| Existing token | Family | Use |
| --- | --- | --- |
| `--font-sans` | Manrope, system sans-serif fallback | Body text, labels and controls |
| `--font-display` | Sora, Manrope fallback | Headings and prominent titles |
| `--font-mono` | JetBrains Mono, Cascadia Code, Consolas | Commands, code and aligned technical values |

The body currently uses a `1.6` line height. Font loading is declared in [`_Layout.cshtml`](VibeRails-Front/Views/Shared/_Layout.cshtml#L32) (Google Fonts); shared cards self-host the same three families from `wwwroot/assets/fonts/shared-card`. Use monospace for technical content, not ordinary navigation or paragraphs.

**Type scale (standard)**

| Role | Target size and line height | Weight |
| --- | --- | --- |
| Marketing hero | `clamp(36px, 6vw, 72px)` / 1.1 | Sora 700 |
| Page title | 32px / 1.2 | Sora 600 |
| Section heading | 24px / 1.3 | Sora 600 |
| Component heading | 18px / 1.4 | Sora 600 |
| Body and editable input | 16px / 1.6 | Manrope 400 |
| Button and field label | 14px / 1.4 | Manrope 600 |
| Supporting text | 12–14px / 1.5 | Manrope 400 |
| Command sample | 14px / 1.6 | JetBrains Mono 400 |

Keep important instructions at body size. Avoid long uppercase labels, and let text wrap instead of reducing its size to fit.

**Named styles (codified where the scale is silent)**

| Style | Spec | Where today |
| --- | --- | --- |
| Wordmark | Sora 700, 1.25rem, uppercase, `--brand-gradient` with a 5s shine | `.brand-text` in the header |
| Hero lead | Sora 600, 1.55rem / 1.22; second line in `--color-wordmark` → `--color-accent` → `--color-mint` | `.hero-lead` on Home |
| Eyebrow | JetBrains Mono 700, 0.72rem, uppercase, `--color-accent` | `.section-eyebrow` |
| Kicker | JetBrains Mono 600, 0.74rem, uppercase, `--color-text-muted` | `.install-kicker` |
| Section tab | Manrope 500, 0.8rem, uppercase; 700 when active | `.app-subnav-link` |
| Badge | Manrope 400, 11px / 1.6 | Status badges |
| Inline code | JetBrains Mono, 0.85em, `--color-accent` on a 12% teal fill | `code` |
| Command | JetBrains Mono 400, 0.8rem / 1.5, `--color-accent`, breaks anywhere | `.install-cmd-compact code` |

The root font size is 16px from 768px and 14px below (`site.css`), so rem sizes shrink by an eighth on phones; editable fields stay at 16px. Letter-spacing is 0 except board field labels (0.04em). Text is set uppercase only by CSS, never typed in capitals. The examples use smaller demo sizes (13px buttons, 12px labels); implement the sizes in the scale above.

### Spacing and layout

Use a 4px spacing unit: **4, 8, 12, 16, 24, 32, 48 and 64px** (proposed tokens `--space-1` to `--space-8`). Start with 8px between related controls, 12px between a heading and its content, 16px inside compact components and 24px between form groups.

| Layout part | Rule |
| --- | --- |
| Header | `--navbar-height` (64px), sticky, a three-column grid with the brand centred; wraps to two rows on phones when the savings chip is present |
| Section bar | Sticky under the header at `--navbar-height` |
| Content | `.main-container`, at most 1600px wide, padded 2.5rem × 2rem; signed-in pages pad 1.25rem on top and `clamp(0.75rem, 2.5vw, 2rem)` at the sides |
| Grid | Bootstrap rows and columns (`row g-3`, `col-lg-3 col-md-6`); breakpoints 576, 768, 992 and 1200px |
| Layout gutter | 24px desktop; 16px small screens |
| Home page | Centred sections, each opened by an eyebrow and a heading |
| App pages | A page header, then left-aligned lists of row cards |

### Shape

| Element | Target geometry |
| --- | --- |
| Standard button, text field and select | 40px minimum desktop height; 4px radius |
| Touch action | 44px minimum effective target in both dimensions |
| Small desktop action | 32px visual height; preserve adequate hit area |
| Card, dialog and dropdown panel | 12px radius; 16–24px internal padding |
| Status badge | Pill radius; compact horizontal padding |
| Layout gutter | 24px desktop; 16px small screens |

| Proposed token | Value | Use |
| --- | --- | --- |
| `--radius-control` | `4px` | Buttons, inputs, selects, inline alerts, empty-state frames, icon tiles up to 40px (larger tiles take `--radius-surface`) |
| `--radius-surface` | `12px` | Cards, dialogs, menus, dropdown panels, page headers |
| `--radius-pill` | `999px` | Status badges, chips and the header's established pills |
| `--radius-round` | `50%` | Round header buttons, avatars and status dots |
| `--control-height` | `40px` (44px on touch) | Buttons, inputs and selects |

Today `--border-radius` is `8px` on buttons, inputs, cards and modals, with 6px on badges and the copy button and 12–16px on panels and tiles. Move each to the scale above when you touch it.

Do not use pills for every control. Reserve them for status, compact categories and established navigation treatments.

### Elevation

| Token | Value | Status | Use |
| --- | --- | --- | --- |
| `--shadow-soft` | `0 1px 3px rgba(0, 0, 0, 0.5)` | current | Cards at rest |
| `--shadow-hover` | `0 4px 6px rgba(0, 0, 0, 0.5)` | current | Home cards on hover |
| `--shadow-menu` | `0 12px 32px #0008` | proposed | Menus and dropdown panels |
| `--shadow-dialog` | `0 24px 80px #0009` | proposed | Dialogs, over a `#080810c9` backdrop |

Elevation comes from fill and border first; shadows are black and soft.

### Motion

Short color and border changes take `--transition-speed` (150ms); the rules are in [Responsive behavior and motion](#responsive-behavior-and-motion).

Brand moments keep their motion on the home page and in the header only: the overshoot curve `cubic-bezier(0.34, 1.56, 0.64, 1)` on the mascot's hover tilt, icon tiles and cards rising in; the wordmark and hero gradient shine; the home cards' 4px lift. They already stop under reduced motion.

### Focus

Show keyboard focus as a 2px solid `--focus-ring` outline with a 2px offset (inset 3px inside tab bars, as the header does today). Preserve the browser focus outline; a custom ring may supplement it but never replace it with something fainter. Today's shared ring (`0 0 0 0.2rem` of 25% purple) is 1.3:1 and is a [known gap](#known-gaps-in-todays-ui).

## Components

### Buttons

**Standard**

| Variant | Appearance | Example |
| --- | --- | --- |
| Primary | Deep purple fill, white text | Create board |
| Secondary | Dark surface, visible neutral border, light text | Preview |
| Ghost | Transparent surface, readable light or teal text | Cancel |
| Destructive | Red-tinted surface and border, pale red text | Disconnect |
| Icon action | Same hierarchy as text buttons; accessible name | Copy command |

| State | Required behavior |
| --- | --- |
| Default | Label and hit area remain clear against the parent surface |
| Hover | Change background or border without moving surrounding content |
| Pressed | Darken the fill; keep the label readable |
| Keyboard focus | Preserve the browser focus outline; a custom ring may supplement it |
| Loading | Keep the button width stable, show a progress label and prevent duplicate submission |
| Disabled | Use the native disabled state and explain any non-obvious prerequisite nearby |

Use a link for navigation and a button for an action. Use sentence case and a leading 16–18px icon only when it helps recognition. An icon-only control needs an accessible label such as “Copy install command.”

| Part | Value |
| --- | --- |
| Size | `--control-height`, 8px × 14px padding, `--radius-control`, 1px border, 14px Manrope 600 |
| Primary | `--action-primary-bg` fill and border, `--action-primary-fg` text; hover `--action-primary-hover`; pressed `--action-primary-active` |
| Secondary | `--color-bg-surface-hover` fill, `--control-border` border, `--color-text`; hover fill `#303040`, border `--color-text-muted` |
| Ghost | Transparent, `--color-text-muted`; hover `--color-bg-surface-hover` fill and `--color-text` |
| Destructive | `#2a171e` fill, `--feedback-error-fg` text, `#a45261` border; hover `#46212e` |
| Disabled | 45% opacity, `cursor: not-allowed` |
| Icon only | Square at `--control-height`, 15px icon |
| Labels | Verb first, sentence case: "Create key", "Send message", "Deny connection" |

**In the code today**

| Today | Look today | Becomes | Files |
| --- | --- | --- | --- |
| `btn btn-primary` | `--color-primary` → `--color-primary-dark` gradient, 8px | Primary | `viberails.css` (Buttons) over Bootstrap 5.3.3 |
| `btn-terminal-connect` | Flat `#8b5cf6`, white text (4.2:1), lifts 1px | Primary | `vibe-theme.css` |
| `rb-button-primary`, `rc-primary`, class-less sharing buttons, Reviews and shared-card buttons | Page-local mint, plum or green fills | Primary | `vibe-runbooks.css`, `vibe-recipes.css`, `session-sharing.css`, `review-requests.css`, `card-sharing.css` |
| `btn btn-secondary`, `vibe-board-button`, `keys-button`, `rb-button`, `rc-button` | Dark fills, assorted borders | Secondary | `viberails.css`, `vibe-boards.css`, `vibe-keys.css`, `vibe-runbooks.css`, `vibe-recipes.css` |
| `btn btn-outline-secondary`, `push-nudge-dismiss`, panel text buttons | Transparent, muted | Ghost | `viberails.css`, `notifications.css` |
| `btn btn-outline-primary` (billing, annual) | Violet outline | Secondary (the target has no outline primary) | `viberails.css` |
| `btn-terminal-disconnect`, `vibe-confirm-danger`, `keys-button-danger`, `rb-button-danger`, `rc-danger` | Red tints of several strengths | Destructive | `vibe-theme.css`, page stylesheets |
| `btn-copy-compact`, `api-key-dialog-close`, `vibe-board-button` with only an icon | 6px squares, assorted tints | Icon only | `viberails.css`, `vibe-api-keys.css`, `vibe-boards.css` |
| `btn-sm` | Bootstrap small | Small desktop action (32px) | Bootstrap |

**When you change it:** use the target look and drop the gradient. Set `border-color` in every state: Bootstrap's own hover, focus and disabled borders (`#0a58ca`, `#0d6efd`) still show through today. Replace page-local button sets with the shared variants.

### Icon buttons and header pills

**Standard (codified):** the header keeps its established shapes.

| Control | Shape and color | Where today |
| --- | --- | --- |
| Account settings, notifications bell | 40px circle (`--radius-round`; 42px today, 44px on touch), `--color-bg-surface-hover` fill, `--color-border` ring, 21px stroke icon in `--color-text`; hover, open or current page: `#303040` fill, `--color-accent` ring and icon | `.nav-settings-link`, `.vibe-notification-bell` |
| Notification count | Pill on the bell's top-right edge, `--color-accent` fill, `--color-bg-base` text, hidden at zero | `.vibe-notification-badge` |
| Sign in | Pill, `--color-accent` text on a 10% teal fill with a 35% teal border, `log-in` icon, "Sign In" over "or Register" | `.nav-login-btn` |
| Profile and logout | Profile pill with avatar, name and email; Logout pill in `--color-text-muted`, red on hover | `.nav-user-profile`, `.nav-logout-btn` |
| Tokens saved chip | Pill, teal tint, `--color-milestone` icon, compact figure ("1.28M") and "Tokens Saved"; the exact total in `title` and `aria-label` | `.nav-personal-savings` |

Icon buttons inside page content follow the Buttons standard (4px squares). After copying, swap the icon for a check and show the success tone for 1.6 seconds instead of a toast.

**When you change it:** replace the legacy fills (`#242431`, `#393149`), the cyan tints and the `#facc15` icon as listed in [Legacy colors](#legacy-colors-to-replace).

### Dropdowns and action menus

**Standard**

Use a native `<select>` for choosing one value from a short, stable list, such as priority or operating system. Keep the selected value visible, provide a persistent label, and preserve keyboard selection behavior.

Use a button-triggered action menu for commands such as “Rename board,” “Duplicate board” and “Archive board.” Commands are not selectable form values. Give the trigger `aria-expanded` and `aria-controls`, close on Escape, and return focus to the trigger. If using ARIA menu roles, implement their arrow-key behavior; otherwise use a simple popover with ordinary buttons.

Keep menu surfaces opaque. Align panels to the trigger, flip them near viewport edges and allow labels to wrap. Separate destructive commands and name the affected object before confirmation. Avoid nested menus for ordinary tasks.

| Part | Value |
| --- | --- |
| Select | A text field (see [Fields](#fields-and-validation)) with a visible arrow; `color-scheme: dark` so the open list is dark |
| Menu panel | `--color-bg-surface-hover` fill, `--control-border` border, `--radius-surface`, `--shadow-menu`, 5px padding, 6px below the trigger |
| Menu item | Full-width button, 40px tall (44px on touch), 5px radius, 15px icon; hover `#343447` |
| Destructive item | `--feedback-error-fg`, separated by a `--color-border` line |

The notifications panel is the app's popover pattern: header (title and a Refresh text button), a status sentence in `role="status"`, up to five rows (a teal mono kicker such as "BOARD INVITATION", the object's name, one sentence of what happened), then "View all invitations". It closes on Escape, an outside click or focus leaving it.

**In the code today**

| Today | Becomes | Files |
| --- | --- | --- |
| `select.form-select` (API key expiry, device link) | Select. Its Bootstrap chevron is 1.7:1 today | `viberails.css` (Forms) over Bootstrap |
| `select.vibe-board-input` (board filters and card details) | Select | `vibe-boards.css` |
| Unclassed selects in Runbooks, Recipes and terminal settings | Select | Page stylesheets |
| `.vibe-notification-panel` (`legacy` palette, 16px corners) | Menu panel | `notifications.css`, `_Notifications.cshtml`, `notifications.js` |
| `<details>` disclosures (board settings, advanced card fields, key revocation) | Disclosure (see [Dialogs](#dialogs)) | `vibe-boards.css`, `vibe-keys.css` |

No screen uses Bootstrap dropdowns, and `bootstrap.bundle.min.js` loads without any of its components in use.

### Fields and validation

**Standard**

- Put a persistent label above every field. Placeholder text may show an example but cannot replace the label.
- Put help text below the field and associate it with `aria-describedby`.
- On invalid input, use `aria-invalid="true"`, a visible boundary and a specific error such as “Enter a board name.”
- Preserve the user's input after validation. Explain how to recover instead of reporting only “Invalid.”
- Validate on submit or after a field has been used; do not announce an error on every keystroke.
- Use `<textarea>` for multiline descriptions and a native search input for filtering lists.
- State required or optional status in text. Do not rely on a red border or an asterisk alone.

| Part | Value |
| --- | --- |
| Field | `--control-height`, 9px × 11px padding, `--color-bg-base` fill, `--control-border` border, `--radius-control`, 16px text |
| Focus | `--focus-ring` outline |
| Invalid | `--feedback-error-fg` border and a 12px error line below with `role="alert"` |
| Label | 14px Manrope 600 in `--color-text`, sentence case, "(optional)" or "(required)" in `--color-text-muted` |
| Help | 12–14px `--color-text-muted`, one sentence ("So we can get back to you.") |
| Placeholder | Examples only: "e.g. Work laptop CLI" |

**In the code today**

| Today | Notes | Files |
| --- | --- | --- |
| `label.form-label` + `.form-control` + `p.form-text` | Base fill, `--color-border` (1.5:1), 8px; disabled at 60% | `viberails.css` (Forms, Form Helper Text, Disabled Form Controls) |
| `.contact-panel` fields | Larger padding, 16px panel corners | `contact.css` |
| `.vibe-board-input` + `span.vibe-card-label` | 4% white fill, legacy teal border; uppercase tracked labels | `vibe-boards.css` |
| `rb-field`, `rc-field`, `.share-manage input`, `pin-digit` | Page-local | Page stylesheets |

Errors appear today as `text-danger` lines or summaries with `role="alert"`; there is no invalid border state, and `_ValidationScriptsPartial` is never rendered.

**When you change it:** use the field spec, sentence-case labels and the invalid state above.

### Checkboxes, radio buttons and switches

**Standard**

Use checkboxes for independent selections, radio buttons for one choice within a group and switches for an immediate on/off preference. Use real native inputs. Make the complete label clickable, and group related choices with `<fieldset>` and `<legend>`.

Label the preference itself, such as “Email notifications,” rather than using “Enable” without context. A checked indicator, selected dot or switch position must accompany color. If the preference needs an explicit save, make that requirement clear.

| Control | Value |
| --- | --- |
| Checkbox | Native, 17px, `accent-color: var(--color-accent)` |
| Radio | Native, 17px, `accent-color: var(--color-primary)` |
| Switch | `input[type=checkbox][role=switch]`, 42 × 24px pill, off `#303040` with a `--control-border` edge, on `--color-accent` with a `--color-bg-base` knob |
| Row | Label left, control right, at least 44px tall |
| Status line | Beside a switch that acts at once: a 9px dot and a sentence ("On — notifications enabled", "Blocked in your browser settings") |

**In the code today**

| Today | Becomes | Files |
| --- | --- | --- |
| `.form-check` lists with a bold name and a muted description (API key capabilities) | Checkbox list in a fieldset | `viberails.css`, `vibe-api-keys.css` |
| `label.vibe-card-toggle` ("Needs your attention", "Blocked") | Checkbox | `vibe-boards.css` |
| `label.vibe-switch` + `.notif-status` (push notifications) | Switch with status line | `viberails.css` (Toggle switch, Notifications card) |
| Radio cards in the share-access dialog (built in script) | Radio | `share-access.css`, `share-access.js` |
| `.form-switch` | Unused; do not adopt | `viberails.css` |

### Cards

**Standard**

Cards group a meaningful object or task. Use a quiet border, one heading and a small set of actions. Let the content determine height; avoid equal-height layouts that create large empty areas. Give action buttons separate targets rather than nesting buttons in a clickable card.

| Part | Value |
| --- | --- |
| Card | `--color-bg-surface` (`--portal-surface` on signed-in pages), `--color-border`, `--radius-surface`, 16–24px padding |
| Heading | Component heading (Sora 600, 18px) |
| Meta | 12px `--color-text-muted`; references such as "VIBE-42" in mono teal |
| Hover | Fill or border change only on task screens; the home page may lift cards 4px |

**In the code today**

| Today | Notes | Files |
| --- | --- | --- |
| `.card` + `.card-body` (Account, Billing) | 8px, faint top sheen, `--shadow-soft` | `viberails.css` (Cards) |
| `.menu-card.border-0` (home features) | No border; lifts, sweeps and tilts its icon box on hover | `viberails.css` (Menu Cards, Feature Icon Box) |
| `.install-card` (home install row) | See [Brand and marketing](#brand-and-marketing) | `viberails.css` (Compact Install Cards) |
| Row cards: `vibe-terminal-item`, `vibe-board-list-row`, `vibe-session-item`, `keys-card`, `api-key-card` | Assorted radii and page palettes | `vibe-theme.css`, `vibe-boards.css`, `vibe-keys.css`, `vibe-api-keys.css` |

### Navigation

**Standard**

Use tabs for views of the same context, such as “Overview,” “Activity” and “Settings.” Pair selected styling with `aria-selected`, the corresponding tab panel and appropriate keyboard behavior. Use navigation links with `aria-current="page"` when selecting a different page. Do not use tab roles for ordinary navigation links.

**Header (codified):** `nav.navbar` sticky at the top: the mascot (36px, 32px on phones) and the wordmark centred; signed out, the Sign in pill on the right; signed in, the profile pill on the left and the savings chip, bell and settings on the right. Below 576px the wordmark hides and the sign-in pill shows its icon only. Rendered by `_Layout.cshtml`.

**Section bar (codified):** `nav.app-subnav` under the header, `--subnav-bg`, `--color-border` bottom line, centred links that scroll sideways when crowded. Links (`.app-subnav-link`) are at least 44px tall, use the Section tab style and `--color-text-muted`; hover turns them `--color-text` with a teal underline; the current page is `--color-accent`, bold, with a 2px teal underline and `aria-current="page"`. Signed-in links lead with an icon; home-page links are text only and jump to sections.

**In-page tabs:** sentence case, Manrope 600, 44px tall, a 2px teal underline on the selected tab (the examples' Component categories). Today only Runbooks' Write and Preview use tab roles; Runbooks and Recipes filters are segmented buttons with `aria-pressed`.

There are no breadcrumbs; screens use back links such as "All boards".

### Page header

**Standard (codified):** every signed-in page opens with one page header: an icon tile, the page's name, one sentence of what you can do there, and page-wide actions on the right.

| Part | Value |
| --- | --- |
| Band | `--portal-surface` with a faint teal-to-violet wash, `--color-border`, `--radius-surface`, 12px × 16px padding |
| Icon tile | 40px, `--radius-control`, 10% teal fill, `--color-accent` icon |
| Title | The Page title style (Sora 600, 32px / 1.2), `--color-text` ("Active Terminals", "API Keys"); today 1.4rem at 700 |
| Subtitle | 0.9rem `--color-text-muted`, sentence case ("Connect to your running VibeRails terminal sessions") |
| Actions | Secondary or ghost buttons; below 576px they wrap under the title |

Today: `.vibe-page-header`, `.vibe-header-icon`, `.vibe-header-title`, `.vibe-header-subtitle`, `.vibe-header-actions` in `vibe-theme.css`, on eleven pages, with 8px corners and a legacy teal border.

### Status and feedback

**Standard**

| Component | Convention |
| --- | --- |
| Status badge | Short label such as “Connected,” “In progress” or “Needs attention”; include a symbol when useful |
| Inline alert | Persistent, actionable information beside the affected content |
| Toast | Brief confirmation of a completed action; keep critical recovery information persistent |
| Loading state | Preserve layout, explain the active task and avoid fabricated progress percentages |
| Empty state | Name what is missing and provide the next useful action |
| Dialog | One clear task, concise title, explicit actions and a close control |

Use `role="status"` or a polite live region for confirmations; reserve `role="alert"` for errors needing immediate attention. Avoid repeatedly announcing counters or animation frames.

| Part | Value |
| --- | --- |
| Badge | Pill, 3px × 9px, 11px Manrope, a [status tone](#status-tones), optional 12px icon or 7px dot |
| Inline alert | `--radius-control`, 12px padding, 13px text, 18px leading icon, a tone's text, fill and border |
| Toast | `--color-bg-surface-hover`, `--control-border`, 3px left edge in `--color-accent`, one sentence and an optional Undo |
| Nudge | Bottom-right card: a teal icon tile, a bold question, one line of context, a ghost "Not now" and a small primary action |
| Milestone notice | Bottom-right card with a lime glow: `sparkles` icon and "MILESTONE" in `--color-milestone`, a Sora headline with the number in lime, a teal "See the counter" link, a ghost close button |

**Badges in the app**

| Domain | Labels and tones |
| --- | --- |
| Board card type | `task`, `feature`, `chore`, `research-spike`: Neutral; `bug`: Danger |
| Board card priority | `critical`: Danger; `high`: Warning; `medium`: Active; `low`: Neutral |
| Story points | "5 pt": Neutral |
| Card panel | "Agent running": Success with a pulsing dot; "Deleted": Danger; "Read-only": Neutral with an eye icon and an explanatory `title` |
| Agent on a terminal or board | "claude", "codex": Active |
| Terminal connection | "Connected": Success; "Connecting...": Warning; "Disconnected": Neutral; failures: Danger |
| API keys | "Active": Success; "Legacy": Warning; "Revoked" and other states: Neutral; capability chips: Neutral |
| Signing keys | "Active", "Valid": Success; "Invalid": Danger; "Revoked": Neutral |
| Push status | Dot plus sentence: on Success, working Accent (pulsing), off Neutral, blocked Danger, unsupported Warning |

**In the code today**

| Today | Becomes | Files |
| --- | --- | --- |
| `vibe-board-badge` (`type-*`, `priority-*`, `is-points`), `vibe-card-chip` (`-live`, `-danger`, `vibe-board-read-only`) | Badges as mapped above | `vibe-boards.css`, `board.js` |
| `badge badge-cli`, `badge-context-local`, `badge-context-global` | Badges (`badge-context-global` text is 4.2:1 today) | `viberails.css` (Badges) |
| `vibe-status-pill` + `vibe-status-indicator` (Consolas) | Badge with a dot | `vibe-theme.css` (Connection Status Pill) |
| `api-key-status-*`, `api-key-capability`, `keys-status-*` | Badges | `vibe-api-keys.css`, `vibe-keys.css` |
| Bootstrap `alert-success`, `-info`, `-warning`, `-secondary` (light fills) | Inline alerts in the matching tone | Contact, Billing, Signing Keys, Link views |
| `vibe-board-alert`, `vibe-terminal-connection-message`, `rb-notice`, `rc-notice` | Inline alerts (Danger or Neutral) | `vibe-boards.css`, `vibe-portal.css`, page stylesheets |
| `.push-nudge` | Nudge | `viberails.css` (Push nudge) |
| `.ts-toast` (gold today) | Milestone notice in lime | `viberails.css` (Milestone notice), `token-milestone.js` |

**When you change it:** never put a heading or paragraph inside a stock Bootstrap alert; the global `p` color makes it unreadable (see [Known gaps](#known-gaps-in-todays-ui)).

### Empty states

**Standard:** a 1px dashed `--control-border` frame with `--radius-control`, centred: a 26px muted icon, a Sora title naming what is missing ("No API Keys", "Your next idea starts here"), one sentence saying how to fill it, and the primary action when it can be done on this page ("Create board").

Today: `.vibe-empty-state` with an `i.empty-state-mark` or `img.vibe-empty-state-icon` and an `h4` in `vibe-theme.css` (signed-in pages add the portal fill and border in `vibe-portal.css`); page variants `keys-empty`, `rc-empty`, `vibe-card-log-empty`.

### Dialogs

**Standard:** prefer native `<dialog>` for modal tasks. Focus an appropriate control when opened, contain keyboard focus, support Escape and return focus to the opener. Destructive confirmations should identify the object and consequence, with the safe action easy to choose.

| Part | Value |
| --- | --- |
| Dialog | `--color-bg-surface`, `--control-border`, `--radius-surface`, 24px padding, up to 420px wide (600px for long forms such as Create an API key), `--shadow-dialog` over a `#080810c9` backdrop |
| Title | Sora 600, 21px; a question for confirmations ("Sign out?", "Delete this sample card?") |
| Body | One or two sentences in `--color-text-muted` naming the object and what happens |
| Actions | Right-aligned: the safe action first ("Cancel", "Keep card"), then the action named for what it does ("Sign out", "Delete sample card"), destructive when it ends or removes something |
| Disclosure | `<details>` with a `--color-border` top line, 13px summary, muted body |

**In the code today**

| Today | Becomes | Files |
| --- | --- | --- |
| `dialog.vibe-confirm-dialog` via `window.vibeRails.confirm` or `form[data-confirm-*]` | Confirmation dialog (keeps its API) | `confirmation.js`, `vibe-theme.css` |
| `dialog.api-key-dialog` (Create an API key) | Form dialog (600px) | `vibe-api-keys.css` |
| `rb-dialog`, `rc-dialog`, `share-dialog`, `share-access-dialog`, `vibe-sharing-dialog`, `vibe-board-artifact-dialog`, `home-image-dialog` | Dialog | Page stylesheets |
| Board card drawer (`aside.vibe-card-panel[role=dialog]`), terminal PIN and composer overlays | Keep their layouts; apply the dialog surface, focus and Escape rules | `vibe-boards.css`, `viberails.css`, `vibe-theme.css` |

Bootstrap modals and offcanvas are not used.

### Brand and marketing

These patterns belong to the public home page and the header. Task screens do not use them.

| Pattern | Rule |
| --- | --- |
| Mascot | The green llama (`wwwroot/assets/img/logo.png`, 256px; `logo.svg` for larger sizes). Full color; never recolor or outline it. Its greens are not interface colors |
| Wordmark | "Vibe Rails" in the Wordmark style, 0.75rem right of the mascot |
| Hero | `h1` "Vibe Rails" at the Marketing hero size in `--brand-gradient` with its shine; the Hero lead below; a Kicker introducing the install cards; soft violet and teal glows behind |
| Section opener | An Eyebrow ("Works with your stack") above a Section heading |
| Install card | A card with a header (platform mark and name), the command in the Command style on a recessed well with a copy icon button, and for Windows a small primary download button. Platform marks are tinted: Windows `#59b3f0`, Linux `#ee7339`, Apple and GitHub `#f5f5f7`. The GitHub card is one link |
| Feature card | Icon tile, centred component heading, description, a teal-ticked list, and a bold closing line |
| Partner tiles | Provider logos in their own colors in "Works with your stack" |
| Token savings | The hero chip and meter count tokens saved with tabular figures and digit separators; milestones switch to the lime milestone tone |

### Responsive behavior and motion

Start with a single column. Add columns only when controls and labels fit naturally. At narrow widths, stack form groups and wrap action rows; avoid horizontal page scrolling. Test at 320px, 375px, 768px and 1024px, including enlarged text and long labels.

Keep menus and dialogs inside the viewport. Do not hide essential actions behind hover. Maintain 16px editable-field text on small screens and adequate touch targets. Preserve visible focus when sticky navigation or overlays are present.

Use the existing 150ms transition duration for short color and border changes. Avoid decorative movement in task-heavy screens, and remove nonessential transitions and animation for `prefers-reduced-motion: reduce`.

### Accessible example

The markup below illustrates semantics. The proposed classes describe a future shared component layer; they are not existing application classes.

```html
<label for="board-priority">Priority</label>
<select id="board-priority" name="priority" aria-describedby="priority-help">
  <option>Normal</option>
  <option>High</option>
  <option>Low</option>
</select>
<p id="priority-help">Choose the priority for this board.</p>
<button type="submit" class="vr-action vr-action-primary">Create board</button>
```

## Voice and copy

- Speak plainly, developer to developer. The hero says what the product is in one sentence: "A developer and enterprise friendly framework that keeps vibe coding from going off the rails."
- The rails pun is the brand's one joke. Do not add more train language.
- Write the product as two words, Vibe Rails, as the header, page titles and hero do. Some app copy runs it together ("VibeRails"); use two words in new copy.
- Buttons are verb first and sentence case. Page and empty-state titles may use Title Case ("Active Terminals", "No API Keys").
- Help, status and error text are full sentences that say what to do next: "Notifications are unavailable. Try Refresh.", "This board has no lanes yet. Add lanes in the desktop app."
- Address the reader as "you"; the team is "we" ("We'll reply to the email address you provided.").
- No emoji. Text glyphs are rare: ↗ on "Send message", ⏎ on the terminal composer's Send.
- Show token counts in full with separators and tabular figures; only the header chip abbreviates ("1.28M"), with the exact total in its tooltip.

## Iconography

- New and updated UI uses [Lucide](https://lucide.dev/) icons: 24-grid strokes with `stroke="currentColor"`, 2px wide (1.8px in the header), 15–18px beside text, `aria-hidden="true"`. In Razor views, inline the SVG as the header already does rather than adding a script. The examples load Lucide from a CDN.
- Existing screens use Font Awesome 6.5.1 Free (`fas`, `fa-solid`, `far`), loaded on every page by `_Layout.cshtml`. Keep it until a screen is updated, and do not mix the two libraries within one component.
- Brand marks (Windows, Ubuntu, Apple, GitHub) come from Font Awesome Brands; Lucide has none. Partner and provider logos are image files in `wwwroot/assets/img`.
- Icon-only controls carry an accessible name; decorative icons are hidden from assistive technology.

## Accessibility

### Adoption checks

Before adopting a component, verify keyboard operation, focus visibility, contrast in every active state, error recovery, reduced motion and narrow-screen wrapping. Aim for at least [4.5:1 ordinary text contrast and 3:1 large text contrast](https://www.w3.org/WAI/WCAG22/Understanding/contrast-minimum.html), and [3:1 for essential visual control boundaries or state indicators](https://www.w3.org/WAI/WCAG22/Understanding/non-text-contrast.html). Disabled controls are exempt from those contrast thresholds but must remain understandable in context.

Reuse existing Bootstrap primitives and consolidate shared rules deliberately. Apply the system incrementally to customer-facing features; this guideline does not change routes, authentication, data access or application behavior.

### Known gaps in today's UI

Fix these where you touch them; never copy them.

| Gap | Where | Fix |
| --- | --- | --- |
| A paragraph inside a stock Bootstrap alert renders near-white on pale green (about 1.2:1), because the global `p { color }` wins | Contact page, message sent | Use an inline alert in the Success tone |
| Hovered and focused `btn-primary` take Bootstrap's blue border (`#0a58ca`), disabled ones `#0d6efd` | Every primary button | Set `border-color` in every state |
| White on flat `#8b5cf6` is 4.23:1 | `btn-terminal-connect`, the copy button on hover | `--action-primary-bg` |
| `--color-secondary` text is 4.2:1 on the canvas | `badge-context-global` | Neutral tone |
| Control borders in `--color-border` are 1.5:1; the `.form-select` chevron is 1.7:1 | Shared fields and selects | `--control-border`, a visible arrow |
| The shared focus ring alone is 1.3:1 | `site.css` | `--focus-ring` outline |
| Disabled `.form-select` falls back to Bootstrap's light fill | Shared selects | Dark disabled style |

## Implementation map

| Concern | Where |
| --- | --- |
| Tokens | `:root` in [`viberails.css`](VibeRails-Front/wwwroot/css/viberails.css#L6); `--portal-surface*` on `body.portal-page` in `vibe-portal.css` |
| Load order on every page | `bootstrap.min.css` (5.3.3), Font Awesome 6.5.1, `viberails.css`, `site.css`, `vibe-theme.css`, `notifications.css`; `vibe-portal.css` on signed-in pages except Home ([`_Layout.cshtml`](VibeRails-Front/Views/Shared/_Layout.cshtml#L32)) |
| Page stylesheets | Loaded through `@section Head`: `vibe-boards.css`, `vibe-api-keys.css`, `vibe-keys.css`, `vibe-runbooks.css`, `vibe-recipes.css`, `session-sharing.css`, `card-sharing.css`, `share-access.css`, `review-requests.css`, `contact.css`, `home-media.css` and others in `wwwroot/css` |
| Header and section bar | `_Layout.cshtml`, `_Notifications.cshtml`, `Components/PersonalTokenSavings` |
| Shared scripts | `confirmation.js` (confirm dialog), `notifications.js`, `token-milestone.js`, `push-notifications.js` |
| Other shells | `_LinkLayout.cshtml` (device link), shared card and shared session pages: same tokens, own stylesheets |

Defined but unused, so do not build on them: Bootstrap `.table` and modal overrides, `.form-switch`, breadcrumb styles, `.terminal-mock`, `.rails-divider`, `.btn-danger-outline`, `Views/Shared/_Layout.cshtml.css` (never linked) and the Tom Select files under `wwwroot/assets/tom-select`.

## Decisions made while merging

The approved choices and standards were kept as written. Where the target was silent, these choices filled the gap; change any of them here.

1. One radius scale: 4px for controls and small tiles, 12px for every surface (cards, dialogs, menus, the notifications panel, page headers), pills for status, round for header buttons and avatars.
2. The header keeps its round icon buttons and pills as established navigation treatments; icon buttons inside content are 4px squares.
3. New UI uses Lucide shapes, inlined as SVG; Font Awesome stays for existing screens and brand marks.
4. Cyan tints fold into teal, and the savings chip icon uses the milestone lime.
5. The notification count becomes a teal pill with dark text.
6. The global section bar keeps short uppercase labels; in-page tabs and field labels use sentence case.
7. Every app badge maps to one status tone (see [Badges in the app](#status-and-feedback)): high priority and legacy keys are orange, medium priority and agents are violet.
8. Stock Bootstrap alerts become dark inline alerts in the matching tone.
9. This file's sizes win over the examples' smaller demo sizes.
10. `DESIGN_SYSTEM.html` is now a plain HTML page that LLMs and people can read directly. The Codex viewer plumbing (host messaging, tooltip helper and the tweak-panel hook) is gone; the approved values live in its `:root` token block, which mirrors [Tokens](#tokens).

## References

- [Vibe Rails public website](https://viberails.ai/) — public brand and installation experience.
- [DESIGN_SYSTEM.html](DESIGN_SYSTEM.html) — interactive examples and the full token reference.
- [Shared tokens and base styling](VibeRails-Front/wwwroot/css/viberails.css#L6) — existing colors, fonts and geometry.
- [Shared button styling](VibeRails-Front/wwwroot/css/viberails.css#L982) and [form styling](VibeRails-Front/wwwroot/css/viberails.css#L1339) — current component baseline.
- [Shared layout](VibeRails-Front/Views/Shared/_Layout.cshtml#L32) — font and stylesheet loading.
- [Terminal and page styling](VibeRails-Front/wwwroot/css/vibe-theme.css) — existing page header, empty state, status pill and confirm dialog treatments.
- [Notifications](VibeRails-Front/wwwroot/css/notifications.css) — the popover pattern.
- [Board styling](VibeRails-Front/wwwroot/css/vibe-boards.css) — board inputs, badges and chips.
- [WCAG 2.2](https://www.w3.org/TR/WCAG22/) — accessibility criteria for contrast, keyboard access, labels, focus and target size.
