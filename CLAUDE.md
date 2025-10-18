# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

NPM Docker Sync is a .NET 8 background service that monitors Docker containers and automatically synchronizes proxy configurations to Nginx Proxy Manager (NPM) based on Docker container labels. It uses Docker.DotNet for Docker API interactions and handles real-time container lifecycle events.

## Build and Run Commands

```bash
# Build the project
dotnet build NpmDockerSync.csproj

# Run locally (requires Docker socket access and NPM credentials)
dotnet run

# Build Docker image
docker build -t npm-docker-sync .

# Check for compilation warnings
dotnet build NpmDockerSync.csproj -warnaserror
```

## Architecture Overview

### Core Service Flow

1. **Startup Sequence** (Program.cs → DockerMonitorService.ExecuteAsync):
   - Docker client and services are registered via DI
   - `DockerNetworkService.InitializeAsync()` detects NPM container networks and Docker host IP
   - `DockerMonitorService` performs initial container scan
   - Event monitoring begins via Docker API Progress<Message> callback

2. **Event Processing Pipeline**:
   ```
   Docker Event → DockerMonitorService.HandleContainerEvent()
                → SyncOrchestrator.ProcessContainer()
                → LabelParser.ParseLabels() + DockerNetworkService.InferForwardHost()
                → NginxProxyManagerClient (Create/Update/Delete proxy host)
   ```

3. **State Management** (SyncOrchestrator):
   - `_containerToProxyHostMap`: Tracks container ID → NPM proxy host ID mappings
   - `_containerLabelHashes`: SHA256 hashes of npm.* labels to detect changes without false positives
   - Both dictionaries are cleaned up when containers are removed

### Key Components

**DockerMonitorService** (BackgroundService)
- Listens to Docker events using `MonitorEventsAsync()` with Progress callback pattern
- Filters for container events: start, stop, update, die, destroy
- Triggers initial scan of all containers on startup

**SyncOrchestrator** (Orchestration Logic)
- Central coordinator between Docker events and NPM API
- Implements label change detection via SHA256 hashing
- Handles three scenarios: create proxy, update proxy, delete proxy (including label removal)
- Infers `npm.proxy.host` when not explicitly provided

**DockerNetworkService** (Network Intelligence)
- Detects which Docker networks the NPM container is on (if `NPM_CONTAINER_NAME` configured)
- Auto-infers `npm.proxy.host` based on network topology:
  - Same network as NPM → use container name (Docker DNS)
  - Different network → use Docker host IP (bridge gateway or `host.docker.internal`)
- Caches network detection results for performance

**LabelParser** (Configuration Parsing)
- Parses `npm.` and `npm-` prefixed Docker labels into `ProxyConfiguration` objects
- Only `npm.proxy.domains` and `npm.proxy.port` are strictly required
- Supports boolean label values: "true", "1", "yes", "on"

**NginxProxyManagerClient** (NPM API Integration)
- Handles JWT authentication with token caching and expiry tracking
- Provides CRUD operations for NPM proxy hosts
- Adds automation metadata to all created proxies: `managed_by`, `container_id`, `created_at`
- Static helper methods: `IsAutomationManaged()`, `GetManagedContainerId()`

### Label Change Detection Strategy

The system uses SHA256 hashing to detect label changes efficiently:

1. Extract all `npm.*` and `npm-` labels from container
2. Sort by key and combine as "key=value|key=value"
3. Compute SHA256 hash
4. Compare with stored hash from previous processing
5. Skip NPM API calls if hash unchanged

This approach handles:
- Label value changes → proxy updated
- Labels added → proxy created
- Labels removed → proxy deleted
- No changes → skipped (performance optimization)

### Network Detection Logic

When `NPM_CONTAINER_NAME` is configured:

1. **Initialization**: Find NPM container, extract its networks
2. **Per-container inference**:
   - Inspect target container's networks
   - Check for intersection with NPM networks
   - If shared network exists → `npm.proxy.host = container_name`
   - If no shared network → `npm.proxy.host = docker_host_ip`
3. **Fallback chain** for Docker host IP:
   - User-provided `DOCKER_HOST_IP` env var
   - Bridge network gateway IP
   - `host.docker.internal` (Docker Desktop)

### Automation Metadata Tracking

Every proxy host created includes NPM `meta` field with:
- `managed_by: "npm-docker-sync"` - Identifies automation-created proxies
- `container_id: "<docker_container_id>"` - Links back to source container
- `created_at: "<ISO8601_timestamp>"` - Creation timestamp

This prevents conflicts with manually created proxies and enables cleanup/auditing.

## Important Implementation Details

### Docker API Usage
- Uses Docker.DotNet 3.125.15 with modern API patterns
- `MonitorEventsAsync()` requires `IProgress<Message>` callback (not deprecated Stream API)
- `ListNetworksAsync()` requires `NetworksListParameters` object
- Always dispose DockerClient in service Dispose()

### NPM API Authentication
- JWT tokens cached in `NginxProxyManagerClient` with 23-hour expiry
- Auto-refreshes on API calls via `EnsureAuthenticated()`
- Uses Bearer token in Authorization header

### Concurrency Considerations
- Uses `ConcurrentDictionary` for thread-safe state tracking
- Docker event handler processes events sequentially (via Progress callback)
- Each container's labels are hashed atomically

### Label Parsing Conventions
- Both `npm.` and `npm-` prefixes supported (checked in order)
- Boolean labels: case-insensitive "true", "1", "yes", "on" = true
- Domain names: comma-separated, trimmed
- Missing `proxy.host` → empty string → inferred later by network service

## Configuration Environment Variables

Required:
- `NPM_URL`, `NPM_EMAIL`, `NPM_PASSWORD` - NPM API credentials
- `DOCKER_HOST` (default: unix:///var/run/docker.sock)

Optional:
- `NPM_CONTAINER_NAME` - Enables automatic `npm.proxy.host` detection
- `DOCKER_HOST_IP` - Override auto-detected Docker host IP

## Testing Considerations

When testing:
- Mock Docker API via `DockerClient` interface
- Test label hash computation for consistency
- Verify network detection logic with various topologies
- Test label change detection (add/modify/remove scenarios)
- Validate NPM metadata is correctly set on all proxy hosts
