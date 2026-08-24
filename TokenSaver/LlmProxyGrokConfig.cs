using System.Text;

namespace TokenSaver;

/// <summary>
/// Native Grok Build CLI side of the local LLM proxy. Mirrors
/// <see cref="LlmProxyClaudeConfig"/> (env-var injection) and Codex's
/// <c>env_http_headers</c> shape: Grok is pointed at
/// <c>/llm/cli-chat</c> → <c>cli-chat-proxy.grok.com</c> through
/// <c>GROK_CLI_CHAT_PROXY_BASE_URL</c> only, and sends
/// <c>viberails_session</c> / <c>viberails_tab</c> via
/// <c>env_http_headers</c> in the user's <c>~/.grok/config.toml</c>.
/// Token values stay in the process environment (see
/// <c>LocalLlmProxyContext</c>), never in the file or on argv.
///
/// <c>GROK_MODELS_BASE_URL</c> is deliberately not set: that switch forces
/// API-key auth and would drop a <c>grok login</c> session.
/// <c>Authorization</c>, <c>X-XAI-Token-Auth</c>, and Grok's other request
/// headers are not written or overwritten here.
///
/// Do not invent a <c>/llm/grok</c> sidecar or a second listener
/// (see <c>API_SEC.md</c> / <c>GROK_HARNES.md</c>).
/// </summary>
public static class LlmProxyGrokConfig
{
    public const string ChatProxyBaseUrlVariable = "GROK_CLI_CHAT_PROXY_BASE_URL";
    public const string ModelsBaseUrlVariable = "GROK_MODELS_BASE_URL";

    public const string SessionHeaderName = LlmProxyCodexConfig.SessionHeaderName;
    public const string TabHeaderName = LlmProxyCodexConfig.TabHeaderName;

    // Names only — the values are read from the process env when Grok builds its HTTP client.
    public const string SessionTokenVariable = "VIBERAILS_LLM_PROXY_SESSION_TOKEN";
    public const string TabTokenVariable = "VIBERAILS_LLM_PROXY_TAB_TOKEN";

    public const string UserConfigFileName = "config.toml";
    public const string DefaultHomeDirectoryName = ".grok";

    // grok-4.6 is the pinned tab model, grok-build the default-route model, and grok-4.5 the
    // default fork_secondary_model — a fork's calls ride the same proxy and 401 without the
    // mapping. env_http_headers is per-model only (a global [models] mapping is ignored —
    // verified against grok 1.0.5), so every model VibeRails expects to see must be listed.
    public static readonly string[] HeaderMappedModels = ["grok-4.6", "grok-build", "grok-4.5"];

    public static string BuildEnvHttpHeadersInlineTable() =>
        "{ " +
        $"\"{SessionHeaderName}\" = \"{SessionTokenVariable}\", " +
        $"\"{TabHeaderName}\" = \"{TabTokenVariable}\"" +
        " }";

    /// <summary>
    /// Points native Grok at <c>/llm/cli-chat/v1</c>. Subscription mode sets only
    /// <c>GROK_CLI_CHAT_PROXY_BASE_URL</c> so <c>grok login</c> session auth stays
    /// in charge. API mode also sets <c>GROK_MODELS_BASE_URL</c> so model discovery
    /// and inference both use <c>XAI_API_KEY</c> against <c>api.x.ai</c>.
    /// Session/tab tokens are injected separately via <c>AddProxyContactDetails</c>.
    /// </summary>
    public static IReadOnlyDictionary<string, string> BuildGrokProxyEnvironment(
        string apiBaseUrl,
        string? mode = null)
    {
        var chatBase = LlmProxyCliChatConfig.BuildCliChatBaseUrl(apiBaseUrl);
        var environment = new Dictionary<string, string>
        {
            [ChatProxyBaseUrlVariable] = chatBase
        };
        if (LlmProxyCliChatConfig.NormalizeMode(mode) == CodexLlmProxySettings.ModeApi)
            environment[ModelsBaseUrlVariable] = chatBase;
        return environment;
    }

    public static string? ResolveUserConfigPath(string? userProfilePath, string? grokHome)
    {
        if (!string.IsNullOrWhiteSpace(grokHome))
            return Path.Combine(grokHome.Trim(), UserConfigFileName);

        if (string.IsNullOrWhiteSpace(userProfilePath))
            return null;

        return Path.Combine(userProfilePath, DefaultHomeDirectoryName, UserConfigFileName);
    }

