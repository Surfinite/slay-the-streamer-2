using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using SlayTheStreamer2.Ti.Chat.YouTubeChat;
using Xunit;

namespace SlayTheStreamer2.Tests.Chat.YouTubeChat;

public class YouTubeLiveBroadcastDiscoveryTests {
    [Fact]
    public async Task Redirect_To_Watch_Url_Returns_VideoId() {
        var http = new RedirectingFakeHttp("https://www.youtube.com/watch?v=ABCD1234");
        var discovery = new YouTubeLiveBroadcastDiscovery(http);
        var result = await discovery.FindLiveVideoIdAsync("UCfake", default);
        Assert.Equal("ABCD1234", result);
    }

    [Fact]
    public async Task Redirect_With_Watch_Url_Different_QueryOrder_Returns_VideoId() {
        var http = new RedirectingFakeHttp("https://www.youtube.com/watch?foo=bar&v=ABCD1234&t=10");
        var discovery = new YouTubeLiveBroadcastDiscovery(http);
        var result = await discovery.FindLiveVideoIdAsync("UCfake", default);
        Assert.Equal("ABCD1234", result);
    }

    [Fact]
    public async Task Redirect_To_Channel_Page_Returns_Null() {
        var http = new RedirectingFakeHttp("https://www.youtube.com/channel/UCfake");
        var discovery = new YouTubeLiveBroadcastDiscovery(http);
        Assert.Null(await discovery.FindLiveVideoIdAsync("UCfake", default));
    }

    [Fact]
    public async Task Http_Throws_Returns_Null() {
        var http = new ThrowingFakeHttp(new HttpRequestException("boom"));
        var discovery = new YouTubeLiveBroadcastDiscovery(http);
        Assert.Null(await discovery.FindLiveVideoIdAsync("UCfake", default));
    }

    [Fact]
    public async Task Status_429_Returns_Null() {
        var http = new ThrowingFakeHttp(new YouTubeHttpStatusException(
            HttpStatusCode.TooManyRequests, null, "429"));
        var discovery = new YouTubeLiveBroadcastDiscovery(http);
        Assert.Null(await discovery.FindLiveVideoIdAsync("UCfake", default));
    }

    [Fact]
    public async Task Empty_ChannelId_Returns_Null() {
        var http = new RedirectingFakeHttp("https://www.youtube.com/watch?v=X");
        var discovery = new YouTubeLiveBroadcastDiscovery(http);
        Assert.Null(await discovery.FindLiveVideoIdAsync("", default));
    }
}

internal sealed class RedirectingFakeHttp : IYouTubeHttp {
    private readonly string _finalUri;
    public RedirectingFakeHttp(string finalUri) => _finalUri = finalUri;
    public Task<HttpResponseMessage> GetWithRedirectAsync(Uri url, System.Threading.CancellationToken ct) {
        var resp = new HttpResponseMessage(HttpStatusCode.OK) {
            RequestMessage = new HttpRequestMessage(HttpMethod.Get, new Uri(_finalUri)),
        };
        return Task.FromResult(resp);
    }
    public Task<HttpResponseMessage> PostJsonAsync(Uri url, string jsonBody, System.Threading.CancellationToken ct) =>
        throw new NotImplementedException();
    public void Dispose() { }
}

internal sealed class ThrowingFakeHttp : IYouTubeHttp {
    private readonly Exception _ex;
    public ThrowingFakeHttp() => _ex = new HttpRequestException("boom");
    public ThrowingFakeHttp(Exception ex) => _ex = ex;
    public Task<HttpResponseMessage> GetWithRedirectAsync(Uri url, System.Threading.CancellationToken ct) => Task.FromException<HttpResponseMessage>(_ex);
    public Task<HttpResponseMessage> PostJsonAsync(Uri url, string jsonBody, System.Threading.CancellationToken ct) => Task.FromException<HttpResponseMessage>(_ex);
    public void Dispose() { }
}
