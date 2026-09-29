using MintLint;

namespace VibeRails.Services.CodeReports;

/// <summary>Resolves explicit Python and Rust module syntax against the bounded repository catalog.</summary>
internal sealed class RepositoryModuleResolver
{
    /// <summary>
    /// Work per graph for each phase: crate-map visits weighted by module path length while
    /// building, then cross-root Python probes and per-location Rust lookups while resolving.
    /// Every source file is untrusted input on the automatic Project Health request, so unusual
    /// layouts stop here and report an omission.
    /// </summary>
    internal const int MaxWorkUnits = 4_000_000;

    private readonly HashSet<string> paths;
    private readonly Dictionary<string, SourceOutline> outlines;
    private readonly HashSet<string> pythonRoots;
    private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> pythonRootLookup;
    private readonly Dictionary<string, string[]> pythonRootsByEntry = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string[]>.AlternateLookup<ReadOnlySpan<char>> pythonRootsByEntryLookup;
    private readonly HashSet<string> cargoRoots;
    private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> cargoRootLookup;
    private readonly Dictionary<string, string?> cargoOwners = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<RustLocation>> rustLocations = new(StringComparer.Ordinal);
    private readonly HashSet<string> limitedFiles = new(StringComparer.Ordinal);
    private int buildWork;
    private int resolveWork;
    private bool crateMapsIncomplete;

    internal RepositoryModuleResolver(IReadOnlyList<(string Path, SourceOutline? Outline)> files,
        IEnumerable<string>? catalogPaths = null)
    {
        paths = files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
        outlines = files.Where(file => file.Outline is not null).ToDictionary(file => file.Path,
            file => file.Outline!, StringComparer.Ordinal);
        var packages = paths.Where(path => path.EndsWith("/__init__.py", StringComparison.Ordinal))
            .Select(Directory).ToHashSet(StringComparer.Ordinal);
        pythonRoots = PythonRoots(packages.GetAlternateLookup<ReadOnlySpan<char>>()).ToHashSet(StringComparer.Ordinal);
        pythonRootLookup = pythonRoots.GetAlternateLookup<ReadOnlySpan<char>>();
        IndexPythonRootEntries();
        pythonRootsByEntryLookup = pythonRootsByEntry.GetAlternateLookup<ReadOnlySpan<char>>();
        cargoRoots = (catalogPaths ?? paths).Where(path => path == "Cargo.toml" || path.EndsWith("/Cargo.toml", StringComparison.Ordinal))
            .Select(Directory).ToHashSet(StringComparer.Ordinal);
        cargoRootLookup = cargoRoots.GetAlternateLookup<ReadOnlySpan<char>>();
        BuildRustModules();
    }

    /// <summary>Source files whose module resolution stopped at the work budget.</summary>
    internal int LimitedFileCount => limitedFiles.Count;

    internal IEnumerable<(string Path, SourceOutlineImport Import)> Resolve(string source, SourceOutline outline)
    {
        if (outline.ImportEvidence.Count == 0 || outline.Language is not ("Python" or "Rust")) yield break;
        var python = outline.Language == "Python";
        if (!python && crateMapsIncomplete) limitedFiles.Add(source);
        var localRoot = python ? LocalRoot(source) : "";
        foreach (var import in outline.ImportEvidence)
        {
            var target = python ? ResolvePython(source, localRoot, import) : ResolveRust(source, import);
            if (target is not null && target != source) yield return (target, import);
        }
    }

    // Crate-map construction and per-file resolution have separate budgets: a cut walk must
    // not also stop every later lookup against the maps that were completed.
    private static bool Charge(ref int counter, int units)
    {
        counter += units;
        return counter <= MaxWorkUnits;
    }

