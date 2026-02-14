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

        // Override to set XDG environment variables for Gemini
        public new Dictionary<string, string> GetEnvironmentVariables(string envName)
        {
            var envBasePath = ParserConfigs.GetEnvPath();
            var geminiBasePath = Path.Combine(envBasePath, envName, ConfigSubdirectory);

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
