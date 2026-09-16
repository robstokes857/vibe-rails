namespace VibeRails.Data.Abstractions;

/// <summary>A storage operation failed without exposing a provider-specific exception to callers.</summary>
public sealed class StorageException(string message, bool isTransient, Exception innerException)
    : Exception(message, innerException)
{
    /// <summary>True when retrying later may succeed, such as temporary writer contention.</summary>
    public bool IsTransient { get; } = isTransient;
}
