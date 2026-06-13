using System;
using System.Collections.Generic;
using System.Text;

namespace BrowseRouter.Core.Routing;

/// <summary>
/// Expands a browser's <c>args</c> template against a parsed URI. Templates may
/// contain the following tokens, all enclosed in <c>{ }</c>:
/// <list type="bullet">
///   <item><c>{url}</c> — full URL (post-filter).</item>
///   <item><c>{rawUrl}</c> — the URL as received from the OS, BEFORE filters.</item>
///   <item><c>{host}</c>, <c>{authority}</c>, <c>{path}</c>, <c>{query}</c>,
///         <c>{fragment}</c>, <c>{userinfo}</c>, <c>{port}</c> — sub-components.</item>
/// </list>
/// If no element of the args list contains ANY token, a final <c>{url}</c>
/// argument is appended (matching original BrowseRouter behaviour).
/// </summary>
public static class ArgsFormatter
{
    /// <summary>
    /// Build the final argument list to hand to <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>.
    /// </summary>
    /// <param name="template">Arguments template (may be null or empty).</param>
    /// <param name="uri">The (post-filter) URI to substitute tokens against.</param>
    /// <param name="rawUrl">The original URL before any filter was applied.</param>
    public static List<string> Format(List<string>? template, Uri uri, string rawUrl)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var result = new List<string>(capacity: (template?.Count ?? 0) + 1);
        var anyTokenSeen = false;

        if (template is { Count: > 0 })
        {
            foreach (var element in template)
            {
                var expanded = ExpandTokens(element, uri, rawUrl, out var tokenSeen);
                result.Add(expanded);
                anyTokenSeen |= tokenSeen;
            }
        }

        if (!anyTokenSeen)
        {
            // Original BrowseRouter convention: when no token was used, the URL is
            // appended as the last argument, so simple configurations
            // ("path": "chrome.exe") still work.
            result.Add(uri.OriginalString);
        }

        return result;
    }

    /// <summary>
    /// Expand a single argv element. Returns the expanded string and reports
    /// (<paramref name="tokenSeen"/>) whether any recognized token was substituted.
    /// Unknown tokens are left intact (no substitution, no scanning further).
    /// </summary>
    /// <remarks>
    /// Literal braces are written as <c>{{</c> and <c>}}</c>, matching the
    /// convention used by .NET format strings. The escape is processed in a
    /// single pass alongside token resolution so <c>{{url}}</c> emits the
    /// literal text <c>{url}</c> and the inner "url" is NOT re-resolved as a
    /// token. A <c>{{X}}</c> with non-empty X also counts as a "token seen"
    /// for the trailing-URL-append decision, so <c>["--template={{url}}"]</c>
    /// produces a single arg (the user clearly meant the placeholder to
    /// replace the URL slot, not sit alongside it).
    /// </remarks>
    private static string ExpandTokens(string input, Uri uri, string rawUrl, out bool tokenSeen)
    {
        tokenSeen = false;
        if (string.IsNullOrEmpty(input))
            return input;

        var sb = new StringBuilder(input.Length);
        var i = 0;
        while (i < input.Length)
        {
            var c = input[i];

            // Escaped opening brace: look for a matching `}}` to decide whether
            // this is a `{{X}}`-style would-be token (non-empty X) or a bare `{{`.
            if (c == '{' && i + 1 < input.Length && input[i + 1] == '{')
            {
                var end = input.IndexOf("}}", i + 2, StringComparison.Ordinal);
                if (end >= 0 && end > i + 2)
                {
                    // Non-empty inner: emit `{X}` literally, count as token-seen,
                    // skip past the closing `}}`.
                    var innerLen = end - (i + 2);
                    sb.Append('{');
                    sb.Append(input, i + 2, innerLen);
                    sb.Append('}');
                    tokenSeen = true;
                    i = end + 2;
                    continue;
                }

                // Bare `{{` (no `}}` follows, or empty inner `{{}}`) — emit one `{`.
                // A truly empty `{{}}` doesn't count as token-seen; a bare `{{` at
                // end of string doesn't either. Both fall through here.
                sb.Append('{');
                i += 2;
                continue;
            }

            // Escaped closing brace: emit `}` literally, skip the duplicate.
            if (c == '}' && i + 1 < input.Length && input[i + 1] == '}')
            {
                sb.Append('}');
                i += 2;
                continue;
            }

            // Real token: `{name}`.
            if (c == '{')
            {
                var close = input.IndexOf('}', i + 1);
                if (close < 0)
                {
                    // Unterminated — output the rest as literal.
                    sb.Append(input, i, input.Length - i);
                    break;
                }

                var token = input.AsSpan(i + 1, close - i - 1);
                var substitution = Resolve(token, uri, rawUrl);
                if (substitution is null)
                {
                    // Unknown token — pass through verbatim so users see what they wrote,
                    // not a silently dropped placeholder.
                    sb.Append(input, i, close - i + 1);
                }
                else
                {
                    sb.Append(substitution);
                    tokenSeen = true;
                }

                i = close + 1;
                continue;
            }

            // Plain character.
            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Resolve a single token; returns <c>null</c> for an unknown name.
    /// </summary>
    private static string? Resolve(ReadOnlySpan<char> token, Uri uri, string rawUrl) => token switch
    {
        "url" => uri.OriginalString,
        "rawUrl" => rawUrl,
        "host" => uri.Host,
        "authority" => uri.Authority,
        "path" => uri.AbsolutePath,
        "query" => uri.Query,
        "fragment" => uri.Fragment,
        "userinfo" => uri.UserInfo,
        "port" => uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => null
    };
}