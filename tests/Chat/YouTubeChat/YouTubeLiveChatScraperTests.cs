using System;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using SlayTheStreamer2.Ti.Chat.YouTubeChat;
using Xunit;

namespace SlayTheStreamer2.Tests.Chat.YouTubeChat;

public class YouTubeLiveChatScraperTests {
    [Fact]
    public async Task ParseInitialPage_Extracts_ApiKey_And_ClientVersion_And_Continuation() {
        var html = FixtureLoader.Load("youtube_live_chat_page.html");
        var http = new StaticBodyFakeHttp(html);
        var scraper = new YouTubeLiveChatScraper(http);
        var result = await scraper.ParseInitialPageAsync("FIXTUREvid001", default);
        Assert.NotNull(result);
        Assert.False(string.IsNullOrEmpty(result!.InnertubeApiKey));
        Assert.Matches(@"^\d+\.\d+\.\d+\.\d+\.\d+$", result.ClientVersion);
        Assert.False(string.IsNullOrEmpty(result.InitialContinuation));
    }

    [Fact]
    public async Task ParseInitialPage_Returns_Null_When_ApiKey_Missing() {
        var html = "<html><body>nothing useful here</body></html>";
        var http = new StaticBodyFakeHttp(html);
        var scraper = new YouTubeLiveChatScraper(http);
        Assert.Null(await scraper.ParseInitialPageAsync("FIXTUREvid001", default));
    }

    [Fact]
    public async Task ParseInitialPage_Http_Throws_Returns_Null() {
        var http = new ScraperThrowingFakeHttp();
        var scraper = new YouTubeLiveChatScraper(http);
        Assert.Null(await scraper.ParseInitialPageAsync("FIXTUREvid001", default));
    }
}

internal sealed class StaticBodyFakeHttp : IYouTubeHttp {
    private readonly string _body;
    public StaticBodyFakeHttp(string body) => _body = body;
    public Task<HttpResponseMessage> GetWithRedirectAsync(Uri url, System.Threading.CancellationToken ct)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_body) });
    public Task<HttpResponseMessage> PostJsonAsync(Uri url, string jsonBody, System.Threading.CancellationToken ct)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_body) });
    public void Dispose() { }
}

internal sealed class PostBodyFakeHttp : IYouTubeHttp {
    private readonly string _body;
    public PostBodyFakeHttp(string body) => _body = body;
    public Task<HttpResponseMessage> GetWithRedirectAsync(Uri url, System.Threading.CancellationToken ct) =>
        throw new NotImplementedException();
    public Task<HttpResponseMessage> PostJsonAsync(Uri url, string jsonBody, System.Threading.CancellationToken ct)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_body) });
    public void Dispose() { }
}

// Distinct from Task 16's ThrowingFakeHttp to avoid collisions across test files in the same namespace.
internal sealed class ScraperThrowingFakeHttp : IYouTubeHttp {
    public Task<HttpResponseMessage> GetWithRedirectAsync(Uri url, System.Threading.CancellationToken ct)
        => Task.FromException<HttpResponseMessage>(new HttpRequestException("boom"));
    public Task<HttpResponseMessage> PostJsonAsync(Uri url, string jsonBody, System.Threading.CancellationToken ct)
        => Task.FromException<HttpResponseMessage>(new HttpRequestException("boom"));
    public void Dispose() { }
}
