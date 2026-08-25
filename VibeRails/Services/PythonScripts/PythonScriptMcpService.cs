using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ModelContextProtocol.Protocol;
using Serilog;
using VibeRails.DTOs;
using VibeRails.Utils;

namespace VibeRails.Services.PythonScripts;

public interface IPythonScriptMcpConfigurationStore
{
    Task<PythonScriptMcpDocument> ReadAsync(CancellationToken cancellationToken = default);
    Task<PythonScriptMcpDocument> SaveAsync(
        PythonScriptMcpConfiguration configuration,
        CancellationToken cancellationToken = default);
    Task<PythonScriptMcpDocument> DeleteAsync(
        string? scriptName,
        CancellationToken cancellationToken = default);
    Task<PythonScriptMcpDocument> RenameAsync(
        string oldScriptName,
        string newScriptName,
        CancellationToken cancellationToken = default);
}

public interface IPythonScriptMcpService
{
    Task<PythonScriptMcpListResponse> GetAsync(CancellationToken cancellationToken = default);
    Task<PythonScriptMcpListResponse> SaveAsync(
        PythonScriptMcpConfigurationRequest request,
        CancellationToken cancellationToken = default);
    Task<PythonScriptMcpListResponse> DeleteAsync(
        string? scriptName,
        CancellationToken cancellationToken = default);
    Task<IList<Tool>> ListToolsAsync(CancellationToken cancellationToken = default);
    Task<CallToolResult> CallAsync(
        string? toolName,
        IDictionary<string, JsonElement>? arguments,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Atomic, cross-process persistence for the user's Python-to-MCP mappings. The PIN is
/// deliberately absent from this document; it is checked by <see cref="IPythonScriptService"/>
/// before a configuration reaches this store.
/// </summary>
public sealed class PythonScriptMcpConfigurationStore : IPythonScriptMcpConfigurationStore
{
    public const string FileName = "python_script_mcp.json";
    internal const int DocumentVersion = 1;

    private readonly string _installDirectory;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public PythonScriptMcpConfigurationStore(string? installDirectory = null)
    {
        _installDirectory = installDirectory ?? PathConstants.GetInstallDirPath();
    }

    private string DocumentPath => Path.Combine(_installDirectory, FileName);
    private string LockPath => DocumentPath + ".lock";

    public Task<PythonScriptMcpDocument> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ReadDocument());
    }

    public async Task<PythonScriptMcpDocument> SaveAsync(
        PythonScriptMcpConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        return await UpdateAsync(document =>
        {
            var configurations = document.Configurations
                .Where(item => !NameEquals(item.ScriptName, configuration.ScriptName))
                .ToList();
            configurations.Add(configuration);
            configurations.Sort((left, right) =>
                StringComparer.OrdinalIgnoreCase.Compare(left.ScriptName, right.ScriptName));
            return new PythonScriptMcpDocument(DocumentVersion, configurations);
        }, cancellationToken);
    }

    public async Task<PythonScriptMcpDocument> DeleteAsync(
        string? scriptName,
        CancellationToken cancellationToken = default)
    {
        var name = (scriptName ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            throw new PythonScriptValidationException("Choose a Python script to remove from MCP.");
        }

        return await UpdateAsync(document => new PythonScriptMcpDocument(
            DocumentVersion,
            document.Configurations.Where(item => !NameEquals(item.ScriptName, name)).ToList()),
            cancellationToken);
    }

    public async Task<PythonScriptMcpDocument> RenameAsync(
        string oldScriptName,
        string newScriptName,
        CancellationToken cancellationToken = default)
    {
        return await UpdateAsync(document => new PythonScriptMcpDocument(
            DocumentVersion,
            document.Configurations.Select(item => NameEquals(item.ScriptName, oldScriptName)
                ? item with { ScriptName = newScriptName }
                : item).ToList()), cancellationToken);
    }

