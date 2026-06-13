using BrowseRouter.Core.Routing;
using System;
using System.Collections.Generic;

namespace BrowseRouter.Tests;

public class ArgsFormatterTests
{
    private static Uri U(string s) => new(s);

    private static List<string> Format(List<string>? template, Uri uri, string rawUrl)
    {
        var sink = new List<string>();
        ArgsFormatter.Format(template, uri, rawUrl, sink);
        return sink;
    }

    [Fact]
    public void No_template_appends_url_argument()
    {
        var actual = Format(template: null, U("https://example.com/x"), "https://example.com/x");
        Assert.Single(actual);
        Assert.Equal("https://example.com/x", actual[0]);
    }

    [Fact]
    public void Empty_template_appends_url_argument()
    {
        var actual = Format(template: [], U("https://example.com/x"), "https://example.com/x");
        Assert.Equal(["https://example.com/x"], actual);
    }

    [Fact]
    public void Template_without_token_still_gets_url_appended()
    {
        var actual = Format(["--incognito"], U("https://example.com/"), "https://example.com/");
        Assert.Equal(["--incognito", "https://example.com/"], actual);
    }

    [Fact]
    public void Template_with_token_does_not_append_url()
    {
        var actual = Format(["--new-tab", "{url}"], U("https://example.com/x"), "https://example.com/x");
        Assert.Equal(["--new-tab", "https://example.com/x"], actual);
    }

    [Fact]
    public void All_known_tokens_expand_correctly()
    {
        var uri = U("https://user:pw@host.example.com:8080/p/a?q=1&q=2#frag");
        List<string> template =
        [
            "{url}", "{host}", "{authority}", "{path}", "{query}", "{fragment}", "{userinfo}", "{port}"
        ];
        var actual = Format(template, uri, uri.OriginalString);
        Assert.Equal(uri.OriginalString, actual[0]);
        Assert.Equal("host.example.com", actual[1]);
        // Uri.Authority in .NET is host+port WITHOUT userinfo (despite the RFC name).
        Assert.Equal("host.example.com:8080", actual[2]);
        Assert.Equal("/p/a", actual[3]);
        Assert.Equal("?q=1&q=2", actual[4]);
        Assert.Equal("#frag", actual[5]);
        Assert.Equal("user:pw", actual[6]);
        Assert.Equal("8080", actual[7]);
    }

    [Fact]
    public void Unknown_token_is_left_intact_and_doesnt_count_as_seen()
    {
        // Unknown {weird} stays as-is; since no recognised token fired, the URL is appended.
        var actual = Format(["--prefix={weird}-suffix"], U("https://example.com/"), "https://example.com/");
        Assert.Equal(["--prefix={weird}-suffix", "https://example.com/"], actual);
    }

    [Fact]
    public void RawUrl_token_uses_pre_filter_url()
    {
        var uri = U("https://real.example.com/");
        var rawUrl = "https://safelinks.example.com/?url=https%3A%2F%2Freal.example.com%2F";
        var actual = Format(["--orig", "{rawUrl}"], uri, rawUrl);
        Assert.Equal(["--orig", rawUrl], actual);
    }

    [Fact]
    public void Multiple_tokens_in_single_arg_all_expand()
    {
        var actual = Format(["ext+container:name=Work&url={url}"], U("https://x.com/"), "https://x.com/");
        Assert.Equal(["ext+container:name=Work&url=https://x.com/"], actual);
    }

    [Fact]
    public void Doubled_braces_emit_literal_braces()
    {
        // {{url}} → literal "{url}", so the browser sees a real token-looking string.
        var actual = Format(["--template={{url}}"], U("https://x.com/"), "https://x.com/");
        Assert.Equal(["--template={url}"], actual);
    }

    [Fact]
    public void Doubled_braces_around_a_known_token_stay_literal()
    {
        // {{url}} must NOT expand the inner token — the escape pass runs first and
        // turns the doubled braces into single literal braces.
        var actual = Format(["prefix{{url}}suffix"], U("https://x.com/"), "https://x.com/");
        Assert.Equal(["prefix{url}suffix"], actual);
    }

    [Fact]
    public void Lone_braces_still_resolve_as_tokens()
    {
        // Sanity: a non-doubled single {url} is still a token, not a literal.
        var actual = Format(["{url}"], U("https://x.com/"), "https://x.com/");
        Assert.Equal(["https://x.com/"], actual);
    }

    [Fact]
    public void Doubled_braces_dont_count_as_token_seen()
    {
        // Important contract: a template consisting ONLY of literal braces
        // (e.g. "{{{{}}}}") must still trigger the trailing-URL append, because
        // no recognised token fired.
        var actual = Format(["{{}}"], U("https://x.com/"), "https://x.com/");
        Assert.Equal(["{}", "https://x.com/"], actual);
    }
}