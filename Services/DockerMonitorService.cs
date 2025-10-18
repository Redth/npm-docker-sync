using Docker.DotNet;
using Docker.DotNet.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NpmDockerSync.Services;

public class DockerMonitorService : BackgroundService
{
    private readonly ILogger<DockerMonitorService> _logger;
    private readonly DockerClient _dockerClient;
    private readonly SyncOrchestrator _syncOrchestrator;
    private readonly DockerNetworkService _networkService;
    private readonly string _dockerHost;

    public DockerMonitorService(
        ILogger<DockerMonitorService> logger,
        SyncOrchestrator syncOrchestrator,
        DockerNetworkService networkService,
        IConfiguration configuration)
    {
        _logger = logger;
        _syncOrchestrator = syncOrchestrator;
        _networkService = networkService;
        _dockerHost = configuration["DOCKER_HOST"] ?? "unix:///var/run/docker.sock";

        _dockerClient = new DockerClientConfiguration(new Uri(_dockerHost))
            .CreateClient();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Docker Monitor Service starting. Connecting to: {DockerHost}", _dockerHost);

        try
        {
            // Initialize network detection
            await _networkService.InitializeAsync(stoppingToken);

            // Perform initial scan of all containers
            await PerformInitialScan(stoppingToken);

            // Start monitoring for events
            await MonitorDockerEvents(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fatal error in Docker Monitor Service");
            throw;
        }
    }

    private async Task PerformInitialScan(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Performing initial scan of containers");

        var containers = await _dockerClient.Containers.ListContainersAsync(
            new ContainersListParameters { All = true },
            stoppingToken);

        foreach (var container in containers)
        {
            if (container.Labels != null && HasProxyLabels(container.Labels))
            {
                _logger.LogInformation("Found container with proxy labels: {ContainerName}",
                    container.Names.FirstOrDefault());
                await _syncOrchestrator.ProcessContainer(container.ID, container.Labels, stoppingToken);
            }
        }

        _logger.LogInformation("Initial scan completed. Found {Count} containers with proxy labels",
            containers.Count(c => c.Labels != null && HasProxyLabels(c.Labels)));
    }

    private async Task MonitorDockerEvents(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Starting Docker event monitoring");

        var eventParameters = new ContainerEventsParameters
        {
            Filters = new Dictionary<string, IDictionary<string, bool>>
            {
                ["type"] = new Dictionary<string, bool> { ["container"] = true }
            }
        };

        var progress = new Progress<Message>(message =>
        {
            try
            {
                if (message.Type == "container")
                {
                    HandleContainerEvent(message, stoppingToken).GetAwaiter().GetResult();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling Docker event: {Action} for {ID}",
                    message.Action, message.Actor?.ID);
            }
        });

        await _dockerClient.System.MonitorEventsAsync(eventParameters, progress, stoppingToken);
    }

    private async Task HandleContainerEvent(Message message, CancellationToken stoppingToken)
    {
        var action = message.Action;
        var containerId = message.Actor?.ID;

        if (string.IsNullOrEmpty(containerId))
            return;

        _logger.LogDebug("Container event: {Action} for {ContainerId}", action, containerId);

        // Handle start and update events - these may include label changes
        if (action is "start" or "update")
        {
            try
            {
                // Fetch container details to get current labels
                var container = await _dockerClient.Containers.InspectContainerAsync(containerId, stoppingToken);

                if (container.Config?.Labels != null)
                {
                    // Always process the container - ProcessContainer will handle:
                    // - Creating proxy if labels are present and it's new
                    // - Updating proxy if labels changed
                    // - Deleting proxy if labels were removed
                    // - Skipping if nothing changed
                    _logger.LogInformation("Container {Name} {Action}", container.Name, action);
                    await _syncOrchestrator.ProcessContainer(containerId, container.Config.Labels, stoppingToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error inspecting container {ContainerId} for {Action} event", containerId, action);
            }
        }
        else if (action is "stop" or "die" or "destroy")
        {
            _logger.LogInformation("Container stopped/removed: {ContainerId}", containerId);
            await _syncOrchestrator.RemoveContainer(containerId, stoppingToken);
        }
    }

    private bool HasProxyLabels(IDictionary<string, string> labels)
    {
        return labels.Any(l => l.Key.StartsWith("npm.") || l.Key.StartsWith("npm-"));
    }

    public override void Dispose()
    {
        _dockerClient?.Dispose();
        base.Dispose();
    }
}