    private async Task<PythonScriptMcpDocument> UpdateAsync(
        Func<PythonScriptMcpDocument, PythonScriptMcpDocument> update,
        CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_installDirectory);
            await using var crossProcessLock = await AcquireCrossProcessLockAsync(cancellationToken);
            var updated = update(ReadDocument());
            WriteDocument(updated);
            return updated;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<FileStream> AcquireCrossProcessLockAsync(CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + 10_000;
        var delayMs = 25;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    LockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.Asynchronous);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (Environment.TickCount64 >= deadline)
                {
                    throw new PythonScriptValidationException(
                        "The Python MCP configuration is in use. Try again in a moment.");
                }
            }

            await Task.Delay(delayMs, cancellationToken);
            delayMs = Math.Min(delayMs * 2, 250);
        }
    }

    private PythonScriptMcpDocument ReadDocument()
    {
        try
        {
            if (!File.Exists(DocumentPath))
            {
                return EmptyDocument();
            }

            var document = JsonSerializer.Deserialize(
                File.ReadAllText(DocumentPath),
                AppJsonSerializerContext.Default.PythonScriptMcpDocument);
            return document is { Version: DocumentVersion, Configurations: not null }
                ? document
                : EmptyDocument();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "[PythonScripts] Could not read {Path}; treating MCP exposure as empty.", DocumentPath);
            return EmptyDocument();
        }
    }

    private void WriteDocument(PythonScriptMcpDocument document)
    {
        var json = JsonSerializer.Serialize(
            document,
            AppJsonSerializerContext.Default.PythonScriptMcpDocument);
        var temporaryPath = $"{DocumentPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, DocumentPath, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "[PythonScripts] MCP temp cleanup failed for {Path}", temporaryPath);
            }
        }
    }

    private static PythonScriptMcpDocument EmptyDocument() =>
        new(DocumentVersion, []);

    private static bool NameEquals(string left, string right) =>
        string.Equals(left, right, StringComparison.Ordinal);
}

/// <summary>
/// Validates MCP metadata, builds dynamic tool schemas, maps typed MCP inputs to argv,
/// and executes only scripts that still satisfy the signed-script hash gate.
/// </summary>
public sealed partial class PythonScriptMcpService : IPythonScriptMcpService
{
    public const string ArgumentModePositional = "positional";
    public const string ArgumentModeOption = "option";
    public const string TypeString = "string";
    public const string TypeInteger = "integer";
    public const string TypeNumber = "number";
    public const string TypeBoolean = "boolean";

    // What the script author says running the tool does. The four MCP annotation hints are
    // derived from this plus RepeatSafe/ReachesNetwork; see BuildAnnotations.
    public const string BehaviorReadOnly = "read-only";
    public const string BehaviorAdditive = "additive";
    public const string BehaviorDestructive = "destructive";

