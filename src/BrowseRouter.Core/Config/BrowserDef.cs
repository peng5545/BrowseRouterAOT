using System.Collections.Generic;

namespace BrowseRouter.Core.Config;

/// <summary>
/// Definition of one browser. <see cref="Path"/> is the executable; <see cref="Args"/>
/// is an optional argv to pass before/after the URL. Tags in arg elements such as
/// <c>{url}</c> / <c>{host}</c> / <c>{path}</c> are substituted per-URL by
/// <see cref="Routing.ArgsFormatter"/>. If no element contains a tag, the URL is
/// appended as a final argument automatically (matching the original BrowseRouter).
/// </summary>
public sealed class BrowserDef
{
    /// <summary>
    /// Path to the executable. May contain <c>%ENV%</c> tokens; expanded with
    /// <see cref="Environment.ExpandEnvironmentVariables(string)"/> at launch.
    /// </summary>
    public required string Path { get; set; }

    /// <summary>
    /// Optional list of arguments. Elements may contain template tags. May be omitted
    /// or empty — then a single <c>{url}</c> argument is added at launch time.
    /// </summary>
    public List<string>? Args { get; init; }
}