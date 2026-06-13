using BrowseRouter.Core.Routing;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace BrowseRouter.Core.Config;

/// <summary>
/// Root configuration object as persisted to <c>%AppData%\BrowseRouterAOT\browsers.json</c>.
/// Deserialised through the source-generated <c>AppJsonContext</c>.
/// </summary>
public sealed class RootConfig
{
    /// <summary>
    /// Host-daemon tuning. Optional; defaults apply when omitted.
    /// </summary>
    public HostOptions Host { get; set; }

    /// <summary>
    /// Notification preferences. Optional.
    /// </summary>
    public NotifyOptions Notify { get; set; }

    /// <summary>
    /// Logging preferences. Optional.
    /// </summary>
    public LogOptions Log { get; set; }

    /// <summary>
    /// Name of the fallback browser when no rule matches. May be <c>null</c>;
    /// in that case the OS default-browser is invoked as a final fallback
    /// (use sparingly — the OS default is BrowseRouter once registered, which
    /// would loop).
    /// </summary>
    public string? DefaultBrowser { get; set; }

    /// <summary>
    /// Browser definitions, keyed by name (referenced by rules).
    /// </summary>
    public Dictionary<string, BrowserDef> Browsers { get; init; }

    /// <summary>
    /// URL-routing rules. Evaluated in order; first match wins.
    /// </summary>
    public List<RuleDef> Rules { get; init; }

    /// <summary>
    /// Source-routing rules. Evaluated BEFORE <see cref="Rules"/>.
    /// </summary>
    public List<SourceRuleDef> SourceRules { get; init; }

    /// <summary>
    /// URL-rewrite filters. Sorted by priority; first changing filter wins.
    /// </summary>
    public List<FilterDef> Filters { get; init; }

    /// <summary>
    /// Convenience: an empty, valid config used as a safe default.
    /// </summary>
    public static RootConfig Empty => new();

    /// <summary>
    /// Constructor the source-generated deserializer calls. The
    /// <see cref="JsonConstructorAttribute"/> makes System.Text.Json route the
    /// deserializer through this overload rather than the parameterless one —
    /// necessary because the source generator otherwise assigns
    /// <c>default(List&lt;T&gt;) = null</c> to <see cref="Rules"/>,
    /// <see cref="SourceRules"/>, and <see cref="Filters"/> for sections that are
    /// missing from the JSON (overriding the <c>= []</c> property initializers).
    /// All three lists plus <see cref="Browsers"/> are normalised here so that
    /// omitting any of them — or writing them as <c>null</c> — yields a
    /// well-formed empty collection.
    /// </summary>
    [JsonConstructor]
    public RootConfig(
        HostOptions? host = null,
        NotifyOptions? notify = null,
        LogOptions? log = null,
        string? defaultBrowser = null,
        Dictionary<string, BrowserDef>? browsers = null,
        List<RuleDef>? rules = null,
        List<SourceRuleDef>? sourceRules = null,
        List<FilterDef>? filters = null
    )
    {
        Host = host ?? new HostOptions();
        Notify = notify ?? new NotifyOptions();
        Log = log ?? new LogOptions();
        DefaultBrowser = defaultBrowser;
        Browsers = browsers ?? new Dictionary<string, BrowserDef>(StringComparer.OrdinalIgnoreCase);
        Rules = rules ?? [];
        SourceRules = sourceRules ?? [];
        // Sort filters by priority at load time so the per-click hot path
        // (FilterPipeline.TryApply) iterates them in priority order without
        // re-allocating an OrderBy buffer on every URL click. Stable sort
        // preserves the on-disk order for ties, matching the original
        // BrowseRouter semantics.
        Filters = (filters ?? []).OrderBy(f => f.Priority).ToList();
    }

    /// <summary>
    /// Validates the configuration and returns a list of warnings for missing
    /// browser definitions referenced by rules. Uses <see cref="RuleEngine.DescribeMatch"/>
    /// and <see cref="RuleEngine.DescribeSource"/> for the rule part of the message so
    /// warnings show the actual value the user wrote (e.g. <c>hostSuffix=github.com</c>)
    /// rather than the CLR type name (e.g. <c>HostSuffixMatch</c>).
    /// </summary>
    public List<string> Validate()
    {
        var warnings = new List<string>();

        if (DefaultBrowser != null && !Browsers.ContainsKey(DefaultBrowser))
        {
            warnings.Add($"DefaultBrowser '{DefaultBrowser}' is not defined in 'browsers'.");
        }

        if (Notify is { Enabled: true, DurationMs: < 500 or > 60000 })
        {
            warnings.Add(
                $"Notify.DurationMs ({Notify.DurationMs}ms) is outside the recommended range of 500ms to 60000ms.");
        }

        // Rules / SourceRules are guaranteed non-null by the JsonConstructor
        // (defaulting to [] on the way in), so a straight foreach is safe.
        foreach (var rule in Rules)
        {
            if (!Browsers.ContainsKey(rule.Browser))
            {
                warnings.Add(
                    $"URL rule matches '{RuleEngine.DescribeRule(rule)}' but references undefined browser '{rule.Browser}'.");
            }
        }

        foreach (var rule in SourceRules)
        {
            if (!Browsers.ContainsKey(rule.Browser))
            {
                warnings.Add(
                    $"Source rule matches '{RuleEngine.DescribeSource(rule.Match)}' but references undefined browser '{rule.Browser}'.");
            }
        }

        return warnings;
    }
}