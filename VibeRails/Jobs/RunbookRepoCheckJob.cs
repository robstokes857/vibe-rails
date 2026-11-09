using System.Net;
using VibeRails.Services;
using VibeRails.Services.Integrations.VibeCodeRemote;

namespace VibeRails.Jobs;

/// <summary>Checks paid runbook repository availability once an hour while a root backend is open.</summary>
public sealed class RunbookRepoCheckJob(ILogger<RunbookRepoCheckJob> logger,
    ISystemResourceService resources, IHttpClientFactory clients, IRemoteAccountKeyStore keys)
    : JobBase(logger, resources)
{
    internal const string HttpClientName = "runbook-repo-check";
    internal const string Endpoint = "https://viberails.ai/api/v1/runbook_repo";
    protected override TimeSpan Interval => TimeSpan.FromHours(1);
    // This header-only check is cheap and should retain its hourly cadence under CPU pressure.
    protected override JobPriority Priority => JobPriority.High;

    protected override async Task ExecuteJob(CancellationToken cancellationToken)
    {
        try
        {
            var key = keys.Read();
            if (string.IsNullOrWhiteSpace(key))
            {
                Record(false, null, "no_api_key");
                return;
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
            request.Headers.Add("X-Api-Key", key);
            using var client = clients.CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            // Initial setup only: no documents, response bodies or local entitlement state yet.
            Record(response.StatusCode == HttpStatusCode.OK, (int)response.StatusCode, "http_response");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { Record(false, null, "timeout"); }
        catch (HttpRequestException) { Record(false, null, "network_error"); }
        catch (Exception)
        {
            // Settings and transport errors must not escape to JobBase's raw exception logging.
            Record(false, null, "check_failed");
        }
    }

    private void Record(bool ok, int? status, string outcome)
    {
        const string message = "[RunbookRepoCheck] HTTP 200: {Ok}; status={Status}; outcome={Outcome}";
        _logger.LogInformation(message, ok, status, outcome);
        // The application's file sink uses Serilog directly; MEL providers are cleared at startup.
        Serilog.Log.Information(message, ok, status, outcome);
    }
}
