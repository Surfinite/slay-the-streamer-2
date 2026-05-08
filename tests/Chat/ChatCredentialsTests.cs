using System;
using SlayTheStreamer2.Ti.Chat;
using Xunit;

namespace SlayTheStreamer2.Tests.Chat;

public class ChatCredentialsTests {
    [Fact]
    public void StoresUsernameLowercased() {
        var c = new ChatCredentials("Surfinite", "abc123");
        Assert.Equal("surfinite", c.Username);
    }

    [Fact]
    public void StripsOauthPrefix() {
        var c = new ChatCredentials("u", "oauth:abc123");
        Assert.Equal("abc123", c.OauthToken);
    }

    [Fact]
    public void StripsOauthPrefixCaseInsensitive() {
        var c = new ChatCredentials("u", "OAuth:abc123");
        Assert.Equal("abc123", c.OauthToken);
    }

    [Fact]
    public void AcceptsBareTokenUnchanged() {
        var c = new ChatCredentials("u", "abc123");
        Assert.Equal("abc123", c.OauthToken);
    }

    [Fact]
    public void NullUsernameThrows() {
        Assert.Throws<ArgumentNullException>(() => new ChatCredentials(null!, "abc"));
    }

    [Fact]
    public void NullTokenThrows() {
        Assert.Throws<ArgumentNullException>(() => new ChatCredentials("u", null!));
    }

    [Fact]
    public void ToStringRedactsToken() {
        var c = new ChatCredentials("Surfinite", "oauth:secret_token_12345");
        var s = c.ToString();
        Assert.DoesNotContain("secret_token_12345", s);
        Assert.Contains("REDACTED", s);
        Assert.Contains("surfinite", s);
    }
}
