namespace VibeRails.Utils
{
    /// <summary>
    /// Parses the small set of argv shapes vb supports. Anything unrecognized is silently ignored.
    /// Surviving shapes:
    ///   vb                                           → web dashboard, browser opens, server stops on browser close
    ///   vb --web                                     → same as bare invocation
    ///   vb --git-guard                               → focused staged-change preflight dashboard
    ///   vb --version | -v                            → print version and exit
    ///   vb --help | -h                               → print help and exit
    ///   vb --vs-code-v1 [--parent-pid &lt;pid&gt;]         → VS Code extension / new-tab child mode
    ///   vb --env &lt;name|llm&gt; --workdir &lt;path&gt; [-- ...]  → LMBootstrap (CLI + web concurrent)
    /// </summary>
    public static class ArgumentParser
    {
        public static ParsedArgs Parse(string[] args)
        {
            var parsed = new ParsedArgs();

            // Bare `vb` or explicit `--web` opens the dashboard. Earlier code matched
            // any arg containing "web", which incorrectly fired on `--workdir C:\my-website`
            // or `--env webapp`.
            if (args.Length == 0
                || args.Any(a => a.Equals("--web", System.StringComparison.OrdinalIgnoreCase))
                || args.Any(a => a.Equals("--git-guard", System.StringComparison.OrdinalIgnoreCase)))
            {
                parsed.OpenBrowser = true;
                parsed.ShutdownOnBrowserClose = true;
            }

            if (args.Length == 0)
            {
                return parsed;
            }

            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];

                switch (arg)
                {
                    case "--help":
                    case "-h":
                        parsed.Help = true;
                        break;

                    case "--version":
                    case "-v":
                        parsed.Version = true;
                        break;

                    case "--web":
                        // OpenBrowser/ShutdownOnBrowserClose already set above; nothing extra to do.
                        break;

                    case "--git-guard":
                        parsed.IsGitGuardMode = true;
                        break;

                    case "--vs-code-v1":
                        parsed.IsVsCodeMode = true;
                        break;

                    case "--parent-pid":
                        // Value is consumed directly from argv by ChildParentWatchdogService;
                        // the parser only needs to skip past it so it doesn't get treated as a separate arg.
                        if (i + 1 < args.Length) i++;
                        break;

                    case "--env":
                        parsed.IsLMBootstrap = true;
                        if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                        {
                            parsed.LMBootstrapCli = args[++i];
                        }
                        break;

                    case "--workdir":
                        if (i + 1 < args.Length)
                        {
                            parsed.WorkDir = args[++i];
                        }
                        break;

                    case "--job-run":
                        if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                        {
                            parsed.JobRunId = args[++i];
                        }
                        break;

                    case "--max-runtime":
                        if (i + 1 < args.Length && int.TryParse(args[i + 1], out var maxRuntime) && maxRuntime > 0)
                        {
                            parsed.MaxRuntimeMinutes = maxRuntime;
                            i++;
                        }
                        break;

                    case "--":
                        // Everything after `--` is passthrough to the underlying LLM CLI.
                        parsed.ExtraArgs = args.Skip(i + 1).ToArray();
                        return parsed;

                    default:
                        if (arg.StartsWith("--parent-pid=", System.StringComparison.OrdinalIgnoreCase))
                        {
                            // `--parent-pid=<pid>` form — also consumed by ChildParentWatchdogService.
                            break;
                        }
                        // Unknown arg: silently ignore.
                        break;
                }
            }

            return parsed;
        }
    }
}
