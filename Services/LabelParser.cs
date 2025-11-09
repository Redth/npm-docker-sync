using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace NpmDockerSync.Services;

public class LabelParser
{
    private readonly ILogger<LabelParser> _logger;
    private readonly IConfiguration _configuration;

    public LabelParser(ILogger<LabelParser> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    public Dictionary<int, ProxyConfiguration> ParseLabels(IDictionary<string, string> labels)
    {
        var configs = new Dictionary<int, ProxyConfiguration>();

        // Find all proxy indices (support npm.proxy.N.* and npm.proxy.* for backward compatibility)
        var indices = GetProxyIndices(labels);

        foreach (var index in indices)
        {
            var config = ParseProxyConfig(labels, index);
            if (config != null)
            {
                configs[index] = config;
            }
        }

        return configs;
    }

    private HashSet<int> GetProxyIndices(IDictionary<string, string> labels)
    {
        var indices = new HashSet<int>();

        // Look for npm.proxy.N.* patterns
        foreach (var key in labels.Keys)
        {
            if (key.StartsWith("npm.proxy.") || key.StartsWith("npm-proxy."))
            {
                var parts = key.Split('.');
                if (parts.Length >= 3 && int.TryParse(parts[2], out var index) && index >= 0 && index < 100)
                {
                    indices.Add(index);
                }
            }
        }

        // Check for backward compatibility: npm.proxy.domains without index = index 0
        if (GetLabelValue(labels, "proxy.domains", null) != null ||
            GetLabelValue(labels, "proxy.domain", null) != null)
        {
            indices.Add(0);
        }

        return indices;
    }

    private ProxyConfiguration? ParseProxyConfig(IDictionary<string, string> labels, int index)
    {
        // For index 0, check both explicit "0." and implicit "" (backward compatibility)
        var prefix = index > 0 ? $"{index}." : "";

        // Support both npm. and npm- prefixes
        var domainNames = GetProxyLabelValue(labels, "proxy", prefix, "domains", index);
        if (string.IsNullOrEmpty(domainNames))
        {
            domainNames = GetProxyLabelValue(labels, "proxy", prefix, "domain", index);

            if (string.IsNullOrEmpty(domainNames))
            {
                _logger.LogDebug("No proxy.domains label found for index {Index}", index);
                return null;
            }
        }

        var forwardHost = GetProxyLabelValue(labels, "proxy", prefix, "host", index);
        var forwardPortStr = GetProxyLabelValue(labels, "proxy", prefix, "port", index);

        // Parse port if provided, otherwise it will be inferred later
        int? forwardPort = null;
        if (!string.IsNullOrEmpty(forwardPortStr))
        {
            if (!int.TryParse(forwardPortStr, out var parsedPort))
            {
                _logger.LogWarning("Invalid npm.proxy.{Prefix}port value: {Port}", prefix, forwardPortStr);
                return null;
            }
            forwardPort = parsedPort;
        }

        var config = new ProxyConfiguration
        {
            Index = index,
            DomainNames = domainNames.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(d => d.Trim())
                .ToList(),
            ForwardHost = forwardHost?.Trim() ?? string.Empty, // Can be empty, will be inferred later
            ForwardPort = forwardPort, // Can be null, will be inferred later
            ForwardScheme = GetProxyLabelValue(labels, "proxy", prefix, "scheme", index) ?? "http",
            SslForced = GetProxyBoolLabel(labels, "proxy", prefix, "ssl.force", index, GetConfigBool("NPM_PROXY_SSL_FORCE", false)),
            CachingEnabled = GetProxyBoolLabel(labels, "proxy", prefix, "caching", index, GetConfigBool("NPM_PROXY_CACHING", false)),
            BlockExploits = GetProxyBoolLabel(labels, "proxy", prefix, "block_common_exploits", index, GetConfigBool("NPM_PROXY_BLOCK_EXPLOITS", true)),
            AllowWebsocketUpgrade = GetProxyBoolLabel(labels, "proxy", prefix, "websockets", index, GetConfigBool("NPM_PROXY_WEBSOCKETS", false)),
            Http2Support = GetProxyBoolLabel(labels, "proxy", prefix, "ssl.http2", index, GetConfigBool("NPM_PROXY_HTTP2", false)),
            HstsEnabled = GetProxyBoolLabel(labels, "proxy", prefix, "ssl.hsts", index, GetConfigBool("NPM_PROXY_HSTS", false)),
            HstsSubdomains = GetProxyBoolLabel(labels, "proxy", prefix, "ssl.hsts.subdomains", index, GetConfigBool("NPM_PROXY_HSTS_SUBDOMAINS", false)),
            AdvancedConfig = GetProxyLabelValue(labels, "proxy", prefix, "advanced.config", index) ?? string.Empty,
        };

        // Parse certificate_id if provided
        var certIdStr = GetProxyLabelValue(labels, "proxy", prefix, "ssl.certificate.id", index);
        if (!string.IsNullOrEmpty(certIdStr) && int.TryParse(certIdStr, out var certId))
        {
            config.CertificateId = certId;
        }

        // Parse access_list_id if provided
        var accessListIdStr = GetProxyLabelValue(labels, "proxy", prefix, "accesslist.id", index);
        if (!string.IsNullOrEmpty(accessListIdStr) && int.TryParse(accessListIdStr, out var accessListId))
        {
            config.AccessListId = accessListId;
        }

        return config;
    }

    private string? GetLabelValue(IDictionary<string, string> labels, string key, int? index)
    {
        // Try npm. prefix first, then npm-
        if (labels.TryGetValue($"npm.{key}", out var value))
            return value;

        if (labels.TryGetValue($"npm-{key}", out value))
            return value;

        return null;
    }

    private bool GetBoolLabel(IDictionary<string, string> labels, string key, int index, bool defaultValue)
    {
        var value = GetLabelValue(labels, key, index);

        if (string.IsNullOrEmpty(value))
            return defaultValue;

        return value.ToLowerInvariant() is "true" or "1" or "yes" or "on";
    }

    private bool GetConfigBool(string key, bool defaultValue)
    {
        var value = _configuration[key];
        
        if (string.IsNullOrEmpty(value))
            return defaultValue;
        
        return value.ToLowerInvariant() is "true" or "1" or "yes" or "on";
    }

    private string? GetProxyLabelValue(IDictionary<string, string> labels, string type, string prefix, string suffix, int index)
    {
        // For index 0, try explicit "0." first, then fall back to implicit "" (backward compatibility)
        if (index == 0)
        {
            // Try explicit index 0 first
            var explicitValue = GetLabelValue(labels, $"{type}.0.{suffix}", index);
            if (!string.IsNullOrEmpty(explicitValue))
                return explicitValue;
        }
        
        // Try with the provided prefix (either "N." or "" for backward compat)
        return GetLabelValue(labels, $"{type}.{prefix}{suffix}", index);
    }

    private bool GetProxyBoolLabel(IDictionary<string, string> labels, string type, string prefix, string suffix, int index, bool defaultValue)
    {
        var value = GetProxyLabelValue(labels, type, prefix, suffix, index);
        
        if (string.IsNullOrEmpty(value))
            return defaultValue;
        
        return value.ToLowerInvariant() is "true" or "1" or "yes" or "on";
    }

    public Dictionary<int, StreamConfiguration> ParseStreamLabels(IDictionary<string, string> labels)
    {
        var configs = new Dictionary<int, StreamConfiguration>();

        // Find all stream indices
        var indices = GetStreamIndices(labels);

        foreach (var index in indices)
        {
            var config = ParseStreamConfig(labels, index);
            if (config != null)
            {
                configs[index] = config;
            }
        }

        return configs;
    }

    private HashSet<int> GetStreamIndices(IDictionary<string, string> labels)
    {
        var indices = new HashSet<int>();

        // Look for npm.stream.N.* patterns
        foreach (var key in labels.Keys)
        {
            if (key.StartsWith("npm.stream.") || key.StartsWith("npm-stream."))
            {
                var parts = key.Split('.');
                if (parts.Length >= 3 && int.TryParse(parts[2], out var index) && index >= 0 && index < 100)
                {
                    indices.Add(index);
                }
            }
        }

        // Check for backward compatibility: npm.stream.incoming.port without index = index 0
        if (GetLabelValue(labels, "stream.incoming.port", null) != null)
        {
            indices.Add(0);
        }

        return indices;
    }

    private StreamConfiguration? ParseStreamConfig(IDictionary<string, string> labels, int index)
    {
        // For index 0, check both explicit "0." and implicit "" (backward compatibility)
        var prefix = index > 0 ? $"{index}." : "";

        // incoming.port is required
        var incomingPortStr = GetProxyLabelValue(labels, "stream", prefix, "incoming.port", index);
        if (string.IsNullOrEmpty(incomingPortStr) || !int.TryParse(incomingPortStr, out var incomingPort))
        {
            _logger.LogDebug("No valid npm.stream.incoming.port found for index {Index}", index);
            return null;
        }

        // Parse forward port (optional, will be inferred later)
        int? forwardPort = null;
        var forwardPortStr = GetProxyLabelValue(labels, "stream", prefix, "forward.port", index);
        if (!string.IsNullOrEmpty(forwardPortStr) && int.TryParse(forwardPortStr, out var parsedPort))
        {
            forwardPort = parsedPort;
        }

        var forwardHost = GetProxyLabelValue(labels, "stream", prefix, "forward.host", index);
        var sslValue = GetProxyLabelValue(labels, "stream", prefix, "ssl", index);

        var config = new StreamConfiguration
        {
            Index = index,
            IncomingPort = incomingPort,
            ForwardHost = forwardHost?.Trim() ?? string.Empty, // Can be empty, will be inferred later
            ForwardPort = forwardPort, // Can be null, will be inferred later
            TcpForwarding = GetProxyBoolLabel(labels, "stream", prefix, "forward.tcp", index, true), // Default: TCP enabled
            UdpForwarding = GetProxyBoolLabel(labels, "stream", prefix, "forward.udp", index, false), // Default: UDP disabled
            SslCertificate = sslValue?.Trim() // Can be cert ID or domain name, will be resolved later
        };

        return config;
    }

    public ProxyHostRequest ToProxyHostRequest(ProxyConfiguration config, string containerId, string syncInstanceId, string npmUrl)
    {
        return new ProxyHostRequest
        {
            DomainNames = config.DomainNames,
            ForwardScheme = config.ForwardScheme,
            ForwardHost = config.ForwardHost,
            ForwardPort = config.ForwardPort ?? 0, // Will be set by SyncOrchestrator before calling this
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
                ["proxy_index"] = config.Index,
                ["created_at"] = DateTime.UtcNow.ToString("o")
            }
        };
    }

