# VibeRails Security Scan Report

Date: 2026-07-21
Scope: Read-only static security review of the `vibe-rails` codebase.
Result: No files were modified during the scan.

## Summary

No obvious critical unauthenticated remote compromise issues stood out. The app is localhost-bound and has a strong two-token auth design. The main risks are around:

- path containment gaps,
- unsafe string-built git/process arguments,
- one real DOM XSS sink,
- missing browser security headers,
- sensitive terminal data handling,
- and a few latent/dead endpoints that would become risky if re-enabled.

## Priority findings

| Severity | Issue | Location | Recommendation |
|---|---|---|---|
| Medium | Path traversal/containment gap in Claude/Codex settings APIs | `VibeRails/Routes/LlmSettingsRoutes.cs:17-88`, `ClaudeLlmCliEnvironment.cs:211-215`, `CodexLlmCliEnvironment.cs:118-122` | Route `envName` through `EnvironmentNameValidator.ResolveEnvironmentDirectory(...)` before building paths. |
| Medium | Git argument injection risk in sandbox operations | `VibeRails/Services/SandboxService.cs:146-180, 356, 374-384` | Replace string-interpolated `ProcessStartInfo.Arguments` with `ArgumentList`/`GitProcessRunner`, add `--` separators, validate branch names. |
| Medium | Possible arbitrary file read through sandbox diff path handling | `VibeRails/Services/SandboxService.cs:171-194` | Reject rooted paths/`..`, enforce containment under `sandbox.Path`, cap file size, only read regular files. |
| Medium | DOM XSS via unescaped API/error output | `VibeRails/wwwroot/js/modules/cli-launcher.js:30, 42-47, 58` | Use `textContent`/DOM nodes or `escapeHtml()` for `response.message`, `standardOutput`, `standardError`, `error.message`, `cliType`. |
| Medium | Missing browser security headers | `VibeRails/Program.cs:278-334` | Add CSP, `X-Content-Type-Options: nosniff`, `X-Frame-Options`/`frame-ancestors`, `Referrer-Policy`, and `Cache-Control: no-store` for authenticated HTML/API. |
| Medium | Session transcript can be sent to cloud summary service without a strong local gate/redaction | `VibeRails/Services/Integrations/VibeCodeRemote/SummaryService.cs:19-33` | Require remote access/API key opt-in before sending; run secret screening/redaction on transcripts; consider POST-only + user confirmation. |
| Medium-low | Recursive sandbox delete lacks containment check | `VibeRails/Services/SandboxService.cs:107-123` | Verify DB `sandbox.Path` is still under the configured sandboxes root before deleting. |
| Medium-low | Silent OSC 52 clipboard writes enabled | `VibeRails/wwwroot/js/modules/vibe-terminal.js:242-248` | Remove global `ClipboardAddon`, or gate clipboard writes behind confirmation/user-initiated copy. |
| Low | Dead/latent proxy endpoints; if fixed they become authenticated SSRF with `apiKey` in query string | `VibeRails/Routes/ProxyRoutes.cs:14-54`, `MapRegisterServices.cs:269-271` | Delete unused routes or register validator safely, move API key to header, allowlist upstream URLs, expand log redaction. |
| Low | Bootstrap redirect validation misses backslash variant | `VibeRails/Routes/AuthRoutes.cs:34-38` | Reject redirects containing `\` or parse as a strict relative URI. |
| Low | Broad localhost CORS with credentials | `VibeRails/Program.cs:187-200` | Narrow allowed origins to the actual bound port + VS Code webview; keep tab-token requirement as primary defense. |
| Low | Cloud API key/state DB/log files rely on default filesystem permissions | `~/.vibe_rails` handling in `Config.cs`, `FileService.cs`, terminal/session storage | Apply restrictive Unix permissions to `~/.vibe_rails`, `settings.json`, `state.db`, logs; consider OS keychain/DPAPI for API key. |
| Info | Full-privilege tool tokens injected into managed terminal environments | `TerminalRunner.cs`, `LocalToolApiContext.cs`, `TerminalTabHostService.cs` | Document as accepted risk or scope tool tokens to fewer capabilities. |

## Notable strengths

- Strong token generation and constant-time comparisons.
- Single-use expiring bootstrap code.
- `HttpOnly` + `SameSite=Lax` session cookie.
- Tab token required on `/api`, `/mcp`, and WebSocket paths.
- Most SQL is parameterized.
- Most frontend rendering uses `escapeHtml`/`textContent`.
- Terminal output is rendered through xterm.js rather than HTML.
- MCP shell/web-research tools are not currently exposed.
- Many process launches correctly use `ArgumentList` and shell-arg sanitizers.

## Recommended first fixes

1. Fix the settings path traversal and sandbox path containment issues.
2. Replace string-built git arguments in `SandboxService` with argument-list execution.
3. Fix `cli-launcher.js` XSS and add response security headers.
4. Gate/redact the summary upload flow.
5. Remove or repair the dead proxy routes.
6. Disable or confirm-gate OSC 52 clipboard writes.

## Limitations

This was a static read-only review, not a runtime pentest. Auth, route decoding, WebSocket behavior, and tests were not dynamically exercised. Third-party/minified frontend bundles and submodules were reviewed at integration points, not line-by-line.
