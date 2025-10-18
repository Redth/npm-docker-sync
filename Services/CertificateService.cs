using Microsoft.Extensions.Logging;

namespace NpmDockerSync.Services;

public class CertificateService
{
    private readonly ILogger<CertificateService> _logger;
    private readonly NginxProxyManagerClient _npmClient;
    private List<Certificate>? _cachedCertificates;
    private DateTime _cacheExpiry = DateTime.MinValue;
    private readonly TimeSpan _cacheLifetime = TimeSpan.FromMinutes(5);

    public CertificateService(
        ILogger<CertificateService> logger,
        NginxProxyManagerClient npmClient)
    {
        _logger = logger;
        _npmClient = npmClient;
    }

    public async Task<int?> FindMatchingCertificateAsync(List<string> domainNames, CancellationToken cancellationToken)
    {
        if (domainNames == null || domainNames.Count == 0)
            return null;

        var certificates = await GetCertificatesAsync(cancellationToken);

        if (certificates.Count == 0)
        {
            _logger.LogDebug("No certificates available in NPM");
            return null;
        }

        var primaryDomain = domainNames[0];

        // Strategy 1: Exact match - certificate covers all requested domains
        var exactMatch = FindExactMatch(certificates, domainNames);
        if (exactMatch != null)
        {
            _logger.LogInformation("Found exact certificate match (ID: {CertId}) for domains: {Domains}",
                exactMatch.Id, string.Join(", ", domainNames));
            return exactMatch.Id;
        }

        // Strategy 2: Primary domain match - certificate covers at least the primary domain
        var primaryMatch = FindPrimaryDomainMatch(certificates, primaryDomain);
        if (primaryMatch != null)
        {
            _logger.LogInformation("Found certificate (ID: {CertId}) matching primary domain: {Domain}",
                primaryMatch.Id, primaryDomain);
            return primaryMatch.Id;
        }

        // Strategy 3: Wildcard match - certificate with wildcard covers the domain
        var wildcardMatch = FindWildcardMatch(certificates, primaryDomain);
        if (wildcardMatch != null)
        {
            _logger.LogInformation("Found wildcard certificate (ID: {CertId}) for domain: {Domain}",
                wildcardMatch.Id, primaryDomain);
            return wildcardMatch.Id;
        }

        _logger.LogWarning("No matching certificate found for domains: {Domains}", string.Join(", ", domainNames));
        return null;
    }

    private async Task<List<Certificate>> GetCertificatesAsync(CancellationToken cancellationToken)
    {
        // Return cached certificates if still valid
        if (_cachedCertificates != null && DateTime.UtcNow < _cacheExpiry)
        {
            _logger.LogDebug("Using cached certificates list ({Count} certificates)", _cachedCertificates.Count);
            return _cachedCertificates;
        }

        _logger.LogDebug("Fetching certificates from NPM");
        _cachedCertificates = await _npmClient.GetCertificatesAsync(cancellationToken);
        _cacheExpiry = DateTime.UtcNow.Add(_cacheLifetime);

        // Filter out deleted certificates
        _cachedCertificates = _cachedCertificates
            .Where(c => c.IsDeleted == 0)
            .ToList();

        _logger.LogInformation("Loaded {Count} active certificates from NPM", _cachedCertificates.Count);
        return _cachedCertificates;
    }

    private Certificate? FindExactMatch(List<Certificate> certificates, List<string> domainNames)
    {
        return certificates.FirstOrDefault(cert =>
            cert.DomainNames != null &&
            domainNames.All(domain => cert.DomainNames.Contains(domain, StringComparer.OrdinalIgnoreCase))
        );
    }

    private Certificate? FindPrimaryDomainMatch(List<Certificate> certificates, string primaryDomain)
    {
        return certificates.FirstOrDefault(cert =>
            cert.DomainNames != null &&
            cert.DomainNames.Contains(primaryDomain, StringComparer.OrdinalIgnoreCase)
        );
    }

    private Certificate? FindWildcardMatch(List<Certificate> certificates, string domain)
    {
        // Extract the root domain (e.g., "app.example.com" -> "example.com")
        var parts = domain.Split('.');
        if (parts.Length < 2)
            return null;

        var rootDomain = string.Join('.', parts.TakeLast(2));
        var wildcardPattern = $"*.{rootDomain}";

        return certificates.FirstOrDefault(cert =>
            cert.DomainNames != null &&
            cert.DomainNames.Any(certDomain =>
                certDomain.StartsWith("*.") &&
                MatchesWildcard(domain, certDomain)
            )
        );
    }

    private bool MatchesWildcard(string domain, string wildcardPattern)
    {
        if (!wildcardPattern.StartsWith("*."))
            return false;

        var wildcardRoot = wildcardPattern[2..]; // Remove "*."
        return domain.EndsWith(wildcardRoot, StringComparison.OrdinalIgnoreCase) &&
               (domain.Length == wildcardRoot.Length || domain[domain.Length - wildcardRoot.Length - 1] == '.');
    }

    public void InvalidateCache()
    {
        _logger.LogDebug("Invalidating certificate cache");
        _cacheExpiry = DateTime.MinValue;
    }
}
