namespace VibeRails.Middleware;

/// <summary>Serves trusted HTML with the nonce from this response's content policy.</summary>
internal sealed class NonceHtmlResult(string htmlPath) : IResult
{
    public async Task ExecuteAsync(HttpContext context)
    {
        var html = await File.ReadAllTextAsync(htmlPath, context.RequestAborted);
        html = html.Replace(SecurityHeadersMiddleware.NoncePlaceholder,
            SecurityHeadersMiddleware.GetNonce(context), StringComparison.Ordinal);
        await Results.Content(html, "text/html; charset=utf-8").ExecuteAsync(context);
    }
}
