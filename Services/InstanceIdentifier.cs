using Docker.DotNet;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace NpmDockerSync.Services;

public class InstanceIdentifier
{
    private readonly ILogger<InstanceIdentifier> _logger;
    private readonly DockerClient _dockerClient;
    private readonly IConfiguration _configuration;
    private string? _instanceId;

    public InstanceIdentifier(
        ILogger<InstanceIdentifier> logger,
        DockerClient dockerClient,
        IConfiguration configuration)
    {
        _logger = logger;
        _dockerClient = dockerClient;
        _configuration = configuration;
    }

    public async Task<string> GetInstanceIdAsync(CancellationToken cancellationToken)
    {
        if (_instanceId != null)
            return _instanceId;

        // Check for explicit instance ID from environment
        var explicitId = _configuration["SYNC_INSTANCE_ID"];
        if (!string.IsNullOrWhiteSpace(explicitId))
        {
            _instanceId = explicitId.Trim();
            _logger.LogInformation("Using explicit sync instance ID: {InstanceId}", _instanceId);
            return _instanceId;
        }

        // Fallback: Use Docker system ID
        try
        {
            var systemInfo = await _dockerClient.System.GetSystemInfoAsync(cancellationToken);

            // Prefer Swarm NodeID if available (for swarm clusters)
            if (!string.IsNullOrEmpty(systemInfo.Swarm?.NodeID))
            {
                _instanceId = $"swarm-{systemInfo.Swarm.NodeID}";
                _logger.LogInformation("Using Docker Swarm Node ID as instance identifier: {InstanceId}", _instanceId);
            }
            // Use Docker daemon ID
            else if (!string.IsNullOrEmpty(systemInfo.ID))
            {
                _instanceId = $"docker-{systemInfo.ID}";
                _logger.LogInformation("Using Docker daemon ID as instance identifier: {InstanceId}", _instanceId);
            }
            // Last resort: use hostname
            else if (!string.IsNullOrEmpty(systemInfo.Name))
            {
                _instanceId = $"host-{systemInfo.Name}";
                _logger.LogWarning("Using Docker hostname as instance identifier: {InstanceId}. Consider setting SYNC_INSTANCE_ID explicitly.", _instanceId);
            }
            else
            {
                throw new Exception("Unable to determine a unique instance identifier");
            }

            return _instanceId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retrieve Docker system info for instance identification");

            // Emergency fallback: use a generated GUID and warn
            _instanceId = $"unknown-{Guid.NewGuid():N}";
            _logger.LogWarning("Using randomly generated instance ID: {InstanceId}. This will change on restart! Set SYNC_INSTANCE_ID explicitly.", _instanceId);

            return _instanceId;
        }
    }
}
