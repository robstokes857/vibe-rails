namespace VibeRails.Services
{
    public class HookInstallationResult
    {
        public bool Success { get; init; }
        public string? ErrorMessage { get; init; }
        public HookInstallationError? ErrorType { get; init; }
        public string? Details { get; init; }

        public static HookInstallationResult Ok() => new() { Success = true };

        public static HookInstallationResult Fail(HookInstallationError errorType, string message, string? details = null)
            => new()
            {
                Success = false,
                ErrorType = errorType,
                ErrorMessage = message,
                Details = details
            };
    }

    public enum HookInstallationError
    {
        HooksDirectoryNotFound,
        HooksDirectoryCreationFailed,
        PermissionDenied,
        FileReadError,
        FileWriteError,
        ChmodExecutionFailed,
        ScriptResourceNotFound,
        PartialInstallationFailure,
        UnknownError
    }
}
