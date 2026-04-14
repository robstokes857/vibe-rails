namespace VibeRails.Services.BertV2;

internal static class BertSearchSchema
{
    public const string DatabaseFileName = "bert_user_text_vectors.db";
    public const string DocumentTableName = "bert_input_documents";
    public const string VectorTableName = "vec_bert_input_documents";
    public const int EmbeddingDimension = 384;
}
