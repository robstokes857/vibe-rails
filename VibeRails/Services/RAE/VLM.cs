namespace VibeRails.Services.RAE;

//vibe language model -> powered by Claude
public class VLM
{
    private readonly I_VLM_RemoteLLMProvider _remoteLLMProvider;
    private readonly I_VLM_Tool_Executor _toolExecutor;

    public VLM(I_VLM_RemoteLLMProvider remoteLLMProvider, I_VLM_Tool_Executor toolExecutor)
    {
        ArgumentNullException.ThrowIfNull(remoteLLMProvider);
        ArgumentNullException.ThrowIfNull(toolExecutor);

        _remoteLLMProvider = remoteLLMProvider;
        _toolExecutor = toolExecutor;
    }

    public async Task<VLM_Result> RunTaskAsync(string userTask, CancellationToken cancellationToken)
    {
        var request = new VLM_RemoteLLMProvider_Request()
        {
            UserTask = userTask,
            Version = _toolExecutor.Version
        };
        var response = await _remoteLLMProvider.ExecuteAsync(request, cancellationToken);

        return await RunTaskAsync(response, cancellationToken);
    }

    public async Task<VLM_Result> RunTaskAsync(VLM_RemoteLLMProvider_Response response, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(response);

        while (!response.STOP)
        {
            if (response.OrderedToolCalls.Count == 0 && response.BatchToolCalls.Count == 0)
            {
                
                break;
            }

            var toolResults = await ExecuteToolCallsAsync(response, cancellationToken);
            response = await _remoteLLMProvider.ToolResultAsync(toolResults, cancellationToken);
        }

        return new VLM_Result(response);
    }

    private async Task<List<VLM_Tool_Result>> ExecuteToolCallsAsync(VLM_RemoteLLMProvider_Response response, CancellationToken cancellationToken)
    {
        var toolResults = new List<VLM_Tool_Result>();

        // Ordered calls run one at a time, in order.
        foreach (var toolCall in response.OrderedToolCalls)
        {
            toolResults.Add(await _toolExecutor.ExecuteAsync(toolCall, cancellationToken));
        }

        // Batch calls have no ordering dependency, so they run concurrently.
        if (response.BatchToolCalls.Count > 0)
        {
            var batchTasks = response.BatchToolCalls
                .Select(toolCall => _toolExecutor.ExecuteAsync(toolCall, cancellationToken));
            toolResults.AddRange(await Task.WhenAll(batchTasks));
        }

        return toolResults;
    }
}

public class VLM_Result
{
    public VLM_Result()
    {
    }

    public VLM_Result(VLM_RemoteLLMProvider_Response response)
    {
        ArgumentNullException.ThrowIfNull(response);
        Success = response.Success;
        ResponseMessage = response.HumanResponseMessage;
        Error = response.Error;
    }

    public bool Success { get; set; }
    public string ResponseMessage { get; set; } = "";
    public string Error { get; set; } = "";
}


//REMOTE LLM PROVIDER

public interface I_VLM_RemoteLLMProvider
{
    Task<VLM_RemoteLLMProvider_Response> ExecuteAsync(VLM_RemoteLLMProvider_Request request, CancellationToken cancellationToken);
    Task<VLM_RemoteLLMProvider_Response> ToolResultAsync(List<VLM_Tool_Result> toolResults, CancellationToken cancellationToken);
}

public class VLM_RemoteLLMProvider_Request
{
    public string UserTask { get; set; } = "";
    public VLM_Tool_Version Version { get; set; }
}

public class VLM_RemoteLLMProvider_Response
{
    public bool Success { get; set; } = true;
    public string HumanResponseMessage { get; set; } = "";
    public string Error { get; set; } = "";
    public List<VLM_Tool_Call> OrderedToolCalls { get; set; } = new List<VLM_Tool_Call>();
    public List<VLM_Tool_Call> BatchToolCalls { get; set; } = new List<VLM_Tool_Call>();
    public bool STOP { get; set; } = false;
}


//TOOLS
public class VLM_Tool_Call
{
    public string ToolName { get; set; } = "";
    public string[] Args { get; set; } = Array.Empty<string>();
}

public interface I_VLM_Tool_Executor
{
    VLM_Tool_Version Version { get; }
    Task<VLM_Tool_Result> ExecuteAsync(VLM_Tool_Call toolCall, CancellationToken cancellationToken);
}

public enum VLM_Tool_Version
{
    NOT_SET,
    V1_Windows_PWSH,
    V1_Linux_BASH,
    V1_MacOS_ZSH,
}

public class VLM_Tool_Result
{
    public bool Success { get; set; } = true;
    public string Data { get; set; } = "";
    public string Error { get; set; } = "";
}
