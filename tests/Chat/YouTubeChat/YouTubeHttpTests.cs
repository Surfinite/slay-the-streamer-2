using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using SlayTheStreamer2.Ti.Chat.YouTubeChat;
using Xunit;

namespace SlayTheStreamer2.Tests.Chat.YouTubeChat;

public class YouTubeHttpTests {
    [Fact]
    public async Task GetWithRedirectAsync_Returns_Response_On_Success() {
        var handler = new FakeHttpMessageHandler(req =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
        using var http = new YouTubeHttp(handler);
        var response = await http.GetWithRedirectAsync(new Uri("https://www.youtube.com/test"), default);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetWithRedirectAsync_Sends_Consent_Cookie() {
        string? capturedCookieHeader = null;
        var handler = new FakeHttpMessageHandler(req => {
            capturedCookieHeader = req.Headers.TryGetValues("Cookie", out var v)
                ? string.Join("; ", v) : null;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var http = new YouTubeHttp(handler);
        await http.GetWithRedirectAsync(new Uri("https://www.youtube.com/test"), default);
        Assert.NotNull(capturedCookieHeader);
        Assert.Contains("CONSENT=YES+cb", capturedCookieHeader);
    }

    [Fact]
    public async Task GetWithRedirectAsync_Throws_YouTubeHttpStatusException_On_429() {
        var handler = new FakeHttpMessageHandler(req => {
            var resp = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            resp.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
            return resp;
        });
        using var http = new YouTubeHttp(handler);
        var ex = await Assert.ThrowsAsync<YouTubeHttpStatusException>(() =>
            http.GetWithRedirectAsync(new Uri("https://www.youtube.com/test"), default));
        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(120), ex.RetryAfter);
    }
}

internal sealed class FakeHttpMessageHandler : HttpMessageHandler {
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;
    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) => _handler = handler;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
        => Task.FromResult(_handler(request));
}
