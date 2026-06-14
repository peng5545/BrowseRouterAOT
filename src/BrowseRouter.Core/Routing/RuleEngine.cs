using BrowseRouter.Core.Config;
using BrowseRouter.Core.UriUtil;
using System.Diagnostics.CodeAnalysis;

namespace BrowseRouter.Core.Routing;

/// <summary>
/// The outcome of resolving a request against the config: which browser was chosen,
/// the URL to hand it (post-filter), and what filter (if any) fired.
/// </summary>
/// <param name="BrowserName">Key from <see cref="RootConfig.Browsers"/>.</param>
/// <param name="Browser">The resolved browser definition.</param>
/// <param name="Uri">The (post-filter) URI to launch.</param>
/// <param name="RawUrl">The original URL string before filtering.</param>
/// <param name="AppliedFilter">Name of the filter that fired, or <c>null</c>.</param>
/// <param name="Reason">Human-readable explanation (which rule fired). Logged.</param>
public sealed record RouteResult(
    string BrowserName,
    BrowserDef Browser,
    Uri Uri,
    string RawUrl,
    string? AppliedFilter,
    string Reason
);

/// <summary>
/// Why a resolve attempt failed (for logging / notification).
/// </summary>
public enum RouteFailureReason
{
    /// <summary>
    /// The supplied URL string couldn't be parsed.
    /// </summary>
    InvalidUrl,

    /// <summary>
    /// No source rule, URL rule, or <see cref="RootConfig.DefaultBrowser"/> applied.
    /// </summary>
    NoRuleMatched,

    /// <summary>
    /// A rule matched but the referenced browser key is absent from <see cref="RootConfig.Browsers"/>.
    /// </summary>
    BrowserNotConfigured
}

/// <summary>
/// Failure outcome for <see cref="RuleEngine.Resolve"/>.
/// </summary>
/// <param name="Reason">Failure kind.</param>
/// <param name="Detail">Diagnostic detail (e.g. the missing browser key).</param>
public sealed record RouteFailure(RouteFailureReason Reason, string Detail);

/// <summary>
/// Pure resolver: given a snapshot of <see cref="RootConfig"/>, a URL, and source-process
/// metadata, decide which browser should open the URL. Stateless and side-effect-free
/// so it can be unit-tested without the Host or Launcher.
/// Order: <see cref="RootConfig.SourceRules"/> → <see cref="RootConfig.Rules"/> →
/// <see cref="RootConfig.DefaultBrowser"/>. Filters are applied to the URL before
/// the chosen browser is invoked; rule-matching runs on the FILTERED URL so that
/// e.g. SafeLinks-unwrapped URLs hit the rule that targets the real host.
/// </summary>
public static class RuleEngine
{
    /// <summary>
    /// Resolve <paramref name="rawUrl"/> against <paramref name="config"/>.
    /// Canonical Try-pattern: on success returns <c>true</c> and <paramref name="route"/>
    /// is non-null; on failure returns <c>false</c> and <paramref name="err"/> is set.
    /// The <see cref="NotNullWhenAttribute"/>s let the compiler prove both out-params'
    /// nullability in the matching branch, so callers don't need null-forgiving operators.
    /// </summary>
    /// <param name="config">Current config snapshot (never null).</param>
    /// <param name="rawUrl">URL as received from the caller (before filters).</param>
    /// <param name="sourceProcessName">Optional calling-process file name.</param>
    /// <param name="sourceProcessPath">Optional calling-process full path.</param>
    /// <param name="sourceWindowTitle">Optional calling-window title.</param>
    /// <param name="route">Matched route, or <c>null</c> on failure.</param>
    /// <param name="err">Failure detail, or <c>null</c> on success.</param>
    /// <param name="onFilterError">Per-filter error sink (filter name, exception).</param>
    /// <returns><c>true</c> on success; <c>false</c> if <paramref name="err"/> is set.</returns>
    public static bool Resolve(
        RootConfig config,
        string rawUrl,
        string? sourceProcessName,
        string? sourceProcessPath,
        string? sourceWindowTitle,
        [NotNullWhen(true)] out RouteResult? route,
        [NotNullWhen(false)] out RouteFailure? err,
        Action<string, Exception>? onFilterError = null
    )
    {
        ArgumentNullException.ThrowIfNull(config);

        // 1. Apply filters to the URL (may rewrite e.g. SafeLinks → real URL).
        // Filters / Rules / SourceRules default to []; treat an explicit null the
        // same way so a user-written `"filters": null` doesn't NRE here.
        var filtered =
            FilterPipeline.TryApply(config.Filters, rawUrl, out var afterFilter, out var appliedFilter, onFilterError)
                ? afterFilter
                : rawUrl;

        // 2. Parse the (possibly rewritten) URL.
        var uri = UriFactory.TryParse(filtered);
        if (uri is null)
        {
            route = null;
            err = new RouteFailure(RouteFailureReason.InvalidUrl, filtered);
            return false;
        }

        // 3. Source rules first (highest precedence per the plan).
        foreach (var rule in config.SourceRules)
        {
            if (rule.Match.IsMatch(sourceProcessName, sourceProcessPath, sourceWindowTitle))
            {
                return PickBrowser(config, rule.Browser, uri, rawUrl, appliedFilter,
                    $"matched source rule ({DescribeSource(rule.Match)})", out route, out err);
            }
        }

        // 4. URL rules — first match wins; honour `exclude` as a negative gate.
        foreach (var rule in config.Rules)
        {
            if (!rule.Match.IsMatch(uri))
                continue;
            if (rule.Exclude is not null && rule.Exclude.IsMatch(uri))
                continue;
            return PickBrowser(config, rule.Browser, uri, rawUrl, appliedFilter,
                $"matched URL rule ({DescribeRule(rule)})", out route, out err);
        }

        // 5. Default browser (if configured).
        if (!string.IsNullOrWhiteSpace(config.DefaultBrowser))
        {
            return PickBrowser(config, config.DefaultBrowser, uri, rawUrl, appliedFilter,
                "fell through to defaultBrowser", out route, out err);
        }

        route = null;
        err = new RouteFailure(RouteFailureReason.NoRuleMatched, uri.Host);
        return false;
    }

