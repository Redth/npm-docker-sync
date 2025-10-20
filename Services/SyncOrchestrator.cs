using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NpmDockerSync.Services;

public class SyncOrchestrator
{
    private readonly ILogger<SyncOrchestrator> _logger;
    private readonly NginxProxyManagerClient _npmClient;
    private readonly LabelParser _labelParser;
    private readonly DockerNetworkService _networkService;
    private readonly CertificateService _certificateService;
    private readonly InstanceIdentifier _instanceIdentifier;
    private readonly NpmMirrorSyncService? _mirrorSyncService;
    private readonly string _npmUrl;
    private string? _instanceId;

    // Track containers and their associated proxy host IDs and label state
    private readonly ConcurrentDictionary<string, int> _containerToProxyHostMap = new();
    private readonly ConcurrentDictionary<string, string> _containerLabelHashes = new();

    public SyncOrchestrator(
        ILogger<SyncOrchestrator> logger,
        NginxProxyManagerClient npmClient,
        LabelParser labelParser,
        DockerNetworkService networkService,
        CertificateService certificateService,
        InstanceIdentifier instanceIdentifier,
        IConfiguration configuration,
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _npmClient = npmClient;
        _labelParser = labelParser;
        _networkService = networkService;
        _certificateService = certificateService;
        _instanceIdentifier = instanceIdentifier;

        // Get mirror sync service if available (optional dependency)
        _mirrorSyncService = serviceProvider.GetService(typeof(NpmMirrorSyncService)) as NpmMirrorSyncService;

        var rawUrl = configuration["NPM_URL"] ?? throw new ArgumentException("NPM_URL is required");
        _npmUrl = UrlNormalizer.Normalize(rawUrl);

        _logger.LogInformation("Using normalized NPM URL: {NpmUrl}", _npmUrl);
    }

    public async Task ProcessContainer(string containerId, IDictionary<string, string> labels, CancellationToken cancellationToken)
    {
        try
        {
            // Ensure we have an instance ID
            await EnsureInstanceIdAsync(cancellationToken);

            var config = _labelParser.ParseLabels(labels);
            var currentLabelHash = ComputeLabelHash(labels);

            // Check if labels were removed (container exists in tracking but no valid config)
            if (config == null)
            {
                if (_containerToProxyHostMap.ContainsKey(containerId))
                {
                    _logger.LogInformation("Proxy labels removed from container {ContainerId}, deleting proxy host", containerId);
                    await RemoveContainer(containerId, cancellationToken);
                }
                else
                {
                    _logger.LogDebug("No proxy labels found for container {ContainerId}", containerId);
                }
                return;
            }

            // Check if labels have changed
            var hasChanged = !_containerLabelHashes.TryGetValue(containerId, out var previousHash) ||
                             previousHash != currentLabelHash;

            if (!hasChanged)
            {
                _logger.LogDebug("Labels unchanged for container {ContainerId}, skipping", containerId);
                return;
            }

            // Infer npm.proxy.host if not explicitly provided
            if (string.IsNullOrEmpty(config.ForwardHost))
            {
                config.ForwardHost = await _networkService.InferForwardHost(containerId, null, cancellationToken);
            }

            // Auto-select certificate if SSL is forced but no certificate specified
            if (config.SslForced && !config.CertificateId.HasValue)
            {
                var certId = await _certificateService.FindMatchingCertificateAsync(config.DomainNames, cancellationToken);
                if (certId.HasValue)
                {
                    config.CertificateId = certId.Value;
                    _logger.LogInformation("Auto-selected certificate ID {CertId} for domains: {Domains}",
                        certId.Value, string.Join(", ", config.DomainNames));
                }
                else
                {
                    _logger.LogWarning("SSL forced but no matching certificate found for domains: {Domains}",
                        string.Join(", ", config.DomainNames));
                }
            }

            _logger.LogInformation("Processing container {ContainerId} with domains: {Domains}, host: {ForwardHost}, cert_id: {CertId}",
                containerId, string.Join(", ", config.DomainNames), config.ForwardHost, config.CertificateId?.ToString() ?? "none");

            // Check if proxy host already exists for this container
            if (_containerToProxyHostMap.TryGetValue(containerId, out var existingHostId))
            {
                // Check if domains changed - if so, delete and recreate instead of updating
                // because NPM may not handle domain changes properly on existing proxies
                var existingHost = await _npmClient.GetProxyHostByIdAsync(existingHostId, cancellationToken);

                if (existingHost != null && DomainsChanged(existingHost.DomainNames, config.DomainNames))
                {
                    _logger.LogInformation("Domains changed for container {ContainerId} (old: [{OldDomains}], new: [{NewDomains}]). Deleting and recreating proxy host.",
                        containerId,
                        string.Join(", ", existingHost.DomainNames ?? new List<string>()),
                        string.Join(", ", config.DomainNames ?? new List<string>()));

                    // Remove the old proxy host
                    await RemoveContainer(containerId, cancellationToken);

                    // Create new proxy host with new domains
                    await CreateOrUpdateProxyHost(containerId, config, cancellationToken);
                }
                else
                {
                    await UpdateExistingProxyHost(containerId, existingHostId, config, cancellationToken);
                }
            }
            else
            {
                await CreateOrUpdateProxyHost(containerId, config, cancellationToken);
            }

            // Update label hash after successful processing
            _containerLabelHashes.AddOrUpdate(containerId, currentLabelHash, (_, _) => currentLabelHash);

            // Trigger mirror sync if configured
            _mirrorSyncService?.RequestSync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing container {ContainerId}", containerId);
        }
    }

