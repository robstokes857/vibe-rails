using MintLint;

namespace VibeRails.Services.CodeReports;

/// <summary>Resolves explicit Python and Rust module syntax against the bounded repository catalog.</summary>
internal sealed class RepositoryModuleResolver
{
    private readonly HashSet<string> paths;
    private readonly Dictionary<string, SourceOutline> outlines;
    private readonly string[] pythonRoots;
    private readonly string[] cargoRoots;
    private readonly Dictionary<string, List<RustLocation>> rustLocations = new(StringComparer.Ordinal);

    internal RepositoryModuleResolver(IReadOnlyList<(string Path, SourceOutline? Outline)> files,
        IEnumerable<string>? catalogPaths = null)
    {
        paths = files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);
        outlines = files.Where(file => file.Outline is not null).ToDictionary(file => file.Path,
            file => file.Outline!, StringComparer.Ordinal);
        pythonRoots = PythonRoots().Distinct(StringComparer.Ordinal).ToArray();
        cargoRoots = (catalogPaths ?? paths).Where(path => path == "Cargo.toml" || path.EndsWith("/Cargo.toml", StringComparison.Ordinal))
            .Select(Directory).Distinct(StringComparer.Ordinal).OrderByDescending(path => path.Length).ToArray();
        BuildRustModules();
    }

    internal IEnumerable<(string Path, SourceOutlineImport Import)> Resolve(string source, SourceOutline outline)
    {
        foreach (var import in outline.ImportEvidence)
        {
            string? target = outline.Language switch
            {
                "Python" => ResolvePython(source, import),
                "Rust" => ResolveRust(source, import),
                _ => null
            };
            if (target is not null && target != source) yield return (target, import);
        }
    }

    private IEnumerable<string> PythonRoots()
    {
        yield return "";
        foreach (var path in paths.Where(path => path.EndsWith(".py", StringComparison.Ordinal)))
        {
            // A src directory is an explicit conventional import root, including namespace packages.
            var segments = Directory(path).Split('/', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 0; i < segments.Length; i++)
                if (segments[i] == "src" && !Enumerable.Range(1, i + 1)
                    .Any(length => paths.Contains(Join(string.Join('/', segments.Take(length)), "__init__.py"))))
                    yield return string.Join('/', segments.Take(i + 1));
            if (!path.EndsWith("/__init__.py", StringComparison.Ordinal)) continue;
            var package = Directory(path);
            while (Directory(package).Length > 0 && paths.Contains(Join(Directory(package), "__init__.py"))) package = Directory(package);
            yield return Directory(package);
        }
    }

    private string? ResolvePython(string source, SourceOutlineImport import)
    {
        if (import.Kind is not ("python-import" or "python-from")) return null;
        var dots = import.Path.TakeWhile(character => character == '.').Count();
        var module = import.Path[dots..];
        if (module.Length > 0 && !Names(module.Split('.'))) return null;
        var roots = pythonRoots.Where(root => Below(source, root)).OrderByDescending(root => root.Length).ToArray();
        var localRoot = roots.FirstOrDefault() ?? "";
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
            return PythonTarget(Join(package, module.Replace('.', '/')), import);
        }
        // Prefer the import root containing the source. Other roots are usable only if the full
        // qualified path is unique: never search by basename or attach an external symbol name.
        var local = PythonTarget(Join(localRoot, module.Replace('.', '/')), import);
        if (local is not null) return local;
        var matches = pythonRoots.Select(root => PythonTarget(Join(root, module.Replace('.', '/')), import))
            .Where(path => path is not null).Distinct(StringComparer.Ordinal).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
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
        // Conventional crate roots cover src/{lib,main}.rs and custom-layout main.rs/lib.rs targets.
        // Cargo aliases, generated modules and #[path] modules deliberately remain unresolved.
        var roots = paths.Where(path => path.EndsWith(".rs", StringComparison.Ordinal)
            && Path.GetFileName(path) is "main.rs" or "lib.rs").ToArray();
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
                foreach (var import in outline.ImportEvidence.Where(import => import.Kind is "rust-inline" or "rust-mod"))
                {
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
        else if (parts[0] != "super") module = "";
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

    private string? CargoOwner(string path) => cargoRoots.FirstOrDefault(root => Below(path, root));
    private string? UniqueExisting(string first, string second) => paths.Contains(first)
        ? paths.Contains(second) ? null : first : paths.Contains(second) ? second : null;
    private static bool Names(IEnumerable<string> parts) => parts.All(part => part.Length > 0
        && (char.IsLetter(part[0]) || part[0] == '_') && part.All(character => char.IsLetterOrDigit(character) || character == '_'));
    private static bool Below(string path, string directory) => directory.Length == 0
        || path.StartsWith(directory + "/", StringComparison.Ordinal);
    private static string Directory(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
    private static string Join(string first, string second) => first.Length == 0 ? second : second.Length == 0 ? first : first + "/" + second;
    private static string RustJoin(string first, string second) => first.Length == 0 ? second : second.Length == 0 ? first : first + "::" + second;
    private sealed record RustLocation(Dictionary<string, string> Crate, string Module);
}
