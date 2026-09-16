namespace VibeRails.Services.Board;

public static class BoardPaths
{
    public static string NormalizeProjectPath(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
}
