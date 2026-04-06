namespace VibeRails.Services.BertV2;

public interface IBertV2BgeEmbedder : IDisposable
{
    float[] GenerateEmbedding(string text);
}