    public async Task RemoveContainer(string containerId, CancellationToken cancellationToken)
    {
        try
        {
            if (_containerToProxyHostMap.TryRemove(containerId, out var proxyHostId))
            {
                _logger.LogInformation("Removing proxy host {HostId} for container {ContainerId}",
                    proxyHostId, containerId);

                await _npmClient.DeleteProxyHostAsync(proxyHostId, cancellationToken);

                // Remove label hash tracking
                _containerLabelHashes.TryRemove(containerId, out _);

                // Trigger mirror sync if configured
                _mirrorSyncService?.RequestSync();
            }
            else
            {
                _logger.LogDebug("No proxy host mapping found for container {ContainerId}", containerId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing container {ContainerId}", containerId);
        }
    }

    private async Task CreateOrUpdateProxyHost(string containerId, ProxyConfiguration config, CancellationToken cancellationToken)
    {
        // Check if a proxy host already exists for ANY of the specified domains
        if (config.DomainNames == null || config.DomainNames.Count == 0)
        {
            _logger.LogWarning("No domain names configured for container {ContainerId}", containerId);
            return;
        }

        // Check for any existing proxy host with overlapping domains
        var existingHost = await _npmClient.GetProxyHostByDomainsAsync(config.DomainNames, cancellationToken);

        if (existingHost != null)
        {
            _logger.LogDebug("Found existing proxy host {HostId} for domains [{Domains}]", 
                existingHost.Id, string.Join(", ", config.DomainNames));
            
            // Check if the existing host is managed by THIS instance
            if (!NginxProxyManagerClient.IsAutomationManaged(existingHost, _instanceId!))
            {
                _logger.LogError("⚠️  CONFLICT: Found existing proxy host {HostId} with domains [{ExistingDomains}] that overlaps with requested domains [{RequestedDomains}]",
                    existingHost.Id, 
                    string.Join(", ", existingHost.DomainNames ?? new List<string>()),
                    string.Join(", ", config.DomainNames));
                
                _logger.LogError("⚠️  This proxy is NOT managed by this automation instance (ID: {InstanceId})", _instanceId);

                var otherInstance = NginxProxyManagerClient.GetManagedInstanceId(existingHost);
                if (otherInstance != null)
                {
                    _logger.LogError("⚠️  Existing proxy is managed by instance: {OtherInstance}", otherInstance);
                }
                else
                {
                    _logger.LogError("⚠️  Existing proxy appears to be manually created (no automation metadata).");
                    _logger.LogError("⚠️  To resolve: Delete the proxy in NPM UI, or remove npm.* labels from container {ContainerId}", containerId);
                }
                return;
            }

            _logger.LogInformation("Found existing automation-managed proxy host {HostId}. Updating...", existingHost.Id);

            var request = _labelParser.ToProxyHostRequest(config, containerId, _instanceId!, _npmUrl);
            await _npmClient.UpdateProxyHostAsync(existingHost.Id, request, cancellationToken);

            _containerToProxyHostMap.AddOrUpdate(containerId, existingHost.Id, (_, _) => existingHost.Id);
        }
        else
        {
            _logger.LogInformation("No existing proxy host found. Creating new proxy host for domains: [{Domains}]",
                string.Join(", ", config.DomainNames));

            try
            {
                var request = _labelParser.ToProxyHostRequest(config, containerId, _instanceId!, _npmUrl);
                var newHost = await _npmClient.CreateProxyHostAsync(request, cancellationToken);

                _containerToProxyHostMap.AddOrUpdate(containerId, newHost.Id, (_, _) => newHost.Id);

                _logger.LogInformation("✅ Created proxy host {HostId} for container {ContainerId}",
                    newHost.Id, containerId);
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("already in use") || ex.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                _logger.LogError("❌ Failed to create proxy host: One or more domains [{Domains}] are already in use in NPM",
                    string.Join(", ", config.DomainNames));
                _logger.LogError("❌ This likely means a manually created proxy exists but wasn't detected. Check NPM UI for existing proxies with these domains.");
                _logger.LogError("❌ NPM API Error: {ErrorMessage}", ex.Message);
                throw;
            }
        }
    }

    private async Task UpdateExistingProxyHost(string containerId, int hostId, ProxyConfiguration config, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Updating existing proxy host {HostId}", hostId);

        var request = _labelParser.ToProxyHostRequest(config, containerId, _instanceId!, _npmUrl);
        await _npmClient.UpdateProxyHostAsync(hostId, request, cancellationToken);
    }

    private async Task EnsureInstanceIdAsync(CancellationToken cancellationToken)
    {
        if (_instanceId == null)
        {
            _instanceId = await _instanceIdentifier.GetInstanceIdAsync(cancellationToken);
            _logger.LogInformation("Sync instance ID: {InstanceId}", _instanceId);
        }
    }

    private string ComputeLabelHash(IDictionary<string, string> labels)
    {
        // Extract only npm-related labels and sort them for consistent hashing
        var npmLabels = labels
            .Where(l => l.Key.StartsWith("npm.") || l.Key.StartsWith("npm-"))
            .OrderBy(l => l.Key)
            .Select(l => $"{l.Key}={l.Value}")
            .ToList();

        if (npmLabels.Count == 0)
            return string.Empty;

        var combined = string.Join("|", npmLabels);
        return Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(combined)));
    }

    private bool DomainsChanged(IEnumerable<string>? existingDomains, IEnumerable<string>? newDomains)
    {
        var existing = (existingDomains ?? Enumerable.Empty<string>()).OrderBy(d => d).ToList();
        var updated = (newDomains ?? Enumerable.Empty<string>()).OrderBy(d => d).ToList();

        return !existing.SequenceEqual(updated);
    }
}
