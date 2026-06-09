namespace BrowseRouter.Core.Config;

/// <summary>
/// One URL-routing rule. Order in <see cref="RootConfig.Rules"/> is significant.
/// </summary>
public sealed class RuleDef
{
    /// <summary>
    /// Key into <see cref="RootConfig.Browsers"/>. If the named browser is missing,
    /// the rule is skipped at evaluation time (a warning is logged once at load).
    /// </summary>
    public required string Browser { get; set; }

    /// <summary>
    /// The matcher that selects this rule.
    /// </summary>
    public required MatchDef Match { get; set; }

    /// <summary>
    /// Optional negative filter. If both <see cref="Match"/> and <see cref="Exclude"/>
    /// match the URL, the rule is treated as NOT matching — useful for
    /// "google.com → chrome, except /maps".
    /// </summary>
    public MatchDef? Exclude { get; set; }
}