using VibeRails.DTOs;

namespace VibeRails.Services.LlmClis.Launchers
{
    public interface IGrokLlmCliLauncher : IBaseLlmCliLauncher { }

    public class GrokLlmCliLauncher : BaseLlmCliLauncher, IGrokLlmCliLauncher
    {
        public override LLM LlmType => LLM.Grok46;
        public override string CliExecutable => "grok";
        public override string ConfigEnvVarName => "";
        protected override string ConfigSubdirectory => "grok";

        public override Dictionary<string, string> GetEnvironmentVariables(string envName)
            => new Dictionary<string, string>();
    }
}
