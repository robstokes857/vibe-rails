using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using VibeRails.Interfaces;
using System.Globalization;
using System.Text;

namespace VibeRails.Services.Mcp;

public class McpClientService : IMcpService
{
    private readonly McpClient _client;
    private readonly ILogger<McpClientService> _logger;

    internal McpClientService(McpClient client, ILogger<McpClientService> logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger ?? NullLogger<McpClientService>.Instance;
    }

    /// <summary>
    /// Gets a builder to configure and create an McpClientService.
    /// </summary>
    public static McpClientBuilder CreateBuilder() => new McpClientBuilder();

    /// <summary>
    /// Creates and connects an MCP client with the specified transport.
    /// </summary>
    public static async Task<McpClientService> ConnectAsync(
        IClientTransport transport,
        ILogger<McpClientService>? logger = null,
        string clientName = "viberails-client",
        string version = "1.0.0",
        CancellationToken cancellationToken = default)
    {
        return await CreateBuilder()
            .WithTransport(transport)
            .WithLogger(logger)
            .WithClientInfo(clientName, version)
            .BuildAsync(cancellationToken);
    }

    public async Task<IEnumerable<McpClientTool>> GetAvailableToolsAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Listing available MCP tools...");
        try
        {
            var result = await _client.ListToolsAsync((RequestOptions?)null, cancellationToken);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError("Failed to list MCP tools: {Error}", EscapeLogText(ex.ToString()));
            throw;
        }
    }

    public async Task<McpToolCallOutcome> CallToolAsync(string toolName, Dictionary<string, object?> arguments, CancellationToken cancellationToken = default)
    {
        // Preserve diagnostic content, but encode untrusted controls so a value cannot
        // impersonate another log record. Do not pass the raw exception to the log sink.
        var logToolName = EscapeLogText(toolName);
        _logger.LogInformation("Calling MCP tool '{ToolName}'...", logToolName);
        try
        {
            var result = await _client.CallToolAsync(
                toolName,
                arguments,
                null,
                null,
                cancellationToken);

            // MCP reports tool-level failures as a normal result with IsError=true (not a thrown
            // fault), so surface that flag instead of returning the error text as if it were output.
            // IsError is nullable on the wire; an absent flag means success.
            var isError = result.IsError ?? false;
            var text = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? string.Empty;

            if (isError)
            {
                _logger.LogWarning("Tool '{ToolName}' reported an error: {Error}", logToolName, EscapeLogText(text));
            }
            else if (string.IsNullOrEmpty(text))
            {
                _logger.LogWarning("Tool '{ToolName}' returned no text content.", logToolName);
            }

            return new McpToolCallOutcome(isError, text);
        }
        catch (Exception ex)
        {
            _logger.LogError("Error calling MCP tool '{ToolName}': {Error}", logToolName, EscapeLogText(ex.ToString()));
            throw;
        }
    }

    /// <summary>Preserves complete log values while making control characters visible and inert.</summary>
    internal static string EscapeLogText(string value)
    {
        // Explicit CR/LF replacement also makes the log-forging boundary visible to CodeQL.
        var line = value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
        var safe = new StringBuilder(line.Length);
        foreach (var character in line)
        {
            var category = char.GetUnicodeCategory(character);
            if (char.IsControl(character) || category is UnicodeCategory.Format
                or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)
                safe.Append("\\u").Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
            else
                safe.Append(character);
        }
        return safe.ToString();
    }

    public async Task<bool> PingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _client.PingAsync((RequestOptions?)null, cancellationToken);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _logger.LogInformation("Shutting down MCP Client.");
        await _client.DisposeAsync();
    }
}

/// <summary>
/// Builder for creating configured McpClientService instances.
/// </summary>
public class McpClientBuilder
{
    private IClientTransport? _transport;
    private ILogger<McpClientService>? _logger;
    private string _clientName = "viberails-client";
    private string _version = "1.0.0";

    public McpClientBuilder WithTransport(IClientTransport transport)
    {
        _transport = transport;
        return this;
    }

    public McpClientBuilder WithLogger(ILogger<McpClientService>? logger)
    {
        _logger = logger;
        return this;
    }

    public McpClientBuilder WithClientInfo(string name, string version)
    {
        _clientName = name;
        _version = version;
        return this;
    }

    public async Task<McpClientService> BuildAsync(CancellationToken cancellationToken = default)
    {
        if (_transport == null) throw new InvalidOperationException("Transport must be set.");

        var options = new McpClientOptions
        {
            ClientInfo = new() { Name = _clientName, Version = _version }
        };

        var client = await McpClient.CreateAsync(_transport, options, null, cancellationToken);
        return new McpClientService(client, _logger ?? NullLogger<McpClientService>.Instance);
    }
}
