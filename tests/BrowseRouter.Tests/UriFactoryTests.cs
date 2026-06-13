using BrowseRouter.Core.UriUtil;

namespace BrowseRouter.Tests;

public class UriFactoryTests
{
    [Theory]
    [InlineData("https://example.com/x", "https://example.com/x")]
    [InlineData("http://example.com", "http://example.com/")]
    public void Verbatim_absolute_urls_parse_directly(string input, string expected)
    {
        Assert.Equal(expected, UriFactory.TryParse(input)?.ToString());
    }

    [Fact]
    public void Bare_host_is_promoted_to_https()
    {
        var uri = UriFactory.TryParse("example.com");
        Assert.NotNull(uri);
        Assert.Equal("https", uri!.Scheme);
        Assert.Equal("example.com", uri.Host);
    }

    [Fact]
    public void Url_encoded_input_is_decoded_as_last_resort()
    {
        var uri = UriFactory.TryParse("https%3A%2F%2Fexample.com%2Fx");
        Assert.NotNull(uri);
        Assert.Equal("example.com", uri!.Host);
        Assert.Equal("https", uri.Scheme);
    }

    [Theory]
    [InlineData("mailto:foo@bar.com")]
    [InlineData("file:///C:/Windows/System32/drivers/etc/hosts")]
    [InlineData("ms-windows-store://pdp/?ProductId=foo")]
    [InlineData("ftp://example.com/x")]
    public void Non_web_schemes_are_rejected(string input)
    {
        // BrowseRouter only routes http/https. A non-web scheme must not be
        // accepted, even if prefixing https:// would technically parse.
        Assert.Null(UriFactory.TryParse(input));
    }

    [Fact]
    public void Url_encoded_non_web_scheme_does_not_slip_through()
    {
        // The percent-decode fallback used to silently swap hosts when a
        // wrapped URL decoded to a different origin. Verify the rejected
        // set is still rejected after decode.
        Assert.Null(UriFactory.TryParse("mailto%3Afoo%40bar.com"));
    }

    [Fact]
    public void Whitespace_or_empty_returns_null()
    {
        Assert.Null(UriFactory.TryParse(""));
        Assert.Null(UriFactory.TryParse("   "));
    }
}