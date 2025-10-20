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
    // Key format: "containerId:proxyIndex" -> NPM proxy host ID
    private readonly ConcurrentDictionary<string, int> _containerProxyMap = new();
    // Key format: "containerId:streamIndex" -> NPM stream ID
    private readonly ConcurrentDictionary<string, int> _containerStreamMap = new();
    // Key format: "containerId" -> hash of all npm.* labels
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

    public async Task ProcessContainer(string containerId, string containerName, IDictionary<string, string> labels, CancellationToken cancellationToken)
    {
        try
        {
            // Ensure we have an instance ID
            await EnsureInstanceIdAsync(cancellationToken);

            var proxyConfigs = _labelParser.ParseLabels(labels);
            var streamConfigs = _labelParser.ParseStreamLabels(labels);
            var currentLabelHash = ComputeLabelHash(labels);

            // Check if labels have changed
            var hasChanged = !_containerLabelHashes.TryGetValue(containerId, out var previousHash) ||
                             previousHash != currentLabelHash;

            if (!hasChanged)
            {
                _logger.LogDebug("Labels unchanged for container {ContainerId}, skipping", containerId);
                return;
            }

            _logger.LogInformation("Processing container {ContainerName} with {ProxyCount} proxy(s) and {StreamCount} stream(s)",
                containerName, proxyConfigs.Count, streamConfigs.Count);

            // Process proxy hosts
            await ProcessProxyHosts(containerId, containerName, proxyConfigs, cancellationToken);

            // Process streams
            await ProcessStreams(containerId, containerName, streamConfigs, cancellationToken);

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

    private async Task ProcessProxyHosts(string containerId, string containerName, Dictionary<int, ProxyConfiguration> configs, CancellationToken cancellationToken)
    {
        // Get all existing proxy indices for this container
        var existingProxyKeys = _containerProxyMap.Keys
            .Where(k => k.StartsWith($"{containerId}:"))
            .ToList();

        var existingIndices = existingProxyKeys
            .Select(k => int.Parse(k.Split(':')[1]))
            .ToHashSet();

        var newIndices = configs.Keys.ToHashSet();

        // Remove proxies that no longer exist
        foreach (var index in existingIndices.Except(newIndices))
        {
            await RemoveProxy(containerId, containerName, index, cancellationToken);
        }

        // Process each proxy configuration
        foreach (var (index, config) in configs)
        {
            await ProcessProxyConfig(containerId, containerName, index, config, cancellationToken);
        }
    }

    private async Task ProcessStreams(string containerId, string containerName, Dictionary<int, StreamConfiguration> configs, CancellationToken cancellationToken)
    {
        // Get all existing stream indices for this container
        var existingStreamKeys = _containerStreamMap.Keys
            .Where(k => k.StartsWith($"{containerId}:"))
            .ToList();

        var existingIndices = existingStreamKeys
            .Select(k => int.Parse(k.Split(':')[1]))
            .ToHashSet();

        var newIndices = configs.Keys.ToHashSet();

        // Remove streams that no longer exist
        foreach (var index in existingIndices.Except(newIndices))
        {
            await RemoveStream(containerId, containerName, index, cancellationToken);
        }

        // Process each stream configuration
        foreach (var (index, config) in configs)
        {
            await ProcessStreamConfig(containerId, containerName, index, config, cancellationToken);
        }
    }

    private async Task ProcessProxyConfig(string containerId, string containerName, int index, ProxyConfiguration config, CancellationToken cancellationToken)
    {
        // Infer npm.proxy.host if not explicitly provided
        if (string.IsNullOrEmpty(config.ForwardHost))
        {
            config.ForwardHost = await _networkService.InferForwardHost(containerId, null, cancellationToken);
        }

        // Infer npm.proxy.port if not explicitly provided
        if (!config.ForwardPort.HasValue)
        {
            var inferredPort = await _networkService.InferForwardPort(containerId, cancellationToken);
            if (inferredPort.HasValue)
            {
                config.ForwardPort = inferredPort.Value;
            }
            else
            {
                _logger.LogError("❌ Cannot create proxy for container {ContainerName} proxy {Index}: No port specified and unable to auto-detect port from container",
                    containerName, index);
                return; // Skip this proxy configuration
            }
        }

        // Auto-select certificate if SSL is forced but no certificate specified
        if (config.SslForced && !config.CertificateId.HasValue)
        {
            var certId = await _certificateService.FindMatchingCertificateAsync(config.DomainNames, cancellationToken);
            if (certId.HasValue)
            {
                config.CertificateId = certId.Value;
                _logger.LogInformation("Auto-selected certificate ID {CertId} for proxy {Index} domains: {Domains}",
                    certId.Value, index, string.Join(", ", config.DomainNames));
            }
            else
            {
                _logger.LogWarning("SSL forced but no matching certificate found for proxy {Index} domains: {Domains}",
                    index, string.Join(", ", config.DomainNames));
            }
        }

        _logger.LogInformation("Processing container {ContainerId} proxy {Index} with domains: {Domains}, host: {ForwardHost}:{ForwardPort}, cert_id: {CertId}",
            containerId, index, string.Join(", ", config.DomainNames), config.ForwardHost, config.ForwardPort, config.CertificateId?.ToString() ?? "none");

        var proxyKey = $"{containerId}:{index}";

        // Check if proxy host already exists for this container:index
        if (_containerProxyMap.TryGetValue(proxyKey, out var existingHostId))
        {
            // NPM doesn't support updates - any change requires delete + recreate
            _logger.LogInformation("Labels changed for container {ContainerName} proxy {Index}. Deleting and recreating proxy host {HostId}.",
                containerName, index, existingHostId);

            // Remove the old proxy host
            await RemoveProxy(containerId, containerName, index, cancellationToken);
        }

        // Create new proxy host
        await CreateOrUpdateProxyHost(containerId, index, config, cancellationToken);
    }

    private async Task ProcessStreamConfig(string containerId, string containerName, int index, StreamConfiguration config, CancellationToken cancellationToken)
    {
        // Infer npm.stream.forward.host if not explicitly provided
        if (string.IsNullOrEmpty(config.ForwardHost))
        {
            config.ForwardHost = await _networkService.InferForwardHost(containerId, null, cancellationToken);
        }

        // Infer npm.stream.forward.port if not explicitly provided
        if (!config.ForwardPort.HasValue)
        {
            var inferredPort = await _networkService.InferForwardPort(containerId, cancellationToken);
            if (inferredPort.HasValue)
            {
                config.ForwardPort = inferredPort.Value;
            }
            else
            {
                _logger.LogError("❌ Cannot create stream for container {ContainerName} stream {Index}: No forward port specified and unable to auto-detect port from container",
                    containerName, index);
                return; // Skip this stream configuration
            }
        }

        // Resolve SSL certificate if specified
        if (!string.IsNullOrEmpty(config.SslCertificate))
        {
            // Check if it's a certificate ID (numeric) or domain name
            if (int.TryParse(config.SslCertificate, out var certId))
            {
                config.CertificateId = certId;
                _logger.LogInformation("Using explicit certificate ID {CertId} for stream {Index}",
                    certId, index);
            }
            else
            {
                // It's a domain name, find matching certificate
                var matchedCertId = await _certificateService.FindMatchingCertificateAsync(
                    new List<string> { config.SslCertificate }, cancellationToken);

                if (matchedCertId.HasValue)
                {
                    config.CertificateId = matchedCertId.Value;
                    _logger.LogInformation("Matched certificate ID {CertId} for stream {Index} domain: {Domain}",
                        matchedCertId.Value, index, config.SslCertificate);
                }
                else
                {
                    _logger.LogError("❌ Cannot create stream for container {ContainerName} stream {Index}: SSL certificate specified ({Domain}) but no matching certificate found",
                        containerName, index, config.SslCertificate);
                    return; // Skip this stream configuration
                }
            }
        }

        // Validate at least one forwarding protocol is enabled
        if (!config.TcpForwarding && !config.UdpForwarding)
        {
            _logger.LogError("❌ Cannot create stream for container {ContainerName} stream {Index}: At least one of TCP or UDP forwarding must be enabled",
                containerName, index);
            return;
        }

        _logger.LogInformation("Processing container {ContainerName} stream {Index}: {IncomingPort} -> {ForwardHost}:{ForwardPort} (TCP:{Tcp}, UDP:{Udp}, SSL:{Ssl})",
            containerName, index, config.IncomingPort, config.ForwardHost, config.ForwardPort,
            config.TcpForwarding ? "yes" : "no",
            config.UdpForwarding ? "yes" : "no",
            config.CertificateId.HasValue ? config.CertificateId.Value.ToString() : "none");

        var streamKey = $"{containerId}:{index}";

        // Check if stream already exists for this container:index
        if (_containerStreamMap.TryGetValue(streamKey, out var existingStreamId))
        {
            // NPM doesn't support updates - any change requires delete + recreate
            _logger.LogInformation("Labels changed for container {ContainerName} stream {Index}. Deleting and recreating stream {StreamId}.",
                containerName, index, existingStreamId);

            // Remove the old stream
            await RemoveStream(containerId, containerName, index, cancellationToken);
        }

        // Create new stream
        await CreateStream(containerId, containerName, index, config, cancellationToken);
    }

    public async Task RemoveContainer(string containerId, string containerName, CancellationToken cancellationToken)
    {
        try
        {
            // Find all proxies for this container
            var proxyKeys = _containerProxyMap.Keys
                .Where(k => k.StartsWith($"{containerId}:"))
                .ToList();

            // Find all streams for this container
            var streamKeys = _containerStreamMap.Keys
                .Where(k => k.StartsWith($"{containerId}:"))
                .ToList();

            if (proxyKeys.Count == 0 && streamKeys.Count == 0)
            {
                _logger.LogDebug("No proxy or stream mappings found for container {ContainerName}", containerName);
                return;
            }

            _logger.LogInformation("Removing {ProxyCount} proxy(s) and {StreamCount} stream(s) for container {ContainerName}",
                proxyKeys.Count, streamKeys.Count, containerName);

            foreach (var proxyKey in proxyKeys)
            {
                var index = int.Parse(proxyKey.Split(':')[1]);
                await RemoveProxy(containerId, containerName, index, cancellationToken);
            }

            foreach (var streamKey in streamKeys)
            {
                var index = int.Parse(streamKey.Split(':')[1]);
                await RemoveStream(containerId, containerName, index, cancellationToken);
            }

            // Remove label hash tracking
            _containerLabelHashes.TryRemove(containerId, out _);

            // Trigger mirror sync if configured
            _mirrorSyncService?.RequestSync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error removing container {ContainerName}", containerName);
        }
    }

    private async Task RemoveProxy(string containerId, string containerName, int index, CancellationToken cancellationToken)
    {
        var proxyKey = $"{containerId}:{index}";

        if (_containerProxyMap.TryRemove(proxyKey, out var proxyHostId))
        {
            _logger.LogInformation("Removing proxy host {HostId} for container {ContainerName} proxy {Index}",
                proxyHostId, containerName, index);

            try
            {
                await _npmClient.DeleteProxyHostAsync(proxyHostId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting proxy host {HostId} for container {ContainerName} proxy {Index}",
                    proxyHostId, containerName, index);
            }
        }
    }

    private async Task RemoveStream(string containerId, string containerName, int index, CancellationToken cancellationToken)
    {
        var streamKey = $"{containerId}:{index}";

        if (_containerStreamMap.TryRemove(streamKey, out var streamId))
        {
            _logger.LogInformation("Removing stream {StreamId} for container {ContainerName} stream {Index}",
                streamId, containerName, index);

            try
            {
                await _npmClient.DeleteStreamAsync(streamId, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting stream {StreamId} for container {ContainerName} stream {Index}",
                    streamId, containerName, index);
            }
        }
    }

    private async Task CreateStream(string containerId, string containerName, int index, StreamConfiguration config, CancellationToken cancellationToken)
    {
        var streamKey = $"{containerId}:{index}";

        try
        {
            var request = _labelParser.ToStreamRequest(config, containerId, _instanceId!, _npmUrl);
            var stream = await _npmClient.CreateStreamAsync(request, cancellationToken);

            _containerStreamMap.AddOrUpdate(streamKey, stream.Id, (_, _) => stream.Id);

            // Build protocol string
            var protocols = new List<string>();
            if (config.TcpForwarding) protocols.Add("tcp");
            if (config.UdpForwarding) protocols.Add("udp");
            var protocolStr = string.Join("+", protocols);

            _logger.LogInformation("✅ Created stream {{id={StreamId}, incoming={IncomingPort}, forward={ForwardHost}:{ForwardPort}, protocol={Protocol}}}",
                stream.Id,
                config.IncomingPort,
                config.ForwardHost,
                config.ForwardPort,
                protocolStr);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "❌ Failed to create stream for container {ContainerName} stream {Index}", containerName, index);
            throw;
        }
    }

    private async Task CreateOrUpdateProxyHost(string containerId, int index, ProxyConfiguration config, CancellationToken cancellationToken)
    {
        // Check if a proxy host already exists for ANY of the specified domains
        if (config.DomainNames == null || config.DomainNames.Count == 0)
        {
            _logger.LogWarning("No domain names configured for container {ContainerId} proxy {Index}", containerId, index);
            return;
        }

        var proxyKey = $"{containerId}:{index}";

        // Check for any existing proxy host with overlapping domains
        var existingHost = await _npmClient.GetProxyHostByDomainsAsync(config.DomainNames, cancellationToken);

        if (existingHost != null)
        {
            _logger.LogDebug("Found existing proxy host {HostId} for domains [{Domains}]",
                existingHost.Id, string.Join(", ", config.DomainNames));

            // Check if the existing host is managed by THIS instance
            if (!NginxProxyManagerClient.IsAutomationManaged(existingHost, _instanceId!))
            {
                _logger.LogError("⚠️ CONFLICT: Found existing proxy host {HostId} with domains [{ExistingDomains}] that overlaps with requested domains [{RequestedDomains}]",
                    existingHost.Id,
                    string.Join(", ", existingHost.DomainNames ?? new List<string>()),
                    string.Join(", ", config.DomainNames));

                _logger.LogError("⚠️ This proxy is NOT managed by this automation instance (ID: {InstanceId})", _instanceId);

                var otherInstance = NginxProxyManagerClient.GetManagedInstanceId(existingHost);
                if (otherInstance != null)
                {
                    _logger.LogError("⚠️ Existing proxy is managed by instance: {OtherInstance}", otherInstance);
                }
                else
                {
                    _logger.LogError("⚠️ Existing proxy appears to be manually created (no automation metadata).");
                    _logger.LogError("⚠️ To resolve: Delete the proxy in NPM UI, or remove npm.* labels from container {ContainerId} proxy {Index}", containerId, index);
                }
                return;
            }

            _logger.LogInformation("Found existing automation-managed proxy host {HostId}. Deleting and recreating...", existingHost.Id);

            // NPM doesn't support updates - delete the old one first
            await _npmClient.DeleteProxyHostAsync(existingHost.Id, cancellationToken);

            // Create new proxy host
            var request = _labelParser.ToProxyHostRequest(config, containerId, _instanceId!, _npmUrl);
            var newHost = await _npmClient.CreateProxyHostAsync(request, cancellationToken);

            _containerProxyMap.AddOrUpdate(proxyKey, newHost.Id, (_, _) => newHost.Id);

            // Build options string
            var options = new List<string>();
            if (config.SslForced) options.Add("ssl");
            if (config.AllowWebsocketUpgrade) options.Add("websockets");
            if (config.Http2Support) options.Add("http2");
            if (config.HstsEnabled) options.Add("hsts");
            if (config.CachingEnabled) options.Add("cache");
            if (config.BlockExploits) options.Add("block_exploits");
            var optionsStr = options.Count > 0 ? string.Join("+", options) : "none";

            _logger.LogInformation("✅ Recreated proxy host {{id={HostId}, domains=[{Domains}], forward={Scheme}://{Host}:{Port}, options={Options}}}",
                newHost.Id,
                string.Join(",", config.DomainNames),
                config.ForwardScheme,
                config.ForwardHost,
                config.ForwardPort,
                optionsStr);
        }
        else
        {
            _logger.LogInformation("No existing proxy host found. Creating new proxy host for domains: [{Domains}]",
                string.Join(", ", config.DomainNames));

            try
            {
                var request = _labelParser.ToProxyHostRequest(config, containerId, _instanceId!, _npmUrl);
                var newHost = await _npmClient.CreateProxyHostAsync(request, cancellationToken);

                _containerProxyMap.AddOrUpdate(proxyKey, newHost.Id, (_, _) => newHost.Id);

                // Build options string
                var options = new List<string>();
                if (config.SslForced) options.Add("ssl");
                if (config.AllowWebsocketUpgrade) options.Add("websockets");
                if (config.Http2Support) options.Add("http2");
                if (config.HstsEnabled) options.Add("hsts");
                if (config.CachingEnabled) options.Add("cache");
                if (config.BlockExploits) options.Add("block_exploits");
                var optionsStr = options.Count > 0 ? string.Join("+", options) : "none";

                _logger.LogInformation("✅ Created proxy host {{id={HostId}, domains=[{Domains}], forward={Scheme}://{Host}:{Port}, options={Options}}}",
                    newHost.Id,
                    string.Join(",", config.DomainNames),
                    config.ForwardScheme,
                    config.ForwardHost,
                    config.ForwardPort,
                    optionsStr);
            }
            catch (HttpRequestException ex) when (ex.Message.Contains("already in use") || ex.StatusCode == System.Net.HttpStatusCode.BadRequest)
            {
                _logger.LogError("❌ Failed to create proxy host for container {ContainerId} proxy {Index}: One or more domains [{Domains}] are already in use in NPM",
                    containerId, index, string.Join(", ", config.DomainNames));
                _logger.LogError("❌ This likely means a manually created proxy exists but wasn't detected. Check NPM UI for existing proxies with these domains.");
                _logger.LogError("❌ NPM API Error: {ErrorMessage}", ex.Message);
                throw;
            }
        }
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
}
