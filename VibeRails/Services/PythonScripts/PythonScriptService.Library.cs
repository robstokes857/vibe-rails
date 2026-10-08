using System.Text.Json;
using VibeRails.DTOs;
using VibeRails.Utils;

namespace VibeRails.Services.PythonScripts;

public sealed partial class PythonScriptService
{
    public const string LibraryFileName = "user_script_library.json";
    private readonly Func<string?> _projectRoot;
    private readonly bool _allProjects;
    private string LibraryPath => Path.Combine(_installDirectory, LibraryFileName);
    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private UserScriptLibrary ReadLibrary()
    {
        try
        {
            if (!File.Exists(LibraryPath)) return new([]);
            return JsonSerializer.Deserialize(File.ReadAllText(LibraryPath),
                AppJsonSerializerContext.Default.UserScriptLibrary)
                ?? throw new PythonScriptValidationException("The script library could not be read.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            throw new PythonScriptValidationException("The script library could not be read. Try again after checking the library file.");
        }
    }

    // All callers hold the same cross-process lock as signing. Atomic replacement keeps
    // readers in other instances from observing a partially written registration document.
    private void WriteLibrary(UserScriptLibrary library)
    {
        Directory.CreateDirectory(_installDirectory);
        var temporary = LibraryPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(library,
                AppJsonSerializerContext.Default.UserScriptLibrary));
            File.Move(temporary, LibraryPath, overwrite: true);
        }
        finally { TryDeleteTemporaryFile(temporary); }
    }

    private string? CurrentProject()
    {
        var root = _projectRoot();
        return string.IsNullOrWhiteSpace(root) ? null : Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    }

    private bool IsVisible(UserScriptRegistration entry) => _allProjects || entry.ProjectPath == null
        || string.Equals(entry.ProjectPath, CurrentProject(), PathComparison);

    private UserScriptRegistration Registration(string? identifier)
    {
        var key = ValidateScriptName(identifier);
        var visible = ReadLibrary().Scripts.Where(IsVisible).ToList();
        var byId = visible.FirstOrDefault(item => item.Id == key);
        if (byId != null) return byId;
        // Names remain a convenience for the local signing helper; UI callers always use
        // the stable ID, so equal file names in different directories cannot collide.
        var matches = visible.Where(item => string.Equals(Path.GetFileName(item.Path), key, PathComparison)).ToList();
        if (matches.Count != 1)
            throw new PythonScriptValidationException(matches.Count == 0
                ? "That script is not registered in this project. Add it from disk first."
                : "More than one script has that file name. Select the script by its library ID.");
        return matches[0];
    }

    private string RegisteredName(string? identifier) => Registration(identifier).Id;

    private string? ProjectForScope(string scope, string path)
    {
        if (scope == "global") return null;
        if (scope != "repo") throw new PythonScriptValidationException("Choose Global or Repo scope.");
        var project = CurrentProject();
        if (project == null) throw new PythonScriptValidationException("Open a repository before adding a Repo script.");
        // Managed files can belong to the current repo. External Repo files must actually
        // live in it; a different checkout cannot accidentally inherit this launch button.
        if (!IsWithin(path, project) && !IsWithin(path, GetScriptsDirectory()))
            throw new PythonScriptValidationException("For Repo scope, select a script inside the current repository or the UserScripts folder.");
        return project;
    }

    private static bool IsWithin(string path, string directory) => path.StartsWith(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar, PathComparison);

    private static string DisplayName(string? value, string path)
    {
        var name = string.IsNullOrWhiteSpace(value) ? Path.GetFileName(path) : value.Trim();
        if (name.Length > 200 || name.Any(char.IsControl))
            throw new PythonScriptValidationException("Display names must be at most 200 characters and contain no control characters.");
        return name;
    }

    private string ValidateLibraryPath(string? requested, bool directory = false)
    {
        if (string.IsNullOrWhiteSpace(requested) || !Path.IsPathFullyQualified(requested)
            || IsNetworkOrDevicePath(requested))
            throw new PythonScriptValidationException("Choose a fully qualified path on a local drive. Network and device paths are not supported.");
        string path;
        try { path = Path.GetFullPath(requested); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new PythonScriptValidationException("That is not a valid local path."); }
        if (IsNetworkOrDevicePath(path) || (OperatingSystem.IsWindows() && IsUnsupportedWindowsDrive(path)))
            throw new PythonScriptValidationException("Network and device paths are not supported.");
        if (path.Any(char.IsControl) || (OperatingSystem.IsWindows()
            && path[Path.GetPathRoot(path)!.Length..].Split(['\\', '/'])
                .Any(part => part.EndsWith('.') || part.EndsWith(' ') || part.Contains(':'))))
            throw new PythonScriptValidationException("Choose a local path without ambiguous file or folder names.");
        var internalScripts = Path.Combine(_installDirectory, "scripts");
        if ((IsWithin(path, internalScripts) || string.Equals(path, internalScripts, PathComparison))
            && !IsWithin(path, GetScriptsDirectory())
            && !string.Equals(path, GetScriptsDirectory(), PathComparison))
            throw new PythonScriptValidationException("That folder contains internal VibeRails scripts. Choose UserScripts or a repository file.");
        ValidatePathComponents(path);
        if (!directory) ValidateScriptName(Path.GetFileName(path).ToLowerInvariant());
        return path;
    }

    private static void ValidatePathComponents(string path)
    {
        // Revalidate every parent as well as the leaf on every read/write/run. A link
        // inserted after registration must never redirect the approved script.
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new PythonScriptValidationException("Symbolic links, junctions and reparse paths cannot be used for scripts.");
            }
        }
    }

    private string RegisteredHash(string id, byte[] content)
    {
        var entry = Registration(id);
        return ComputeCanonicalHash(entry.Id + "\n" + entry.Path, content);
    }

    /// <summary>Create a registered user file in the managed folder or a selected local folder.</summary>
    public async Task<PythonScriptListResponse> CreateAsync(PythonScriptSaveRequest request, CancellationToken cancellationToken = default)
    {
        var name = ValidateScriptName(request.Name);
        var existing = ReadLibrary().Scripts.FirstOrDefault(item => item.Id == name && IsVisible(item));
        if (existing != null)
        {
            using (await AcquireCrossProcessWriteLockAsync(cancellationToken))
            {
                existing = Registration(name);
                var restorePath = ValidateLibraryPath(existing.Path);
                if (PathEntryExists(restorePath))
                    throw new PythonScriptValidationException("The script already exists. Reopen it before saving.");
                WriteDocument(WithoutApprovals(ReadDocument(), existing.Id));
                await WriteNewFileAtomicallyAsync(restorePath, EncodeScriptContent(request.Content), name, cancellationToken);
            }
            return await GetStatusAsync(cancellationToken);
        }
        var directory = ValidateLibraryPath(string.IsNullOrWhiteSpace(request.Directory)
            ? GetScriptsDirectory() : request.Directory, directory: true);
        var path = ValidateLibraryPath(Path.Combine(directory, name));
        var bytes = EncodeScriptContent(request.Content);
        var entry = NewRegistration(path, request.DisplayName, request.Scope, request.RequirePinEachRun);
        using (await AcquireCrossProcessWriteLockAsync(cancellationToken))
        {
            var library = ReadLibrary();
            EnsureNotRegistered(library, path);
            Directory.CreateDirectory(directory);
            ValidateLibraryPath(path);
            await WriteNewFileAtomicallyAsync(path, bytes, name, cancellationToken);
            library.Scripts.Add(entry);
            WriteLibrary(library);
        }
        return await GetStatusAsync(cancellationToken);
    }

    /// <summary>Register the chosen file in place without copying or signing it.</summary>
    public async Task<PythonScriptListResponse> ImportAsync(PythonScriptImportRequest request, CancellationToken cancellationToken = default)
    {
        var path = ValidateLibraryPath(request.SourcePath);
        var entry = NewRegistration(path, request.DisplayName, request.Scope, request.RequirePinEachRun);
        using (await AcquireCrossProcessWriteLockAsync(cancellationToken))
        {
            DecodeUtf8OrThrow(ReadScriptBytes(path));
            var library = ReadLibrary();
            EnsureNotRegistered(library, path);
            library.Scripts.Add(entry);
            WriteLibrary(library);
        }
        return await GetStatusAsync(cancellationToken);
    }

    private UserScriptRegistration NewRegistration(string path, string? displayName, string scope, bool requirePin) =>
        new(Guid.NewGuid().ToString("N") + Path.GetExtension(path).ToLowerInvariant(), path,
            DisplayName(displayName, path), ProjectForScope(scope, path), requirePin);

    private static void EnsureNotRegistered(UserScriptLibrary library, string path)
    {
        if (library.Scripts.Any(entry => string.Equals(entry.Path, path, PathComparison)))
            throw new PythonScriptValidationException("That file is already registered. Edit its settings to change its scope or display name.");
    }

    /// <summary>Update presentation and visibility. Weakening a run PIN always requires the PIN.</summary>
    public async Task<PythonScriptListResponse> UpdateSettingsAsync(PythonScriptSettingsRequest request, CancellationToken cancellationToken = default)
    {
        using (await AcquireCrossProcessWriteLockAsync(cancellationToken))
        {
            var entry = Registration(request.Name);
            var existingRequirement = entry.RequirePinEachRun || (ReadDocument().RequirePinEachRunNames ?? []).Contains(entry.Id);
            if (existingRequirement != request.RequirePinEachRun)
            {
                var signing = ReadDocument();
                if (signing.Pin == null || !VerifyPin(signing, request.Pin))
                    throw new PythonScriptValidationException("Enter your signing PIN to change the run PIN requirement.");
            }
            if (!request.RequirePinEachRun && existingRequirement) {
                var document = ReadDocument();
                WriteDocument(document with { RequirePinEachRunNames = (document.RequirePinEachRunNames ?? []).Where(id => id != entry.Id).ToList() });
            }
            var updated = entry with { DisplayName = DisplayName(request.DisplayName, entry.Path),
                ProjectPath = ProjectForScope(request.Scope, entry.Path), RequirePinEachRun = request.RequirePinEachRun };
            var library = ReadLibrary();
            library.Scripts[library.Scripts.FindIndex(item => item.Id == entry.Id)] = updated;
            WriteLibrary(library);
        }
        return await GetStatusAsync(cancellationToken);
    }

    /// <summary>Rename a file in its current directory; the path-bound signature becomes invalid.</summary>
    public async Task<PythonScriptListResponse> RenameAsync(PythonScriptRenameRequest request, CancellationToken cancellationToken = default)
    {
        using (await AcquireCrossProcessWriteLockAsync(cancellationToken))
        {
            var entry = Registration(request.Name);
            var name = ValidateScriptName(request.NewName);
            var source = ValidateLibraryPath(entry.Path);
            ReadScriptBytes(source);
            var target = ValidateLibraryPath(Path.Combine(Path.GetDirectoryName(source)!, name));
            if (source == target) return await GetStatusAsync(cancellationToken);
            var library = ReadLibrary();
            if (!string.Equals(source, target, PathComparison)) EnsureNotRegistered(library, target);
            // Clear first: even a failed move cannot transfer an approval to a new file.
            WriteDocument(WithoutApprovals(ReadDocument(), entry.Id));
            File.Move(source, target);
            library.Scripts[library.Scripts.FindIndex(item => item.Id == entry.Id)] = entry with {
                Path = target, DisplayName = entry.DisplayName == Path.GetFileName(source) ? name : entry.DisplayName };
            WriteLibrary(library);
        }
        return await GetStatusAsync(cancellationToken);
    }

    /// <summary>Remove the registration, retaining the user's file on disk.</summary>
    public async Task<PythonScriptListResponse> DeleteAsync(string? name, CancellationToken cancellationToken = default)
    {
        using (await AcquireCrossProcessWriteLockAsync(cancellationToken))
        {
            var entry = Registration(name);
            var library = ReadLibrary();
            WriteDocument(WithoutApprovals(ReadDocument(), entry.Id));
            library.Scripts.RemoveAll(item => item.Id == entry.Id);
            WriteLibrary(library);
        }
        return await GetStatusAsync(cancellationToken);
    }
}
