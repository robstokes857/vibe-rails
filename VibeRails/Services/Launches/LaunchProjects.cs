using VibeRails.DB;
using VibeRails.Launches;

namespace VibeRails.Services.Launches;

/// <summary>Known local projects, including closed windows. Names are presentation, never identity.</summary>
public static class LaunchProjects
{
    public static async Task<IReadOnlyList<LaunchProject>> ListAsync(IRepository repository, string current, CancellationToken ct = default)
    {
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in new[] { current }.Concat(await repository.GetKnownProjectPathsAsync(ct)))
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path))
                    paths.TryAdd(LaunchProjectScope.Key(path), LaunchProjectScope.Root(path));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { }
        }
        List<LaunchProject> projects = [];
        foreach (var (key, path) in paths)
            projects.Add(new(key, await repository.GetProjectDisplayNameAsync(path, ct), path, key == LaunchProjectScope.Key(current)));
        return projects.OrderByDescending(p => p.IsCurrent).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ThenBy(p => p.Path).ToArray();
    }
}
