using GameCloudSync.Core.Services;

namespace GameCloudSync.Core.Util;

public enum WebDavUrlError
{
    None,
    UnsupportedScheme,
    Malformed,
    QueryOrFragment
}

/// <summary>Checks and cleans up the WebDAV server address typed in the settings.</summary>
public static class WebDavUrl
{
    /// <summary>
    /// Trims the address and adds <c>https://</c> when the scheme was left out. An empty address is
    /// valid and means "not configured". <paramref name="normalized"/> is the cleaned-up address.
    /// </summary>
    public static WebDavUrlError Normalize(string? input, out string normalized)
    {
        normalized = (input ?? "").Trim();
        if (normalized.Length == 0)
            return WebDavUrlError.None;
        if (!normalized.Contains("://", StringComparison.Ordinal))
            normalized = "https://" + normalized;
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
            return WebDavUrlError.Malformed;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return WebDavUrlError.UnsupportedScheme;
        // Requests append paths to the address, which a query string or fragment would swallow.
        if (uri.Query.Length > 0 || uri.Fragment.Length > 0)
            return WebDavUrlError.QueryOrFragment;
        return WebDavUrlError.None;
    }

    public static string Describe(WebDavUrlError error, string url) => error switch
    {
        WebDavUrlError.UnsupportedScheme => Localization.T("Url.Unsupported"),
        WebDavUrlError.Malformed => Localization.T("Url.Malformed", url),
        WebDavUrlError.QueryOrFragment => Localization.T("Url.Query"),
        _ => ""
    };
}
