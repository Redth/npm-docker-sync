# Nginx-Proxy-Manager Docker Sync

Monitor 🐳 Docker containers and automatically synchronize proxy configurations specified as docker labels to [Nginx Proxy Manager](https://nginxproxymanager.com) (inspired by [lucaslorentz/caddy-docker-proxy](https://github.com/lucaslorentz/caddy-docker-proxy).

🎁 Bonus: 1:n mirroring of hosts and access lists for keeping multiple instances of Nginx-Proxy-Manager synchronized (inspired by [jeffersonraimon/npm-sync](https://github.com/jeffersonraimon/npm-sync)).

## Features

- Monitors Docker events in real-time
- Automatically creates/updates/removes proxy hosts and streams in Nginx-Proxy-Manager
- Supports all NPM proxy host configuration options via labels
- **Stream hosts (TCP/UDP forwarding)** - Forward non-HTTP traffic like databases, game servers, custom protocols
- **Multiple proxy hosts/streams per container** - Route different domains/ports on the same container
- **Automatic port detection** - Infers port from container's EXPOSE or -p mappings when not specified
- Runs as a containerized service
- Initial scan of existing containers on startup
- Tracks automation-managed proxies/streams via metadata (won't interfere with manually created entries)
- Automatic network detection and SSL certificate selection
- Multi-instance support for managing the same NPM from multiple Docker hosts
- Bonus: High Availability Mirror Sync (optional feature to synchronize primary NPM to secondary instances)


## Configuration

### Required Environment Variables

- `DOCKER_HOST`: Docker socket path (default: `unix:///var/run/docker.sock`)
- `NPM_URL`: Nginx Proxy Manager URL (e.g., `http://nginx-proxy-manager:81`)
  - Automatically normalized (lowercase, trailing slashes removed, default ports omitted)
  - Examples that are treated as identical: `https://npm.example.com/`, `HTTPS://npm.example.com`, `https://NPM.EXAMPLE.COM:443`
- `NPM_EMAIL`: NPM admin email
- `NPM_PASSWORD`: NPM admin password

### Optional Environment Variables

- `SYNC_INSTANCE_ID`: Unique identifier for this sync instance (for multi-host deployments)
  - **Recommended for multi-host setups**: Set to a unique value per Docker host (e.g., `docker-host-1`, `prod-server-a`)
  - If not set, automatically uses Docker daemon ID or Swarm node ID
  - Multiple sync instances with different IDs can safely manage the same NPM instance
- `NPM_CONTAINER_NAME`: Name or ID of the NPM container for network detection (enables automatic `npm.proxy.host` inference)
- `DOCKER_HOST_IP`: Explicit Docker host IP address
  - **Recommended**: Set to your host machine's LAN IP (e.g., `192.168.1.100`)
  - Used when containers aren't on the same network as NPM
  - If not set, will try `host.docker.internal` or Docker bridge gateway

### Proxy Defaults (Optional)

Set global default values for proxy configurations. These can be overridden per-container using labels.

- `NPM_PROXY_SSL_FORCE`: Default for SSL redirect (`true`/`false`, default: `false`)
- `NPM_PROXY_CACHING`: Default for caching (`true`/`false`, default: `false`)
- `NPM_PROXY_BLOCK_EXPLOITS`: Default for blocking common exploits (`true`/`false`, default: `true`)
- `NPM_PROXY_WEBSOCKETS`: Default for WebSocket upgrades (`true`/`false`, default: `false`)
- `NPM_PROXY_HTTP2`: Default for HTTP/2 support (`true`/`false`, default: `false`)
- `NPM_PROXY_HSTS`: Default for HSTS (`true`/`false`, default: `false`)
- `NPM_PROXY_HSTS_SUBDOMAINS`: Default for HSTS subdomains (`true`/`false`, default: `false`)

**Example**: Set `NPM_PROXY_SSL_FORCE=true` to force SSL for all containers by default, then use `npm.proxy.ssl.force=false` on specific containers to override.

## Docker Labels

Add labels to your containers to configure proxy hosts. Supports both `npm.` and `npm-` prefixes.

### Required Labels

- `npm.proxy.domains`: Comma-separated list of domain names (e.g., `app.example.com,www.app.example.com`) (NOTE: `npm.proxy.domain` works too).

### Optional Labels (Configuration)

- `npm.proxy.port`: Target port (e.g., `8080`)
  - **Auto-detected if omitted**: Uses first exposed port from container's EXPOSE directive or -p port mappings
  - If no port is specified and auto-detection fails, proxy creation will be skipped with an error
- `npm.proxy.host`: Target host to forward to (e.g., `myapp` or `192.168.1.100`)
  - **Auto-detected if omitted**: Uses container name if on same network as NPM, otherwise uses Docker host IP
- `npm.proxy.scheme`: Forward scheme (`http` or `https`, default: `http`)
- `npm.proxy.ssl.force`: Force SSL redirect (`true`/`false`, default: `false` or `NPM_PROXY_SSL_FORCE`)
- `npm.proxy.ssl.certificate.id`: SSL certificate ID from NPM
  - **Auto-selected if omitted and SSL is forced**: Automatically finds matching certificate by domain name
- `npm.proxy.caching`: Enable caching (`true`/`false`, default: `false` or `NPM_PROXY_CACHING`)
- `npm.proxy.block_common_exploits`: Block common exploits (`true`/`false`, default: `true` or `NPM_PROXY_BLOCK_EXPLOITS`)
- `npm.proxy.websockets`: Allow WebSocket upgrades (`true`/`false`, default: `false` or `NPM_PROXY_WEBSOCKETS`)
- `npm.proxy.ssl.http2`: Enable HTTP/2 (`true`/`false`, default: `false` or `NPM_PROXY_HTTP2`)
- `npm.proxy.ssl.hsts`: Enable HSTS (`true`/`false`, default: `false` or `NPM_PROXY_HSTS`)
- `npm.proxy.ssl.hsts.subdomains`: Enable HSTS for subdomains (`true`/`false`, default: `false` or `NPM_PROXY_HSTS_SUBDOMAINS`)
- `npm.proxy.accesslist.id`: Access list ID from NPM
- `npm.proxy.advanced.config`: Advanced Nginx configuration

### Multiple Proxy Hosts Per Container

You can create multiple proxy hosts for a single container using numbered indices (0-99). This is useful when you want to route different domains to different ports on the same container.

**Syntax**: Use `npm.proxy.N.*` where N is the index number.

**Example - Route two domains to different ports:**
```yaml
labels:
  npm.proxy.0.domains: api.example.com
  npm.proxy.0.port: 8080

  npm.proxy.1.domains: admin.example.com
  npm.proxy.1.port: 9090
  npm.proxy.1.ssl.force: true
```

**Backward compatibility**: Labels without an index (e.g., `npm.proxy.domains`) are treated as index 0.

```yaml
# These are equivalent:
npm.proxy.domains: example.com       # Index 0 (implicit)
npm.proxy.0.domains: example.com     # Index 0 (explicit)
```

> Labels can use either the `npm.` or `npm-` prefix (e.g., `npm.proxy.scheme` or `npm-proxy.scheme`).
>
> **Note**: Label values override environment variable defaults. If you set `NPM_PROXY_SSL_FORCE=true` globally, you can still use `npm.proxy.ssl.force=false` on specific containers to disable it.

## Stream Hosts (TCP/UDP Forwarding)

Stream hosts allow you to forward TCP and/or UDP traffic through NPM. Perfect for non-HTTP services like databases, game servers, or custom protocols.

### Required Labels

- `npm.stream.incoming.port`: The port NPM listens on for incoming connections

### Optional Labels

- `npm.stream.forward.port`: Target port to forward to
  - **Auto-detected if omitted**: Uses first exposed port from container's EXPOSE directive or -p port mappings
- `npm.stream.forward.host`: Target host to forward to
  - **Auto-detected if omitted**: Uses container name if on same network as NPM, otherwise uses Docker host IP
- `npm.stream.forward.tcp`: Enable TCP forwarding (`true`/`false`, default: `true`)
- `npm.stream.forward.udp`: Enable UDP forwarding (`true`/`false`, default: `false`)
- `npm.stream.ssl`: SSL certificate (can be certificate ID or domain name for auto-matching)
  - If omitted: No SSL certificate (valid)
  - If numeric: Uses that certificate ID
  - If domain name: Auto-matches certificate (exact match, then wildcard), errors if no match found

### Multiple Streams Per Container

Like proxy hosts, you can create multiple streams per container using numbered indices:

```yaml
labels:
  # Stream 0: MySQL on port 3306
  npm.stream.0.incoming.port: 3306
  npm.stream.0.forward.port: 3306

  # Stream 1: Redis on port 6379
  npm.stream.1.incoming.port: 6379
  npm.stream.1.forward.port: 6379
  npm.stream.1.forward.tcp: true
```

**Example - Game server with UDP:**
```yaml
labels:
  npm.stream.incoming.port: 27015
  npm.stream.forward.port: 27015
  npm.stream.forward.tcp: true
  npm.stream.forward.udp: true  # Enable both TCP and UDP
```

**Example - Secure database with SSL:**
```yaml
labels:
  npm.stream.incoming.port: 5432
  npm.stream.forward.port: 5432
  npm.stream.ssl: db.example.com  # Auto-match certificate by domain
```

> **Note**: At least one of `npm.stream.forward.tcp` or `npm.stream.forward.udp` must be enabled. TCP is enabled by default.

## Automatic Network Detection

When `NPM_CONTAINER_NAME` is configured, the service automatically detects:

1. **Shared Network Scenario**: If your container is on the same Docker network as NPM
   - Forward host is set to the container name (Docker DNS handles routing)
   - Example: Container `myapp` → `npm.proxy.host: myapp`

2. **External Network Scenario**: If your container is NOT on the same network as NPM
   - Forward host is set to the Docker host IP address
   - Docker host IP is detected from the bridge network gateway or uses `host.docker.internal`
   - Example: Container on different network → `npm.proxy.host: 172.17.0.1`

3. **Manual Override**: You can always explicitly set `npm.proxy.host` to override auto-detection

## Automatic SSL Certificate Selection

When `npm.proxy.ssl.force` is set to `true` but no `npm.proxy.ssl.certificate.id` is specified, the service automatically searches for a matching SSL certificate:

**Matching Strategy** (in order of preference):
1. **Exact Match**: Certificate that covers all specified domain names
2. **Primary Domain Match**: Certificate that covers at least the primary (first) domain
3. **Wildcard Match**: Certificate with wildcard (e.g., `*.example.com`) that covers the domain

**Example Scenarios**:
- Domain `app.example.com` matches certificate for `*.example.com`
- Domains `app.example.com, api.example.com` matches certificate for `*.example.com`
- Domain `blog.example.com` matches exact certificate for `blog.example.com`

**Benefits**:
- No need to manually look up certificate IDs
- Automatically uses the best matching certificate
- Works with Let's Encrypt and custom certificates
- Certificate list is cached (5 minutes) for performance

**Manual Override**: Specify `npm.certificate_id` to use a specific certificate

## Example Usage

### Docker Compose (Shared Network)

With automatic network detection enabled:

```yaml
services:
  npm-docker-sync:
    image: ghcr.io/redth/npm-docker-sync:latest
    environment:
      - DOCKER_HOST=unix:///var/run/docker.sock
      - NPM_URL=http://nginx-proxy-manager:81
      - NPM_EMAIL=admin@example.com
      - NPM_PASSWORD=changeme
      - NPM_CONTAINER_NAME=nginx-proxy-manager  # Enable auto-detection
      # Optional: Set global defaults for all proxies
      - NPM_PROXY_SSL_FORCE=true
      - NPM_PROXY_BLOCK_EXPLOITS=true
      - NPM_PROXY_WEBSOCKETS=false
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock:ro
    networks:
      - npm
    restart: unless-stopped
networks:
  npm:
    external: true  # Assuming NPM is on this network
```

### Example label usage:

```yaml
services:
  # Example 1: Simple setup with auto-detection
  myapp:
    image: nginx:alpine
    container_name: myapp
    networks:
      - npm
    labels:
      npm.proxy.domains: "app.example.com"
      # npm.proxy.port auto-detected from EXPOSE in nginx:alpine
      # npm.proxy.host auto-detected as "myapp"
      npm.proxy.ssl.force: "true"
      # npm.proxy.ssl.certificate.id auto-selected by domain match

  # Example 2: Multiple proxies on one container
  multiport-app:
    image: myapp:latest
    container_name: multiport-app
    networks:
      - npm
    labels:
      # Web UI on port 8080
      npm.proxy.0.domains: "web.example.com"
      npm.proxy.0.port: "8080"
      npm.proxy.0.ssl.force: "true"

      # API on port 9000
      npm.proxy.1.domains: "api.example.com"
      npm.proxy.1.port: "9000"
      npm.proxy.1.websockets: "true"

  # Example: Container on different network (uses Docker host IP)
  otherapp:
    image: nginx:alpine
    container_name: otherapp
    # Not on the proxy network
    labels:
      npm.domains: "other.example.com"
      npm.proxy.port: "80"
      # npm.proxy.host is auto-detected as Docker host IP (e.g., 172.17.0.1)

  # Example: Manual override of npm.proxy.host
  customapp:
    image: nginx:alpine
    container_name: customapp
    networks:
      - npm
    labels:
      npm.proxy.domain: "custom.example.com"
      npm.proxy.host: "192.168.1.50"  # Explicitly specified
      npm.proxy.port: "8080"
      npm.proxy.scheme: "https"
```

### Docker Run

```bash
docker run -d \
  --name npm-docker-sync \
  -e DOCKER_HOST=unix:///var/run/docker.sock \
  -e NPM_URL=http://nginx-proxy-manager:81 \
  -e NPM_EMAIL=admin@example.com \
  -e NPM_PASSWORD=changeme \
  -v /var/run/docker.sock:/var/run/docker.sock:ro \
  ghcr.io/redth/npm-docker-sync:latest
```

## How It Works

1. On startup, the service performs an initial scan of all existing containers
2. It monitors Docker events for container start, stop, update, and destroy events
3. When a container with `npm.*` or `npm-*` labels is detected:
   - The labels are parsed into a proxy configuration
   - A label hash is computed to detect changes
   - A proxy host is created or updated in Nginx Proxy Manager
   - Metadata is added to track automation-managed proxies:
     - `managed_by`: Set to "npm-docker-sync"
     - `npm_instance`: The NPM URL this instance manages
     - `container_id`: The Docker container ID
     - `created_at`: ISO 8601 timestamp
   - The mapping between container and proxy host is tracked
4. When labels are changed (without restarting):
   - The service detects the change via label hash comparison
   - The proxy host is updated with new configuration
   - If labels are removed, the proxy host is deleted
5. When a container stops or is removed:
   - The associated proxy host is deleted from Nginx Proxy Manager

### Label Change Detection

The service intelligently handles label changes without requiring container restarts:

- **Label changes detected**: When you update `npm.*` labels on a running container, the proxy host is automatically updated
- **Labels removed**: If you remove all `npm.*` labels from a tracked container, the proxy host is automatically deleted
- **No changes**: If labels haven't changed, updates are skipped to avoid unnecessary NPM API calls
- **Hash-based tracking**: Uses SHA256 hashing of sorted label key-value pairs to detect changes efficiently

### Conflict with Manual Proxy Hosts

**Important**: If a proxy host for the same domain(s) already exists in NPM and was **manually created** (not by this automation), the tool will:
- Detect that the existing proxy is not managed by this automation instance (checks for automation metadata)
- Skip creating/updating the proxy to avoid conflicts
- Log detailed error messages with ⚠️ and ❌ indicators explaining the conflict
- Provide instructions on how to resolve the conflict

**Domain Conflict Detection**:
- The tool performs **case-insensitive** domain matching
- Checks if **any** of the container's domains overlap with existing proxy hosts
- NPM does **not** allow duplicate domains - if creation fails, you'll see an error: "domain already in use"

**Resolution Options**:
1. Delete the manually created proxy in NPM UI, then restart the container
2. Remove the `npm.*` labels from the container to let it be managed manually
3. Check the logs for detailed conflict information and follow the suggested resolution steps

**Best Practice**: Either let the automation manage all proxy hosts via labels, or manually manage them in NPM. Avoid mixing both approaches for the same domains.

## Automation Tracking & Multi-Instance Support

All proxy hosts created by this tool include metadata in the `meta` field:

- **`managed_by`**: Always set to `"npm-docker-sync"` to identify automation-created proxies
- **`sync_instance_id`**: Unique identifier for the sync instance that created this proxy
- **`npm_url`**: The NPM URL being managed (e.g., `https://npm.example.com`)
- **`container_id`**: The Docker container ID that triggered the proxy creation
- **`created_at`**: ISO 8601 timestamp of when the proxy was created

### Multi-Instance Safety

When multiple sync instances manage the **same NPM instance**, they will:
- Only modify proxy hosts they created (matched by `sync_instance_id`)
- Skip proxy hosts created by other sync instances
- Log warnings when encountering proxies from other instances
- Safely coexist without conflicts

**Example scenario**: You have two Docker hosts syncing to one NPM instance
- Host A with `SYNC_INSTANCE_ID=docker-host-a` manages containers on host A
- Host B with `SYNC_INSTANCE_ID=docker-host-b` manages containers on host B
- Each only touches proxies with matching `sync_instance_id` metadata
- Both can proxy to the same NPM instance at `https://npm.example.com`
- Manual proxies and other automation tools are also safe

**Auto-Detection**: If `SYNC_INSTANCE_ID` is not set, the tool automatically uses:
1. Docker Swarm Node ID (if running in swarm mode)
2. Docker daemon ID (default for standalone Docker)
3. Hostname (fallback with warning)

**Best Practice**: Set `SYNC_INSTANCE_ID` explicitly in multi-host setups to ensure consistent identification across container restarts.

You can check if a proxy is automation-managed using the helper methods:
```csharp
var isManaged = NginxProxyManagerClient.IsAutomationManaged(proxyHost, syncInstanceId);
var containerId = NginxProxyManagerClient.GetManagedContainerId(proxyHost);
var instanceId = NginxProxyManagerClient.GetManagedInstanceId(proxyHost);
var npmUrl = NginxProxyManagerClient.GetManagedNpmUrl(proxyHost);
```

## NPM Mirror Sync (Optional High Availability Feature)

The mirror sync feature allows you to automatically synchronize your primary NPM instance to one or more secondary instances for high availability and redundancy.

> NOTE: If you _only_ want the sync functionality, or prefer to run it as a separate container, or just want to check out a different project, have a look at https://github.com/jeffersonraimon/npm-sync

### Mirror Sync Configuration

Configure mirror sync using these optional environment variables:

- `NPM_MIRROR{n}_URL`: URL for secondary NPM instance number `n` (e.g., `NPM_MIRROR1_URL=http://npm-mirror-1:81`)
- `NPM_MIRROR{n}_EMAIL`: Email for mirror `n` (falls back to `NPM_MIRROR_EMAIL`, then `NPM_EMAIL`)
- `NPM_MIRROR{n}_PASSWORD`: Password for mirror `n` (falls back to `NPM_MIRROR_PASSWORD`, then `NPM_PASSWORD`)
- `NPM_MIRROR{n}_SYNC_INTERVAL`: Optional sync interval (minutes) for mirror `n`
- `NPM_MIRROR_SYNC_INTERVAL`: Global sync interval fallback (minutes) used when per-mirror interval is not specified (default: `5`)
- `NPM_MIRROR_EMAIL`: Global email fallback for all mirrors
- `NPM_MIRROR_PASSWORD`: Global password fallback for all mirrors
- Legacy fallback (still supported): `NPM_MIRROR_URLS` with optional `NPM_MIRROR{n}_EMAIL` and `NPM_MIRROR{n}_PASSWORD`

### What Gets Synced

- ✅ Proxy Hosts
- ✅ Redirection Hosts
- ✅ Streams (TCP/UDP)
- ✅ Dead Hosts (404 pages)
- ✅ Access Lists
- ⚠️ SSL Certificates (matched by name/domain, not auto-created)

### Smart Sync Features

- SHA256 hashing prevents unnecessary updates
- ID mapping handles different IDs between instances
- Triggered automatically on Docker label changes
- Periodic sync on configurable interval
- Metadata tagging (`mirrored_from`, `mirrored_at`)

### Mirror Sync Example

```yaml
services:
  npm-docker-sync:
    build: .
    environment:
      - DOCKER_HOST=unix:///var/run/docker.sock
      - NPM_URL=http://nginx-proxy-manager:81
      - NPM_EMAIL=admin@example.com
      - NPM_PASSWORD=changeme
      # Mirror sync configuration
      - NPM_MIRROR1_URL=http://npm-mirror-1:81
      - NPM_MIRROR2_URL=http://npm-mirror-2:81
      - NPM_MIRROR_EMAIL=admin@example.com
      - NPM_MIRROR_PASSWORD=changeme
      - NPM_MIRROR_SYNC_INTERVAL=5
    volumes:
      - /var/run/docker.sock:/var/run/docker.sock:ro
    restart: unless-stopped
```

## Building

```bash
docker build -t npm-docker-sync .
```

## Development

```bash
dotnet restore
dotnet build
dotnet run
```


## License
Copyright 2025 redth

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the “Software”), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED “AS IS”, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
