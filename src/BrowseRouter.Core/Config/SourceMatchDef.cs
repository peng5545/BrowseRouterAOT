using System;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace BrowseRouter.Core.Config;

/// <summary>
/// Polymorphic source matcher base. Source rules route based on which process /
/// window initiated the URL open, not on the URL itself. Used for cases like
/// "links from Teams always open in Edge".
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ProcessMatch), "process")]
[JsonDerivedType(typeof(ProcessPathMatch), "processPath")]
[JsonDerivedType(typeof(ProcessPathPrefixMatch), "processPathPrefix")]
[JsonDerivedType(typeof(WindowTitleContainsMatch), "windowTitleContains")]
[JsonDerivedType(typeof(WindowTitleRegexMatch), "windowTitleRegex")]
public abstract class SourceMatchDef
{
    /// <summary>
    /// True when the source (calling process / its window) matches this rule.
    /// All parameters may be <c>null</c> — caller couldn't determine that piece
    /// (e.g. orphaned launch from SYSTEM service). Implementations should
    /// tolerate nulls without throwing.
    /// </summary>
    public abstract bool IsMatch(string? processName, string? processPath, string? windowTitle);
}

/// <summary>
/// Matches by process filename (no directory), case-insensitive.
/// E.g. <c>"TEAMS.EXE"</c> matches <c>C:\...\TEAMS.EXE</c> or <c>teams.exe</c>.
/// </summary>
public sealed class ProcessMatch : SourceMatchDef
{
    /// <summary>
    /// The exact process file name to match. Required.
    /// </summary>
    public required string Value { get; set; }

    /// <inheritdoc/>
    public override bool IsMatch(string? processName, string? processPath, string? windowTitle) =>
        !string.IsNullOrEmpty(processName) && string.Equals(processName, Value, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Matches the exact full path of the calling process (case-insensitive).
/// </summary>
public sealed class ProcessPathMatch : SourceMatchDef
{
    /// <summary>
    /// The exact full path to match. Required.
    /// </summary>
    public required string Value { get; set; }

    /// <inheritdoc/>
    public override bool IsMatch(string? processName, string? processPath, string? windowTitle) =>
        !string.IsNullOrEmpty(processPath) && string.Equals(processPath, Value, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Matches when the calling process path starts with <see cref="Value"/>.
/// Useful for "all tools under <c>C:\Tools\</c>".
/// </summary>
public sealed class ProcessPathPrefixMatch : SourceMatchDef
{
    /// <summary>
    /// The path prefix to match. Required.
    /// </summary>
    public required string Value { get; set; }

    /// <inheritdoc/>
    public override bool IsMatch(string? processName, string? processPath, string? windowTitle) =>
        !string.IsNullOrEmpty(processPath) && processPath.StartsWith(Value, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Substring match on the calling process's main window title.
/// </summary>
public sealed class WindowTitleContainsMatch : SourceMatchDef
{
    /// <summary>
    /// The substring to look for. Required.
    /// </summary>
    public required string Value { get; set; }

    /// <inheritdoc/>
    public override bool IsMatch(string? processName, string? processPath, string? windowTitle) =>
        !string.IsNullOrEmpty(windowTitle) && windowTitle.Contains(Value, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Regex match on the calling process's main window title.
/// </summary>
public sealed class WindowTitleRegexMatch : SourceMatchDef
{
    private Regex? _compiled;

    /// <summary>
    /// The regex pattern. Required.
    /// </summary>
    public required string Value { get; set; }

    /// <inheritdoc/>
    public override bool IsMatch(string? processName, string? processPath, string? windowTitle)
    {
        if (string.IsNullOrEmpty(windowTitle))
            return false;
        var rx = _compiled ??= new Regex(Value, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return rx.IsMatch(windowTitle);
    }
}