    /// <summary>
    /// Look up <paramref name="browserKey"/> in <paramref name="config"/>.Browsers.
    /// Returns <c>true</c> on success (route set, err null) or <c>false</c> on miss.
    /// </summary>
    private static bool PickBrowser(
        RootConfig config,
        string browserKey,
        Uri uri,
        string rawUrl,
        string? appliedFilter,
        string reason,
        [NotNullWhen(true)] out RouteResult? route,
        [NotNullWhen(false)] out RouteFailure? err
    )
    {
        if (!config.Browsers.TryGetValue(browserKey, out var def))
        {
            route = null;
            err = new RouteFailure(RouteFailureReason.BrowserNotConfigured, browserKey);
            return false;
        }

        route = new RouteResult(browserKey, def, uri, rawUrl, appliedFilter, reason);
        err = null;
        return true;
    }

    /// <summary>
    /// Short human label for a URL matcher, for log/notification messages.
    /// </summary>
    public static string DescribeMatch(MatchDef m) => m switch
    {
        HostSuffixMatch h => $"hostSuffix={h.Value}",
        ExactHostMatch e => $"exactHost={e.Value}",
        PathPrefixMatch p => p.Host is { Length: > 0 } host ? $"pathPrefix={p.Value}@{host}" : $"pathPrefix={p.Value}",
        RegexMatch r => $"regex={r.Value}",
        _ => m.GetType().Name
    };

    /// <summary>
    /// Render a rule (its match clause plus, if present, its exclude clause)
    /// as a single human-readable string for log/notification messages. The
    /// exclude clause is appended in parentheses so a rule like
    /// <c>github.com → chrome, except /maps</c> logs as
    /// <c>hostSuffix=github.com (exclude pathPrefix=/maps)</c>.
    /// </summary>
    public static string DescribeRule(RuleDef rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var head = DescribeMatch(rule.Match);
        return rule.Exclude is null ? head : $"{head} (exclude {DescribeMatch(rule.Exclude)})";
    }

    /// <summary>
    /// Short human label for a source matcher.
    /// </summary>
    public static string DescribeSource(SourceMatchDef m) => m switch
    {
        ProcessMatch p => $"process={p.Value}",
        ProcessPathMatch p => $"processPath={p.Value}",
        ProcessPathPrefixMatch p => $"processPathPrefix={p.Value}",
        WindowTitleContainsMatch w => $"windowTitleContains={w.Value}",
        WindowTitleRegexMatch w => $"windowTitleRegex={w.Value}",
        _ => m.GetType().Name
    };
}