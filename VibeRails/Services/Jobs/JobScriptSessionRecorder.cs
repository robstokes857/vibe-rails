using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using VibeRails.DB;
using VibeRails.DTOs;
using VibeRails.Services.Board;
using VibeRails.Services.Terminal;

namespace VibeRails.Services.Jobs;

/// <summary>Normal session history for native workflows without a Worker/PTY recording.</summary>
internal sealed class JobScriptSessionRecorder(IRepository repository, string sessionId)
{
    private readonly SessionOutputWriter _writer = new(repository);
    private readonly object _completionLock = new();
    private Task? _completion;

    internal static async Task<JobScriptSessionRecorder> StartAsync(IServiceProvider services,
        IJobStore jobs, JobRunRecord run, string workingDirectory)
    {
        var repository = services.GetRequiredService<IRepository>();
        var id = Guid.NewGuid().ToString();
        await repository.CreateSessionAsync(id, "Shell", run.JobName, workingDirectory, Environment.ProcessId, run.Id);
        var recorder = new JobScriptSessionRecorder(repository, id);
        recorder._writer.Initialize(id);
        try
        {
            await jobs.LinkRunTerminalSessionAsync(run.Id, id);
            await repository.SetSessionDisplayNameAsync(id, $"Automation: {run.JobName}");
        }
        catch
        {
            await recorder.CompleteAsync(1, "The Automation recording could not be linked to its run.");
            throw;
        }
        if (JobRunner.GetBoardCardKey(run) is not null)
        {
            try
            {
                await BoardAutomationSessionLinker.LinkAsync(services.GetRequiredService<IBoardStore>(), run, id);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[Jobs] Could not link script session {SessionId} to its Board card", id);
            }
        }
        return recorder;
    }

    internal void WriteLine(string text, bool isError, DateTime timestampUtc) =>
        _writer.EnqueueLine(Encoding.UTF8.GetBytes(text + "\r\n"), isError, timestampUtc);

    internal Task CompleteAsync(int exitCode, string? error = null)
    {
        lock (_completionLock)
            return _completion ??= CompleteCoreAsync(exitCode, error);
    }

    private async Task CompleteCoreAsync(int exitCode, string? error)
    {
        if (!string.IsNullOrWhiteSpace(error)) WriteLine(error, true, DateTime.UtcNow);
        await _writer.DisposeAsync();
        await repository.CompleteSessionAsync(sessionId, exitCode);
    }
}
