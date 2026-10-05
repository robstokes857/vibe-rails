namespace VibeRails.Services.BertV2;

/// <summary>Changing the model or chunk format invalidates derived work automatically.</summary>
public static class SearchIndexVersions
{
    public const string Model = "bge-small-en-v1.5:384:uncased:2";
    public const string Chunks = "bge-wordpiece-uncased:window512:target192:overlap24:4";
}
