using VibeRails.DTOs;

namespace VibeRails.Services.LlmClis.Launchers
{
    public interface ICopilotLlmCliLauncher : IBaseLlmCliLauncher { }

    public class CopilotLlmCliLauncher : BaseLlmCliLauncher, ICopilotLlmCliLauncher
    {
        public override LLM LlmType => LLM.Copilot;
        public override string CliExecutable => "copilot";
        public override string ConfigEnvVarName => "";
        protected override string ConfigSubdirectory => "copilot";

        public new Dictionary<string, string> GetEnvironmentVariables(string envName)
            => new Dictionary<string, string>();
    }
}
