using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace NpmDockerSync.Services;

public class NginxProxyManagerClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<NginxProxyManagerClient> _logger;
    private readonly string _baseUrl;
    private readonly string _email;
    private readonly string _password;
    private string? _token;
    private DateTime _tokenExpiry = DateTime.MinValue;

    public NginxProxyManagerClient(
        HttpClient httpClient,
        ILogger<NginxProxyManagerClient> logger,
        IConfiguration configuration)
    {
        _httpClient = httpClient;
        _logger = logger;

        var rawUrl = configuration["NPM_URL"] ?? throw new ArgumentException("NPM_URL is required");
        _baseUrl = UrlNormalizer.Normalize(rawUrl);
        _email = configuration["NPM_EMAIL"] ?? throw new ArgumentException("NPM_EMAIL is required");
        _password = configuration["NPM_PASSWORD"] ?? throw new ArgumentException("NPM_PASSWORD is required");

        _httpClient.BaseAddress = new Uri(_baseUrl);
    }

    private async Task EnsureAuthenticated(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_token) && DateTime.UtcNow < _tokenExpiry)
            return;

        _logger.LogInformation("Authenticating with Nginx Proxy Manager");

        var loginRequest = new
        {
            identity = _email,
            secret = _password
        };

        var response = await _httpClient.PostAsJsonAsync("/api/tokens", loginRequest, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);

        if (result?.Token == null)
            throw new Exception("Failed to obtain authentication token");

        _token = result.Token;
        _tokenExpiry = DateTime.UtcNow.AddHours(23); // Tokens typically last 24 hours

        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        _logger.LogInformation("Successfully authenticated with Nginx Proxy Manager");
    }

    public async Task<List<ProxyHost>> GetProxyHostsAsync(CancellationToken cancellationToken)
    {
        await EnsureAuthenticated(cancellationToken);

        var response = await _httpClient.GetAsync("/api/nginx/proxy-hosts", cancellationToken);
        response.EnsureSuccessStatusCode();

        var hosts = await response.Content.ReadFromJsonAsync<List<ProxyHost>>(cancellationToken);
        return hosts ?? new List<ProxyHost>();
    }

    public async Task<ProxyHost?> GetProxyHostByDomainAsync(string domain, CancellationToken cancellationToken)
    {
        var hosts = await GetProxyHostsAsync(cancellationToken);
        
        // Try exact match first (case-insensitive)
        var exactMatch = hosts.FirstOrDefault(h => 
            h.DomainNames?.Any(d => string.Equals(d, domain, StringComparison.OrdinalIgnoreCase)) == true);
        
        if (exactMatch != null)
        {
            _logger.LogDebug("Found exact match for domain {Domain} in proxy host {HostId}", domain, exactMatch.Id);
            return exactMatch;
        }
        
        _logger.LogDebug("No proxy host found for domain {Domain} (searched {Count} hosts)", domain, hosts.Count);
        return null;
    }
    
    public async Task<ProxyHost?> GetProxyHostByDomainsAsync(IEnumerable<string> domains, CancellationToken cancellationToken)
    {
        var hosts = await GetProxyHostsAsync(cancellationToken);
        var domainList = domains.ToList();

        // Find any host that has ANY of the specified domains (case-insensitive)
        var matchingHost = hosts.FirstOrDefault(h =>
            h.DomainNames?.Any(hostDomain =>
                domainList.Any(d => string.Equals(d, hostDomain, StringComparison.OrdinalIgnoreCase))) == true);

        if (matchingHost != null)
        {
            _logger.LogDebug("Found proxy host {HostId} with overlapping domains: host has [{HostDomains}], searching for [{SearchDomains}]",
                matchingHost.Id,
                string.Join(", ", matchingHost.DomainNames ?? new List<string>()),
                string.Join(", ", domainList));
        }
        else
        {
            _logger.LogDebug("No proxy host found for any of the domains: [{Domains}] (searched {Count} hosts)",
                string.Join(", ", domainList), hosts.Count);
        }

        return matchingHost;
    }

    public async Task<ProxyHost?> GetProxyHostByIdAsync(int hostId, CancellationToken cancellationToken)
    {
        await EnsureAuthenticated(cancellationToken);

        try
        {
            var response = await _httpClient.GetAsync($"/api/nginx/proxy-hosts/{hostId}", cancellationToken);
            response.EnsureSuccessStatusCode();

            var host = await response.Content.ReadFromJsonAsync<ProxyHost>(cancellationToken);
            return host;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogDebug("Proxy host {HostId} not found", hostId);
            return null;
        }
    }

    public async Task<ProxyHost> CreateProxyHostAsync(ProxyHostRequest request, CancellationToken cancellationToken)
    {
        await EnsureAuthenticated(cancellationToken);

        _logger.LogInformation("Creating proxy host for domains: {Domains}", string.Join(", ", request.DomainNames));

        var response = await _httpClient.PostAsJsonAsync("/api/nginx/proxy-hosts", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<ProxyHost>(cancellationToken);
        return result ?? throw new Exception("Failed to create proxy host");
    }

    public async Task<ProxyHost> UpdateProxyHostAsync(int hostId, ProxyHostRequest request, CancellationToken cancellationToken)
    {
        await EnsureAuthenticated(cancellationToken);

        _logger.LogInformation("Updating proxy host {HostId} for domains: {Domains}",
            hostId, string.Join(", ", request.DomainNames));

        var response = await _httpClient.PutAsJsonAsync($"/api/nginx/proxy-hosts/{hostId}", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<ProxyHost>(cancellationToken);
        return result ?? throw new Exception("Failed to update proxy host");
    }

    public async Task DeleteProxyHostAsync(int hostId, CancellationToken cancellationToken)
    {
        await EnsureAuthenticated(cancellationToken);

        _logger.LogInformation("Deleting proxy host {HostId}", hostId);

        var response = await _httpClient.DeleteAsync($"/api/nginx/proxy-hosts/{hostId}", cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<List<Stream>> GetStreamsAsync(CancellationToken cancellationToken)
    {
        await EnsureAuthenticated(cancellationToken);

        var response = await _httpClient.GetAsync("/api/nginx/streams", cancellationToken);
        response.EnsureSuccessStatusCode();

        var streams = await response.Content.ReadFromJsonAsync<List<Stream>>(cancellationToken);
        return streams ?? new List<Stream>();
    }

    public async Task<Stream> CreateStreamAsync(StreamRequest request, CancellationToken cancellationToken)
    {
        await EnsureAuthenticated(cancellationToken);

        _logger.LogInformation("Creating stream for incoming port: {IncomingPort} -> {ForwardHost}:{ForwardPort}",
            request.IncomingPort, request.ForwardingHost, request.ForwardingPort);

        var response = await _httpClient.PostAsJsonAsync("/api/nginx/streams", request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<Stream>(cancellationToken);
        return result ?? throw new Exception("Failed to create stream");
    }

    public async Task DeleteStreamAsync(int streamId, CancellationToken cancellationToken)
    {
        await EnsureAuthenticated(cancellationToken);

        _logger.LogInformation("Deleting stream {StreamId}", streamId);

        var response = await _httpClient.DeleteAsync($"/api/nginx/streams/{streamId}", cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<List<Certificate>> GetCertificatesAsync(CancellationToken cancellationToken)
    {
        await EnsureAuthenticated(cancellationToken);

        var response = await _httpClient.GetAsync("/api/nginx/certificates", cancellationToken);
        response.EnsureSuccessStatusCode();

        var certificates = await response.Content.ReadFromJsonAsync<List<Certificate>>(cancellationToken);
        return certificates ?? new List<Certificate>();
    }

    public async Task<T?> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        await EnsureAuthenticated(cancellationToken);

        var response = await _httpClient.GetAsync(path, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken);
    }

    public static bool IsAutomationManaged(ProxyHost host, string syncInstanceId)
    {
        if (host.Meta == null)
            return false;

        // Check if managed by npm-docker-sync
        if (!host.Meta.TryGetValue("managed_by", out var managedBy) ||
            managedBy?.ToString() != "npm-docker-sync")
            return false;

        // Check if managed by THIS sync instance
        if (host.Meta.TryGetValue("sync_instance_id", out var instance))
        {
            return instance?.ToString() == syncInstanceId;
        }

        // Backward compatibility: if no sync_instance_id, assume it's ours
        // (for proxies created before this feature was added)
        return true;
    }

    public static string? GetManagedContainerId(ProxyHost host)
    {
        if (host.Meta == null)
            return null;

        return host.Meta.TryGetValue("container_id", out var containerId)
            ? containerId?.ToString()
            : null;
    }

    public static int? GetProxyIndex(ProxyHost host)
    {
        if (host.Meta == null)
            return null;

        if (host.Meta.TryGetValue("proxy_index", out var index) && int.TryParse(index?.ToString(), out var indexInt))
            return indexInt;

        return null;
    }

    public static string? GetManagedInstanceId(ProxyHost host)
    {
        if (host.Meta == null)
            return null;

        return host.Meta.TryGetValue("sync_instance_id", out var instance)
            ? instance?.ToString()
            : null;
    }

    // Stream helper methods
    public static bool IsStreamAutomationManaged(Stream stream, string syncInstanceId)
    {
        if (stream.Meta == null)
            return false;

        // Check if managed by npm-docker-sync
        if (!stream.Meta.TryGetValue("managed_by", out var managedBy) ||
            managedBy?.ToString() != "npm-docker-sync")
            return false;

        // Check if managed by THIS sync instance
        if (stream.Meta.TryGetValue("sync_instance_id", out var instance))
        {
            return instance?.ToString() == syncInstanceId;
        }

        // Backward compatibility: if no sync_instance_id, assume it's ours
        return true;
    }

    public static string? GetStreamContainerId(Stream stream)
    {
        if (stream.Meta == null)
            return null;

        return stream.Meta.TryGetValue("container_id", out var containerId)
            ? containerId?.ToString()
            : null;
    }

    public static int? GetStreamIndex(Stream stream)
    {
        if (stream.Meta == null)
            return null;

        if (stream.Meta.TryGetValue("stream_index", out var index) && int.TryParse(index?.ToString(), out var indexInt))
            return indexInt;

        return null;
    }

    public static string? GetManagedNpmUrl(ProxyHost host)
    {
        if (host.Meta == null)
            return null;

        return host.Meta.TryGetValue("npm_url", out var url)
            ? url?.ToString()
            : null;
    }
}

public class TokenResponse
{
    [JsonPropertyName("token")]
    public string? Token { get; set; }

    [JsonPropertyName("expires")]
    public string? Expires { get; set; }
}

public class ProxyHost
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("created_on")]
    public string? CreatedOn { get; set; }

    [JsonPropertyName("modified_on")]
    public string? ModifiedOn { get; set; }

    [JsonPropertyName("owner_user_id")]
    public int OwnerUserId { get; set; }

    [JsonPropertyName("domain_names")]
    public List<string>? DomainNames { get; set; }

    [JsonPropertyName("forward_scheme")]
    public string? ForwardScheme { get; set; }

    [JsonPropertyName("forward_host")]
    public string? ForwardHost { get; set; }

    [JsonPropertyName("forward_port")]
    public int ForwardPort { get; set; }

    [JsonPropertyName("access_list_id")]
    public int? AccessListId { get; set; }

    [JsonPropertyName("certificate_id")]
    public int? CertificateId { get; set; }

    [JsonPropertyName("ssl_forced")]
    [JsonConverter(typeof(BoolToIntConverter))]
    public int SslForced { get; set; }

    [JsonPropertyName("caching_enabled")]
    [JsonConverter(typeof(BoolToIntConverter))]
    public int CachingEnabled { get; set; }

    [JsonPropertyName("block_exploits")]
    [JsonConverter(typeof(BoolToIntConverter))]
    public int BlockExploits { get; set; }

    [JsonPropertyName("advanced_config")]
    public string? AdvancedConfig { get; set; }

    [JsonPropertyName("enabled")]
    [JsonConverter(typeof(BoolToIntConverter))]
    public int Enabled { get; set; }

    [JsonPropertyName("http2_support")]
    [JsonConverter(typeof(BoolToIntConverter))]
    public int Http2Support { get; set; }

    [JsonPropertyName("hsts_enabled")]
    [JsonConverter(typeof(BoolToIntConverter))]
    public int HstsEnabled { get; set; }

    [JsonPropertyName("hsts_subdomains")]
    [JsonConverter(typeof(BoolToIntConverter))]
    public int HstsSubdomains { get; set; }

    [JsonPropertyName("allow_websocket_upgrade")]
    [JsonConverter(typeof(BoolToIntConverter))]
    public int AllowWebsocketUpgrade { get; set; }

    [JsonPropertyName("meta")]
    public Dictionary<string, object>? Meta { get; set; }
}

public class ProxyHostRequest
{
    [JsonPropertyName("domain_names")]
    public List<string> DomainNames { get; set; } = new();

    [JsonPropertyName("forward_scheme")]
    public string ForwardScheme { get; set; } = "http";

    [JsonPropertyName("forward_host")]
    public string ForwardHost { get; set; } = string.Empty;

    [JsonPropertyName("forward_port")]
    public int ForwardPort { get; set; }

    [JsonPropertyName("access_list_id")]
    public int AccessListId { get; set; } = 0;

    [JsonPropertyName("certificate_id")]
    public int CertificateId { get; set; } = 0;

    [JsonPropertyName("ssl_forced")]
    public int SslForced { get; set; } = 0;

    [JsonPropertyName("caching_enabled")]
    public int CachingEnabled { get; set; } = 0;

    [JsonPropertyName("block_exploits")]
    public int BlockExploits { get; set; } = 1;

    [JsonPropertyName("advanced_config")]
    public string AdvancedConfig { get; set; } = string.Empty;

    [JsonPropertyName("meta")]
    public Dictionary<string, object> Meta { get; set; } = new();

    [JsonPropertyName("allow_websocket_upgrade")]
    public int AllowWebsocketUpgrade { get; set; } = 0;

    [JsonPropertyName("http2_support")]
    public int Http2Support { get; set; } = 0;

    [JsonPropertyName("hsts_enabled")]
    public int HstsEnabled { get; set; } = 0;

    [JsonPropertyName("hsts_subdomains")]
    public int HstsSubdomains { get; set; } = 0;
}

public class Certificate
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("created_on")]
    public string? CreatedOn { get; set; }

    [JsonPropertyName("modified_on")]
    public string? ModifiedOn { get; set; }

    [JsonPropertyName("provider")]
    public string? Provider { get; set; }

    [JsonPropertyName("nice_name")]
    public string? NiceName { get; set; }

    [JsonPropertyName("domain_names")]
    public List<string>? DomainNames { get; set; }

    [JsonPropertyName("expires_on")]
    public string? ExpiresOn { get; set; }

    [JsonPropertyName("owner_user_id")]
    public int OwnerUserId { get; set; }

    [JsonPropertyName("is_deleted")]
    [JsonConverter(typeof(BoolToIntConverter))]
    public int IsDeleted { get; set; }
}

public class AccessList
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("items")]
    public List<AccessListItem>? Items { get; set; }

    [JsonPropertyName("satisfy_any")]
    [JsonConverter(typeof(BoolToIntConverter))]
    public int SatisfyAny { get; set; }

    [JsonPropertyName("pass_auth")]
    [JsonConverter(typeof(BoolToIntConverter))]
    public int PassAuth { get; set; }

    [JsonPropertyName("is_deleted")]
    [JsonConverter(typeof(BoolToIntConverter))]
    public int IsDeleted { get; set; }
}