    public StreamRequest ToStreamRequest(StreamConfiguration config, string containerId, string syncInstanceId, string npmUrl)
    {
        return new StreamRequest
        {
            IncomingPort = config.IncomingPort,
            ForwardingHost = config.ForwardHost,
            ForwardingPort = config.ForwardPort ?? 0, // Will be set by SyncOrchestrator before calling this
            TcpForwarding = config.TcpForwarding ? 1 : 0,
            UdpForwarding = config.UdpForwarding ? 1 : 0,
            CertificateId = config.CertificateId ?? 0,
            Meta = new Dictionary<string, object>
            {
                ["managed_by"] = "npm-docker-sync",
                ["sync_instance_id"] = syncInstanceId,
                ["npm_url"] = npmUrl,
                ["container_id"] = containerId,
                ["stream_index"] = config.Index,
                ["created_at"] = DateTime.UtcNow.ToString("o")
            }
        };
    }
}

public class ProxyConfiguration
{
    public int Index { get; set; } = 0;
    public List<string> DomainNames { get; set; } = new();
    public string ForwardScheme { get; set; } = "http";
    public string ForwardHost { get; set; } = string.Empty;
    public int? ForwardPort { get; set; }
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

public class StreamConfiguration
{
    public int Index { get; set; } = 0;
    public int IncomingPort { get; set; }
    public string ForwardHost { get; set; } = string.Empty;
    public int? ForwardPort { get; set; }
    public bool TcpForwarding { get; set; } = true;
    public bool UdpForwarding { get; set; } = false;
    public string? SslCertificate { get; set; } // Can be certificate ID or domain name
    public int? CertificateId { get; set; } // Resolved certificate ID
}
