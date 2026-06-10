using BrowseRouter.Core.Config;
using BrowseRouter.Core.Json;
using System;
using System.Text.Json;

namespace BrowseRouter.Tests;

public class ConfigSerializationTests
{
    [Fact]
    public void Polymorphic_match_round_trips_via_source_generated_context()
    {
        const string json = """
                            {
                              "browsers": {
                                "edge":   { "path": "edge.exe" },
                                "chrome": { "path": "chrome.exe", "args": ["--new-tab", "{url}"] }
                              },
                              "defaultBrowser": "edge",
                              "rules": [
                                { "browser": "edge",   "match": { "type": "hostSuffix",  "value": "teams.microsoft.com" } },
                                { "browser": "chrome", "match": { "type": "regex",       "value": "^https?://localhost/" } },
                                { "browser": "edge",   "match": { "type": "pathPrefix",  "value": "/maps", "host": "google.com" } },
                                {
                                  "browser": "chrome",
                                  "match":   { "type": "hostSuffix", "value": "google.com" },
                                  "exclude": { "type": "pathPrefix", "value": "/maps" }
                                }
                              ],
                              "sourceRules": [
                                { "browser": "edge", "match": { "type": "process", "value": "TEAMS.EXE" } }
                              ],
                              "filters": [
                                { "name": "strip", "find": "x", "replace": "y", "priority": 1 }
                              ]
                            }
                            """;

        var cfg = JsonSerializer.Deserialize(json, AppJsonContext.Default.RootConfig);
        Assert.NotNull(cfg);
        Assert.Equal("edge", cfg.DefaultBrowser);
        Assert.Equal(2, cfg.Browsers.Count);
        Assert.Equal(4, cfg.Rules.Count);
        Assert.Single(cfg.SourceRules);
        Assert.Single(cfg.Filters);

        Assert.IsType<HostSuffixMatch>(cfg.Rules[0].Match);
        Assert.IsType<RegexMatch>(cfg.Rules[1].Match);
        Assert.IsType<PathPrefixMatch>(cfg.Rules[2].Match);
        var withExclude = cfg.Rules[3];
        Assert.IsType<HostSuffixMatch>(withExclude.Match);
        Assert.IsType<PathPrefixMatch>(withExclude.Exclude);

        Assert.IsType<ProcessMatch>(cfg.SourceRules[0].Match);

        // Browsers dictionary respects the JSON-array shape for `args`.
        Assert.NotNull(cfg.Browsers["chrome"].Args);
        Assert.Equal(["--new-tab", "{url}"], cfg.Browsers["chrome"].Args!);
    }

    [Fact]
    public void Round_trip_serialization_preserves_types()
    {
        var original = new RootConfig
        {
            DefaultBrowser = "edge",
            Browsers = new(StringComparer.OrdinalIgnoreCase)
            {
                ["edge"] = new BrowserDef { Path = "msedge.exe", Args = ["{url}"] }
            },
            Rules =
            [
                new RuleDef { Browser = "edge", Match = new HostSuffixMatch { Value = "example.com" } },
                new RuleDef
                {
                    Browser = "edge",
                    Match = new HostSuffixMatch { Value = "google.com" },
                    Exclude = new PathPrefixMatch { Value = "/maps" }
                }
            ]
        };

        var json = JsonSerializer.Serialize(original, AppJsonContext.Default.RootConfig);
        var round = JsonSerializer.Deserialize(json, AppJsonContext.Default.RootConfig);

        Assert.NotNull(round);
        Assert.Equal(original.DefaultBrowser, round.DefaultBrowser);
        Assert.Equal(2, round.Rules.Count);
        Assert.IsType<HostSuffixMatch>(round.Rules[1].Match);
        Assert.IsType<PathPrefixMatch>(round.Rules[1].Exclude);
    }

