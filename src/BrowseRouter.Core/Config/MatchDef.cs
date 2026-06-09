using System;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

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
        var host = uri.Host;
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
/// </summary>
public sealed class ExactHostMatch : MatchDef
{
    /// <summary>
    /// The exact host name to match. Required.
    /// </summary>
    public required string Value { get; set; }

    /// <inheritdoc/>
    public override bool IsMatch(Uri uri) =>
        string.Equals(uri.Host, Value, StringComparison.OrdinalIgnoreCase);
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
        if (!string.IsNullOrEmpty(Host))
        {
            var hostMatch = new HostSuffixMatch { Value = Host };
            if (!hostMatch.IsMatch(uri))
                return false;
        }

        var path = uri.AbsolutePath;
        return path.StartsWith(Value, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Matches when <see cref="Value"/> (a regular expression) matches the full URL
/// (<see cref="Uri.OriginalString"/>). AOT note: this matcher constructs a runtime
/// (interpreted) <see cref="Regex"/> — it cannot be source-generated since the pattern
/// comes from user config. <see cref="RegexOptions.Compiled"/> is intentionally NOT
/// set because AOT does not support runtime IL emit.
/// </summary>
public sealed class RegexMatch : MatchDef
{
    private Regex? _compiled;

    /// <summary>
    /// The regular expression to apply to the full URL. Required.
    /// </summary>
    public required string Value { get; set; }

    /// <inheritdoc/>
    public override bool IsMatch(Uri uri)
    {
        // Lazily build (and cache) the regex so reconfiguration triggers a fresh build.
        var rx = _compiled ??= new Regex(Value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return rx.IsMatch(uri.OriginalString);
    }
}