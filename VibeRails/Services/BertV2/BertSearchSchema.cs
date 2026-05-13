namespace VibeRails.Services.BertV2;

internal static class BertSearchSchema
{
    public const string DatabaseFileName = "bert_user_text_vectors.db";
    public const string DocumentTableName = "bert_input_documents";
    public const string VectorTableName = "vec_bert_input_documents";
    public const int EmbeddingDimension = 384;

    // Session-level aggregate embeddings (sliding-window chunks over a full session's
    // concatenated user inputs). Lives in the same DB file as the per-message tables.
    public const string SessionDocumentTableName = "bert_session_documents";
    public const string SessionVectorTableName = "vec_bert_session_documents";
}