    /// <summary>
    /// Merges the two <c>env_http_headers</c> mappings onto every <see cref="HeaderMappedModels"/>
    /// table. Token values are never written. <c>base_url</c> is never written (the process port
    /// dies with VibeRails; env vars are the source of truth). Idempotent when the mappings are
    /// already present — including the nested-table form grok's own serializer normalizes to.
    ///
    /// Model keys with a dot are quoted (<c>[model."grok-4.6"]</c>): TOML splits unquoted dotted
    /// keys, so the unquoted form addresses the phantom table <c>model."grok-4"."6"</c> and grok
    /// never attaches the headers (verified against grok 1.0.5). Sections an earlier VibeRails
    /// wrote in that broken unquoted form are removed when they hold nothing but our mappings.
    /// </summary>
    public static string MergeEnvHttpHeaders(string? existingToml)
    {
        var text = existingToml ?? string.Empty;
        foreach (var model in HeaderMappedModels)
            text = RemoveLegacyPhantomSections(text, model);
        foreach (var model in HeaderMappedModels)
            text = EnsureModelEnvHttpHeaders(text, model);
        return text;
    }

    public static bool ContainsMappedEnvHttpHeaders(string? toml, string model)
    {
        if (string.IsNullOrEmpty(toml))
            return false;

        var nested = ExtractSection(toml, NestedHeadersTableHeader(model));
        if (nested is not null && SectionHasBothMappings(nested))
            return true;

        var section = ExtractSection(toml, ModelTableHeader(model));
        return section is not null && SectionHasBothMappings(section);
    }

    private static string EnsureModelEnvHttpHeaders(string toml, string model)
    {
        var nestedHeader = NestedHeadersTableHeader(model);
        var nested = ExtractSection(toml, nestedHeader);
        if (nested is not null)
            return SectionHasBothMappings(nested)
                ? toml
                : UpsertNestedHeaderAssignments(toml, nestedHeader, nested);

        var header = ModelTableHeader(model);
        var section = ExtractSection(toml, header);
        if (section is null)
        {
            var suffix = new StringBuilder();
            if (toml.Length > 0 && !toml.EndsWith('\n'))
                suffix.Append('\n');
            if (toml.Length > 0)
                suffix.Append('\n');
            suffix.Append(header).Append('\n');
            suffix.Append("env_http_headers = ").Append(BuildEnvHttpHeadersInlineTable()).Append('\n');
            return toml + suffix;
        }

        if (SectionHasBothMappings(section))
            return toml;

        return ReplaceSection(toml, header, WithInlineEnvHttpHeaders(section));
    }

    private static string WithInlineEnvHttpHeaders(string section)
    {
        var lines = SplitKeepEndings(section);
        if (lines.Count == 0)
            return section;

        var headerLine = lines[0];
        if (!headerLine.EndsWith('\n'))
            headerLine += "\n";
        var rest = new List<string>();
        var replaced = false;

        for (var i = 1; i < lines.Count; i++)
        {
            if (IsEnvHttpHeadersAssignment(lines[i]))
            {
                rest.Add(MergeInlineEnvHttpHeadersLine(lines[i]));
                replaced = true;
            }
            else
            {
                rest.Add(lines[i]);
            }
        }

        if (!replaced)
            rest.Insert(0, "env_http_headers = " + BuildEnvHttpHeadersInlineTable() + "\n");

        return headerLine + string.Concat(rest);
    }

    private static string UpsertNestedHeaderAssignments(string toml, string header, string section)
    {
        var updated = section;
        if (!ContainsHeaderMapping(section, SessionHeaderName, SessionTokenVariable))
            updated = AppendAssignment(updated, SessionHeaderName, SessionTokenVariable);
        if (!ContainsHeaderMapping(section, TabHeaderName, TabTokenVariable))
            updated = AppendAssignment(updated, TabHeaderName, TabTokenVariable);
        return ReplaceSection(toml, header, updated);
    }

