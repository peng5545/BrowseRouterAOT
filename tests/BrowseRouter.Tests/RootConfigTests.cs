using BrowseRouter.Core.Config;
using System;

namespace BrowseRouter.Tests;

public class RootConfigTests
{
    [Fact]
    public void Validate_returns_no_warnings_for_fully_resolved_config()
    {
        var cfg = new RootConfig
        {
            DefaultBrowser = "edge",
            Browsers = new(StringComparer.OrdinalIgnoreCase)
            {
                ["edge"] = new BrowserDef { Path = "msedge.exe" }
            },
            Rules = [new RuleDef { Browser = "edge", Match = new HostSuffixMatch { Value = "example.com" } }],
            SourceRules = [new SourceRuleDef { Browser = "edge", Match = new ProcessMatch { Value = "TEAMS.EXE" } }]
        };

        var warnings = cfg.Validate();

        Assert.Empty(warnings);
    }

    [Fact]
    public void Validate_warns_when_DefaultBrowser_is_undefined()
    {
        var cfg = new RootConfig
        {
            DefaultBrowser = "ghost",
            Browsers = new(StringComparer.OrdinalIgnoreCase)
            {
                ["edge"] = new BrowserDef { Path = "msedge.exe" }
            }
        };

        var warnings = cfg.Validate();

        var warning = Assert.Single(warnings);
        Assert.Contains("DefaultBrowser 'ghost'", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_does_not_warn_when_DefaultBrowser_is_null()
    {
        // null DefaultBrowser is legal (final fallback is the OS default).
        var cfg = new RootConfig
        {
            DefaultBrowser = null,
            Browsers = new(StringComparer.OrdinalIgnoreCase)
            {
                ["edge"] = new BrowserDef { Path = "msedge.exe" }
            }
        };

        Assert.Empty(cfg.Validate());
    }

    [Fact]
    public void Validate_warns_when_URL_rule_references_undefined_browser()
    {
        var cfg = new RootConfig
        {
            Browsers = new(StringComparer.OrdinalIgnoreCase)
            {
                ["edge"] = new BrowserDef { Path = "msedge.exe" }
            },
            Rules = [new RuleDef { Browser = "ghost", Match = new HostSuffixMatch { Value = "example.com" } }]
        };

        var warnings = cfg.Validate();

        var warning = Assert.Single(warnings);
        Assert.Contains("URL rule", warning, StringComparison.Ordinal);
        Assert.Contains("'ghost'", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_warns_when_source_rule_references_undefined_browser()
    {
        var cfg = new RootConfig
        {
            Browsers = new(StringComparer.OrdinalIgnoreCase)
            {
                ["edge"] = new BrowserDef { Path = "msedge.exe" }
            },
            SourceRules = [new SourceRuleDef { Browser = "ghost", Match = new ProcessMatch { Value = "TEAMS.EXE" } }]
        };

        var warnings = cfg.Validate();

        var warning = Assert.Single(warnings);
        Assert.Contains("Source rule", warning, StringComparison.Ordinal);
        Assert.Contains("'ghost'", warning, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_collects_multiple_warnings_across_rule_kinds()
    {
        var cfg = new RootConfig
        {
            DefaultBrowser = "missing1",
            Browsers = new(StringComparer.OrdinalIgnoreCase)
            {
                ["edge"] = new BrowserDef { Path = "msedge.exe" }
            },
            Rules = [new RuleDef { Browser = "missing2", Match = new HostSuffixMatch { Value = "example.com" } }],
            SourceRules = [new SourceRuleDef { Browser = "missing3", Match = new ProcessMatch { Value = "TEAMS.EXE" } }]
        };

        var warnings = cfg.Validate();

        Assert.Equal(3, warnings.Count);
        Assert.Contains(warnings, w => w.Contains("DefaultBrowser 'missing1'", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("'missing2'", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("'missing3'", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_uses_case_insensitive_lookup_for_browser_keys()
    {
        // The Browsers dictionary is created with OrdinalIgnoreCase; Validate must
        // respect that, otherwise a user writing "Edge" in a rule would warn even
        // when "edge" is defined.
        var cfg = new RootConfig
        {
            Browsers = new(StringComparer.OrdinalIgnoreCase)
            {
                ["edge"] = new BrowserDef { Path = "msedge.exe" }
            },
            Rules = [new RuleDef { Browser = "EDGE", Match = new HostSuffixMatch { Value = "example.com" } }]
        };

        Assert.Empty(cfg.Validate());
    }

    [Fact]
    public void Validate_warning_uses_human_friendly_describe_match_text()
    {
        // Pin the new text shape: warnings must show what the user actually wrote
        // (e.g. hostSuffix=example.com) — not the CLR type name. This protects
        // against a silent regression to GetType().Name.
        var cfg = new RootConfig
        {
            Browsers = new(StringComparer.OrdinalIgnoreCase)
            {
                ["edge"] = new BrowserDef { Path = "msedge.exe" }
            },
            Rules = [new RuleDef { Browser = "ghost", Match = new HostSuffixMatch { Value = "example.com" } }],
            SourceRules =
            [
                new SourceRuleDef { Browser = "ghost", Match = new ProcessMatch { Value = "TEAMS.EXE" } }
            ]
        };

        var warnings = cfg.Validate();

        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.Contains("hostSuffix=example.com", StringComparison.Ordinal)
                                       && w.Contains("URL rule", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("process=TEAMS.EXE", StringComparison.Ordinal)
                                       && w.Contains("Source rule", StringComparison.Ordinal));
    }
}