public class AccessListItem
{
    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("directive")]
    public string? Directive { get; set; }

    [JsonPropertyName("address")]
    public string? Address { get; set; }
}

public class RedirectionHost
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("domain_names")]
    public List<string>? DomainNames { get; set; }

    [JsonPropertyName("forward_domain_name")]
    public string? ForwardDomainName { get; set; }

    [JsonPropertyName("forward_scheme")]
    public string? ForwardScheme { get; set; }

    [JsonPropertyName("certificate_id")]
    public int? CertificateId { get; set; }

    [JsonPropertyName("ssl_forced")]
    [JsonConverter(typeof(BoolToIntConverter))]
    public int SslForced { get; set; }

    [JsonPropertyName("hsts_enabled")]
    [JsonConverter(typeof(BoolToIntConverter))]
    public int HstsEnabled { get; set; }

    [JsonPropertyName("hsts_subdomains")]
    [JsonConverter(typeof(BoolToIntConverter))]
    public int HstsSubdomains { get; set; }

    [JsonPropertyName("http2_support")]
    [JsonConverter(typeof(BoolToIntConverter))]
    public int Http2Support { get; set; }

    [JsonPropertyName("block_exploits")]
    [JsonConverter(typeof(BoolToIntConverter))]
    public int BlockExploits { get; set; }

    [JsonPropertyName("preserve_path")]
    [JsonConverter(typeof(BoolToIntConverter))]
    public int PreservePath { get; set; }

    [JsonPropertyName("advanced_config")]
    public string? AdvancedConfig { get; set; }

    [JsonPropertyName("is_deleted")]
    [JsonConverter(typeof(BoolToIntConverter))]
    public int IsDeleted { get; set; }
}

