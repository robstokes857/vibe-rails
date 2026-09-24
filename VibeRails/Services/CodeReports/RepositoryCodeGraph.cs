using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using MintLint;
using VibeRails.DTOs;
using VibeRails.Services.GitPreflight;

namespace VibeRails.Services.CodeReports;

/// <summary>Builds a bounded working-tree map from Git's file catalog and parser evidence.</summary>
public sealed class RepositoryCodeGraph
{
    internal const int MaxFiles = 1000;
    internal const int MaxNodes = 2800;
    private const int MaxEdges = 10000;
    private const int MaxFileBytes = 128 * 1024;
    private const long MaxSourceBytes = 16 * 1024 * 1024;

    /// <summary>Reads only regular, repository-contained files; never follows links.</summary>
    public async Task<CodeGraphResponse> ReadAsync(string repositoryPath, IReadOnlyList<string> priorityFiles,
        CancellationToken cancellationToken)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
        var paths = await ListFilesAsync(root, cancellationToken);
        var priority = priorityFiles.ToHashSet(StringComparer.Ordinal);
        var candidates = paths.Where(path => IsSafePath(path) && MintLintAnalyzer.SupportsFile(path)
                && (priority.Contains(path) || IsSourcePath(path))).Distinct(StringComparer.Ordinal)
            .OrderByDescending(priority.Contains).ThenBy(path => path, StringComparer.Ordinal).ToArray();
        var truncated = candidates.Length > MaxFiles;
        var guard = new GitStagedSnapshotProvider.WorkingTreePathGuard(root);
        var files = new List<(string Path, SourceOutline? Outline)>();
        long bytesRead = 0;
        foreach (var path in candidates.Take(MaxFiles))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(Path.Combine(root, path));
            if (!guard.IsReadableRegularFile(fullPath)) continue;
            SourceOutline? outline = null;
            try
            {
                // Bound the read itself, including files that grow after the stat.
                await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
                if (stream.Length <= MaxFileBytes && bytesRead + stream.Length <= MaxSourceBytes)
                {
                    var buffer = new byte[MaxFileBytes + 1];
                    var length = await stream.ReadAtLeastAsync(buffer, buffer.Length, false, cancellationToken);
                    bytesRead += length;
                    if (length <= MaxFileBytes && !buffer.AsSpan(0, length).Contains((byte)0))
                        outline = SourceOutline.Read(path, Encoding.UTF8.GetString(buffer, 0, length));
                    else truncated = true;
                }
                else truncated = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                truncated = true;
            }
            files.Add((path, outline));
        }
        return Build(Path.GetFileName(root), files, truncated, cancellationToken);
    }

    internal static CodeGraphResponse Build(string name,
        IReadOnlyList<(string Path, SourceOutline? Outline)> files, bool truncated,
        CancellationToken cancellationToken = default)
    {
        var nodes = new List<CodeGraphNode>();
        var edges = new List<CodeGraphEdge>();
        var domains = new Dictionary<string, string>(StringComparer.Ordinal);
        var fileNodes = new Dictionary<string, CodeGraphNode>(StringComparer.Ordinal);
        foreach (var (path, outline) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "";
            var segments = directory.Split('/');
            // Preserve real directory ancestry. Large-map overviews can then show the
            // top-level areas and progressively reveal their children.
            var ancestors = directory.Length == 0 ? new[] { "" }
                : segments.Take(32).Select((_, index) => string.Join('/', segments.Take(index + 1))).ToArray();
            if (nodes.Count + ancestors.Count(path => !domains.ContainsKey(path)) + 1 > MaxNodes)
            { truncated = true; break; }
            if (segments.Length > 32) truncated = true;
            string? domainId = null;
            foreach (var ancestor in ancestors)
            {
                if (!domains.TryGetValue(ancestor, out var id))
                {
                    id = Id("domain", ancestor);
                    domains.Add(ancestor, id);
                    nodes.Add(new(id, DisplayName(ancestor.Length == 0 ? name : Path.GetFileName(ancestor)), "module",
                        ancestor.Length == 0 ? "." : ancestor, domainId, Summary: "Directory in the current working tree."));
                    if (domainId is not null) edges.Add(new(Id("contains-domain", ancestor), domainId, id, "contains"));
                }
                domainId = id;
            }
            var file = new CodeGraphNode(Id("file", path), DisplayName(Path.GetFileName(path)), "file", path, domainId,
                outline?.Language, "Current working-tree structure. Saved report measurements may describe an earlier revision.");
            nodes.Add(file);
            fileNodes.Add(path, file);
            edges.Add(new(Id("contains", path), domainId!, file.Id, "contains"));
        }

        // File nodes take precedence over optional declarations, so every included report file
        // remains selectable even when the graph's symbol budget is exhausted.
        foreach (var (path, outline) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (outline is null || !fileNodes.TryGetValue(path, out var file)) continue;
            if (outline.Declarations.Count > 12) truncated = true;
            foreach (var declaration in outline.Declarations.Distinct().Take(12))
            {
                if (nodes.Count == MaxNodes) { truncated = true; break; }
                var id = Id("symbol", $"{path}:{declaration.Line}:{declaration.Kind}:{declaration.Name}");
                nodes.Add(new(id, DisplayName(declaration.Name), declaration.Kind, $"{path}:{declaration.Line}", file.Id,
                    outline.Language, "Declaration identified by the source parser; no coverage or quality measurement is inferred."));
                edges.Add(new(Id("contains", id), file.Id, id, "contains"));
            }
        }

        var declarations = files.Where(file => file.Outline is not null && fileNodes.ContainsKey(file.Path))
            .SelectMany(file => file.Outline!.Declarations.Where(symbol => symbol.Kind is "class" or "interface")
                .Select(symbol => (symbol.Name, file.Path)))
            .Distinct().GroupBy(item => item.Name, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single().Path, StringComparer.Ordinal);
        var relations = new HashSet<(string Source, string Target)>();
        var domainRelations = new Dictionary<(string Source, string Target), string>();
        foreach (var (path, outline) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (outline is null || !fileNodes.TryGetValue(path, out var source)) continue;
            foreach (var reference in outline.References)
            {
                if (!declarations.TryGetValue(reference.Name, out var targetPath) || targetPath == path) continue;
                AddReference(source, fileNodes[targetPath], $"{path}:{reference.Line} mentions {reference.Name}");
            }
            foreach (var import in outline.Imports.Where(value => value.StartsWith('.')))
            {
                // Resolve only exact repository files or conventional JS index modules.
                var relative = ResolveImport(path, import);
                if (relative is null) continue;
                foreach (var suffix in new[] { "", ".js", ".mjs", ".ts", ".tsx", ".jsx", "/index.js", "/index.ts" })
                {
                    if (!fileNodes.TryGetValue(relative + suffix, out var target)) continue;
                    AddReference(source, target, $"{path} imports {import}");
                    break;
                }
            }
        }
        // Domain connections are real cross-directory source evidence, rendered at overview scale.
        foreach (var (pair, evidence) in domainRelations)
            edges.Add(new(Id("domain-ref", pair.Source + pair.Target), pair.Source, pair.Target, "references", evidence));

        return new("1.0", new(name.Length > 200 ? name[..200] : name), nodes, edges, DateTime.UtcNow,
            truncated, fileNodes.Count,
            "Working-tree directories, declarations, local imports and unambiguous type-name references. References are lexical evidence, not resolved calls or runtime dependencies.");

        void AddReference(CodeGraphNode source, CodeGraphNode target, string evidence)
        {
            if (source.Id == target.Id || relations.Contains((source.Id, target.Id))) return;
            var newDomainRelation = source.ParentId != target.ParentId
                && !domainRelations.ContainsKey((source.ParentId!, target.ParentId!));
            if (edges.Count + domainRelations.Count + (newDomainRelation ? 2 : 1) > MaxEdges)
            { truncated = true; return; }
            relations.Add((source.Id, target.Id));
            edges.Add(new(Id("reference", source.Id + target.Id), source.Id, target.Id, "references", evidence));
            if (source.ParentId != target.ParentId)
                domainRelations.TryAdd((source.ParentId!, target.ParentId!), evidence);
        }
    }

    private static string? ResolveImport(string source, string import)
    {
        // Source paths are slash-separated on every host. Do not interpret import text as
        // an OS path (drive letters, device paths or above-root traversal).
        var parts = source.Split('/').SkipLast(1).ToList();
        foreach (var part in import.Split('/'))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (parts.Count == 0) return null;
                parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(part);
        }
        var path = string.Join('/', parts);
        return IsSafePath(path) ? path : null;
    }

    internal static bool IsSafePath(string? path) => !string.IsNullOrWhiteSpace(path)
        && path.Length <= 4096 && !path.StartsWith('/') && !path.Contains('\\') && !path.Contains(':')
        && !path.Any(char.IsControl) && !path.Split('/').Any(part => part is "" or "." or "..");

    private static bool IsSourcePath(string path) => IsSafePath(path) && MintLintAnalyzer.SupportsFile(path)
        && !path.Split('/').Any(part => part is ".git" or "node_modules" or "bin" or "obj" or "vendor" or "assets");

    private static string Id(string kind, string path) => kind + ":" + Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..24];

    private static string DisplayName(string name) => name.Length > 500 ? name[..497] + "…" : name;

    private static async Task<string[]> ListFilesAsync(string root, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var process = new Process { StartInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8
        }};
        foreach (var arg in new[] { "ls-files", "-z", "--cached", "--others", "--exclude-standard" })
            process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        try
        {
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            var buffer = new char[4 * 1024 * 1024 + 1];
            var count = await process.StandardOutput.ReadBlockAsync(buffer.AsMemory(), timeout.Token);
            if (count == buffer.Length) throw new InvalidOperationException("Repository file catalog exceeds the map limit.");
            await process.WaitForExitAsync(timeout.Token);
            await error;
            if (process.ExitCode != 0) throw new InvalidOperationException("Could not read repository files for the code map.");
            return new string(buffer, 0, count).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }
}
