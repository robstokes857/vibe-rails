# Public README images

The repository and VS Code Marketplace READMEs share this image set. The October 30, 2026
refresh uses the supplied product captures and `VibeRailsDemo.pptx`:

| Published image | Source |
| --- | --- |
| `terminal-session.png` | `base_files/big_claude_session.png` |
| `rules.png` | `base_files/rules.png` |
| `session-handoff.png` | `base_files/sendto.png` |
| `jira-board.png` | `base_files/Jira.png` |
| `code-quality-graph.png` | Embedded code graph PNG in `VibeRailsDemo.pptx` (slide 6). |
| `code-quality-cards.gif` | `base_files/codequality_cards.gif`; already matched the supplied file. |

These are product captures; visible project names, scores, and session text describe the captured
example, not a product guarantee. The graph uses the deck's still image to avoid loading the
16 MB `code_map.gif` in both READMEs. Other image files in this directory are retained for existing
references, but are not part of the refreshed README image set.

The standalone logo remains `vscode-viberails/media/vs-logo.png`, whose bytes match the mascot
embedded in the supplied deck and promo assets. The terminal screenshot has been replaced
because it still displayed the retired logo inside the application navigation.

Refreshed captures use new image paths so cached GitHub and Marketplace images cannot retain
the retired logo.

When refreshing this set, inspect each full-size image and GIF before publishing, keep captions
consistent with what is visible, and update both READMEs together. Marketplace image URLs point
to `main`, so new assets must reach `main` before publishing the extension listing.
