using System;
using System.Net;

namespace BrowseRouter.Core.UriUtil;

/// <summary>
/// Parses input strings into <see cref="System.Uri"/> using a small fallback chain.
/// Logic ported from BrowseRouter (Model/UriFactory.cs) — kept identical so behaviour
/// matches the original: try as-is, then prepend <c>https://</c>, then URL-decode.
/// </summary>
public static class UriFactory
{
    /// <summary>
    /// Attempt to parse <paramref name="url"/> as an absolute URI.
    /// Tries the string verbatim, then prefixes <c>https://</c>, then runs
    /// <see cref="WebUtility.UrlDecode(string)"/> once. Returns <c>null</c> if
    /// none of those produce a valid absolute URI.
    /// </summary>
    public static Uri? TryParse(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            Uri.TryCreate($"https://{url}", UriKind.Absolute, out uri))
            return uri;

        var decoded = WebUtility.UrlDecode(url);
        return Uri.TryCreate(decoded, UriKind.Absolute, out uri) ? uri : null;
    }
}