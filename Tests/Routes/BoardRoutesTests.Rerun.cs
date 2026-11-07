using System.Net;
using Xunit;
namespace Tests.Routes;
public sealed partial class BoardRoutesTests
{
    [Fact]
    public async Task LegacyRerunEndpointIsRetiredAndAuthenticated()
    {
        const string path = "/api/v1/board/cards/card/automations/rerun";
        using var denied = await SendAsync(HttpMethod.Post, path);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var retired = await PostJsonAsync(path, new { jobId = 1, eventKey = "old" });
        Assert.Equal(HttpStatusCode.Gone, retired.StatusCode);
    }
}