public class Stream
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("incoming_port")]
    public int IncomingPort { get; set; }

    [JsonPropertyName("forwarding_host")]
    public string? ForwardingHost { get; set; }

    [JsonPropertyName("forwarding_port")]
    public int ForwardingPort { get; set; }

    [JsonPropertyName("tcp_forwarding")]
    [JsonConverter(typeof(BoolToIntConverter))]
    public int TcpForwarding { get; set; }

    [JsonPropertyName("udp_forwarding")]
    [JsonConverter(typeof(BoolToIntConverter))]
    public int UdpForwarding { get; set; }

    [JsonPropertyName("certificate_id")]
    public int? CertificateId { get; set; }

    [JsonPropertyName("meta")]
    public Dictionary<string, object>? Meta { get; set; }

    [JsonPropertyName("is_deleted")]
    [JsonConverter(typeof(BoolToIntConverter))]
    public int IsDeleted { get; set; }
}

public class StreamRequest
{
    [JsonPropertyName("incoming_port")]
    public int IncomingPort { get; set; }

    [JsonPropertyName("forwarding_host")]
    public string ForwardingHost { get; set; } = string.Empty;

    [JsonPropertyName("forwarding_port")]
    public int ForwardingPort { get; set; }

    [JsonPropertyName("tcp_forwarding")]
    public int TcpForwarding { get; set; } = 1;

