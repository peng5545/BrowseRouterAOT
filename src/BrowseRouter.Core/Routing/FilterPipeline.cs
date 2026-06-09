using BrowseRouter.Core.Config;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace BrowseRouter.Core.Routing;

/// <summary>
/// Applies <see cref="FilterDef"/> rewrites to a URL. Filters are sorted ascending by
/// <see cref="FilterDef.Priority"/>; the first filter whose regex actually changes
/// the URL wins (matching original BrowseRouter semantics — see
/// <c>BrowseRouterX/BrowseRouter/Config/FilterPreference.cs::TryApply</c>).
/// A single faulty filter (bad regex, exception during replace) is logged
/// (via <paramref>
///     <name>onError</name>
/// </paramref>
/// ) and skipped — other filters keep running.
/// Also supports an <c>unescape($N)</c> macro that URL-decodes the captured group.
/// </summary>
public static partial class FilterPipeline
{
    /// <summary>
    /// Matches <c>unescape($N)</c> macros inside a replacement template.
    /// </summary>
    [GeneratedRegex(@"unescape\(\$(\d+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex UnescapeMacroRegex();

    /// <summary>
    /// Matches plain <c>$N</c> group references inside a replacement template.
    /// </summary>
    [GeneratedRegex(@"\$(\d+)", RegexOptions.CultureInvariant)]
    private static partial Regex GroupReferenceRegex();

    private static readonly Regex UnescapeMacro = UnescapeMacroRegex();
    private static readonly Regex GroupRef = GroupReferenceRegex();

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

        foreach (var filter in filters.OrderBy(f => f.Priority))
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
    /// Returns the input unchanged when the find pattern doesn't match.
    /// </summary>
    private static string ApplyOne(FilterDef filter, string input)
    {
        // Cache the compiled regex on the FilterDef instance — it survives across
        // every URL click until the owning RootConfig is replaced by a config
        // reload, at which point the old instance (and its cached regex) becomes
        // GC-eligible. AOT forbids RegexOptions.Compiled (no JIT for IL emit),
        // so we still pay the parse cost on first use, but never again.
        var find = filter.CompiledRegex ??= new Regex(
            filter.Find, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return find.Replace(input, match => ExpandReplacement(filter.Replace, match));
    }

    /// <summary>
    /// Expand <c>unescape($N)</c> first (so a literal <c>$1</c> inside the unescaped
    /// payload isn't re-interpreted), then plain <c>$N</c>.
    /// </summary>
    private static string ExpandReplacement(string template, Match match)
    {
        var afterUnescape = UnescapeMacro.Replace(template, m =>
        {
            var n = int.Parse(m.Groups[1].ValueSpan, System.Globalization.CultureInfo.InvariantCulture);
            return n < match.Groups.Count ? System.Net.WebUtility.UrlDecode(match.Groups[n].Value) : string.Empty;
        });

        return GroupRef.Replace(afterUnescape, m =>
        {
            var n = int.Parse(m.Groups[1].ValueSpan, System.Globalization.CultureInfo.InvariantCulture);
            return n < match.Groups.Count ? match.Groups[n].Value : string.Empty;
        });
    }
}