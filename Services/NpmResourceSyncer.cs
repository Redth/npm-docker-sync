using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace NpmDockerSync.Services;

public class NpmResourceSyncer
{
    private readonly ILogger _logger;
    private readonly NginxProxyManagerClient _primary;
    private readonly SecondaryNpmClient _secondary;

    // ID mapping: primary ID -> secondary ID
    private readonly ConcurrentDictionary<string, int> _certificateIdMap = new();
    private readonly ConcurrentDictionary<string, int> _accessListIdMap = new();

    public NpmResourceSyncer(
        ILogger logger,
        NginxProxyManagerClient primary,
        SecondaryNpmClient secondary)
    {
        _logger = logger;
        _primary = primary;
        _secondary = secondary;
    }

    public async Task SyncCertificatesAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Syncing certificates...");

        var primaryCerts = await _primary.GetCertificatesAsync(cancellationToken);
        var secondaryCerts = await _secondary.GetAsync<List<Certificate>>(
            "/api/nginx/certificates", cancellationToken) ?? new List<Certificate>();

        var synced = 0;
        var skipped = 0;

        foreach (var cert in primaryCerts.Where(c => c.IsDeleted == 0))
        {
            try
            {
                var certHash = ComputeResourceHash(cert);
                var existing = secondaryCerts.FirstOrDefault(c =>
                    c.NiceName == cert.NiceName || MatchesDomains(c.DomainNames, cert.DomainNames));

                if (existing != null)
                {
                    var existingHash = ComputeResourceHash(existing);
                    if (existingHash == certHash)
                    {
                        _certificateIdMap[$"cert-{cert.Id}"] = existing.Id;
                        skipped++;
                        continue;
                    }

                    // Update existing certificate
                    _logger.LogDebug("Updating certificate: {Name}", cert.NiceName);
                    // Note: NPM doesn't allow updating certificates directly
                    // Would need to delete and recreate, which is risky
                    // For now, just map the existing one
                    _certificateIdMap[$"cert-{cert.Id}"] = existing.Id;
                    skipped++;
                }
                else
                {
                    // Note: Creating certificates via API is complex (requires uploading files)
                    // This would need the certificate files from the primary instance
                    _logger.LogWarning("Cannot automatically sync new certificate: {Name} (requires file upload)",
                        cert.NiceName);
                    skipped++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to sync certificate: {Name}", cert.NiceName);
            }
        }

        _logger.LogInformation("Certificates: {Synced} synced, {Skipped} skipped", synced, skipped);
    }

    public async Task SyncAccessListsAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Syncing access lists...");

        var primaryLists = await _primary.GetAsync<List<AccessList>>(
            "/api/nginx/access-lists", cancellationToken) ?? new List<AccessList>();
        var secondaryLists = await _secondary.GetAsync<List<AccessList>>(
            "/api/nginx/access-lists", cancellationToken) ?? new List<AccessList>();

        var synced = 0;
        var skipped = 0;

        foreach (var list in primaryLists.Where(l => l.IsDeleted == 0))
        {
            try
            {
                var listHash = ComputeResourceHash(list);
                var existing = secondaryLists.FirstOrDefault(l => l.Name == list.Name);

                if (existing != null)
                {
                    var existingHash = ComputeResourceHash(existing);
                    if (existingHash == listHash)
                    {
                        _accessListIdMap[$"access-{list.Id}"] = existing.Id;
                        skipped++;
                        continue;
                    }

                    // Update existing
                    _logger.LogDebug("Updating access list: {Name}", list.Name);
                    var updatePayload = CreateAccessListPayload(list);
                    await _secondary.PutAsync<AccessList>(
                        $"/api/nginx/access-lists/{existing.Id}", updatePayload, cancellationToken);
                    _accessListIdMap[$"access-{list.Id}"] = existing.Id;
                    synced++;
                }
                else
                {
                    // Create new
                    _logger.LogDebug("Creating access list: {Name}", list.Name);
                    var createPayload = CreateAccessListPayload(list);
                    var created = await _secondary.PostAsync<AccessList>(
                        "/api/nginx/access-lists", createPayload, cancellationToken);
                    if (created != null)
                    {
                        _accessListIdMap[$"access-{list.Id}"] = created.Id;
                    }
                    synced++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to sync access list: {Name}", list.Name);
            }
        }

        _logger.LogInformation("Access Lists: {Synced} synced, {Skipped} skipped", synced, skipped);
    }

    public async Task SyncProxyHostsAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Syncing proxy hosts...");

        var primaryHosts = await _primary.GetProxyHostsAsync(cancellationToken);
        var secondaryHosts = await _secondary.GetAsync<List<ProxyHost>>(
            "/api/nginx/proxy-hosts", cancellationToken) ?? new List<ProxyHost>();

        var synced = 0;
        var skipped = 0;

