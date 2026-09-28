namespace VibeRails.Services.Integrations.VibeCodeRemote;

/// <summary>
/// Shared configuration contract for session-data uploads and the Settings capability flag.
/// Keeping the parser here ensures both surfaces accept exactly the same endpoint shapes.
/// </summary>
internal static class DataExportEndpointConfiguration
{
    /// <summary>
    /// The only host session and database uploads may use. Configuration, environment
    /// variables, and settings.json cannot retarget this.
    /// </summary>
    internal const string ExportUrl = "https://viberails.ai/api/v1/data-exports";

    internal static readonly Uri ExportUri = new(ExportUrl);
}
