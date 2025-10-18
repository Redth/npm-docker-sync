using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NpmDockerSync.Services;

public class NpmMirrorSyncService : BackgroundService
{
    private readonly ILogger<NpmMirrorSyncService> _logger;
    private readonly NginxProxyManagerClient _primaryClient;
    private readonly List<SecondaryNpmClient> _secondaryClients;
    private readonly IConfiguration _configuration;
    private TimeSpan _syncInterval;
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private bool _syncRequested;

    public NpmMirrorSyncService(
        ILogger<NpmMirrorSyncService> logger,
        NginxProxyManagerClient primaryClient,
        IConfiguration configuration)
    {
        _logger = logger;
        _primaryClient = primaryClient;
        _configuration = configuration;
        _secondaryClients = new List<SecondaryNpmClient>();
        _syncInterval = TimeSpan.FromMinutes(5);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Load mirror configurations (supports numbered env vars and legacy fallback)
        var mirrorConfigurations = GetMirrorConfigurations();

        if (mirrorConfigurations.Count == 0)
        {
            _logger.LogInformation("NPM mirror sync is disabled (no mirror configuration found)");
            return;
        }

        // Initialize secondary clients
        InitializeSecondaryClients(mirrorConfigurations);

        if (_secondaryClients.Count == 0)
        {
            _logger.LogWarning("No secondary NPM instances configured for mirroring");
            return;
        }

        _syncInterval = DetermineSyncInterval(mirrorConfigurations);

        _logger.LogInformation("NPM mirror sync started. Syncing to {Count} secondary instance(s) every {Interval}",
            _secondaryClients.Count, _syncInterval);

        // Initial sync
        await PerformSync(stoppingToken);

        // Periodic sync loop
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_syncInterval, stoppingToken);
                await PerformSync(stoppingToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in mirror sync loop");
            }
        }

        _logger.LogInformation("NPM mirror sync stopped");
    }

    public void RequestSync()
    {
        _syncRequested = true;
        _logger.LogDebug("Mirror sync requested due to configuration change");
    }

    private void InitializeSecondaryClients(IEnumerable<MirrorConfiguration> mirrorConfigurations)
    {
        _secondaryClients.Clear();

        foreach (var config in mirrorConfigurations)
        {
            var client = new SecondaryNpmClient(config.Url, config.Email, config.Password, _logger);
            _secondaryClients.Add(client);
            _logger.LogInformation("Added secondary NPM instance ({Identifier}): {Url}", config.Identifier, config.Url);
        }
    }

    private List<MirrorConfiguration> GetMirrorConfigurations()
    {
        var mirrorConfigurations = new List<MirrorConfiguration>();
        var regex = new Regex(@"^NPM_MIRROR(?<index>\d+)_URL$", RegexOptions.IgnoreCase);
        var numberedIndexes = _configuration.AsEnumerable()
            .Where(kvp => kvp.Key is not null)
            .Select(kvp => regex.Match(kvp.Key!))
            .Where(match => match.Success)
            .Select(match => match.Groups["index"].Value)
            .Select(value => int.TryParse(value, out var index) ? index : (int?)null)
            .Where(index => index.HasValue)
            .Select(index => index!.Value)
            .Distinct()
            .OrderBy(index => index)
            .ToList();

        foreach (var index in numberedIndexes)
        {
            var prefix = $"NPM_MIRROR{index}_";
            var url = _configuration[$"{prefix}URL"];

            if (string.IsNullOrWhiteSpace(url))
            {
                _logger.LogWarning("Skipping mirror {Index} - {Key} is not configured", index, $"{prefix}URL");
                continue;
            }

            var trimmedUrl = url.Trim();
            var email = _configuration[$"{prefix}EMAIL"]
                ?? _configuration["NPM_MIRROR_EMAIL"]
                ?? _configuration["NPM_EMAIL"];
            var password = _configuration[$"{prefix}PASSWORD"]
                ?? _configuration["NPM_MIRROR_PASSWORD"]
                ?? _configuration["NPM_PASSWORD"];

            if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
            {
                _logger.LogWarning("Skipping mirror {Index} ({Url}) - missing credentials", index, trimmedUrl);
                continue;
            }

            int? intervalMinutes = null;
            if (int.TryParse(_configuration[$"{prefix}SYNC_INTERVAL"], out var perMirrorInterval) && perMirrorInterval > 0)
            {
                intervalMinutes = perMirrorInterval;
            }

            mirrorConfigurations.Add(new MirrorConfiguration($"#{index}", trimmedUrl, email, password, intervalMinutes));
        }

        if (mirrorConfigurations.Count > 0)
        {
            return mirrorConfigurations;
        }

        // Legacy fallback: NPM_MIRROR_URLS with optional host-based credentials
        var legacyUrls = _configuration["NPM_MIRROR_URLS"];
        if (string.IsNullOrWhiteSpace(legacyUrls))
        {
            return mirrorConfigurations;
        }

        var urls = legacyUrls.Split(',', StringSplitOptions.RemoveEmptyEntries);
        foreach (var url in urls)
        {
            var trimmedUrl = url.Trim();
            if (string.IsNullOrEmpty(trimmedUrl))
            {
                continue;
            }

            var key = GetUrlKey(trimmedUrl);
            var email = _configuration[$"NPM_MIRROR_{key}_EMAIL"]
                ?? _configuration["NPM_MIRROR_EMAIL"]
                ?? _configuration["NPM_EMAIL"];
            var password = _configuration[$"NPM_MIRROR_{key}_PASSWORD"]
                ?? _configuration["NPM_MIRROR_PASSWORD"]
                ?? _configuration["NPM_PASSWORD"];

            if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
            {
                _logger.LogWarning("Skipping mirror {Url} - missing credentials", trimmedUrl);
                continue;
            }

            mirrorConfigurations.Add(new MirrorConfiguration(key, trimmedUrl, email, password, null));
        }

        if (mirrorConfigurations.Count > 0)
        {
            _logger.LogInformation("NPM_MIRROR_URLS is using legacy configuration. Consider migrating to numbered NPM_MIRRORn_* variables.");
        }

        return mirrorConfigurations;
    }

    private TimeSpan DetermineSyncInterval(IReadOnlyCollection<MirrorConfiguration> mirrorConfigurations)
    {
        var intervals = new List<int>();

        if (int.TryParse(_configuration["NPM_MIRROR_SYNC_INTERVAL"], out var globalInterval) && globalInterval > 0)
        {
            intervals.Add(globalInterval);
        }

        foreach (var config in mirrorConfigurations)
        {
            if (config.SyncIntervalMinutes.HasValue && config.SyncIntervalMinutes.Value > 0)
            {
                intervals.Add(config.SyncIntervalMinutes.Value);
            }
        }

        var minutes = intervals.Count > 0 ? intervals.Min() : 5;
        return TimeSpan.FromMinutes(minutes);
    }

    private sealed record MirrorConfiguration(string Identifier, string Url, string Email, string Password, int? SyncIntervalMinutes);

    private string GetUrlKey(string url)
    {
        // Convert URL to a key for env var lookup (e.g., http://npm2:81 -> NPM2)
        var normalized = UrlNormalizer.Normalize(url);
        var uri = new Uri(normalized);
        return uri.Host.ToUpperInvariant().Replace(".", "_").Replace("-", "_");
    }

    private async Task PerformSync(CancellationToken cancellationToken)
    {
        if (!await _syncLock.WaitAsync(0, cancellationToken))
        {
            _logger.LogDebug("Sync already in progress, skipping");
            return;
        }

        try
        {
            _logger.LogInformation("Starting NPM mirror sync...");
            var startTime = DateTime.UtcNow;

            foreach (var secondary in _secondaryClients)
            {
                try
                {
                    await SyncToSecondary(secondary, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to sync to secondary instance: {Url}", secondary.Url);
                }
            }

            var duration = DateTime.UtcNow - startTime;
            _logger.LogInformation("NPM mirror sync completed in {Duration:0.00}s", duration.TotalSeconds);
            _syncRequested = false;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private async Task SyncToSecondary(SecondaryNpmClient secondary, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Syncing to secondary instance: {Url}", secondary.Url);

        var syncer = new NpmResourceSyncer(_logger, _primaryClient, secondary);

        // Sync in order of dependencies
        await syncer.SyncCertificatesAsync(cancellationToken);
        await syncer.SyncAccessListsAsync(cancellationToken);
        await syncer.SyncProxyHostsAsync(cancellationToken);
        await syncer.SyncRedirectionHostsAsync(cancellationToken);
        await syncer.SyncStreamsAsync(cancellationToken);
        await syncer.SyncDeadHostsAsync(cancellationToken);

        _logger.LogInformation("Sync to {Url} completed successfully", secondary.Url);
    }
}

public class SecondaryNpmClient
{
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;
    private readonly string _email;
    private readonly string _password;
    private string? _token;
    private DateTime _tokenExpiry = DateTime.MinValue;

    public string Url { get; }

    public SecondaryNpmClient(string url, string email, string password, ILogger logger)
    {
        Url = UrlNormalizer.Normalize(url);
        _email = email;
        _password = password;
        _logger = logger;
        _httpClient = new HttpClient { BaseAddress = new Uri(Url) };
    }

    public async Task EnsureAuthenticatedAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(_token) && DateTime.UtcNow < _tokenExpiry)
            return;

        var loginRequest = new { identity = _email, secret = _password };
        var response = await _httpClient.PostAsJsonAsync("/api/tokens", loginRequest, cancellationToken);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken);
        if (result?.Token == null)
            throw new Exception($"Failed to authenticate with secondary NPM at {Url}");

        _token = result.Token;
        _tokenExpiry = DateTime.UtcNow.AddHours(23);
        _httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);
    }

    public async Task<T?> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        await EnsureAuthenticatedAsync(cancellationToken);
        var response = await _httpClient.GetAsync(path, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken);
    }

    public async Task<T?> PostAsync<T>(string path, object body, CancellationToken cancellationToken)
    {
        await EnsureAuthenticatedAsync(cancellationToken);
        var response = await _httpClient.PostAsJsonAsync(path, body, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken);
    }

    public async Task<T?> PutAsync<T>(string path, object body, CancellationToken cancellationToken)
    {
        await EnsureAuthenticatedAsync(cancellationToken);
        var response = await _httpClient.PutAsJsonAsync(path, body, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken);
    }

    public async Task DeleteAsync(string path, CancellationToken cancellationToken)
    {
        await EnsureAuthenticatedAsync(cancellationToken);
        var response = await _httpClient.DeleteAsync(path, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
