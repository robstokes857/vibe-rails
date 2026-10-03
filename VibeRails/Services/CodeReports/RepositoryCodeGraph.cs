using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MintLint;
using VibeRails.DTOs;
using VibeRails.Services.GitPreflight;

namespace VibeRails.Services.CodeReports;

/// <summary>Builds a bounded working-tree map from Git's file catalog and parser evidence.</summary>
public sealed class RepositoryCodeGraph
{
    internal const int MaxFiles = 2000;
    internal const int MaxPriorityFiles = 1000;
    internal const int MaxNodes = 2800;
    internal const int MaxSerializedBytes = 8 * 1024 * 1024;
    private const int MaxEdges = 10000;
    private const int MaxEvidenceLength = 512;
    private const int MaxFileBytes = 128 * 1024;
    private const long MaxSourceBytes = 16 * 1024 * 1024;
    private const int MaxCatalogChars = 4 * 1024 * 1024;
    private const int MaxDeclarationsPerFile = 12;
    private const int MaxDirectoryDepth = 32;
    // Diagnostic prose names the limits from the constants above, so a changed limit cannot leave a stale number behind.
    private static readonly string FileLimitText = Text($"source files omitted by the {MaxFiles:N0}-file limit. Prioritize a report file to include it.");
    private static readonly string NodeLimitText = Text($"source files omitted because their directory ancestry exceeds the {MaxNodes:N0}-node limit.");
    private static readonly string FileSizeText = Text($"files kept without outlines: source exceeds {MaxFileBytes / 1024} KiB.");
    private static readonly string SourceBudgetText = Text($"files kept without outlines: the {MaxSourceBytes / (1024 * 1024)} MiB source-read budget was reached.");
    private static readonly string DepthText = Text($"files have ancestry shortened to {MaxDirectoryDepth} directory levels.");
    private static readonly string DeclarationLimitText = Text($"declarations omitted by the {MaxDeclarationsPerFile}-declarations-per-file limit. Open the source for the full outline.");
    private static readonly string DeclarationNodeLimitText = Text($"declarations omitted by the {MaxNodes:N0}-node limit; file structure takes priority.");
    private static readonly string EvidenceLengthText = Text($"reference descriptions shortened to {MaxEvidenceLength} characters.");
    private static readonly string EdgeLimitText = Text($"references omitted by the {MaxEdges:N0}-edge limit.");
    private static readonly string SerializedSizeText = Text($"map exceeded {MaxSerializedBytes / (1024 * 1024)} MiB; references, then declarations, then file structure were trimmed.");
    private static readonly string SerializedNodesText = Text($"nodes omitted to keep the map below {MaxSerializedBytes / (1024 * 1024)} MiB.");
    private static readonly string SerializedEdgesText = Text($"edges omitted to keep the map below {MaxSerializedBytes / (1024 * 1024)} MiB.");
    private const string GraphDescription = "Working-tree directories, declarations, local module imports and namespace-scoped "
        + "type-name mentions. References are source evidence, not resolved calls or runtime dependencies.";
    // Large repositories with a cold Git index can take a while to enumerate; this is a bound, not a target.
    private static readonly TimeSpan CatalogTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Reads only regular, repository-contained files; never follows links.</summary>
    public async Task<CodeGraphResponse> ReadAsync(string repositoryPath, IReadOnlyList<string> priorityFiles,
        CancellationToken cancellationToken)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
        var paths = await ListFilesAsync(root, cancellationToken);
        var priority = priorityFiles.ToHashSet(StringComparer.Ordinal);
        var supported = paths.Where(path => IsSafePath(path) && MintLintAnalyzer.SupportsFile(path))
            .Distinct(StringComparer.Ordinal).ToArray();
        var diagnostics = new CodeGraphDiagnosticsBuilder
        {
            SupportedFiles = supported.Length,
            IncludesDependencies = false,
            ExcludedDependencyFiles = supported.Count(path => path.Split('/').Any(IsDependencyDirectory)),
            ExcludedBuildOutputFiles = supported.Count(path => !path.Split('/').Any(IsDependencyDirectory)
                && IsCSharpBuildOutput(path))
        };
        var candidates = supported.Where(IsSourcePath)
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var selected = SelectPaths(candidates, priority, MaxFiles, MaxNodes).ToArray();
        // Fewer selected than eligible means the file or node budget left some out: say the map is partial.
        var truncated = selected.Length < candidates.Length;
        diagnostics.Add(selected.Length == MaxFiles ? "file-limit" : "file-node-limit",
            candidates.Length - selected.Length, selected.Length == MaxFiles ? FileLimitText : NodeLimitText);
        var guard = new GitStagedSnapshotProvider.WorkingTreePathGuard(root);
        var files = new List<(string Path, SourceOutline? Outline)>();
        long bytesRead = 0;
        // One read buffer for the whole map: a fresh 128 KiB array per file is a large-object
        // allocation for each of up to MaxFiles files. Only [0, length) is read after each fill.
        var buffer = new byte[MaxFileBytes + 1];
        foreach (var path in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = Path.GetFullPath(Path.Combine(root, path));
            // A link, device or unreadable entry is a file the map silently omits, so say the map is partial.
            if (!guard.IsReadableRegularFile(fullPath))
            {
                truncated = true;
                diagnostics.Add("unreadable-path", 1, "files omitted: unavailable, unreadable or refused by the repository path guard.");
                continue;
            }
            SourceOutline? outline = null;
            try
            {
                // Bound the read itself, including files that grow after the stat.
                await using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
                if (stream.Length <= MaxFileBytes && bytesRead + stream.Length <= MaxSourceBytes)
                {
                    var length = await stream.ReadAtLeastAsync(buffer, buffer.Length, false, cancellationToken);
                    bytesRead += length;
                    if (length <= MaxFileBytes && !buffer.AsSpan(0, length).Contains((byte)0))
                        outline = SourceOutline.Read(path, Encoding.UTF8.GetString(buffer, 0, length));
                    else
                    {
                        truncated = true;
                        diagnostics.Add(length > MaxFileBytes ? "file-size" : "binary-source", 1,
                            length > MaxFileBytes ? FileSizeText : "files kept without outlines: source contains NUL bytes.");
                    }
                }
                else
                {
                    truncated = true;
                    diagnostics.Add(stream.Length > MaxFileBytes ? "file-size" : "source-budget", 1,
                        stream.Length > MaxFileBytes ? FileSizeText : SourceBudgetText);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                truncated = true;
                diagnostics.Add("read-error", 1, "files kept without outlines: source could not be read. Retry after checking file access.");
            }
            files.Add((path, outline));
        }
        return Build(RepositoryName(root), files, truncated, cancellationToken, diagnostics, paths);
    }

