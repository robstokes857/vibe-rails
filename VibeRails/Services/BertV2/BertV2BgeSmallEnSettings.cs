using VibeRails.Services.BertBaseClasses;
using VibeRails.Utils;

namespace VibeRails.Services.BertV2;

public class BertV2BgeSmallEnSettings : IBertSettings
{
    public string ModelPath { get; }
    public string VocabPath { get; }
    public string DataDirectory { get; }

    public BertV2BgeSmallEnSettings()
    {
        var installRoot = PathConstants.GetInstallDirPath();
        var modelDir = Path.Combine(installRoot, PathConstants.MODELS_SUBDIR, "bertv2");
        ModelPath = Path.Combine(modelDir, "model.onnx");
        VocabPath = Path.Combine(modelDir, "vocab.txt");
        DataDirectory = Path.Combine(installRoot, PathConstants.VECTOR_SUBDIR, "bertv2");
    }
}
