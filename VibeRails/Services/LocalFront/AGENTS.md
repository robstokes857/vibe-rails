# Local Front mode and the Visual Studio profile

One Start/F5 runs the desktop under the Visual Studio debugger against VibeRails-Front's local
Docker stack (VB-8NI09-170 desktop mode, VB-BB4ED-171 launch). Front's half of the contract is
`docs/local-desktop-development.md` in VibeRails-Front. Setup, troubleshooting and the recorded
acceptance run are in the vibe-books runbook
[local-development/visual-studio.md](../../../../vibe-books/local-development/visual-studio.md).

## Activation

| Variable | Set by | Meaning |
| --- | --- | --- |
| `VIBERAILS_LOCAL_FRONT_ORIGIN` | the "VibeRails + Local Front" profile | The local Front origin, `https://localhost:5164` |
| `VIBERAILS_LOCAL_FRONT_START` | the same profile | `1`: the root backend runs `run.ps1 start` before it starts |
| `VIBERAILS_FRONT_CHECKOUT` | you, per user (optional) | The VibeRails-Front checkout when it is not the sibling folder |

`LocalFrontMode.Current` reads these once per process. Rules, all fail closed:

- **Active** only in a Debug build under `ASPNETCORE_ENVIRONMENT=Development`, with an https
  origin on `localhost` or a loopback address and no user info, path, query or fragment. Any
  other request makes `Program.cs` exit with code 2 before the web host is built. It never runs
  as a normal desktop.
- **Any process that sees the origin variable**, valid or not, publishes nothing to production,
  and every `IHttpClientFactory` client gets `ProductionFrontTripwireHandler`, which fails
  requests to viberails.ai and its subdomains. AI-provider traffic is not affected.
- Nothing is persisted. The next process without the variable is a normal one.
- Terminal tab children and Automation runs inherit the variables from the root and are in
  local mode too. PTY shells and CLIs do not: `TerminalRunner` removes both variables, so a
  `vb` or `dotnet test` typed in a terminal starts normally.

## What changes in a local process

- `VibeRails:FrontendUrl` is replaced in memory with the local origin. Account linking, terminal
  registration, terminal WSS, the HTTP relay, push notifications and summaries go there.
  `Program.cs` refuses a configured FrontendUrl that is neither production nor the local origin.
- The runtime API key (`ParserConfigs.GetApiKey`) is the local key from
  `~/.vibe_rails/local-front-keys.json` (`LocalFrontKeyStore`, keyed by normalized origin).
  Sign-in (`LocalFrontAccountKeyStore`) and the Settings key field read and write only that
  file. settings.json, with the production key, its email and fingerprint, is never written
  or shown.
- Paused, before any lock, metadata change or network call:
  - Board sync, sharing and import (`BoardSyncService`, `BoardSharingService`);
  - remote-board launch polling, session upload, complete backups and token-savings publishing
    (their hosted services are not registered);
  - the one-shot data export;
  - signing-key registration (no credential).

  Queues, links, cursors, checkpoints and saved preferences are untouched, so the next normal run
  carries on. Other VibeRails processes keep their normal behavior.
- The dashboard shows a "Local Front" badge. The sign-in panel accepts only `<origin>/link`.

## The preflight

`LocalFrontStartup.RunAsync` runs in the active root backend only, before
`WebApplication.CreateSlimBuilder`:

1. Resolves the checkout: `VIBERAILS_FRONT_CHECKOUT`, otherwise `VibeRails-Front` beside the
   folder that holds `VibeRails.slnx` above the build output. It checks for `run.ps1`, the
   compose file, `Dockerfile.local` and the project.
2. Takes `~/.vibe_rails/local-front-start.lock`, so concurrent Starts run one after another.
3. Runs `pwsh -NoProfile -NonInteractive -File <front>\run.ps1 start -RepoRoot <front>
   -TimeoutSeconds 300`. That is explicit argv, the checkout as working directory, and a
   30-minute overall bound. Its output goes to the console and the log.
4. On Windows the script is in a kill-on-close job, so Stop Debugging also stops it. Silent
   breakaway keeps a Docker Desktop the script launches out of that job. Cancelling stops the
   script only.
5. Maps exit codes 1–8 to an actionable message and exits with the same code. Exit 0 is followed
   by a GET of `<origin>/dev/login` with default certificate validation from this process.

Stop Debugging stops the desktop; the containers keep running for a fast next Start. Stop them
with `pwsh -File <front>\run.ps1 down`, which keeps the local database. Never use `reset` for
this.

## Tests

`Tests/Services/LocalFront/` covers origin parsing, activation, the tripwire, the key file, the
Settings boundary with a saved production key, local pairing, sync/upload pauses, DI
registration, and preflight orchestration with a fake runner. Frontend coverage is in
`Tests/wwwroot/js/remote-account-link.test.mjs`.