    // A lexical prefix can consume the entire budget before reaching the application's UI
    // or later monorepo packages. Give each directory a turn, after honoring report paths.
    // Every file is one node and each directory it introduces is one more (see Build), so the
    // selection also charges that ancestry against the node budget: a wide tree keeps fewer files
    // than the file limit, but every selected file is one Build can keep. A file whose ancestry no
    // longer fits is skipped (with the rest of its directory) rather than ending the selection, so
    // cheaper files in directories already on the map still get their turn.
    internal static IEnumerable<string> SelectPaths(IEnumerable<string> paths, ISet<string> priority, int limit,
        int nodeLimit = int.MaxValue)
    {
        var ordered = paths.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var remaining = limit;
        var nodes = 0;
        var directories = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in ordered.Where(priority.Contains))
        {
            if (remaining <= 0) yield break;
            if (!Charge(path)) continue;
            remaining--;
            yield return path;
        }
        var queues = new Queue<Queue<string>>(ordered.Where(path => !priority.Contains(path))
            .GroupBy(path => Path.GetDirectoryName(path), StringComparer.Ordinal)
            .Select(group => new Queue<string>(group)));
        while (remaining > 0 && queues.TryDequeue(out var queue))
        {
            var path = queue.Dequeue();
            // Siblings share this file's ancestry, so none of them would fit either.
            if (!Charge(path)) continue;
            remaining--;
            yield return path;
            if (queue.Count > 0) queues.Enqueue(queue);
        }

