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
/// 
/// <para><b>Escaping literal braces.</b> Use <c>{{</c> for a literal <c>{</c>
/// and <c>}}</c> for a literal <c>}</c>. The pair <c>{{X}}</c> (with non-empty
/// X) is recognised as a <i>would-be</i> token placeholder and emits the
/// literal text <c>{X}</c> — the inner <c>X</c> is NOT re-resolved. A
/// <c>{{X}}</c> counts as a "token seen" for the trailing-URL-append
/// decision (see <see cref="Format"/>).</para>
/// 
/// <para><b>Known limitations.</b>
/// <list type="bullet">
///   <item>An unterminated token (e.g. <c>{host</c> with no closing <c>}</c>)
///   is copied through verbatim — it does NOT throw, but no expansion happens.</item>
///   <item>Tokens are matched by exact name; <c>{hostSuffix}</c> does NOT
///   expand to <c>{host}</c>'s value plus <c>Suffix</c>. Use a single
///   recognised token, or pass the URL through one of the pre-defined
///   components and let the browser split it.</item>
///   <item>Closing braces in the middle of a token's argument (e.g.
///   <c>{port}</c> followed by <c>}</c>) will be consumed by the token parser
///   if they appear as <c>}}</c>. A stray single <c>}</c> after a token is
///   copied through verbatim.</item>
/// </list>
/// </para>
/// 
/// <para><b>Shell-injection safety.</b> Expanded arguments are written to the
/// <see cref="IList{T}"/> sink; the caller is responsible for routing each
/// element through a safe argv API (e.g.
/// <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>). Do NOT
/// concatenate expanded values into a raw command string — that is where
/// shell-metacharacter injection would happen. <see>
///     <cref>BrowserLauncher</cref>
/// </see>
/// uses <c>ArgumentList</c>, so production usage is safe.</para>
/// </summary>
public static class ArgsFormatter
{
    /// <summary>
    /// Expand <paramref name="template"/> and write the resulting arguments
    /// directly to <paramref name="sink"/>. The sink is typically
    /// <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/>, but
    /// any <see cref="IList{T}"/> works (tests pass a <see cref="List{T}"/>).
    /// </summary>
    /// <param name="template">Arguments template (may be null or empty).</param>
    /// <param name="uri">The (post-filter) URI to substitute tokens against.</param>
    /// <param name="rawUrl">The original URL before any filter was applied.</param>
    /// <param name="sink">Receives each expanded argument, in order.</param>
    public static void Format(List<string>? template, Uri uri, string rawUrl, IList<string> sink)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(sink);
        var anyTokenSeen = false;

        if (template is { Count: > 0 })
        {
            foreach (var element in template)
            {
                var expanded = ExpandTokens(element, uri, rawUrl, out var tokenSeen);
                sink.Add(expanded);
                anyTokenSeen |= tokenSeen;
            }
        }

        if (!anyTokenSeen)
        {
            // Original BrowseRouter convention: when no token was used, the URL is
            // appended as the last argument, so simple configurations
            // ("path": "chrome.exe") still work.
            sink.Add(uri.OriginalString);
        }
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

            switch (c)
            {
                // Escaped opening brace: look for a matching `}}` to decide whether
                // this is a `{{X}}`-style would-be token (non-empty X) or a bare `{{`.
                case '{' when i + 1 < input.Length && input[i + 1] == '{':
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
                case '}' when i + 1 < input.Length && input[i + 1] == '}':
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