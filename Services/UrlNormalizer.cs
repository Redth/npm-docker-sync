namespace NpmDockerSync.Services;

public static class UrlNormalizer
{
    /// <summary>
    /// Normalizes a URL for consistent comparison by:
    /// - Converting to lowercase
    /// - Trimming whitespace
    /// - Removing trailing slashes
    /// - Ensuring scheme is present (defaults to https if missing)
    /// - Normalizing to absolute URI format
    /// </summary>
    public static string Normalize(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("URL cannot be null or empty", nameof(url));

        url = url.Trim();

        // Add scheme if missing
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "https://" + url;
        }

        // Parse and normalize using Uri class
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException($"Invalid URL format: {url}", nameof(url));
        }

        // Build normalized URL: scheme + host + port (if non-default)
        var normalized = $"{uri.Scheme.ToLowerInvariant()}://{uri.Host.ToLowerInvariant()}";

        // Include port if it's not the default for the scheme
        if ((uri.Scheme == "http" && uri.Port != 80) ||
            (uri.Scheme == "https" && uri.Port != 443))
        {
            normalized += $":{uri.Port}";
        }

        // Include path if present (but normalize it)
        if (!string.IsNullOrEmpty(uri.AbsolutePath) && uri.AbsolutePath != "/")
        {
            normalized += uri.AbsolutePath.TrimEnd('/');
        }

        return normalized;
    }
}
