using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace NpmDockerSync.Services;

public class DockerNetworkService
{
    private readonly ILogger<DockerNetworkService> _logger;
    private readonly DockerClient _dockerClient;
    private readonly string? _npmContainerName;
    private readonly string? _dockerHostIp;
    private string? _detectedDockerHostIp;
    private HashSet<string>? _npmNetworks;

    public DockerNetworkService(
        ILogger<DockerNetworkService> logger,
        DockerClient dockerClient,
        IConfiguration configuration)
    {
        _logger = logger;
        _dockerClient = dockerClient;
        _npmContainerName = configuration["NPM_CONTAINER_NAME"];
        _dockerHostIp = configuration["DOCKER_HOST_IP"];
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        // Detect NPM container networks if container name is provided
        if (!string.IsNullOrEmpty(_npmContainerName))
        {
            await DetectNpmNetworks(cancellationToken);
        }

        // Detect Docker host IP if not explicitly provided
        if (string.IsNullOrEmpty(_dockerHostIp))
        {
            await DetectDockerHostIp(cancellationToken);
        }
        else
        {
            _detectedDockerHostIp = _dockerHostIp;
        }

        _logger.LogInformation("Network detection initialized. Docker Host IP: {HostIp}, NPM Networks: {Networks}",
            _detectedDockerHostIp ?? "not detected",
            _npmNetworks != null ? string.Join(", ", _npmNetworks) : "not detected");
    }

    private async Task DetectNpmNetworks(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Looking for NPM container: {ContainerName}", _npmContainerName);

            var containers = await _dockerClient.Containers.ListContainersAsync(
                new ContainersListParameters { All = true },
                cancellationToken);

            var npmContainer = containers.FirstOrDefault(c =>
                c.Names.Any(n => n.TrimStart('/') == _npmContainerName) ||
                c.ID.StartsWith(_npmContainerName!));

            if (npmContainer == null)
            {
                _logger.LogWarning("NPM container '{Name}' not found", _npmContainerName);
                return;
            }

            var containerDetails = await _dockerClient.Containers.InspectContainerAsync(npmContainer.ID, cancellationToken);

            if (containerDetails.NetworkSettings?.Networks != null)
            {
                _npmNetworks = containerDetails.NetworkSettings.Networks.Keys.ToHashSet();
                _logger.LogInformation("NPM container found on networks: {Networks}",
                    string.Join(", ", _npmNetworks));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error detecting NPM container networks");
        }
    }

    private async Task DetectDockerHostIp(CancellationToken cancellationToken)
    {
        try
        {
            // Strategy 1: Try host.docker.internal first (works on Docker Desktop and with --add-host)
            // This is the most reliable way to reach the host from a container
            if (await TestHostReachability("host.docker.internal", cancellationToken))
            {
                _detectedDockerHostIp = "host.docker.internal";
                _logger.LogInformation("Using host.docker.internal for Docker host");
                return;
            }

            // Strategy 2: Use Docker bridge gateway
            // Works if target ports are exposed on 0.0.0.0 (all interfaces)
            var networks = await _dockerClient.Networks.ListNetworksAsync(new NetworksListParameters(), cancellationToken);
            var bridgeNetwork = networks.FirstOrDefault(n => n.Name == "bridge");

            if (bridgeNetwork?.IPAM?.Config != null && bridgeNetwork.IPAM.Config.Count > 0)
            {
                var gateway = bridgeNetwork.IPAM.Config[0].Gateway;
                if (!string.IsNullOrEmpty(gateway))
                {
                    _detectedDockerHostIp = gateway;
                    _logger.LogInformation("Using Docker bridge gateway IP: {IP}", gateway);
                    _logger.LogWarning("Using bridge gateway. Ensure target ports are exposed on 0.0.0.0 or set DOCKER_HOST_IP env var");
                    return;
                }
            }

            // Strategy 3: Final fallback
            _detectedDockerHostIp = "host.docker.internal";
            _logger.LogWarning("Could not detect Docker host IP. Using 'host.docker.internal' - may not work on all systems");
            _logger.LogWarning("Consider setting DOCKER_HOST_IP environment variable to your host's IP address");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error detecting Docker host IP");
            _detectedDockerHostIp = "host.docker.internal";
        }
    }

    private async Task<bool> TestHostReachability(string hostname, CancellationToken cancellationToken)
    {
        try
        {
            // Try to resolve the hostname
            var addresses = await System.Net.Dns.GetHostAddressesAsync(hostname);
            return addresses.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task<string> InferForwardHost(string containerId, string? explicitHost, CancellationToken cancellationToken)
    {
        // If explicitly provided, use it
        if (!string.IsNullOrEmpty(explicitHost))
            return explicitHost;

        try
        {
            // Get container details
            var container = await _dockerClient.Containers.InspectContainerAsync(containerId, cancellationToken);
            var containerName = container.Name.TrimStart('/');

            // Check if container is on the same network as NPM
            if (_npmNetworks != null && container.NetworkSettings?.Networks != null)
            {
                var containerNetworks = container.NetworkSettings.Networks.Keys.ToHashSet();
                var sharedNetworks = _npmNetworks.Intersect(containerNetworks).ToList();

                if (sharedNetworks.Any())
                {
                    _logger.LogInformation("Container {ContainerName} shares network(s) with NPM: {Networks}. Using container name as forward host.",
                        containerName, string.Join(", ", sharedNetworks));
                    return containerName;
                }
            }

            // Container is not on the same network, use Docker host IP
            if (!string.IsNullOrEmpty(_detectedDockerHostIp))
            {
                _logger.LogInformation("Container {ContainerName} is not on NPM network. Using Docker host IP: {HostIp}",
                    containerName, _detectedDockerHostIp);
                return _detectedDockerHostIp;
            }

            // Fallback to container name (might not work, but it's a reasonable guess)
            _logger.LogWarning("Could not determine optimal forward host for {ContainerName}, using container name as fallback",
                containerName);
            return containerName;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error inferring forward host for container {ContainerId}", containerId);
            throw;
        }
    }

    public string? GetDockerHostIp() => _detectedDockerHostIp;
}
