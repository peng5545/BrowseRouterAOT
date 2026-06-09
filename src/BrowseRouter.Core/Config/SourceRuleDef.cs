namespace BrowseRouter.Core.Config;

/// <summary>
/// One source-routing rule. Evaluated before <see cref="RootConfig.Rules"/>.
/// </summary>
public sealed class SourceRuleDef
{
    /// <summary>
    /// Key into <see cref="RootConfig.Browsers"/>.
    /// </summary>
    public required string Browser { get; set; }

    /// <summary>
    /// The source matcher (process / window title) that selects this rule.
    /// </summary>
    public required SourceMatchDef Match { get; set; }
}