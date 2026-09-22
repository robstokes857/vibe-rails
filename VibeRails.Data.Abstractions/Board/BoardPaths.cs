namespace VibeRails.Services.Board;

public static class BoardPaths
{
    public static StringComparison ProjectPathComparison => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static string NormalizeProjectPath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
}