    private static string AppendAssignment(string section, string headerName, string envVarName)
    {
        var builder = new StringBuilder(section);
        if (!section.EndsWith('\n'))
            builder.Append('\n');
        builder.Append('"').Append(headerName).Append("\" = \"").Append(envVarName).Append("\"\n");
        return builder.ToString();
    }

    private static bool SectionHasBothMappings(string section) =>
        ContainsHeaderMapping(section, SessionHeaderName, SessionTokenVariable)
        && ContainsHeaderMapping(section, TabHeaderName, TabTokenVariable);

    private static bool ContainsHeaderMapping(string section, string headerName, string envVarName)
    {
        return section.Contains(headerName, StringComparison.Ordinal)
            && section.Contains(envVarName, StringComparison.Ordinal);
    }

    private static string MergeInlineEnvHttpHeadersLine(string line)
    {
        if (ContainsHeaderMapping(line, SessionHeaderName, SessionTokenVariable)
            && ContainsHeaderMapping(line, TabHeaderName, TabTokenVariable))
        {
            return line;
        }

        var open = line.IndexOf('{');
        var close = line.LastIndexOf('}');
        if (open < 0 || close <= open)
            return "env_http_headers = " + BuildEnvHttpHeadersInlineTable() + TrailingNewline(line);

        var inner = line[(open + 1)..close].Trim();
        var additions = new List<string>();
        if (!ContainsHeaderMapping(inner, SessionHeaderName, SessionTokenVariable))
            additions.Add($"\"{SessionHeaderName}\" = \"{SessionTokenVariable}\"");
        if (!ContainsHeaderMapping(inner, TabHeaderName, TabTokenVariable))
            additions.Add($"\"{TabHeaderName}\" = \"{TabTokenVariable}\"");
        if (additions.Count == 0)
            return line;

        var mergedInner = string.IsNullOrEmpty(inner)
            ? string.Join(", ", additions)
            : inner.TrimEnd().TrimEnd(',') + ", " + string.Join(", ", additions);
        return line[..(open + 1)] + " " + mergedInner + " " + line[close..];
    }

    private static bool IsEnvHttpHeadersAssignment(string line)
    {
        var trimmed = line.AsSpan().TrimStart();
        return trimmed.StartsWith("env_http_headers", StringComparison.Ordinal)
            && trimmed.Contains('=');
    }

    private static string ModelTableHeader(string model) => $"[model.{TomlModelKey(model)}]";

    private static string NestedHeadersTableHeader(string model) =>
        $"[model.{TomlModelKey(model)}.env_http_headers]";

    /// <summary>
    /// The model name as a single TOML key. Bare keys allow only <c>A-Za-z0-9_-</c>; anything
    /// else (the dot in <c>grok-4.6</c>) must be quoted or the header addresses a different,
    /// nested table.
    /// </summary>
    private static string TomlModelKey(string model) =>
        IsBareTomlKey(model) ? model : $"\"{model}\"";

    private static bool IsBareTomlKey(string key)
    {
        if (key.Length == 0)
            return false;

        foreach (var c in key)
        {
            if (c is not ((>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '_'))
                return false;
        }

        return true;
    }

    /// <summary>
    /// Removes the sections an earlier VibeRails left under the UNQUOTED dotted header — both the
    /// form our writer produced (<c>[model.grok-4.6]</c> + inline table) and the nested form
    /// grok's serializer normalized it to (<c>[model.grok-4.6.env_http_headers]</c>). Those
    /// configure a phantom model, so grok never reads them. Only sections whose entire body is
    /// our own mapping content are removed; anything else in them is not ours to delete.
    /// </summary>
    private static string RemoveLegacyPhantomSections(string toml, string model)
    {
        if (TomlModelKey(model) == model)
            return toml; // The bare form IS the correct form for this model.

        toml = RemoveSectionIf(toml, $"[model.{model}.env_http_headers]", IsOurNestedMappingBody);
        toml = RemoveSectionIf(toml, $"[model.{model}]", IsOurInlineMappingBody);
        return toml;
    }

    private static bool IsOurNestedMappingBody(string body)
    {
        var sawMapping = false;
        foreach (var line in SplitKeepEndings(body))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;
            if (IsOurMappingPair(trimmed, SessionHeaderName, SessionTokenVariable)
                || IsOurMappingPair(trimmed, TabHeaderName, TabTokenVariable))
            {
                sawMapping = true;
                continue;
            }

            return false;
        }

        return sawMapping;
    }

