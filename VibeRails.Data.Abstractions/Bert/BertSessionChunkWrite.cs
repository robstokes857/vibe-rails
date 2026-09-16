namespace VibeRails.Services.BertV2;

public readonly record struct BertSessionChunkWrite(int ChunkIndex, string Text, float[] Embedding);