    [Fact]
    public void Comments_and_trailing_commas_are_tolerated()
    {
        const string json = """
                            {
                              // a comment
                              "defaultBrowser": "edge",
                              "browsers": {
                                "edge": { "path": "msedge.exe" },
                              },
                            }
                            """;
        var cfg = JsonSerializer.Deserialize(json, AppJsonContext.Default.RootConfig);
        Assert.Equal("edge", cfg!.DefaultBrowser);
    }

    [Fact]
    public void HostOptions_default_to_tray_enabled_when_omitted()
    {
        // Existing users' config files don't have the new key. Backwards compat:
        // deserializing a config without `host` at all must yield EnableTrayIcon=true
        // so a Host launched against an old config still surfaces the tray.
        const string json = """{ "defaultBrowser": "edge" }""";
        var cfg = JsonSerializer.Deserialize(json, AppJsonContext.Default.RootConfig);
        Assert.NotNull(cfg);
        Assert.True(cfg.Host.EnableTrayIcon);
    }

    [Fact]
    public void HostOptions_round_trip_enable_tray_icon()
    {
        // Explicit false must survive a save/load round-trip via the source-generated
        // context (which uses camelCase), so the user can actually disable the tray
        // by editing the config file.
        const string json = """{ "host": { "enableTrayIcon": false } }""";
        var cfg = JsonSerializer.Deserialize(json, AppJsonContext.Default.RootConfig);
        Assert.NotNull(cfg);
        Assert.False(cfg.Host.EnableTrayIcon);

        var round = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(cfg, AppJsonContext.Default.RootConfig),
            AppJsonContext.Default.RootConfig);
        Assert.False(round!.Host.EnableTrayIcon);
    }

    [Fact]
    public void NotifyOptions_round_trip_duration_ms()
    {
        // Verify that durationMs is correctly round-tripped.
        const string json = """{ "notify": { "enabled": true, "durationMs": 5000 } }""";
        var cfg = JsonSerializer.Deserialize(json, AppJsonContext.Default.RootConfig);
        Assert.NotNull(cfg);
        Assert.True(cfg.Notify.Enabled);
        Assert.Equal(5000, cfg.Notify.DurationMs);

        var round = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(cfg, AppJsonContext.Default.RootConfig),
            AppJsonContext.Default.RootConfig);
        Assert.Equal(5000, round!.Notify.DurationMs);
    }

    [Theory]
    [InlineData("""{ "defaultBrowser": "edge" }""")]
    [InlineData("""{ "defaultBrowser": "edge", "rules": null }""")]
    [InlineData("""{ "defaultBrowser": "edge", "sourceRules": null, "filters": null }""")]
    [InlineData("""{ "defaultBrowser": "edge", "rules": null, "sourceRules": null, "filters": null }""")]
    public void Rules_sourceRules_filters_default_to_empty_when_missing_or_null(string json)
    {
        // The three rule-list sections are all optional. A user config may omit them
        // entirely, or set them to `null` (e.g. from a templated editor). Either way
        // the deserialized config must surface empty lists so the rule engine has
        // nothing to iterate and silently falls through to the default browser.
        var cfg = JsonSerializer.Deserialize(json, AppJsonContext.Default.RootConfig);
        Assert.NotNull(cfg);
        Assert.NotNull(cfg.Rules);
        Assert.NotNull(cfg.SourceRules);
        Assert.NotNull(cfg.Filters);
        Assert.Empty(cfg.Rules);
        Assert.Empty(cfg.SourceRules);
        Assert.Empty(cfg.Filters);
    }

    [Fact]
    public void Validate_does_not_throw_when_rule_lists_are_explicitly_null_in_json()
    {
        // The [JsonConstructor] on RootConfig normalises null/missing list sections
        // to empty via `?? []`, so a hand-written `"rules": null` in the config file
        // must not crash the validator. We omit DefaultBrowser so Validate has nothing
        // to warn about.
        const string json = """{ "rules": null, "sourceRules": null, "filters": null }""";
        var cfg = JsonSerializer.Deserialize(json, AppJsonContext.Default.RootConfig);
        Assert.NotNull(cfg);

        var warnings = cfg.Validate();
        Assert.Empty(warnings);
    }
}