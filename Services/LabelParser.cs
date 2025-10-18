using Microsoft.Extensions.Logging;

namespace NpmDockerSync.Services;

public class LabelParser
{
    private readonly ILogger<LabelParser> _logger;

    public LabelParser(ILogger<LabelParser> logger)
    {
        _logger = logger;
    }

    public ProxyConfiguration? ParseLabels(IDictionary<string, string> labels)
    {
        // Support both npm. and npm- prefixes
        var domainNames = GetLabelValue(labels, "proxy.domains");
        if (string.IsNullOrEmpty(domainNames))
        {
            domainNames = GetLabelValue(labels, "proxy.domain");

            if (string.IsNullOrEmpty(domainNames))
            {
                _logger.LogDebug("No proxy.domains label found");
                return null;
            }
        }

        var forwardHost = GetLabelValue(labels, "proxy.host");
        var forwardPortStr = GetLabelValue(labels, "proxy.port");

        // npm.proxy.port is required
        if (string.IsNullOrEmpty(forwardPortStr))
        {
            _logger.LogWarning("Missing required label: npm.proxy.port");
            return null;
        }

        if (!int.TryParse(forwardPortStr, out var forwardPort))
        {
            _logger.LogWarning("Invalid npm.proxy.port value: {Port}", forwardPortStr);
            return null;
        }

        var config = new ProxyConfiguration
        {
            DomainNames = domainNames.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(d => d.Trim())
                .ToList(),
            ForwardHost = forwardHost?.Trim() ?? string.Empty, // Can be empty, will be inferred later
            ForwardPort = forwardPort,
            ForwardScheme = GetLabelValue(labels, "proxy.scheme") ?? "http",
            SslForced = GetBoolLabel(labels, "proxy.ssl.force", false),
            CachingEnabled = GetBoolLabel(labels, "proxy.caching", false),
            BlockExploits = GetBoolLabel(labels, "proxy.block_common_exploits", true),
            AllowWebsocketUpgrade = GetBoolLabel(labels, "proxy.websockets", false),
            Http2Support = GetBoolLabel(labels, "proxy.ssl.http2", false),
            HstsEnabled = GetBoolLabel(labels, "proxy.ssl.hsts", false),
            HstsSubdomains = GetBoolLabel(labels, "proxy.ssl.hsts.subdomains", false),
            AdvancedConfig = GetLabelValue(labels, "proxy.advanced.config") ?? string.Empty,
        };

        // Parse certificate_id if provided
        var certIdStr = GetLabelValue(labels, "proxy.ssl.certificate.id");
        if (!string.IsNullOrEmpty(certIdStr) && int.TryParse(certIdStr, out var certId))
        {
            config.CertificateId = certId;
        }

        // Parse access_list_id if provided
        var accessListIdStr = GetLabelValue(labels, "proxy.accesslist.id");
        if (!string.IsNullOrEmpty(accessListIdStr) && int.TryParse(accessListIdStr, out var accessListId))
        {
            config.AccessListId = accessListId;
        }

        return config;
    }

    private string? GetLabelValue(IDictionary<string, string> labels, string key)
    {
        // Try npm. prefix first, then npm-
        if (labels.TryGetValue($"npm.{key}", out var value))
            return value;

        if (labels.TryGetValue($"npm-{key}", out value))
            return value;

        return null;
    }

    private bool GetBoolLabel(IDictionary<string, string> labels, string key, bool defaultValue)
    {
        var value = GetLabelValue(labels, key);

        if (string.IsNullOrEmpty(value))
            return defaultValue;

        return value.ToLowerInvariant() is "true" or "1" or "yes" or "on";
    }

    public ProxyHostRequest ToProxyHostRequest(ProxyConfiguration config, string containerId, string syncInstanceId, string npmUrl)
    {
        return new ProxyHostRequest
        {
            DomainNames = config.DomainNames,
            ForwardScheme = config.ForwardScheme,
            ForwardHost = config.ForwardHost,
            ForwardPort = config.ForwardPort,
            AccessListId = config.AccessListId ?? 0,
            CertificateId = config.CertificateId ?? 0,
            SslForced = config.SslForced ? 1 : 0,
            CachingEnabled = config.CachingEnabled ? 1 : 0,
            BlockExploits = config.BlockExploits ? 1 : 0,
            AllowWebsocketUpgrade = config.AllowWebsocketUpgrade ? 1 : 0,
            Http2Support = config.Http2Support ? 1 : 0,
            HstsEnabled = config.HstsEnabled ? 1 : 0,
            HstsSubdomains = config.HstsSubdomains ? 1 : 0,
            AdvancedConfig = config.AdvancedConfig,
            Meta = new Dictionary<string, object>
            {
                ["managed_by"] = "npm-docker-sync",
                ["sync_instance_id"] = syncInstanceId,
                ["npm_url"] = npmUrl,
                ["container_id"] = containerId,
                ["created_at"] = DateTime.UtcNow.ToString("o")
            }
        };
    }
}

public class ProxyConfiguration
{
    public List<string> DomainNames { get; set; } = new();
    public string ForwardScheme { get; set; } = "http";
    public string ForwardHost { get; set; } = string.Empty;
    public int ForwardPort { get; set; }
    public int? AccessListId { get; set; }
    public int? CertificateId { get; set; }
    public bool SslForced { get; set; }
    public bool CachingEnabled { get; set; }
    public bool BlockExploits { get; set; } = true;
    public string AdvancedConfig { get; set; } = string.Empty;
    public bool AllowWebsocketUpgrade { get; set; }
    public bool Http2Support { get; set; }
    public bool HstsEnabled { get; set; }
    public bool HstsSubdomains { get; set; }
}
