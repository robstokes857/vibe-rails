using VibeRails.DTOs;

namespace VibeRails.Services.LlmClis.Launchers
{
    public interface IAntigravityLlmCliLauncher : IBaseLlmCliLauncher { }

    public class AntigravityLlmCliLauncher : BaseLlmCliLauncher, IAntigravityLlmCliLauncher
    {
        public override LLM LlmType => LLM.Antigravity;

        // Google Antigravity's CLI binary is `agy` (not "antigravity"). Every other CLI's enum
        // name lowercased equals its executable; agy is the exception, so the in-app PTY path
        // maps it explicitly in CommandService.PrepareSession.
        public override string CliExecutable => "agy";

        // Launch-flag-only, like Copilot. agy exposes sandbox/permissions as launch flags and
        // has no documented per-environment config-dir env var we can target (the Node-era
        // Gemini CLI used XDG_CONFIG_HOME; the Go-based agy exposes no verified equivalent), so
        // we inject no config env var and isolate nothing. Revisit if Google documents one.
        public override string ConfigEnvVarName => "";
        protected override string ConfigSubdirectory => "antigravity";

        public override Dictionary<string, string> GetEnvironmentVariables(string envName)
            => new Dictionary<string, string>();
    }
}
