namespace VibeRails.Services.BertBaseClasses;

public interface IBertSettings
{
    string ModelPath { get; }
    string VocabPath { get; }
    string DataDirectory { get; }
}