    private static bool IsOurInlineMappingBody(string body)
    {
        var sawAssignment = false;
        foreach (var line in SplitKeepEndings(body))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;
            if (!sawAssignment && string.Equals(
                NormalizeSpaces(trimmed),
                "env_http_headers = " + BuildEnvHttpHeadersInlineTable(),
                StringComparison.Ordinal))
            {
                sawAssignment = true;
                continue;
            }

            return false;
        }

        return sawAssignment;
    }

    /// <summary>One <c>key = "ENV_VAR"</c> line, with the key bare or quoted.</summary>
    private static bool IsOurMappingPair(string line, string headerName, string envVarName)
    {
        var normalized = NormalizeSpaces(line);
        return string.Equals(normalized, $"{headerName} = \"{envVarName}\"", StringComparison.Ordinal)
            || string.Equals(normalized, $"\"{headerName}\" = \"{envVarName}\"", StringComparison.Ordinal);
    }

    private static string NormalizeSpaces(string line)
    {
        var builder = new StringBuilder(line.Length);
        var pendingSpace = false;
        foreach (var c in line)
        {
            if (c is ' ' or '\t')
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    private static string RemoveSectionIf(string toml, string header, Func<string, bool> bodyQualifies)
    {
        var start = IndexOfSectionHeader(toml, header);
        if (start < 0)
            return toml;

        var lineEnd = toml.IndexOf('\n', start);
        var bodyStart = lineEnd < 0 ? toml.Length : lineEnd + 1;
        var end = FindNextTableHeader(toml, bodyStart);
        if (!bodyQualifies(toml[bodyStart..end]))
            return toml;

        return string.Concat(toml.AsSpan(0, start), toml.AsSpan(end));
    }

    private static string? ExtractSection(string toml, string header)
    {
        var start = IndexOfSectionHeader(toml, header);
        if (start < 0)
            return null;

        var lineEnd = toml.IndexOf('\n', start);
        var bodyStart = lineEnd < 0 ? toml.Length : lineEnd + 1;
        var end = FindNextTableHeader(toml, bodyStart);
        return toml[start..end];
    }

    private static string ReplaceSection(string toml, string header, string replacement)
    {
        var start = IndexOfSectionHeader(toml, header);
        if (start < 0)
            return toml + (toml.EndsWith('\n') ? "" : "\n") + replacement;

        var lineEnd = toml.IndexOf('\n', start);
        var bodyStart = lineEnd < 0 ? toml.Length : lineEnd + 1;
        var end = FindNextTableHeader(toml, bodyStart);
        return string.Concat(toml.AsSpan(0, start), replacement, toml.AsSpan(end));
    }

    private static int IndexOfSectionHeader(string toml, string header)
    {
        var searchFrom = 0;
        while (searchFrom <= toml.Length)
        {
            var index = toml.IndexOf(header, searchFrom, StringComparison.Ordinal);
            if (index < 0)
                return -1;

            if (IsStartOfLine(toml, index) && IsEndOfHeaderLine(toml, index + header.Length))
                return index;

            searchFrom = index + header.Length;
        }

        return -1;
    }

    private static bool IsEndOfHeaderLine(string text, int afterHeader)
    {
        var i = afterHeader;
        while (i < text.Length && text[i] is ' ' or '\t')
            i++;
        return i == text.Length || text[i] is '\r' or '\n';
    }

    private static int FindNextTableHeader(string toml, int start)
    {
        for (var i = start; i < toml.Length; i++)
        {
            if (toml[i] == '[' && IsStartOfLine(toml, i))
                return i;
        }

        return toml.Length;
    }

    private static bool IsStartOfLine(string text, int index) =>
        index == 0 || text[index - 1] == '\n';

    private static List<string> SplitKeepEndings(string text)
    {
        var lines = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
                continue;

            lines.Add(text[start..(i + 1)]);
            start = i + 1;
        }

        if (start < text.Length)
            lines.Add(text[start..]);

        return lines;
    }

    private static string TrailingNewline(string line) =>
        line.EndsWith("\r\n", StringComparison.Ordinal) ? "\r\n"
        : line.EndsWith('\n') ? "\n"
        : "\n";
}