        bool Charge(string path)
        {
            var ancestors = Ancestors(path);
            var cost = 1 + ancestors.Count(ancestor => !directories.Contains(ancestor));
            if (nodes + cost > nodeLimit) return false;
            nodes += cost;
            directories.UnionWith(ancestors);
            return true;
        }
    }

    // The directory nodes a file introduces: the repository root for a top-level file, else each
    // prefix of its directory, at most MaxDirectoryDepth levels deep. Shared by SelectPaths and Build so the
    // selection budget and the node budget count the same nodes.
    private static string[] Ancestors(string path)
    {
        var directory = Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "";
        if (directory.Length == 0) return [""];
        var segments = directory.Split('/');
        return segments.Take(MaxDirectoryDepth).Select((_, index) => string.Join('/', segments.Take(index + 1))).ToArray();
    }

    internal static string RepositoryName(string root)
    {
        var name = Path.GetFileName(root);
        return string.IsNullOrWhiteSpace(name) ? root : name;
    }

    internal static CodeGraphResponse Build(string name,
        IReadOnlyList<(string Path, SourceOutline? Outline)> files, bool truncated,
        CancellationToken cancellationToken = default, CodeGraphDiagnosticsBuilder? diagnostics = null,
        IEnumerable<string>? catalogPaths = null)
    {
        diagnostics ??= new CodeGraphDiagnosticsBuilder { SupportedFiles = files.Count };
        var limitedScopes = files.Count(file => file.Outline?.ReferenceScope?.Truncated == true);
        if (limitedScopes > 0)
        {
            truncated = true;
            diagnostics.Add("reference-scope-limit", limitedScopes,
                "files have incomplete reference evidence because scope or identifier text exceeded the analysis budget.");
        }
        var limitedImports = files.Count(file => file.Outline?.ImportEvidenceTruncated == true);
        if (limitedImports > 0)
        {
            truncated = true;
            diagnostics.Add("import-evidence-limit", limitedImports,
                "files have incomplete module import evidence because import path text exceeded the analysis budget.");
        }
        var nodes = new List<CodeGraphNode>();
        var edges = new List<CodeGraphEdge>();
        var domains = new Dictionary<string, string>(StringComparer.Ordinal);
        var fileNodes = new Dictionary<string, CodeGraphNode>(StringComparer.Ordinal);
        foreach (var (path, outline) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Preserve real directory ancestry. Large-map overviews can then show the
            // top-level areas and progressively reveal their children.
            var ancestors = Ancestors(path);
            if (nodes.Count + ancestors.Count(ancestor => !domains.ContainsKey(ancestor)) + 1 > MaxNodes)
            {
                truncated = true;
                diagnostics.Add("file-node-limit", files.Count - fileNodes.Count, NodeLimitText);
                break;
            }
            // Ancestors stops at MaxDirectoryDepth levels; a deeper path loses the rest of its ancestry.
            if (path.AsSpan().Count('/') > MaxDirectoryDepth)
            {
                truncated = true;
                diagnostics.Add("directory-depth", 1, DepthText);
            }
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
            var declarationsForFile = outline.Declarations.Distinct().ToArray();
            // The flag and the omission count describe the same distinct list.
            if (declarationsForFile.Length > MaxDeclarationsPerFile) truncated = true;
            diagnostics.Add("declaration-limit", Math.Max(0, declarationsForFile.Length - MaxDeclarationsPerFile), DeclarationLimitText);
            var declarationsToKeep = declarationsForFile.Take(MaxDeclarationsPerFile).ToArray();
            diagnostics.Add("declaration-node-limit", Math.Max(0, declarationsToKeep.Length - (MaxNodes - nodes.Count)), DeclarationNodeLimitText);
            foreach (var declaration in declarationsToKeep)
            {
                if (nodes.Count == MaxNodes) { truncated = true; break; }
                var id = Id("symbol", $"{path}:{declaration.Line}:{declaration.Kind}:{declaration.Name}");
                nodes.Add(new(id, DisplayName(declaration.Name), declaration.Kind, $"{path}:{declaration.Line}", file.Id,
                    outline.Language, "Declaration identified by the source parser; no coverage or quality measurement is inferred."));
                edges.Add(new(Id("contains", id), file.Id, id, "contains"));
            }
        }

        var referenceResolver = new RepositoryReferenceResolver(files.Where(file => fileNodes.ContainsKey(file.Path)));
        var moduleResolver = new RepositoryModuleResolver(files.Where(file => fileNodes.ContainsKey(file.Path)).ToArray(), catalogPaths);
        var relations = new HashSet<(string Source, string Target)>();
        var domainRelations = new Dictionary<(string Source, string Target), string>();
        foreach (var (path, outline) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (outline is null || !fileNodes.TryGetValue(path, out var source)) continue;
            // Explicit module evidence wins over a weaker type mention for the same file pair.
            foreach (var reference in moduleResolver.Resolve(path, outline))
            {
                var target = fileNodes[reference.Path];
                if (!Admit(source, target)) continue;
                var import = reference.Import;
                var verb = import.Kind == "rust-mod" ? "declares module" : "imports";
                var importedName = import.ImportedName is null ? "" : $" ({import.ImportedName})";
                AddReference(source, target, $"{path}:{import.Line} {verb} {import.Path}{importedName}");
            }
            foreach (var reference in referenceResolver.Resolve(path, outline, cancellationToken))
            {
                var target = fileNodes[reference.Path];
                if (Admit(source, target)) AddReference(source, target, reference.Evidence);
            }
            foreach (var import in outline.Imports.Where(value =>
                outline.Language is "JavaScript" or "TypeScript" && (value.StartsWith("./") || value.StartsWith("../"))))
            {
                // Resolve only exact repository files or conventional JS index modules.
                var relative = ResolveImport(path, import);
                if (relative is null) continue;
                foreach (var candidate in ImportCandidates(relative, outline.Language))
                {
                    if (!fileNodes.TryGetValue(candidate, out var target)) continue;
                    if (Admit(source, target)) AddReference(source, target, $"{path} imports {import}");
                    break;
                }
            }
        }
        if (referenceResolver.LimitedFileCount > 0)
        {
            truncated = true;
            diagnostics.Add("reference-work-limit", referenceResolver.LimitedFileCount,
                "files have incomplete references because the candidate-analysis work budget was reached.");
        }
        if (moduleResolver.LimitedFileCount > 0)
        {
            truncated = true;
            diagnostics.Add("module-work-limit", moduleResolver.LimitedFileCount,
                "files have incomplete module imports because the module-resolution work budget was reached.");
        }
        // Domain connections are real cross-directory source evidence, rendered at overview scale.
        foreach (var (pair, evidence) in domainRelations)
            edges.Add(new(Id("domain-ref", pair.Source + pair.Target), pair.Source, pair.Target, "references", evidence));

        return FitAtlasByteLimit(name.Length > 200 ? name[..200] : name, nodes, edges, truncated, diagnostics, cancellationToken);

        // Decided before a caller builds any evidence text: a repeated pair or one past the edge
        // budget costs nothing more. Every pair is remembered, so an omitted file pair counts once
        // even when several imported names target it; the resolvers' work budgets bound the set.
        bool Admit(CodeGraphNode source, CodeGraphNode target)
        {
            if (source.Id == target.Id || !relations.Add((source.Id, target.Id))) return false;
            var newDomainRelation = source.ParentId != target.ParentId
                && !domainRelations.ContainsKey((source.ParentId!, target.ParentId!));
            if (edges.Count + domainRelations.Count + (newDomainRelation ? 2 : 1) <= MaxEdges) return true;
            truncated = true;
            diagnostics.Add("edge-limit", 1, EdgeLimitText);
            return false;
        }

        void AddReference(CodeGraphNode source, CodeGraphNode target, string evidence)
        {
            if (evidence.Length > MaxEvidenceLength)
            {
                evidence = evidence[..256] + "…" + evidence[^(MaxEvidenceLength - 257)..];
                truncated = true;
                diagnostics.Add("evidence-length", 1, EvidenceLengthText);
            }
            edges.Add(new(Id("reference", source.Id + target.Id), source.Id, target.Id, "references", evidence));
            if (source.ParentId != target.ParentId)
                domainRelations.TryAdd((source.ParentId!, target.ParentId!), evidence);
        }
    }

    private static readonly string[] TypeScriptImportExtensions = [".ts", ".tsx", ".mts", ".cts", ".js", ".jsx", ".mjs", ".cjs"];
    private static readonly string[] JavaScriptImportExtensions = [".js", ".jsx", ".mjs", ".cjs", ".ts", ".tsx", ".mts", ".cts"];
    // Extensions an import can name outright. Any other dotted tail is part of the module stem
    // (`./user.service`, `./app.module`), which still needs the extension and index probes.
    private static readonly string[] ExplicitImportExtensions =
        [".js", ".jsx", ".mjs", ".cjs", ".ts", ".tsx", ".mts", ".cts", ".json", ".vue", ".svelte", ".css"];

    private static IEnumerable<string> ImportCandidates(string path, string? language)
    {
        var extension = Path.GetExtension(path);
        // TypeScript commonly writes the emitted JS extension in source imports.
        // Follow source extension substitution before looking for an emitted file.
        if (language == "TypeScript")
        {
            var stem = path[..^extension.Length];
            if (extension == ".js") { yield return stem + ".ts"; yield return stem + ".tsx"; }
            if (extension == ".mjs") yield return stem + ".mts";
            if (extension == ".cjs") yield return stem + ".cts";
        }
        yield return path;
        if (ExplicitImportExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) yield break;
        var extensions = language == "TypeScript" ? TypeScriptImportExtensions : JavaScriptImportExtensions;
        foreach (var candidate in extensions) yield return path + candidate;
        foreach (var candidate in extensions) yield return path + "/index" + candidate;
    }

    /// <summary>Trims the graph to the serialized byte budget, then snapshots it into the response.</summary>
    private static CodeGraphResponse FitAtlasByteLimit(string name,
        List<CodeGraphNode> nodes, List<CodeGraphEdge> edges, bool truncated,
        CodeGraphDiagnosticsBuilder diagnostics, CancellationToken cancellationToken)
    {
        var captured = DateTime.UtcNow;
        // Compose copies the lists, so a returned response is never a view onto further trimming.
        CodeGraphResponse Compose(bool partial) => new("1.0", new(name), nodes.ToArray(), edges.ToArray(),
            captured, partial, nodes.Count(node => node.Kind == "file"), GraphDescription, diagnostics.Snapshot());

        var response = Compose(truncated);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(response, AppJsonSerializerContext.Default.CodeGraphResponse).Length;
        if (bytes <= MaxSerializedBytes) return response;

        diagnostics.Add("serialized-size", 1, SerializedSizeText);

        bytes = JsonSerializer.SerializeToUtf8Bytes(Compose(true), AppJsonSerializerContext.Default.CodeGraphResponse).Length;
        // Preserve the file hierarchy before optional connections and declarations. If unusually
        // long paths alone exhaust the budget, remove leaves in reverse construction order.
        for (var i = edges.Count - 1; i >= 0 && bytes > MaxSerializedBytes; i--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (edges[i].Kind == "references") RemoveEdgeAt(i);
        }
        for (var i = nodes.Count - 1; i >= 0 && bytes > MaxSerializedBytes; i--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (nodes[i].Kind is not ("file" or "module")) RemoveNodeAt(i);
        }
        for (var i = nodes.Count - 1; i >= 0 && bytes > MaxSerializedBytes; i--)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RemoveNodeAt(i);
        }

        // Diagnostic counts and the file count can change the final JSON length slightly.
        // Recheck the actual wire payload rather than relying solely on subtraction estimates. Each
        // pass removes at least the measured overshoot by those estimates before measuring again,
        // so an estimate error costs a few full serializations, not one per removed item.
        long overshoot;
        while ((overshoot = JsonSerializer.SerializeToUtf8Bytes(Compose(true), AppJsonSerializerContext.Default.CodeGraphResponse).Length
            - MaxSerializedBytes) > 0 && nodes.Count > 0)
        {
            for (var removed = 0L; removed < overshoot && nodes.Count > 0;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var before = bytes;
                var referenceIndex = edges.FindLastIndex(edge => edge.Kind == "references");
                if (referenceIndex >= 0) RemoveEdgeAt(referenceIndex);
                else
                {
                    var declarationIndex = nodes.FindLastIndex(node => node.Kind is not ("file" or "module"));
                    RemoveNodeAt(declarationIndex >= 0 ? declarationIndex : nodes.Count - 1);
                }
                removed += Math.Max(1, before - bytes);
            }
        }
        var bounded = Compose(true);
        if (bytes > MaxSerializedBytes ||
            JsonSerializer.SerializeToUtf8Bytes(bounded, AppJsonSerializerContext.Default.CodeGraphResponse).Length > MaxSerializedBytes)
            throw new InvalidOperationException("Could not bound the code map response.");
        return bounded;

        void RemoveNodeAt(int index)
        {
            var id = nodes[index].Id;
            for (var edgeIndex = edges.Count - 1; edgeIndex >= 0; edgeIndex--)
                if (edges[edgeIndex].Source == id || edges[edgeIndex].Target == id) RemoveEdgeAt(edgeIndex);
            bytes -= JsonSerializer.SerializeToUtf8Bytes(nodes[index], AppJsonSerializerContext.Default.CodeGraphNode).Length
                + (nodes.Count > 1 ? 1 : 0);
            nodes.RemoveAt(index);
            diagnostics.Add("serialized-nodes", 1, SerializedNodesText);
        }

        void RemoveEdgeAt(int index)
        {
            bytes -= JsonSerializer.SerializeToUtf8Bytes(edges[index], AppJsonSerializerContext.Default.CodeGraphEdge).Length
                + (edges.Count > 1 ? 1 : 0);
            edges.RemoveAt(index);
            diagnostics.Add("serialized-edges", 1, SerializedEdgesText);
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

    // Dependency directories are always excluded, including priority paths. MSBuild's bin/obj output is excluded for C# only,
    // because other ecosystems keep entry points and sources there. Segment names are matched
    // case-insensitively: a checkout on a case-insensitive file system can spell them either way.
    private static bool IsSourcePath(string path)
    {
        if (!IsSafePath(path) || !MintLintAnalyzer.SupportsFile(path)) return false;
        var isCSharp = Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase);
        foreach (var part in path.Split('/'))
        {
            if (IsDependencyDirectory(part) || (isCSharp && IsBuildOutputDirectory(part))) return false;
        }
        return true;
    }

    private static bool IsCSharpBuildOutput(string path) =>
        Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase)
        && path.Split('/').Any(IsBuildOutputDirectory);

    // `assets` holds vendored bundles far more often than first-party code (Bootstrap, PDF.js and
    // xterm here): one minified bundle over 128 KiB marks every map partial, and its one-letter
    // names create bogus references. Saved analysis remains accessible separately from the map.
    private static bool IsDependencyDirectory(string part) =>
        part.Equals(".git", StringComparison.OrdinalIgnoreCase)
        || part.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
        || part.Equals("vendor", StringComparison.OrdinalIgnoreCase)
        || part.Equals("assets", StringComparison.OrdinalIgnoreCase);

    private static bool IsBuildOutputDirectory(string part) =>
        part.Equals("bin", StringComparison.OrdinalIgnoreCase)
        || part.Equals("obj", StringComparison.OrdinalIgnoreCase);

    private static string Text(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static string Id(string kind, string path) => kind + ":" + Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..24];

    private static string DisplayName(string name) => name.Length > 500 ? name[..497] + "…" : name;

    private static async Task<string[]> ListFilesAsync(string root, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(CatalogTimeout);
        using var process = new Process { StartInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8
        }};
        foreach (var arg in new[] { "ls-files", "-z", "--cached", "--others", "--exclude-standard" })
            process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        Task<string>? error = null;
        try
        {
            // Drain stderr so a chatty git cannot fill its pipe and stall the catalog read.
            error = process.StandardError.ReadToEndAsync(timeout.Token);
            var buffer = new char[MaxCatalogChars + 1];
            var count = await process.StandardOutput.ReadBlockAsync(buffer.AsMemory(), timeout.Token);
            if (count == buffer.Length) throw new InvalidOperationException("Repository file catalog exceeds the map limit.");
            await process.WaitForExitAsync(timeout.Token);
            await error;
            if (process.ExitCode != 0) throw new InvalidOperationException("Could not read repository files for the code map.");
            return new string(buffer, 0, count).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our own timeout fired rather than the caller giving up: report it as a bound, not a server fault.
            throw new InvalidOperationException(
                "Reading the repository file catalog took longer than " + CatalogTimeout.TotalMinutes + " minutes.");
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            // The drain task outlives a failed read; observe it so its fault never escapes unhandled.
            if (error is not null) _ = error.ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);
        }
    }
}