    // Roots depend only on a file's directory. Each distinct directory walks its prefixes once,
    // against span lookups, so deep or repetitive layouts cost their path length and no more.
    private IEnumerable<string> PythonRoots(HashSet<string>.AlternateLookup<ReadOnlySpan<char>> packages)
    {
        yield return "";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            if (!path.EndsWith(".py", StringComparison.Ordinal)) continue;
            var directory = Directory(path);
            if (!seen.Add(directory)) continue;
            var insidePackage = false;
            for (var start = 0; start < directory.Length;)
            {
                var slash = directory.IndexOf('/', start);
                var end = slash < 0 ? directory.Length : slash;
                insidePackage |= packages.Contains(directory.AsSpan(0, end));
                // A src directory is an explicit conventional import root, including namespace
                // packages, unless a package already encloses it.
                if (!insidePackage && directory.AsSpan(start, end - start).SequenceEqual("src")) yield return directory[..end];
                start = end + 1;
            }
            if (directory.Length == 0 || !packages.Contains(directory)) continue;
            // The directory above the outermost enclosing package is an import root.
            var packageEnd = directory.Length;
            while (true)
            {
                var parentEnd = directory.LastIndexOf('/', packageEnd - 1);
                if (parentEnd <= 0 || !packages.Contains(directory.AsSpan(0, parentEnd))) break;
                packageEnd = parentEnd;
            }
            var rootEnd = directory.LastIndexOf('/', packageEnd - 1);
            yield return rootEnd < 0 ? "" : directory[..rootEnd];
        }
    }

    // Which roots have a top-level entry of each name. A module can only live below a root that
    // has its first segment as a direct child, so stdlib and external imports probe nothing.
    private void IndexPythonRootEntries()
    {
        var entries = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            if (!path.EndsWith(".py", StringComparison.Ordinal)) continue;
            var end = 0;
            while (true)
            {
                if (pythonRootLookup.TryGetValue(path.AsSpan(0, end), out var root))
                {
                    var start = end == 0 ? 0 : end + 1;
                    var slash = path.IndexOf('/', start);
                    var entry = slash < 0 ? path[start..^3] : path[start..slash];
                    if (entry.Length > 0)
                    {
                        if (!entries.TryGetValue(entry, out var roots)) entries[entry] = roots = new(StringComparer.Ordinal);
                        roots.Add(root);
                    }
                }
                var next = path.IndexOf('/', end + 1);
                if (next < 0) break;
                end = next;
            }
        }
        foreach (var (entry, roots) in entries) pythonRootsByEntry[entry] = roots.ToArray();
    }

    private string LocalRoot(string source)
    {
        // The deepest import root containing the source, by walking its directory prefixes.
        for (var end = source.LastIndexOf('/'); end > 0; end = source.LastIndexOf('/', end - 1))
            if (pythonRootLookup.TryGetValue(source.AsSpan(0, end), out var root)) return root;
        return "";
    }

    private string? ResolvePython(string source, string localRoot, SourceOutlineImport import)
    {
        if (import.Kind is not ("python-import" or "python-from")) return null;
        var dots = import.Path.TakeWhile(character => character == '.').Count();
        var module = import.Path[dots..];
        if (module.Length > 0 && !Names(module.Split('.'))) return null;
        var modulePath = module.Replace('.', '/');
        if (dots > 0)
        {
            var package = Directory(source);
            // A top-level script is not a package. Relative imports may not escape their package root.
            if (package == localRoot) return null;
            for (var i = 1; i < dots; i++)
            {
                package = Directory(package);
                if (package == localRoot || !Below(package, localRoot)) return null;
            }
            return PythonTarget(Join(package, modulePath), import);
        }
        // Prefer the import root containing the source. Other roots are usable only if the full
        // qualified path is unique: never search by basename or attach an external symbol name.
        var local = PythonTarget(Join(localRoot, modulePath), import);
        if (local is not null) return local;
        var separator = module.IndexOf('.');
        if (!pythonRootsByEntryLookup.TryGetValue(separator < 0 ? module : module.AsSpan(0, separator), out var roots)) return null;
        string? match = null;
        foreach (var root in roots)
        {
            if (!Charge(ref resolveWork, 1)) { limitedFiles.Add(source); return null; }
            var candidate = PythonTarget(Join(root, modulePath), import);
            if (candidate is null || candidate == match) continue;
            if (match is not null) return null;
            match = candidate;
        }
        return match;
    }

    private string? PythonTarget(string module, SourceOutlineImport import)
    {
        if (module.Length == 0) return null;
        var file = UniqueExisting(module + ".py", Join(module, "__init__.py"));
        if (import.Kind == "python-from" && import.ImportedName is { } member && member != "*" && Names([member])
            && !paths.Contains(module + ".py"))
        {
            var child = Join(module, member);
            var childFile = UniqueExisting(child + ".py", Join(child, "__init__.py"));
            if (childFile is not null) return childFile;
        }
        return file;
    }

    private void BuildRustModules()
    {
        // The catalog, including Cargo.toml paths when available, establishes workspace boundaries.
        // Conventional crate roots cover src/{lib,main}.rs, custom-layout main.rs/lib.rs targets and
        // Cargo's auto-discovered bin, test, example and bench targets (IsCargoTargetRoot).
        // Cargo aliases, generated modules and #[path] modules deliberately remain unresolved.
        // Shallow roots first, so a budget cut leaves the conventional top-level crates complete.
        var roots = paths.Where(path => path.EndsWith(".rs", StringComparison.Ordinal)
                && (Path.GetFileName(path) is "main.rs" or "lib.rs" || IsCargoTargetRoot(path)))
            .OrderBy(path => path.AsSpan().Count('/')).ThenBy(path => path, StringComparer.Ordinal).ToArray();
        foreach (var root in roots)
        {
            var crate = new Dictionary<string, string>(StringComparer.Ordinal) { [""] = root };
            var queue = new Queue<(string File, string Module, string ModuleDirectory)>();
            queue.Enqueue((root, "", Directory(root)));
            while (queue.TryDequeue(out var current))
            {
                if (!rustLocations.TryGetValue(current.File, out var locations))
                    rustLocations[current.File] = locations = [];
                locations.Add(new(crate, current.Module));
                if (!outlines.TryGetValue(current.File, out var outline)) continue;
                foreach (var import in outline.ImportEvidence)
                {
                    if (import.Kind is not ("rust-inline" or "rust-mod")) continue;
                    // Shared files are walked once per crate root, and every lookup below hashes the
                    // module path; charge both so repeated deep trees stop instead of running for minutes.
                    if (!Charge(ref buildWork, 1 + current.Module.Length / 16)) { crateMapsIncomplete = true; return; }
                    var scope = import.Scope ?? "";
                    var parent = RustJoin(current.Module, scope);
                    if (!crate.ContainsKey(parent)) continue;
                    var module = RustJoin(parent, import.Path);
                    if (module.Count(character => character == ':') > 128) continue;
                    if (import.Kind == "rust-inline") { crate.TryAdd(module, current.File); continue; }
                    var stem = Join(Join(current.ModuleDirectory, scope.Replace("::", "/", StringComparison.Ordinal)), import.Path);
                    var target = UniqueExisting(stem + ".rs", Join(stem, "mod.rs"));
                    if (target is null || CargoOwner(target) != CargoOwner(root) || !crate.TryAdd(module, target)) continue;
                    queue.Enqueue((target, module, stem));
                }
            }
        }
    }

    private string? ResolveRust(string source, SourceOutlineImport import)
    {
        if (import.Kind is not ("rust-mod" or "rust-use")) return null;
        if (!rustLocations.TryGetValue(source, out var locations))
        {
            // A mod declaration gives a direct filename even if its crate root fell outside the map.
            if (import.Kind != "rust-mod") return null;
            var directory = Path.GetFileName(source) is "mod.rs" or "main.rs" or "lib.rs"
                ? Directory(source) : source[..^3];
            var stem = Join(Join(directory, (import.Scope ?? "").Replace("::", "/", StringComparison.Ordinal)), import.Path);
            var target = UniqueExisting(stem + ".rs", Join(stem, "mod.rs"));
            return target is not null && CargoOwner(target) == CargoOwner(source) ? target : null;
        }
        if (!Charge(ref resolveWork, locations.Count)) { limitedFiles.Add(source); return null; }
        var resolved = locations.Select(location => ResolveRustAt(location, import)).Distinct(StringComparer.Ordinal).Take(2).ToArray();
        // Shared source can participate in several crate targets. Keep only an agreed target.
        return resolved.Length == 1 ? resolved[0] : null;
    }

    private static string? ResolveRustAt(RustLocation location, SourceOutlineImport import)
    {
        var module = RustJoin(location.Module, import.Scope ?? "");
        if (import.Kind == "rust-mod")
            return location.Crate.GetValueOrDefault(RustJoin(module, import.Path));
        if (import.Path.StartsWith("::", StringComparison.Ordinal)) return null;
        var parts = import.Path.Split("::", StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        var cursor = 0;
        var explicitlyLocal = parts[0] is "crate" or "self" or "super";
        if (parts[0] == "crate") { module = ""; cursor++; }
        else if (parts[0] == "self") cursor++;
        // A bare first name starts in the current module when that module declares it (Rust 2018:
        // `use child::Item;` beside `mod child;`); otherwise it is a crate-root module (2015) or an
        // external crate, which the root lookup below leaves unresolved.
        else if (parts[0] != "super" && !location.Crate.ContainsKey(RustJoin(module, parts[0]))) module = "";
        while (cursor < parts.Length && parts[cursor] == "super")
        {
            if (module.Length == 0) return null;
            var separator = module.LastIndexOf("::", StringComparison.Ordinal);
            module = separator < 0 ? "" : module[..separator];
            cursor++;
        }
        if (!location.Crate.TryGetValue(module, out var target)) return null;
        for (; cursor < parts.Length; cursor++)
        {
            var next = RustJoin(module, parts[cursor]);
            if (location.Crate.TryGetValue(next, out var nextTarget)) { module = next; target = nextTarget; continue; }
            // A final imported item (or glob) belongs to the known module. Longer unresolved paths
            // might be external crates, aliases or associated items; do not infer their files.
            return cursor == parts.Length - 1 && (module.Length > 0 || explicitlyLocal) ? target : null;
        }
        return target;
    }

    // The deepest Cargo.toml directory above the path, found by walking its directory prefixes
    // and remembered per directory; the catalog can hold far more manifests than the map has files.
    private string? CargoOwner(string path)
    {
        var directory = Directory(path);
        if (cargoOwners.TryGetValue(directory, out var owner)) return owner;
        var end = directory.Length;
        while (true)
        {
            if (cargoRootLookup.TryGetValue(directory.AsSpan(0, end), out var root)) { owner = root; break; }
            if (end == 0) break;
            end = Math.Max(0, directory.LastIndexOf('/', end - 1));
        }
        cargoOwners[directory] = owner;
        return owner;
    }

    private static readonly string[] CargoTargetDirectories = ["src/bin", "tests", "examples", "benches"];

    /// <summary>
    /// Cargo compiles every .rs file directly inside a package's src/bin, tests, examples or benches
    /// directory as its own crate (their subdirectory main.rs form is already a main.rs root). With
    /// no Cargo.toml in the catalog, the repository root stands in for the package.
    /// </summary>
    private bool IsCargoTargetRoot(string path)
    {
        var directory = Directory(path);
        foreach (var target in CargoTargetDirectories)
        {
            if (directory == target) return cargoRoots.Count == 0 || cargoRoots.Contains("");
            if (directory.Length > target.Length && directory[^(target.Length + 1)] == '/'
                && directory.EndsWith(target, StringComparison.Ordinal)
                && cargoRoots.Contains(directory[..^(target.Length + 1)]))
                return true;
        }
        return false;
    }

    private string? UniqueExisting(string first, string second) => paths.Contains(first)
        ? paths.Contains(second) ? null : first : paths.Contains(second) ? second : null;
    private static bool Names(IEnumerable<string> parts) => parts.All(part => part.Length > 0
        && (char.IsLetter(part[0]) || part[0] == '_') && part.All(character => char.IsLetterOrDigit(character) || character == '_'));
    private static bool Below(string path, string directory) => directory.Length == 0
        || (path.Length > directory.Length && path[directory.Length] == '/' && path.StartsWith(directory, StringComparison.Ordinal));
    private static string Directory(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
    private static string Join(string first, string second) => first.Length == 0 ? second : second.Length == 0 ? first : first + "/" + second;
    private static string RustJoin(string first, string second) => first.Length == 0 ? second : second.Length == 0 ? first : first + "::" + second;
    private sealed record RustLocation(Dictionary<string, string> Crate, string Module);
}
