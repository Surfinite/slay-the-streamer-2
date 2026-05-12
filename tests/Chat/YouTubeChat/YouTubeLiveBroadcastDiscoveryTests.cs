using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using SlayTheStreamer2.Ti.Chat.YouTubeChat;
using Xunit;

namespace SlayTheStreamer2.Tests.Chat.YouTubeChat;

public class YouTubeLiveBroadcastDiscoveryTests {
    [Fact]
    public async Task Canonical_Watch_Link_Returns_VideoId() {
        var body = "<html><head><link rel=\"canonical\" href=\"https://www.youtube.com/watch?v=ABCD1234\"></head></html>";
        var http = new DiscoveryBodyFakeHttp(body);
        var discovery = new YouTubeLiveBroadcastDiscovery(http);
        var result = await discovery.FindLiveVideoIdAsync("UCfake", default);
        Assert.Equal("ABCD1234", result);
    }

    [Fact]
    public async Task Canonical_With_Hyphens_And_Underscores_In_VideoId_Extracted() {
        var body = "<link rel=\"canonical\" href=\"https://www.youtube.com/watch?v=II6NztxN-hEQ_x\">";
        var http = new DiscoveryBodyFakeHttp(body);
        var discovery = new YouTubeLiveBroadcastDiscovery(http);
        var result = await discovery.FindLiveVideoIdAsync("UCfake", default);
        Assert.Equal("II6NztxN-hEQ_x", result);
    }

    [Fact]
    public async Task Canonical_Pointing_To_Channel_Page_Returns_Null() {
        // When no live broadcast, the canonical link points to the channel page (not /watch).
        var body = "<link rel=\"canonical\" href=\"https://www.youtube.com/channel/UCfake\">";
        var http = new DiscoveryBodyFakeHttp(body);
        var discovery = new YouTubeLiveBroadcastDiscovery(http);
        Assert.Null(await discovery.FindLiveVideoIdAsync("UCfake", default));
    }

    [Fact]
    public async Task No_Canonical_Link_Returns_Null() {
        var body = "<html><body>nothing useful</body></html>";
        var http = new DiscoveryBodyFakeHttp(body);
        var discovery = new YouTubeLiveBroadcastDiscovery(http);
        Assert.Null(await discovery.FindLiveVideoIdAsync("UCfake", default));
    }

    [Fact]
    public async Task Http_Throws_Returns_Null() {
        var http = new DiscoveryThrowingFakeHttp(new HttpRequestException("boom"));
        var discovery = new YouTubeLiveBroadcastDiscovery(http);
        Assert.Null(await discovery.FindLiveVideoIdAsync("UCfake", default));
    }

    [Fact]
    public async Task Status_429_Returns_Null() {
        var http = new DiscoveryThrowingFakeHttp(new YouTubeHttpStatusException(
            HttpStatusCode.TooManyRequests, null, "429"));
        var discovery = new YouTubeLiveBroadcastDiscovery(http);
        Assert.Null(await discovery.FindLiveVideoIdAsync("UCfake", default));
    }

    [Fact]
    public async Task Empty_ChannelId_Returns_Null() {
        var http = new DiscoveryBodyFakeHttp("<link rel=\"canonical\" href=\"https://www.youtube.com/watch?v=X\">");
        var discovery = new YouTubeLiveBroadcastDiscovery(http);
        Assert.Null(await discovery.FindLiveVideoIdAsync("", default));
    }
}

internal sealed class DiscoveryBodyFakeHttp : IYouTubeHttp {
    private readonly string _body;
    public DiscoveryBodyFakeHttp(string body) => _body = body;
    public Task<HttpResponseMessage> GetWithRedirectAsync(Uri url, System.Threading.CancellationToken ct)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_body) });
    public Task<HttpResponseMessage> PostJsonAsync(Uri url, string jsonBody, System.Threading.CancellationToken ct)
        => throw new NotImplementedException();
    public void Dispose() { }
}

internal sealed class DiscoveryThrowingFakeHttp : IYouTubeHttp {
    private readonly Exception _ex;
    public DiscoveryThrowingFakeHttp(Exception ex) => _ex = ex;
    public Task<HttpResponseMessage> GetWithRedirectAsync(Uri url, System.Threading.CancellationToken ct) => Task.FromException<HttpResponseMessage>(_ex);
    public Task<HttpResponseMessage> PostJsonAsync(Uri url, string jsonBody, System.Threading.CancellationToken ct) => Task.FromException<HttpResponseMessage>(_ex);
    public void Dispose() { }
}
