/**
 * Central home for the strings and timings shared across the extension.
 *
 * Nothing here may import `vscode` — `backend-manager.ts` pulls from this file and
 * stays runnable outside the extension host.
 */

// --- Commands -------------------------------------------------------------

export const COMMAND_OPEN = 'viberails.open';
export const COMMAND_STOP = 'viberails.stop';
export const COMMAND_TEST_CONNECTION_INFO = 'viberails._test.getConnectionInfo';

// --- Webview / output -----------------------------------------------------

/**
 * Also hardcoded in the `when` clauses of the keybinding contributions in
 * package.json (those cannot reference constants). Keep the two in sync.
 */
export const WEBVIEW_VIEW_TYPE = 'viberailsDashboard';
export const OUTPUT_CHANNEL_NAME = 'VibeRails Backend';

// --- Auth -----------------------------------------------------------------

/** Header the backend accepts in place of the session cookie (webviews can't set cookies). */
export const SESSION_TOKEN_HEADER = 'viberails_session';
/** Required alongside the session token on every `/api/*` call. */
export const TAB_TOKEN_HEADER = 'viberails_tab';
/** Cookie name the bootstrap response sets; same value as the session header. */
export const SESSION_COOKIE_NAME = 'viberails_session';

// --- Backend process contract ---------------------------------------------

/** Prefix of the single stdout line the backend prints with its bootstrap URL. */
export const BOOTSTRAP_STDOUT_PREFIX = 'vs-code-v1=';
export const LAUNCH_ARG_VS_CODE = '--vs-code-v1';

// --- Backend HTTP paths ---------------------------------------------------

/** Unauthenticated readiness probe (whitelisted in CookieAuthMiddleware). */
export const HEALTH_PATH = '/health';
/** Auth-gated; returns 200 and then stops the host ~50ms later. */
export const SHUTDOWN_PATH = '/api/v1/shutdown';

// --- Settings -------------------------------------------------------------

export const CONFIG_SECTION = 'viberails';
export const CONFIG_STARTUP_TIMEOUT_MS = 'startupTimeoutMs';

// --- Test hooks -----------------------------------------------------------

/** Workspace-folder fallback for the smoke test, which runs VS Code with no folder open. */
export const SMOKE_WORKSPACE_ENV = 'VIBERAILS_SMOKE_WORKSPACE';

// --- Timings --------------------------------------------------------------

export const DEFAULT_STARTUP_TIMEOUT_MS = 30000;
/** Keep in sync with the bounds declared for viberails.startupTimeoutMs in package.json. */
export const MIN_STARTUP_TIMEOUT_MS = 5000;
export const MAX_STARTUP_TIMEOUT_MS = 600000;

export const BOOTSTRAP_REQUEST_TIMEOUT_MS = 5000;
export const BOOTSTRAP_RETRY_ATTEMPTS = 3;
export const BOOTSTRAP_RETRY_DELAY_MS = 500;

export const HEALTH_CHECK_ATTEMPTS = 3;
export const HEALTH_CHECK_DELAY_MS = 500;
export const HEALTH_CHECK_TIMEOUT_MS = 2000;

/** How long to wait for the POST /api/v1/shutdown response itself. */
export const GRACEFUL_SHUTDOWN_REQUEST_TIMEOUT_MS = 2000;
/** How long to wait for the process to exit after a successful shutdown request. */
export const GRACEFUL_SHUTDOWN_EXIT_TIMEOUT_MS = 3000;
/** How long to wait for the process to exit after closing its stdin. */
export const STDIN_CLOSE_EXIT_TIMEOUT_MS = 1000;