        foreach (var host in primaryHosts.Where(h => h.Enabled == 1))
        {
            try
            {
                var hostHash = ComputeResourceHash(host);
                var primaryDomain = host.DomainNames?.FirstOrDefault();
                if (primaryDomain == null) continue;

                var existing = secondaryHosts.FirstOrDefault(h =>
                    h.DomainNames?.Contains(primaryDomain) == true);

                var payload = CreateProxyHostPayload(host);

                if (existing != null)
                {
                    var existingHash = ComputeResourceHash(existing);
                    if (existingHash == hostHash)
                    {
                        skipped++;
                        continue;
                    }

                    // Update existing
                    _logger.LogDebug("Updating proxy host: {Domain}", primaryDomain);
                    await _secondary.PutAsync<ProxyHost>(
                        $"/api/nginx/proxy-hosts/{existing.Id}", payload, cancellationToken);
                    synced++;
                }
                else
                {
                    // Create new
                    _logger.LogDebug("Creating proxy host: {Domain}", primaryDomain);
                    await _secondary.PostAsync<ProxyHost>(
                        "/api/nginx/proxy-hosts", payload, cancellationToken);
                    synced++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to sync proxy host: {Domains}",
                    string.Join(", ", host.DomainNames ?? new List<string>()));
            }
        }

