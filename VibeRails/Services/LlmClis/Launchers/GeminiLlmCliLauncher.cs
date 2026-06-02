using VibeRails.DTOs;
using VibeRails.Utils;

namespace VibeRails.Services.LlmClis.Launchers
{
    public interface IGeminiLlmCliLauncher : IBaseLlmCliLauncher { }

    public class GeminiLlmCliLauncher : BaseLlmCliLauncher, IGeminiLlmCliLauncher
    {
        public override LLM LlmType => LLM.Gemini;
        public override string CliExecutable => "gemini";
        public override string ConfigEnvVarName => "XDG_CONFIG_HOME"; // Gemini uses XDG spec
        protected override string ConfigSubdirectory => "gemini";

        // Override to set XDG environment variables for Gemini. Must be `override` (not
        // `new`): callers reach launchers through IBaseLlmCliLauncher, so a `new` shadow
        // would dispatch to the base single-var method and silently drop Gemini's XDG set.
        public override Dictionary<string, string> GetEnvironmentVariables(string envName)
        {
            var envBasePath = ParserConfigs.GetEnvPath();
            // Containment guard (matches the base): envName can arrive unvalidated from the
            // launch routes, so resolve it under the envs root before building XDG paths.
            var envDir = EnvironmentNameValidator.ResolveEnvironmentDirectory(envBasePath, envName);
            var geminiBasePath = Path.Combine(envDir, ConfigSubdirectory);

            return new Dictionary<string, string>
            {
                ["XDG_CONFIG_HOME"] = Path.Combine(geminiBasePath, "config"),
                ["XDG_DATA_HOME"] = Path.Combine(geminiBasePath, "data"),
                ["XDG_CACHE_HOME"] = Path.Combine(geminiBasePath, "cache"),
                ["XDG_STATE_HOME"] = Path.Combine(geminiBasePath, "state")
            };
        }
    }
}
