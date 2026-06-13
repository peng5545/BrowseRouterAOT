using System;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading;

namespace BrowseRouter.Core.Config;

/// <summary>
/// Polymorphic URL matcher base. JSON shape:
/// <c>{ "type": "hostSuffix" | "exactHost" | "pathPrefix" | "regex", "value": "...", ... }</c>.
/// Concrete subclasses implement <see cref="IsMatch"/> against a parsed <see cref="Uri"/>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HostSuffixMatch), "hostSuffix")]
[JsonDerivedType(typeof(ExactHostMatch), "exactHost")]
[JsonDerivedType(typeof(PathPrefixMatch), "pathPrefix")]
[JsonDerivedType(typeof(RegexMatch), "regex")]
public abstract class MatchDef
{
    /// <summary>
    /// Return true if this matcher matches the given URI.
    /// </summary>
    public abstract bool IsMatch(Uri uri);
}

/// <summary>
/// Matches when the URI host equals <see cref="Value"/> or ends with
/// <c>"." + Value</c>. Comparison is case-insensitive. Example value: <c>github.com</c>
/// matches <c>github.com</c>, <c>www.github.com</c>, but NOT <c>notgithub.com</c>.
///
/// <para>IDN/punycode: the URI's host is normalised via <see cref="Uri.IdnHost"/>
/// (Unicode form) before comparison, so a config value of <c>bücher.example</c>
/// matches a URI whose <see cref="Uri.Host"/> returns <c>xn--bcher-kva.example</c>.</para>
/// </summary>
public sealed class HostSuffixMatch : MatchDef
{
    /// <summary>
    /// The host suffix to match. Required.
    /// </summary>
    public required string Value { get; set; }

    /// <inheritdoc/>
    public override bool IsMatch(Uri uri)
    {
        var host = UrlMatcherHelpers.NormalizeHost(uri);
        var suffix = Value;
        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(suffix))
            return false;
        if (string.Equals(host, suffix, StringComparison.OrdinalIgnoreCase))
            return true;
        return host.Length > suffix.Length &&
               host[^(suffix.Length + 1)] == '.' &&
               host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Matches when the URI host equals <see cref="Value"/> (case-insensitive).
/// IDN-normalised; see <see cref="HostSuffixMatch"/> for the rationale.
/// </summary>
public sealed class ExactHostMatch : MatchDef
{
    /// <summary>
    /// The exact host name to match. Required.
    /// </summary>
    public required string Value { get; set; }

    /// <inheritdoc/>
    public override bool IsMatch(Uri uri) =>
        string.Equals(UrlMatcherHelpers.NormalizeHost(uri), Value, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Best-effort IDN normalisation. <see cref="Uri.Host"/> returns the
/// canonical (Punycode-encoded) form for non-ASCII hosts. We decode the
/// Punycode back to Unicode via <see cref="System.Globalization.IdnMapping"/>
/// so a config value of <c>bücher.example</c> matches a URI whose
/// <see cref="Uri.Host"/> is <c>xn--bcher-kva.example</c>. Falls back to
/// the raw <see cref="Uri.Host"/> on any decode failure (malformed
/// punycode, .NET internal exceptions) so a degenerate input still
/// produces a stable comparison result instead of crashing the hot path.
/// </summary>
internal static class UrlMatcherHelpers
{
    private static readonly System.Globalization.IdnMapping Idn = new();

    internal static string NormalizeHost(Uri uri)
    {
        try
        {
            // For all-ASCII hosts, IdnMapping.GetUnicode returns the input
            // unchanged. For hosts with non-ASCII, it decodes the Punycode
            // back to Unicode (e.g. "xn--bcher-kva.example" →
            // "bücher.example").
            return Idn.GetUnicode(uri.Host);
        }
        catch
        {
            return uri.Host;
        }
    }
}

/// <summary>
/// Matches when the URI path starts with <see cref="Value"/> (case-insensitive,
/// ordinal). If <see cref="Host"/> is non-null, the host must additionally match it
/// as a hostSuffix (see <see cref="HostSuffixMatch"/>).
/// </summary>
public sealed class PathPrefixMatch : MatchDef
{
    /// <summary>
    /// The path prefix to match (must include leading <c>/</c>). Required.
    /// </summary>
    public required string Value { get; set; }

    /// <summary>
    /// Optional host suffix gate; when set, host must end with this too.
    /// </summary>
    public string? Host { get; set; }

    /// <inheritdoc/>
    public override bool IsMatch(Uri uri)
    {
        // Inline the host-suffix check to avoid allocating a new
        // HostSuffixMatch on every match. Logic mirrors HostSuffixMatch.IsMatch
        // (and reuses the same IDN normalisation helper).
        if (!string.IsNullOrEmpty(Host))
        {
            var host = UrlMatcherHelpers.NormalizeHost(uri);
            var suffix = Host;
            var exact = string.Equals(host, suffix, StringComparison.OrdinalIgnoreCase);
            var sub = host.Length > suffix.Length &&
                      host[^(suffix.Length + 1)] == '.' &&
                      host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
            if (!exact && !sub)
                return false;
        }

        var path = uri.AbsolutePath;
        return path.StartsWith(Value, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Matches when <see cref="Value"/> (a regular expression) matches the full URL
/// (<see cref="Uri.OriginalString"/>). <b>Substring, not anchored</b> — a pattern
/// of <c>google</c> will match <c>https://malicious-google.evil.com</c>. Anchor
/// with <c>^…$</c> explicitly if full-string match is required.
///
/// <para>AOT note: this matcher constructs a runtime (interpreted) <see cref="Regex"/>
/// — it cannot be source-generated since the pattern comes from user config.
/// <see cref="RegexOptions.Compiled"/> is intentionally NOT set because AOT does
/// not support runtime IL emit.</para>
///
/// <para>Compilation is wrapped in a <see cref="Lazy{T}"/> with
/// <see cref="LazyThreadSafetyMode.ExecutionAndPublication"/>: the first
/// concurrent caller may build the <see cref="Regex"/>, subsequent callers
/// reuse the cached instance, and a malformed pattern yields a null regex
/// that makes <see cref="IsMatch"/> always return false (no exception on the
/// hot path).</para>
/// </summary>
public sealed class RegexMatch : MatchDef
{
    private readonly Lazy<Regex?> _compiled;

    /// <summary>
    /// The regular expression to apply to the full URL. Required.
    /// </summary>
    public required string Value { get; set; } = "";

    /// <summary>
    /// STJ uses the parameterless ctor + property setters; we initialise the
    /// <see cref="Lazy{T}"/> here so the regex isn't built until first use.
    /// </summary>
    public RegexMatch()
    {
        _compiled = new Lazy<Regex?>(Compile, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    private Regex? Compile()
    {
        try
        {
            return new Regex(Value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        catch (ArgumentException)
        {
            // Malformed pattern in user config. Surface as "never matches" so
            // the daemon stays alive; RootConfig.Validate() can warn at load
            // time about this same condition.
            return null;
        }
    }

    /// <inheritdoc/>
    public override bool IsMatch(Uri uri) => _compiled.Value?.IsMatch(uri.OriginalString) ?? false;
}