    private const int MaxParameters = 32;
    private static readonly HashSet<string> ReservedToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "validate_vca",
        "search_history",
        "pause_token_saver",
        "resume_token_saver",
        "get_token_saver_status",
        "python_script_signing_help"
    };

    private readonly IPythonScriptService _pythonScriptService;
    private readonly IPythonScriptMcpConfigurationStore _store;

    public PythonScriptMcpService(
        IPythonScriptService pythonScriptService,
        IPythonScriptMcpConfigurationStore store)
    {
        _pythonScriptService = pythonScriptService;
        _store = store;
    }

    public async Task<PythonScriptMcpListResponse> GetAsync(
        CancellationToken cancellationToken = default)
    {
        var document = await _store.ReadAsync(cancellationToken);
        return new PythonScriptMcpListResponse(document.Configurations);
    }

    public async Task<PythonScriptMcpListResponse> SaveAsync(
        PythonScriptMcpConfigurationRequest request,
        CancellationToken cancellationToken = default)
    {
        var canonicalScriptName = await _pythonScriptService.AuthorizeMcpExposureAsync(
            request.ScriptName,
            request.Pin,
            cancellationToken);
        var document = await _store.ReadAsync(cancellationToken);
        var configuration = ValidateConfiguration(request, canonicalScriptName, document.Configurations);
        var updated = await _store.SaveAsync(configuration, cancellationToken);
        return new PythonScriptMcpListResponse(updated.Configurations);
    }

    public async Task<PythonScriptMcpListResponse> DeleteAsync(
        string? scriptName,
        CancellationToken cancellationToken = default)
    {
        var updated = await _store.DeleteAsync(scriptName, cancellationToken);
        return new PythonScriptMcpListResponse(updated.Configurations);
    }

    public async Task<IList<Tool>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        var document = await _store.ReadAsync(cancellationToken);
        var scripts = await _pythonScriptService.GetStatusAsync(cancellationToken);
        var existingNames = scripts.Scripts
            .Select(script => script.Name)
            .ToHashSet(StringComparer.Ordinal);

        var scriptsDirectory = _pythonScriptService.GetScriptsDirectory();
        var tools = new List<Tool>();
        foreach (var configuration in document.Configurations.Where(configuration =>
            existingNames.Contains(configuration.ScriptName)))
        {
            try
            {
                tools.Add(ToTool(
                    ValidateStoredConfiguration(configuration, document.Configurations),
                    scriptsDirectory));
            }
            catch (PythonScriptValidationException ex)
            {
                Log.Warning(ex, "[PythonScripts] Skipping invalid MCP configuration for {Script}",
                    configuration.ScriptName);
            }
        }
        return tools;
    }

    public async Task<CallToolResult> CallAsync(
        string? toolName,
        IDictionary<string, JsonElement>? arguments,
        CancellationToken cancellationToken = default)
    {
        var document = await _store.ReadAsync(cancellationToken);
        var configuration = document.Configurations.FirstOrDefault(item =>
            string.Equals(item.ToolName, toolName, StringComparison.Ordinal));
        if (configuration == null)
        {
            return Error($"Unknown Python script MCP tool '{toolName}'.");
        }

        try
        {
            configuration = ValidateStoredConfiguration(configuration, document.Configurations);
            var argv = BuildArguments(configuration, arguments);
            var run = await _pythonScriptService.RunAsync(
                configuration.ScriptName,
                argv,
                cancellationToken);
            var output = FormatRunResult(run);
            return new CallToolResult
            {
                IsError = run.TimedOut || run.ExitCode != 0,
                Content = [new TextContentBlock { Text = output }]
            };
        }
        catch (PythonScriptValidationException ex)
        {
            return Error(ex.Message);
        }
    }

    private static PythonScriptMcpConfiguration ValidateConfiguration(
        PythonScriptMcpConfigurationRequest request,
        string canonicalScriptName,
        IReadOnlyList<PythonScriptMcpConfiguration> existing)
    {
        var toolName = (request.ToolName ?? string.Empty).Trim();
        if (!ToolNamePattern().IsMatch(toolName))
        {
            throw new PythonScriptValidationException(
                "MCP tool names must be 1-64 letters, numbers, underscores, periods, or hyphens.");
        }
        if (ReservedToolNames.Contains(toolName))
        {
            throw new PythonScriptValidationException($"'{toolName}' is reserved by VibeRails.");
        }
        if (existing.Any(item =>
            !string.Equals(item.ScriptName, canonicalScriptName, StringComparison.Ordinal)
            && string.Equals(item.ToolName, toolName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new PythonScriptValidationException($"An MCP tool named '{toolName}' already exists.");
        }

        var description = (request.Description ?? string.Empty).Trim();
        if (description.Length is < 1 or > 500)
        {
            throw new PythonScriptValidationException(
                "Describe when the agent should run this script (1-500 characters).");
        }

        var behavior = (request.Behavior ?? string.Empty).Trim().ToLowerInvariant();
        if (behavior is not (BehaviorReadOnly or BehaviorAdditive or BehaviorDestructive))
        {
            throw new PythonScriptValidationException(
                "Declare what the script does: 'read-only', 'additive', or 'destructive'.");
        }

        var requestedParameters = request.Parameters ?? [];
        if (requestedParameters.Count > MaxParameters)
        {
            throw new PythonScriptValidationException($"A script can expose at most {MaxParameters} MCP inputs.");
        }

        var parameters = new List<PythonScriptMcpParameter>(requestedParameters.Count);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var optionalPositionalWithoutDefaultSeen = false;
        foreach (var requested in requestedParameters)
        {
            var name = (requested.Name ?? string.Empty).Trim();
            if (!ParameterNamePattern().IsMatch(name) || !names.Add(name))
            {
                throw new PythonScriptValidationException(
                    "Each MCP input needs a unique Python-style name (letters, numbers, and underscores).");
            }

            var parameterDescription = (requested.Description ?? string.Empty).Trim();
            if (parameterDescription.Length is < 1 or > 300)
            {
                throw new PythonScriptValidationException($"Describe the MCP input '{name}' (1-300 characters).");
            }

            var type = NormalizeType(requested.Type, name);
            var mode = NormalizeMode(requested.ArgumentMode, name);
            var flag = mode == ArgumentModeOption ? (requested.Flag ?? string.Empty).Trim() : null;
            if (mode == ArgumentModeOption && !OptionFlagPattern().IsMatch(flag!))
            {
                throw new PythonScriptValidationException(
                    $"The option for '{name}' must look like --output or -o.");
            }

            if (requested.Required && requested.DefaultValue is not null)
            {
                throw new PythonScriptValidationException(
                    $"'{name}' cannot be required and have a default value at the same time.");
            }

            var defaultValue = NormalizeDefault(requested.DefaultValue, type, name);
            if (mode == ArgumentModePositional)
            {
                if (optionalPositionalWithoutDefaultSeen)
                {
                    throw new PythonScriptValidationException(
                        "An optional positional input without a default must be the final positional input.");
                }
                if (!requested.Required && defaultValue is null)
                {
                    optionalPositionalWithoutDefaultSeen = true;
                }
            }

            parameters.Add(new PythonScriptMcpParameter(
                name,
                parameterDescription,
                type,
                requested.Required,
                defaultValue,
                mode,
                flag));
        }

        return new PythonScriptMcpConfiguration(
            canonicalScriptName,
            toolName,
            description,
            parameters,
            behavior,
            // Nothing changes, so re-running changes nothing more. The author does not get to
            // claim otherwise, and does not have to think about it.
            behavior == BehaviorReadOnly || request.RepeatSafe,
            request.ReachesNetwork);
    }

    private static PythonScriptMcpConfiguration ValidateStoredConfiguration(
        PythonScriptMcpConfiguration configuration,
        IReadOnlyList<PythonScriptMcpConfiguration> existing)
    {
        return ValidateConfiguration(
            new PythonScriptMcpConfigurationRequest(
                configuration.ScriptName,
                configuration.ToolName,
                configuration.Description,
                configuration.Parameters,
                configuration.Behavior,
                configuration.RepeatSafe,
                configuration.ReachesNetwork,
                Pin: null),
            configuration.ScriptName,
            existing);
    }

    private static Tool ToTool(PythonScriptMcpConfiguration configuration, string scriptsDirectory)
    {
        var scriptPath = Path.Combine(scriptsDirectory, configuration.ScriptName);
        return new Tool
        {
            Name = configuration.ToolName,
            Title = configuration.ScriptName,
            // Tell the caller where the source is and invite it to read it. An agent that can see
            // what a script does before running it is a stronger gate than any hint we ship.
            Description = configuration.Description
                + "\n\nSource: " + scriptPath
                + "\nRead this file to verify what it does before relying on it.",
            InputSchema = BuildInputSchema(configuration.Parameters),
            Annotations = BuildAnnotations(configuration),
            Meta = new JsonObject
            {
                ["viberailsCategory"] = "python-script",
                ["scriptName"] = configuration.ScriptName,
                ["scriptPath"] = scriptPath
            }
        };
    }

    /// <summary>
    /// The author's declaration mapped onto MCP's four behavior hints. All four are sent
    /// explicitly: an unspecified <c>destructiveHint</c> means "assume destructive" and an
    /// unspecified <c>idempotentHint</c> means "assume not idempotent", so omitting them would
    /// misrepresent a read-only tool to any client that does not also read <c>readOnlyHint</c>.
    /// </summary>
    private static ToolAnnotations BuildAnnotations(PythonScriptMcpConfiguration configuration)
    {
        return new ToolAnnotations
        {
            Title = configuration.ScriptName,
            ReadOnlyHint = configuration.Behavior == BehaviorReadOnly,
            DestructiveHint = configuration.Behavior == BehaviorDestructive,
            IdempotentHint = configuration.RepeatSafe,
            OpenWorldHint = configuration.ReachesNetwork
        };
    }

    private static JsonElement BuildInputSchema(IReadOnlyList<PythonScriptMcpParameter> parameters)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var parameter in parameters)
        {
            var property = new JsonObject
            {
                ["type"] = parameter.Type,
                ["description"] = parameter.Description
            };
            if (parameter.DefaultValue is not null)
            {
                property["default"] = DefaultNode(parameter.DefaultValue, parameter.Type!);
            }
            properties[parameter.Name!] = property;
            if (parameter.Required) required.Add(parameter.Name);
        }

        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false
        };
        if (required.Count > 0) schema["required"] = required;
        using var document = JsonDocument.Parse(schema.ToJsonString());
        return document.RootElement.Clone();
    }

    private static List<string> BuildArguments(
        PythonScriptMcpConfiguration configuration,
        IDictionary<string, JsonElement>? suppliedArguments)
    {
        suppliedArguments ??= new Dictionary<string, JsonElement>();
        var configuredNames = configuration.Parameters
            .Select(parameter => parameter.Name!)
            .ToHashSet(StringComparer.Ordinal);
        var unexpected = suppliedArguments.Keys.FirstOrDefault(name => !configuredNames.Contains(name));
        if (unexpected is not null)
        {
            throw new PythonScriptValidationException($"Unexpected MCP input '{unexpected}'.");
        }

        var positional = new List<string>();
        var options = new List<string>();
        foreach (var parameter in configuration.Parameters)
        {
            string? value;
            if (suppliedArguments.TryGetValue(parameter.Name!, out var supplied))
            {
                value = FormatJsonValue(supplied, parameter.Type!, parameter.Name!);
            }
            else if (parameter.DefaultValue is not null)
            {
                value = parameter.DefaultValue;
            }
            else if (parameter.Required)
            {
                throw new PythonScriptValidationException($"Missing required MCP input '{parameter.Name}'.");
            }
            else
            {
                continue;
            }

            if (parameter.ArgumentMode == ArgumentModePositional)
            {
                positional.Add(value);
            }
            else if (parameter.Type == TypeBoolean)
            {
                if (bool.Parse(value)) options.Add(parameter.Flag!);
            }
            else
            {
                options.Add(parameter.Flag!);
                options.Add(value);
            }
        }

        positional.AddRange(options);
        return positional;
    }

    private static string FormatJsonValue(JsonElement value, string type, string name)
    {
        return type switch
        {
            TypeString when value.ValueKind == JsonValueKind.String => value.GetString() ?? string.Empty,
            TypeInteger when value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var integer) =>
                integer.ToString(CultureInfo.InvariantCulture),
            TypeNumber when value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
                && double.IsFinite(number) => number.ToString("R", CultureInfo.InvariantCulture),
            TypeBoolean when value.ValueKind is JsonValueKind.True or JsonValueKind.False =>
                value.GetBoolean().ToString().ToLowerInvariant(),
            _ => throw new PythonScriptValidationException(
                $"MCP input '{name}' must be a JSON {type}.")
        };
    }

    private static string NormalizeType(string? value, string name)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is TypeString or TypeInteger or TypeNumber or TypeBoolean
            ? normalized
            : throw new PythonScriptValidationException(
                $"MCP input '{name}' has an unsupported type.");
    }

    private static string NormalizeMode(string? value, string name)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized is ArgumentModePositional or ArgumentModeOption
            ? normalized
            : throw new PythonScriptValidationException(
                $"Choose positional or named option mapping for '{name}'.");
    }

    private static string? NormalizeDefault(string? value, string type, string name)
    {
        if (value is null) return null;
        return type switch
        {
            TypeString => value,
            TypeInteger when long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer) =>
                integer.ToString(CultureInfo.InvariantCulture),
            TypeNumber when double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
                && double.IsFinite(number) => number.ToString("R", CultureInfo.InvariantCulture),
            TypeBoolean when bool.TryParse(value, out var boolean) => boolean.ToString().ToLowerInvariant(),
            _ => throw new PythonScriptValidationException(
                $"The default for '{name}' is not a valid {type}.")
        };
    }

    private static JsonNode DefaultNode(string value, string type) => type switch
    {
        TypeInteger => JsonValue.Create(long.Parse(value, CultureInfo.InvariantCulture)),
        TypeNumber => JsonValue.Create(double.Parse(value, CultureInfo.InvariantCulture)),
        TypeBoolean => JsonValue.Create(bool.Parse(value)),
        _ => JsonValue.Create(value)
    };

    private static string FormatRunResult(PythonScriptRunResponse run)
    {
        var lines = new List<string>
        {
            $"Script: {run.Name}",
            $"Exit code: {run.ExitCode}",
            $"Timed out: {run.TimedOut.ToString().ToLowerInvariant()}"
        };
        // Ahead of stdout, and not merely because it is the answer: ReturnJson is extracted
        // from the FULL stdout while the copy below is capped, so a chatty script's return
        // value can be the one thing the truncation cuts. Printing it here is the difference
        // between the caller reading a value and re-parsing a transcript that no longer has it.
        if (!string.IsNullOrWhiteSpace(run.ReturnJson))
        {
            lines.Add(string.Empty);
            lines.Add("Return value:");
            lines.Add(run.ReturnJson);
        }
        if (!string.IsNullOrWhiteSpace(run.StandardOutput))
        {
            lines.Add(string.Empty);
            lines.Add("stdout:");
            lines.Add(run.StandardOutput.TrimEnd());
        }
        if (!string.IsNullOrWhiteSpace(run.StandardError))
        {
            lines.Add(string.Empty);
            lines.Add("stderr:");
            lines.Add(run.StandardError.TrimEnd());
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static CallToolResult Error(string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = message }]
    };

    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_.-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ToolNamePattern();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ParameterNamePattern();

    [GeneratedRegex(@"^--?[A-Za-z0-9][A-Za-z0-9-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex OptionFlagPattern();
}
