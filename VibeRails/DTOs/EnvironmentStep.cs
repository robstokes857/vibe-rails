namespace VibeRails.DTOs
{
    /// <summary>
    /// When a step runs relative to the environment's LLM.
    /// </summary>
    public enum EnvironmentStepPhase
    {
        /// <summary>Before the LLM launches. A non-zero exit aborts the launch.</summary>
        PreLaunch = 0,

        /// <summary>
        /// After the PTY exits. For a Worker or Job that is "the agent finished"; for a browser tab
        /// it is "the tab closed".
        /// </summary>
        PostExit = 1,

        /// <summary>
        /// Never runs on its own. Executes only when the environment's Initial Message references
        /// it via <c>{{step:&lt;id&gt;}}</c>, hidden and captured, with its output substituted into
        /// the prompt. Lifecycle runs query by phase, so these are excluded automatically.
        /// </summary>
        Manual = 2
    }

    /// <summary>
    /// One shell command attached to an environment, run in its own native terminal window before
    /// the LLM launches or after its PTY exits. Deliberately outside the PTY pipeline: the user
    /// watches it in a real window, one step at a time.
    /// </summary>
    public class EnvironmentStep
    {
        /// <summary>
        /// GUID string, generated client-side at step creation and preserved across saves (the
        /// save path is delete-all + reinsert, so stability comes from round-tripping the value,
        /// not from the row surviving). {{step:&lt;id&gt;}} prompt references rely on this.
        /// </summary>
        public string Id { get; set; } = "";
        public int EnvironmentId { get; set; }
        public EnvironmentStepPhase Phase { get; set; } = EnvironmentStepPhase.PreLaunch;

        /// <summary>0-based, unique within (EnvironmentId, Phase). Assigned from array order on save.</summary>
        public int Position { get; set; }

        /// <summary>Optional label. Blank falls back to the command's first line in the UI and in logs.</summary>
        public string Name { get; set; } = "";

        public string Command { get; set; } = "";

        /// <summary>Start the window minimized so it does not steal focus. Windows-only; ignored elsewhere.</summary>
        public bool StartMinimized { get; set; }

        public int TimeoutSeconds { get; set; } = DefaultTimeoutSeconds;
        public bool Enabled { get; set; } = true;
        public DateTime CreatedUTC { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedUTC { get; set; } = DateTime.UtcNow;

        public const int DefaultTimeoutSeconds = 600;
        public const int MinTimeoutSeconds = 1;
        public const int MaxTimeoutSeconds = 3600;

        /// <summary>What to call this step in a toast, a log line, or a progress row.</summary>
        public string DisplayName =>
            !string.IsNullOrWhiteSpace(Name)
                ? Name.Trim()
                : FirstCommandLine();

        private string FirstCommandLine()
        {
            foreach (var line in Command.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0)
                    return trimmed.Length <= 60 ? trimmed : trimmed[..60] + "…";
            }

            return "(empty step)";
        }
    }
}
