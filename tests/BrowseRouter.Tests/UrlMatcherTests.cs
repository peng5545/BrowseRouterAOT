using BrowseRouter.Core.Config;
using System;

namespace BrowseRouter.Tests;

public class UrlMatcherTests
{
    private static Uri U(string s) => new(s);

    // ── HostSuffix ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("github.com", "https://github.com/foo", true)]
    [InlineData("github.com", "https://www.github.com/foo", true)]
    [InlineData("github.com", "https://api.www.github.com/x", true)]
    [InlineData("github.com", "https://notgithub.com/x", false)]
    [InlineData("github.com", "https://github.com.evil.org/x", false)]
    [InlineData("GITHUB.COM", "https://github.com/x", true)] // case-insensitive
    public void HostSuffix_matches_correctly(string suffix, string url, bool expected)
    {
        var m = new HostSuffixMatch { Value = suffix };
        Assert.Equal(expected, m.IsMatch(U(url)));
    }

    // ── ExactHost ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("github.com", "https://github.com/x", true)]
    [InlineData("github.com", "https://www.github.com/x", false)]
    [InlineData("GITHUB.COM", "https://github.com/x", true)]
    public void ExactHost_matches_correctly(string host, string url, bool expected)
    {
        var m = new ExactHostMatch { Value = host };
        Assert.Equal(expected, m.IsMatch(U(url)));
    }

    // ── PathPrefix ──────────────────────────────────────────────────────────

    [Fact]
    public void PathPrefix_without_host_matches_any_host()
    {
        var m = new PathPrefixMatch { Value = "/maps" };
        Assert.True(m.IsMatch(U("https://google.com/maps/place")));
        Assert.True(m.IsMatch(U("https://bing.com/maps")));
        Assert.False(m.IsMatch(U("https://google.com/search")));
    }

    [Fact]
    public void PathPrefix_with_host_gate_requires_both()
    {
        var m = new PathPrefixMatch { Value = "/maps", Host = "google.com" };
        Assert.True(m.IsMatch(U("https://google.com/maps")));
        Assert.True(m.IsMatch(U("https://www.google.com/maps/place")));
        Assert.False(m.IsMatch(U("https://bing.com/maps"))); // wrong host
        Assert.False(m.IsMatch(U("https://google.com/search"))); // wrong path
    }

    // ── Regex ───────────────────────────────────────────────────────────────

    [Fact]
    public void Regex_matches_against_full_url()
    {
        var m = new RegexMatch { Value = @"^https?://localhost(:\d+)?(/|$)" };
        Assert.True(m.IsMatch(U("http://localhost/")));
        Assert.True(m.IsMatch(U("https://localhost:8080/x")));
        Assert.False(m.IsMatch(U("http://example.com/localhost"))); // anchored
    }

    [Fact]
    public void Regex_is_case_insensitive_by_default()
    {
        var m = new RegexMatch { Value = "GITHUB" };
        Assert.True(m.IsMatch(U("https://github.com")));
    }
}