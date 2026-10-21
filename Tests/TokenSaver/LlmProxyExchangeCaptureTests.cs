using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Http;
using Moq;
using TokenSaver;
using Xunit;

namespace Tests.TokenSaver;

public sealed class LlmProxyExchangeCaptureTests
{
    [Fact]
    public async Task ServedModel_IsRecordedAfterRelay_EvenWhenTheResponseExceedsTheCaptureCap()
    {
        const int captureCap = 32 * 1024 * 1024;
        byte[] firstEvent = "event: response.created\ndata: {\"type\":\"response.created\",\"response\":{\"model\":\"served-snapshot\"}}\n\n"u8.ToArray();
        using var upstream = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new EventThenPaddingStream(firstEvent, captureCap + 1))
        };
        upstream.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        using var http = new HttpClient(new UpstreamHandler(upstream));
        var sink = new RecordingExchangeSink();
        using var client = new CountingClientStream(sink, firstEvent);
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/llm/openai/v1/responses";
        byte[] request = "{\"model\":\"requested-alias\"}"u8.ToArray();
        using var requestBody = new MemoryStream(request);
        context.Request.Body = requestBody;
        context.Request.ContentLength = request.Length;
        context.Response.Body = client;
        var auth = new Mock<ILlmProxyAuthGate>();
        auth.Setup(gate => gate.ValidateSessionToken(It.IsAny<string?>())).Returns(true);
        auth.Setup(gate => gate.ValidateTabToken(It.IsAny<string?>())).Returns(true);
        auth.Setup(gate => gate.ValidateTokens(It.IsAny<string?>(), It.IsAny<string?>())).Returns(true);
        var events = new Mock<ILlmProxyEventSink>(MockBehavior.Loose);

        await LlmProxyRelay.HandleAsync(
            context, new ClientFactory(http), auth.Object, events.Object,
            new Uri("https://api.openai.com/v1/responses"), "openai", "test proxy",
            bodyTransform: null, TestContext.Current.CancellationToken, sink);

        // Every upstream byte was delivered, and the sink was untouched during every client write.
        Assert.Equal(firstEvent.Length + captureCap + 1, client.BytesWritten);
        var exchange = Assert.IsType<LlmProxyExchange>(sink.Exchange);
        Assert.True(exchange.ResponseTruncated);
        Assert.Equal(captureCap, Encoding.UTF8.GetByteCount(exchange.Response));
        Assert.StartsWith(Encoding.UTF8.GetString(firstEvent), exchange.Response);
        Assert.Equal("requested-alias", exchange.Model);
        Assert.Equal("served-snapshot", exchange.ServedModel);
        events.Verify(s => s.Diagnostic(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<Exception?>()), Times.Never);
    }

    private sealed class RecordingExchangeSink : ILlmProxyExchangeSink
    {
        internal LlmProxyExchange? Exchange { get; private set; }
        public void Record(LlmProxyExchange exchange) => Exchange = exchange;
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal("llm-proxy", name);
            return client;
        }
    }

    private sealed class UpstreamHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }

    private sealed class CountingClientStream(RecordingExchangeSink sink, byte[] prefix) : MemoryStream
    {
        internal int BytesWritten { get; private set; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Assert.Null(sink.Exchange);
            if (BytesWritten == 0)
                Assert.True(buffer.Span.StartsWith(prefix));
            BytesWritten += buffer.Length;
            return ValueTask.CompletedTask;
        }
    }

    // Generates a response larger than the cap without retaining a second giant body in the test.
    private sealed class EventThenPaddingStream(byte[] prefix, int paddingBytes) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => prefix.Length + paddingBytes;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            var count = (int)Math.Min(buffer.Length, Length - _position);
            var prefixCount = Math.Min(count, Math.Max(prefix.Length - _position, 0));
            if (prefixCount > 0)
                prefix.AsSpan(_position, prefixCount).CopyTo(buffer);
            buffer[prefixCount..count].Fill((byte)'x');
            _position += count;
            return count;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
