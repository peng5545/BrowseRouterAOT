using BrowseRouter.Core.Config;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace BrowseRouter.Core.Routing;

/// <summary>
/// Applies <see cref="FilterDef"/> rewrites to a URL. Filters are expected to be
/// pre-sorted ascending by <see cref="FilterDef.Priority"/> — <see cref="RootConfig"/>
/// does this in its JSON constructor, so the per-click hot path iterates them
/// in priority order without re-allocating an OrderBy buffer on every URL click.
/// The first filter whose regex actually changes the URL wins (matching the
/// original BrowseRouter semantics). A single faulty filter (bad regex,
/// exception during replace) is logged (via <paramref>
///     <name>onError</name>
/// </paramref>
/// ) and skipped — other filters keep running.
/// Also supports an <c>unescape($N)</c> macro that URL-decodes the captured group.
/// </summary>
public static partial class FilterPipeline
{
    /// <summary>
    /// Combined pattern: matches either <c>unescape($N)</c> or plain <c>$N</c>
    /// in a single pass, avoiding two sequential regex replacements on the
    /// template string.
    /// Group 1 = captured <c>$N</c> inside unescape, Group 2 = plain <c>$N</c>.
    /// </summary>
    [GeneratedRegex(@"unescape\(\$(\d+)\)|\$(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex CombinedReplaceRegex();

    /// <summary>
    /// Try to apply filters; returns true (and sets <paramref name="output"/> to the
    /// rewritten URL) if any filter changed the input. Otherwise returns false and
    /// <paramref name="output"/> equals <paramref name="input"/>.
    /// </summary>
    /// <param name="filters">Filter list (typically <see cref="RootConfig.Filters"/>).</param>
    /// <param name="input">URL string to rewrite.</param>
    /// <param name="output">Receives the rewritten URL.</param>
    /// <param name="appliedFilterName">Name of the filter that fired, if any.</param>
    /// <param name="onError">Per-filter error sink (filter name, exception); may be null.</param>
    public static bool TryApply(
        IEnumerable<FilterDef>? filters,
        string input,
        out string output,
        out string? appliedFilterName,
        Action<string, Exception>? onError = null
    )
    {
        output = input;
        appliedFilterName = null;
        if (filters is null)
            return false;

        // Filters come pre-sorted by RootConfig. Iteration order = priority
        // order. The first filter that actually changes the input wins.
        foreach (var filter in filters)
        {
            string candidate;
            try
            {
                candidate = ApplyOne(filter, input);
            }
            catch (Exception ex)
            {
                onError?.Invoke(filter.Name, ex);
                continue;
            }

            if (!string.Equals(candidate, input, StringComparison.Ordinal))
            {
                output = candidate;
                appliedFilterName = filter.Name;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Apply one filter to <paramref name="input"/>, expanding both <c>$N</c>
    /// (verbatim group) and <c>unescape($N)</c> (URL-decoded group) macros.
    /// Returns the input unchanged when the find pattern doesn't match or
    /// the filter's regex failed to compile (treated as a no-op, not an error).
    /// </summary>
    private static string ApplyOne(FilterDef filter, string input)
    {
        // CompiledRegex is a Lazy<Regex?> with ExecutionAndPublication thread
        // safety; if a bad pattern in the config yielded null, just skip the
        // filter (the URL passes through unchanged) instead of throwing.
        var find = filter.CompiledRegex.Value;
        return find is null ? input : find.Replace(input, match => ExpandReplacement(filter.Replace, match));
    }

    /// <summary>
    /// Expand <c>unescape($N)</c> and <c>$N</c> in a single pass using the
    /// combined regex. Group 1 = unescape($N) captured group number,
    /// Group 2 = plain $N. This avoids running two separate regex replacements
    /// on the template per URL click.
    /// </summary>
    private static string ExpandReplacement(string template, Match match)
    {
        return CombinedReplaceRegex()
            .Replace(template, m =>
            {
                if (m.Groups[1].Success)
                {
                    // unescape($N) — URL-decode the captured group.
                    var n = int.Parse(m.Groups[1].ValueSpan, System.Globalization.CultureInfo.InvariantCulture);
                    return n < match.Groups.Count
                        ? System.Net.WebUtility.UrlDecode(match.Groups[n].Value)
                        : string.Empty;
                }

                // Plain $N — verbatim group reference.
                var n2 = int.Parse(m.Groups[2].ValueSpan, System.Globalization.CultureInfo.InvariantCulture);
                return n2 < match.Groups.Count ? match.Groups[n2].Value : string.Empty;
            });
    }
}