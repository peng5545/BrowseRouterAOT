using System.Net;

namespace BrowseRouter.Core.UriUtil;

/// <summary>
/// Parses input strings into <see cref="System.Uri"/> using a small fallback chain
/// tailored to the program's HTTP/HTTPS-only role. The chain:
/// <list type="number">
/// <item>Pre-validate any explicit <c>scheme:</c> prefix in the input. A non-web
/// scheme (<c>mailto:</c>, <c>file:</c>, <c>ms-windows-store:</c>, custom) is
/// rejected outright so it can't be silently accepted (or hijacked into an
/// <c>https://scheme:…</c> string by the next step).</item>
/// <item>Try the input verbatim — only accept <c>http</c>/<c>https</c>.</item>
/// <item>If no scheme was present, prefix <c>https://</c> and try again.</item>
/// <item>As a last resort, percent-decode the input once (handles SafeLinks and
/// similar wrappers). The decoded result must still be http/https.</item>
/// </list>
/// Returns <c>null</c> if none of the above produce an http/https URI.
/// </summary>
public static class UriFactory
{
    /// <summary>
    /// Attempt to parse <paramref name="url"/> as an http/https absolute URI.
    /// </summary>
    public static Uri? TryParse(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        // Pre-validate scheme (if any). We can't trust Uri.TryCreate to
        // reject non-web schemes consistently: e.g. "mailto:foo@bar" returns
        // false from TryCreate(Absolute), which would then let the
        // "https://" prefix fallback below happily produce the nonsense
        // "https://mailto:foo@bar". A cheap manual scan of the prefix
        // blocks that.
        if (TryExtractScheme(url, out var scheme))
        {
            return IsWebSchemeName(scheme) && Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsWebScheme(uri)
                ? uri
                : null;
        }

        // No scheme — promote to https://.
        if (Uri.TryCreate($"https://{url}", UriKind.Absolute, out var promoted))
            return promoted;

        // Percent-decode fallback. Only accept if the decoded form is itself
        // http/https — this prevents a percent-encoded "mailto:foo@bar" from
        // silently swapping the host after decode.
        var decoded = WebUtility.UrlDecode(url);
        if (!string.IsNullOrEmpty(decoded) &&
            decoded != url &&
            Uri.TryCreate(decoded, UriKind.Absolute, out var decodedUri) &&
            IsWebScheme(decodedUri))
            return decodedUri;

        return null;
    }

    /// <summary>
    /// Detect an explicit <c>scheme:</c> prefix in <paramref name="input"/>.
    /// Returns true (and the lowercase scheme name) when the input starts with
    /// an alphabetic scheme followed by ':' and that colon is the first
    /// "delimiter" character (no '/', '?', '#', or whitespace before it).
    /// RFC 3986: scheme = ALPHA *( ALPHA / DIGIT / "+" / "-" / "." ).
    /// </summary>
    private static bool TryExtractScheme(string input, out string scheme)
    {
        scheme = string.Empty;
        if (input.Length == 0)
            return false;
        var first = input[0];
        if (!IsAsciiAlpha(first))
            return false;
        var i = 1;
        while (i < input.Length)
        {
            var c = input[i];
            if (c == ':')
            {
                scheme = input[..i].ToUpperInvariant();
                return true;
            }

            if (!IsAsciiSchemeChar(c))
                return false;
            i++;
        }

        return false;
    }

    private static bool IsAsciiAlpha(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsAsciiSchemeChar(char c) =>
        IsAsciiAlpha(c) || c is >= '0' and <= '9' || c == '+' || c == '-' || c == '.';

    private static bool IsWebSchemeName(string scheme) => scheme is "HTTP" or "HTTPS";

    private static bool IsWebScheme(Uri uri) => uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
}