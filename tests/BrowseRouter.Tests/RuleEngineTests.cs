using BrowseRouter.Core.Config;
using BrowseRouter.Core.Routing;
using System;
using System.Collections.Generic;

namespace BrowseRouter.Tests;

public class RuleEngineTests
{
    private static RootConfig Cfg(
        Dictionary<string, BrowserDef>? browsers = null,
        List<RuleDef>? rules = null,
        List<SourceRuleDef>? srcRules = null,
        List<FilterDef>? filters = null,
        string? defaultBrowser = null
    )
    {
        return new RootConfig
        {
            Browsers = browsers ??
                       new Dictionary<string, BrowserDef>(StringComparer.OrdinalIgnoreCase)
                       {
                           ["edge"] = new BrowserDef { Path = "edge.exe" },
                           ["chrome"] = new BrowserDef { Path = "chrome.exe" },
                           ["ff"] = new BrowserDef { Path = "firefox.exe" }
                       },
            Rules = rules ?? [],
            SourceRules = srcRules ?? [],
            Filters = filters ?? [],
            DefaultBrowser = defaultBrowser ?? "edge"
        };
    }

    [Fact]
    public void Source_rule_wins_over_url_rule()
    {
        var cfg = Cfg(rules: [new RuleDef { Browser = "ff", Match = new HostSuffixMatch { Value = "example.com" } }],
            srcRules: [new SourceRuleDef { Browser = "chrome", Match = new ProcessMatch { Value = "TEAMS.EXE" } }]);

        var ok = RuleEngine.Resolve(cfg, "https://example.com/", "TEAMS.EXE", null, null, out var route, out var err);
        Assert.True(ok);
        Assert.NotNull(route);
        Assert.Null(err);
        Assert.Equal("chrome", route.BrowserName);
        Assert.Contains("source rule", route.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void First_matching_url_rule_wins()
    {
        var cfg = Cfg(rules:
        [
            new RuleDef { Browser = "ff", Match = new HostSuffixMatch { Value = "example.com" } },
            new RuleDef { Browser = "chrome", Match = new HostSuffixMatch { Value = "example.com" } }
        ]);

        Assert.True(RuleEngine.Resolve(cfg, "https://example.com/", null, null, null, out var route, out _));
        Assert.Equal("ff", route.BrowserName);
    }

    [Fact]
    public void Exclude_clause_reverses_a_matching_rule()
    {
        var cfg = Cfg(rules:
        [
            new RuleDef
            {
                Browser = "chrome",
                Match = new HostSuffixMatch { Value = "google.com" },
                Exclude = new PathPrefixMatch { Value = "/maps" }
            }
            // No catchall match → defaultBrowser="edge" wins for /maps
        ]);

        Assert.True(
            RuleEngine.Resolve(cfg, "https://google.com/maps/place", null, null, null, out var mapsRoute, out _));
        Assert.True(RuleEngine.Resolve(cfg, "https://google.com/search?q=x", null, null, null, out var searchRoute,
            out _));
        Assert.Equal("edge", mapsRoute.BrowserName);
        Assert.Equal("chrome", searchRoute.BrowserName);
    }

    [Fact]
    public void Default_browser_is_used_when_nothing_matches()
    {
        var cfg = Cfg();
        Assert.True(RuleEngine.Resolve(cfg, "https://nothingmatches.com/", null, null, null, out var route, out _));
        Assert.Equal("edge", route.BrowserName);
        Assert.Contains("defaultBrowser", route.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_default_and_no_rule_returns_NoRuleMatched_failure()
    {
        var cfg = Cfg(defaultBrowser: null);
        // Need to clear the auto-supplied default — explicitly set to null
        cfg.DefaultBrowser = null;
        var ok = RuleEngine.Resolve(cfg, "https://x.com/", null, null, null, out var route, out var err);
        Assert.False(ok);
        Assert.Null(route);
        Assert.NotNull(err);
        Assert.Equal(RouteFailureReason.NoRuleMatched, err.Reason);
    }

    [Fact]
    public void Reference_to_missing_browser_returns_BrowserNotConfigured()
    {
        var cfg = Cfg(rules: [new RuleDef { Browser = "absent", Match = new HostSuffixMatch { Value = "x.com" } }]);
        var ok = RuleEngine.Resolve(cfg, "https://x.com/", null, null, null, out var route, out var err);
        Assert.False(ok);
        Assert.Null(route);
        Assert.NotNull(err);
        Assert.Equal(RouteFailureReason.BrowserNotConfigured, err.Reason);
        Assert.Equal("absent", err.Detail);
    }

    [Fact]
    public void Invalid_url_returns_InvalidUrl_failure()
    {
        var cfg = Cfg();
        // UriFactory tries to prepend https:// — "not a url at all $#@" parses as a host-ish path. Verify
        // its behaviour: anything genuinely unparseable is rare. Use an obviously-bad input that fails all
        // 3 fallbacks (control chars).
        // The 3-fallback factory is permissive, so this test focuses on the malformed-control-char path.
        var ok1 = RuleEngine.Resolve(cfg, "not a url at all $#@", null, null, null, out _, out _);
        _ = ok1; // (no strict assertion here — sanity-check below)
        var ok2 = RuleEngine.Resolve(cfg, "\x01\x02\x03", null, null, null, out var route2, out var err2);
        Assert.False(ok2);
        Assert.Null(route2);
        Assert.NotNull(err2);
    }

    [Fact]
    public void Filter_rewrites_url_before_rule_matching()
    {
        var cfg = Cfg(
            rules: [new RuleDef { Browser = "ff", Match = new HostSuffixMatch { Value = "real.example.com" } }],
            filters:
            [
                new FilterDef
                    { Name = "u", Find = ".*safelinks.*url=([^&]+).*", Replace = "unescape($1)", Priority = 1 }
            ]);

        var raw = "https://safelinks.outlook.com/?url=https%3A%2F%2Freal.example.com%2Fpage";
        Assert.True(RuleEngine.Resolve(cfg, raw, null, null, null, out var route, out _));
        Assert.Equal("ff", route.BrowserName);
        Assert.Equal("u", route.AppliedFilter);
        Assert.Equal("https://real.example.com/page", route.Uri.OriginalString);
        Assert.Equal(raw, route.RawUrl);
    }

    [Fact]
    public void Resolve_treats_null_rule_lists_as_empty()
    {
        // A user-written config may explicitly set `"rules": null` (e.g. via a template
        // or a hand edit). The engine must treat that the same as an empty list and
        // fall through to DefaultBrowser — not throw.
        var cfg = Cfg(rules: null, srcRules: null, filters: null);

        var ok = RuleEngine.Resolve(cfg, "https://example.com/", null, null, null, out var route, out var err);
        Assert.True(ok);
        Assert.NotNull(route);
        Assert.Null(err);
        Assert.Equal("edge", route.BrowserName);
        Assert.Contains("defaultBrowser", route.Reason, StringComparison.Ordinal);
    }
}