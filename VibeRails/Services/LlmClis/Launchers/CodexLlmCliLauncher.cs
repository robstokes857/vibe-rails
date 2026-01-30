using VibeRails.DTOs;

namespace VibeRails.Services.LlmClis.Launchers
{
    public interface ICodexLlmCliLauncher : IBaseLlmCliLauncher { }

    public class CodexLlmCliLauncher : BaseLlmCliLauncher, ICodexLlmCliLauncher
    {
        public override LLM LlmType => LLM.Codex;
        public override string CliExecutable => "codex";
        public override string ConfigEnvVarName => "CODEX_HOME";
        protected override string ConfigSubdirectory => "codex";
    }
}
