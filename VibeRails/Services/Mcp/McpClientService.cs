using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Text.Json.Serialization;
using VibeRails.Interfaces;

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
            _logger.LogError(ex, "Failed to list MCP tools.");
            throw;
        }
    }

    public async Task<string> CallToolAsync(string toolName, Dictionary<string, object?> arguments, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Calling MCP tool '{ToolName}'...", toolName);
        try
        {
            var result = await _client.CallToolAsync(
                toolName,
                arguments,
                null,
                null,
                cancellationToken);

            // Extract text from the first content block
            var text = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? string.Empty;

            if (string.IsNullOrEmpty(text))
            {
                _logger.LogWarning("Tool '{ToolName}' returned no text content.", toolName);
            }

            return text;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error calling MCP tool '{ToolName}'.", toolName);
            throw;
        }
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

/// <summary>
/// Required for Native AOT to handle JSON serialization of MCP tool arguments.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(string))]
internal partial class McpSdkJsonContext : JsonSerializerContext
{
}
