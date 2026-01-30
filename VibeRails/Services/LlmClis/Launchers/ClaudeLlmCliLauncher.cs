using VibeRails.DTOs;

namespace VibeRails.Services.LlmClis.Launchers
{
    public interface IClaudeLlmCliLauncher : IBaseLlmCliLauncher { }

    public class ClaudeLlmCliLauncher : BaseLlmCliLauncher, IClaudeLlmCliLauncher
    {
        public override LLM LlmType => LLM.Claude;
        public override string CliExecutable => "claude";
        public override string ConfigEnvVarName => "CLAUDE_CONFIG_DIR";
        protected override string ConfigSubdirectory => "claude";
    }
}
