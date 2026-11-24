namespace VibeRails.Services.Integrations.VibeCodeRemote;

/// <summary>
/// How VibeRails tells viberails.ai where something came from: the repository's folder name and
/// this computer's name, never a path. Board publication and Send to handoffs (VIBE-363) share
/// these rules so viberails.ai sees one convention.
/// </summary>
public static class RemoteSourceNames
{
    public const int MaxRepositoryChars = 120;
    public const int MaxComputerChars = 100;

    /// <summary>The last folder of <paramref name="projectPath"/>, at most 120 characters.</summary>
    public static string Repository(string projectPath)
    {
        var name = Path.GetFileName(projectPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return name.Length <= MaxRepositoryChars ? name : name[..MaxRepositoryChars];
    }

    /// <summary>This computer's name, at most 100 characters.</summary>
    public static string Computer()
    {
        var name = Environment.MachineName;
        return name.Length <= MaxComputerChars ? name : name[..MaxComputerChars];
    }
}
