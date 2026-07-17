using VibeRails.DTOs;
using VibeRails.Utils;

namespace VibeRails.Services.LlmClis.Launchers
{
    public interface IOpencodeLlmCliLauncher : IBaseLlmCliLauncher { }

    public class OpencodeLlmCliLauncher : BaseLlmCliLauncher, IOpencodeLlmCliLauncher
    {
        public override LLM LlmType => LLM.OpenCode;
        public override string CliExecutable => "opencode";
        public override string ConfigEnvVarName => "XDG_CONFIG_HOME";
        protected override string ConfigSubdirectory => "opencode";

        public override Dictionary<string, string> GetEnvironmentVariables(string envName)
        {
            var envBasePath = ParserConfigs.GetEnvPath();
            var envDir = EnvironmentNameValidator.ResolveEnvironmentDirectory(envBasePath, envName);

            // OPENCODE_CONFIG_DIR is additive, so it does not isolate the environment from the
            // user's global config. OpenCode resolves its standard config directory as
            // $XDG_CONFIG_HOME/opencode; leave XDG_DATA_HOME unchanged so auth remains global.
            return new Dictionary<string, string>
            {
                [ConfigEnvVarName] = envDir
            };
        }
    }
}
