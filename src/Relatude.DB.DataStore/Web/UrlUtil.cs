namespace Relatude.DB.Web;

/// <summary>Small URL helpers for url manager implementations.</summary>
public static class UrlUtil {

    /// <summary>The host part of an absolute URL in lower case, or null for a relative URL.</summary>
    public static string? GetHost(string url) {
        var posScheme = url.IndexOf("://", StringComparison.Ordinal);
        if (posScheme == -1) return null;
        var start = posScheme + 3;
        var end = start;
        while (end < url.Length && url[end] != '/' && url[end] != '?' && url[end] != '#') end++;
        if (end == start) return null;
        var host = url[start..end];
        var posPort = host.IndexOf(':');
        if (posPort != -1) host = host[..posPort];
        var posCredentials = host.IndexOf('@');
        if (posCredentials != -1) host = host[(posCredentials + 1)..];
        return host.ToLowerInvariant();
    }

    /// <summary>The path of the URL: scheme, host, query and fragment removed. Always starts with "/". A trailing slash is trimmed (except for the root path itself).</summary>
    public static string GetPath(string url) {
        var posScheme = url.IndexOf("://", StringComparison.Ordinal);
        if (posScheme != -1) {
            var posPath = url.IndexOf('/', posScheme + 3);
            url = posPath == -1 ? "/" : url[posPath..];
        }
        var posQuery = url.IndexOfAny(['?', '#']);
        if (posQuery != -1) url = url[..posQuery];
        if (url.Length == 0) return "/";
        if (!url.StartsWith('/')) url = "/" + url;
        if (url.Length > 1 && url.EndsWith('/')) url = url[..^1];
        return url;
    }

    /// <summary>The path segments of the URL, empty for the root path.</summary>
    public static string[] GetSegments(string url) {
        var path = GetPath(url);
        if (path.Length <= 1) return [];
        return path[1..].Split('/');
    }

    /// <summary>The last path segment of the URL, or null for the root path.</summary>
    public static string? GetLastSegment(string url) {
        var path = GetPath(url);
        if (path.Length <= 1) return null;
        var pos = path.LastIndexOf('/');
        return path[(pos + 1)..];
    }

    /// <summary>The value of a query parameter, or null when the URL has no query or the parameter is missing. Matches whole names only, values are not decoded.</summary>
    public static string? GetQueryParameter(string url, string name) {
        var posQuery = url.IndexOf('?');
        if (posQuery == -1 || posQuery == url.Length - 1) return null;
        var posFragment = url.IndexOf('#', posQuery);
        var end = posFragment == -1 ? url.Length : posFragment;
        var pos = posQuery + 1;
        while (pos < end) {
            var posNext = url.IndexOf('&', pos);
            if (posNext == -1 || posNext > end) posNext = end;
            var posEquals = url.IndexOf('=', pos);
            if (posEquals != -1 && posEquals < posNext && posEquals - pos == name.Length
                && string.CompareOrdinal(url, pos, name, 0, name.Length) == 0) {
                return url[(posEquals + 1)..posNext];
            }
            pos = posNext + 1;
        }
        return null;
    }
}