    [JsonPropertyName("udp_forwarding")]
    public int UdpForwarding { get; set; } = 0;

    [JsonPropertyName("certificate_id")]
    public int CertificateId { get; set; } = 0;

    [JsonPropertyName("meta")]
    public Dictionary<string, object> Meta { get; set; } = new();
}

public class DeadHost
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("domain_names")]
    public List<string>? DomainNames { get; set; }

    [JsonPropertyName("certificate_id")]
    public int? CertificateId { get; set; }

    [JsonPropertyName("ssl_forced")]
    [JsonConverter(typeof(BoolToIntConverter))]
    public int SslForced { get; set; }

    [JsonPropertyName("hsts_enabled")]
    [JsonConverter(typeof(BoolToIntConverter))]
    public int HstsEnabled { get; set; }

    [JsonPropertyName("hsts_subdomains")]
    [JsonConverter(typeof(BoolToIntConverter))]
    public int HstsSubdomains { get; set; }

    [JsonPropertyName("http2_support")]
    [JsonConverter(typeof(BoolToIntConverter))]
    public int Http2Support { get; set; }

    [JsonPropertyName("advanced_config")]
    public string? AdvancedConfig { get; set; }

    [JsonPropertyName("is_deleted")]
    [JsonConverter(typeof(BoolToIntConverter))]
    public int IsDeleted { get; set; }
}
