
namespace VibeRails.Services.BertBaseClasses;

public abstract class BertBase
{
    protected readonly string ModelPath;
    protected readonly string VocabPath;
    protected readonly string DataDirectory;

    protected BertBase(IBertSettings settings)
    {
        ModelPath = settings.ModelPath;
        VocabPath = settings.VocabPath;
        DataDirectory = settings.DataDirectory;

        if (!File.Exists(ModelPath))
            throw new FileNotFoundException($"Model not found: {ModelPath}");
        if (!File.Exists(VocabPath))
            throw new FileNotFoundException($"Vocab not found: {VocabPath}");

        Directory.CreateDirectory(DataDirectory);
    }
}
