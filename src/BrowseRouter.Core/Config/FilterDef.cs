using System.Text.RegularExpressions;
using System.Threading;

namespace BrowseRouter.Core.Config;

/// <summary>
/// One URL-rewrite filter. <see cref="Find"/> is a regex; <see cref="Replace"/> is a
/// .NET regex replacement template (e.g. <c>$1</c>), with an extra
/// <c>unescape($N)</c> macro that URL-decodes the captured group. Filters with
/// numerically smaller <see cref="Priority"/> are tried first; the first filter
/// that actually changes the URL wins (matching the original BrowseRouter).
/// </summary>
public sealed class FilterDef
{
    /// <summary>
    /// Human-readable name (logged when filter fires).
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// Regex pattern applied to the URL. Required.
    /// </summary>
    public required string Find { get; set; } = string.Empty;

    /// <summary>
    /// Replacement template. Supports <c>$N</c> and <c>unescape($N)</c>. Required.
    /// </summary>
    public required string Replace { get; set; }

    /// <summary>
    /// Lower = higher priority. Default <c>0</c>.
    /// </summary>
    public int Priority { get; set; }

    // Lazily-built regex, owned by this FilterDef instance. The host reuses the
    // same FilterDef across every URL click (filters live inside the cached
    // RootConfig snapshot), so a single parse here is reused for the entire
    // window between config reloads. On reload the old RootConfig is dropped
    // and these Regex objects become garbage with their owning FilterDef.
    //
    // AOT note: RegexOptions.Compiled is intentionally NOT set — AOT does not
    // support runtime IL emit. Same caveat as RegexMatch / WindowTitleRegexMatch.
    //
    // Lazy<Regex?> with ExecutionAndPublication: first concurrent caller wins
    // the build, others reuse the cached instance. A bad pattern returns null
    // so FilterPipeline.ApplyOne skips the filter without throwing on the hot
    // path.
    internal readonly Lazy<Regex?> CompiledRegex;

    public FilterDef()
    {
        CompiledRegex = new Lazy<Regex?>(() =>
        {
            try
            {
                // Find is `required` so the C# nullable analysis should
                // accept it as non-null, but the constructor runs before the
                // required-property check fires — null-forgiving is the
                // accepted pattern for this gap.
                return new Regex(Find, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }
}