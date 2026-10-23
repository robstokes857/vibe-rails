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
        UnknownError,

        /// <summary>
        /// <c>core.hooksPath</c> resolves outside the repository's git directory and the caller
        /// did not confirm that directory. <see cref="HookInstallationResult.Details"/> holds it.
        /// </summary>
        HooksDirectoryOutsideGitDirectory
    }
}