        _logger.LogInformation("Proxy Hosts: {Synced} synced, {Skipped} skipped", synced, skipped);
    }

    public async Task SyncRedirectionHostsAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Syncing redirection hosts...");

        var primaryHosts = await _primary.GetAsync<List<RedirectionHost>>(
            "/api/nginx/redirection-hosts", cancellationToken) ?? new List<RedirectionHost>();
        var secondaryHosts = await _secondary.GetAsync<List<RedirectionHost>>(
            "/api/nginx/redirection-hosts", cancellationToken) ?? new List<RedirectionHost>();

        var synced = 0;
        var skipped = 0;

        foreach (var host in primaryHosts.Where(h => h.IsDeleted == 0))
        {
            try
            {
                var hostHash = ComputeResourceHash(host);
                var primaryDomain = host.DomainNames?.FirstOrDefault();
                if (primaryDomain == null) continue;

                var existing = secondaryHosts.FirstOrDefault(h =>
                    h.DomainNames?.Contains(primaryDomain) == true);

                var payload = CreateRedirectionHostPayload(host);

                if (existing != null)
                {
                    var existingHash = ComputeResourceHash(existing);
                    if (existingHash == hostHash)
                    {
                        skipped++;
                        continue;
                    }

                    _logger.LogDebug("Updating redirection host: {Domain}", primaryDomain);
                    await _secondary.PutAsync<RedirectionHost>(
                        $"/api/nginx/redirection-hosts/{existing.Id}", payload, cancellationToken);
                    synced++;
                }
                else
                {
                    _logger.LogDebug("Creating redirection host: {Domain}", primaryDomain);
                    await _secondary.PostAsync<RedirectionHost>(
                        "/api/nginx/redirection-hosts", payload, cancellationToken);
                    synced++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to sync redirection host: {Domains}",
                    string.Join(", ", host.DomainNames ?? new List<string>()));
            }
        }

        _logger.LogInformation("Redirection Hosts: {Synced} synced, {Skipped} skipped", synced, skipped);
    }

    public async Task SyncStreamsAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Syncing streams...");

        var primaryStreams = await _primary.GetAsync<List<Stream>>(
            "/api/nginx/streams", cancellationToken) ?? new List<Stream>();
        var secondaryStreams = await _secondary.GetAsync<List<Stream>>(
            "/api/nginx/streams", cancellationToken) ?? new List<Stream>();

        var synced = 0;
        var skipped = 0;

        foreach (var stream in primaryStreams.Where(s => s.IsDeleted == 0))
        {
            try
            {
                var streamHash = ComputeResourceHash(stream);
                var existing = secondaryStreams.FirstOrDefault(s =>
                    s.IncomingPort == stream.IncomingPort);

                var payload = CreateStreamPayload(stream);

                if (existing != null)
                {
                    var existingHash = ComputeResourceHash(existing);
                    if (existingHash == streamHash)
                    {
                        skipped++;
                        continue;
                    }

                    _logger.LogDebug("Updating stream: port {Port}", stream.IncomingPort);
                    await _secondary.PutAsync<Stream>(
                        $"/api/nginx/streams/{existing.Id}", payload, cancellationToken);
                    synced++;
                }
                else
                {
                    _logger.LogDebug("Creating stream: port {Port}", stream.IncomingPort);
                    await _secondary.PostAsync<Stream>(
                        "/api/nginx/streams", payload, cancellationToken);
                    synced++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to sync stream: port {Port}", stream.IncomingPort);
            }
        }

        _logger.LogInformation("Streams: {Synced} synced, {Skipped} skipped", synced, skipped);
    }

    public async Task SyncDeadHostsAsync(CancellationToken cancellationToken)
    {
        _logger.LogDebug("Syncing dead hosts (404)...");

        var primaryHosts = await _primary.GetAsync<List<DeadHost>>(
            "/api/nginx/dead-hosts", cancellationToken) ?? new List<DeadHost>();
        var secondaryHosts = await _secondary.GetAsync<List<DeadHost>>(
            "/api/nginx/dead-hosts", cancellationToken) ?? new List<DeadHost>();

        var synced = 0;
        var skipped = 0;

        foreach (var host in primaryHosts.Where(h => h.IsDeleted == 0))
        {
            try
            {
                var hostHash = ComputeResourceHash(host);
                var primaryDomain = host.DomainNames?.FirstOrDefault();
                if (primaryDomain == null) continue;

                var existing = secondaryHosts.FirstOrDefault(h =>
                    h.DomainNames?.Contains(primaryDomain) == true);

                var payload = CreateDeadHostPayload(host);

                if (existing != null)
                {
                    var existingHash = ComputeResourceHash(existing);
                    if (existingHash == hostHash)
                    {
                        skipped++;
                        continue;
                    }

                    _logger.LogDebug("Updating dead host: {Domain}", primaryDomain);
                    await _secondary.PutAsync<DeadHost>(
                        $"/api/nginx/dead-hosts/{existing.Id}", payload, cancellationToken);
                    synced++;
                }
                else
                {
                    _logger.LogDebug("Creating dead host: {Domain}", primaryDomain);
                    await _secondary.PostAsync<DeadHost>(
                        "/api/nginx/dead-hosts", payload, cancellationToken);
                    synced++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to sync dead host: {Domains}",
                    string.Join(", ", host.DomainNames ?? new List<string>()));
            }
        }

        _logger.LogInformation("Dead Hosts: {Synced} synced, {Skipped} skipped", synced, skipped);
    }

    private string ComputeResourceHash(object resource)
    {
        var json = JsonSerializer.Serialize(resource, new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToBase64String(hash);
    }

    private bool MatchesDomains(List<string>? domains1, List<string>? domains2)
    {
        if (domains1 == null || domains2 == null) return false;
        var set1 = new HashSet<string>(domains1, StringComparer.OrdinalIgnoreCase);
        var set2 = new HashSet<string>(domains2, StringComparer.OrdinalIgnoreCase);
        return set1.SetEquals(set2);
    }

    private int MapCertificateId(int? primaryId)
    {
        if (primaryId == null || primaryId == 0) return 0;
        return _certificateIdMap.TryGetValue($"cert-{primaryId}", out var secondaryId) ? secondaryId : 0;
    }

    private int MapAccessListId(int? primaryId)
    {
        if (primaryId == null || primaryId == 0) return 0;
        return _accessListIdMap.TryGetValue($"access-{primaryId}", out var secondaryId) ? secondaryId : 0;
    }

    private object CreateAccessListPayload(AccessList list)
    {
        return new
        {
            name = list.Name,
            items = list.Items,
            satisfy_any = list.SatisfyAny,
            pass_auth = list.PassAuth
        };
    }

    private object CreateProxyHostPayload(ProxyHost host)
    {
        return new
        {
            domain_names = host.DomainNames,
            forward_scheme = host.ForwardScheme,
            forward_host = host.ForwardHost,
            forward_port = host.ForwardPort,
            certificate_id = MapCertificateId(host.CertificateId),
            ssl_forced = host.SslForced,
            hsts_enabled = host.HstsEnabled,
            hsts_subdomains = host.HstsSubdomains,
            http2_support = host.Http2Support,
            block_exploits = host.BlockExploits,
            caching_enabled = host.CachingEnabled,
            allow_websocket_upgrade = host.AllowWebsocketUpgrade,
            access_list_id = MapAccessListId(host.AccessListId),
            advanced_config = host.AdvancedConfig ?? "",
            meta = new Dictionary<string, object>
            {
                ["mirrored_from"] = _primary.GetType().Name,
                ["mirrored_at"] = DateTime.UtcNow.ToString("o")
            }
        };
    }

    private object CreateRedirectionHostPayload(RedirectionHost host)
    {
        return new
        {
            domain_names = host.DomainNames,
            forward_domain_name = host.ForwardDomainName,
            forward_scheme = host.ForwardScheme,
            preserve_path = host.PreservePath,
            certificate_id = MapCertificateId(host.CertificateId),
            ssl_forced = host.SslForced,
            hsts_enabled = host.HstsEnabled,
            hsts_subdomains = host.HstsSubdomains,
            http2_support = host.Http2Support,
            block_exploits = host.BlockExploits,
            advanced_config = host.AdvancedConfig ?? ""
        };
    }

    private object CreateStreamPayload(Stream stream)
    {
        return new
        {
            incoming_port = stream.IncomingPort,
            forwarding_host = stream.ForwardingHost,
            forwarding_port = stream.ForwardingPort,
            tcp_forwarding = stream.TcpForwarding,
            udp_forwarding = stream.UdpForwarding
        };
    }

    private object CreateDeadHostPayload(DeadHost host)
    {
        return new
        {
            domain_names = host.DomainNames,
            certificate_id = MapCertificateId(host.CertificateId),
            ssl_forced = host.SslForced,
            hsts_enabled = host.HstsEnabled,
            hsts_subdomains = host.HstsSubdomains,
            http2_support = host.Http2Support,
            advanced_config = host.AdvancedConfig ?? ""
        };
    }
}
