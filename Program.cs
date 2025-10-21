using Docker.DotNet;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NpmDockerSync.Services;
using Serilog;
using Serilog.Core;
using Serilog.Events;

// Configure Serilog
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
    .MinimumLevel.Override("System.Net.Http", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.With<ShortSourceContextEnricher>()
    .WriteTo.Console(
        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss} [{Level:u3}] [{ShortContext}] {Message:lj}{NewLine}{Exception}"
    )
    .CreateLogger();

var builder = Host.CreateApplicationBuilder(args);

// Add configuration sources (order matters: later sources override earlier ones)
builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables();

// Use Serilog for logging
builder.Services.AddSerilog();

// Register Docker client
builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var dockerHost = config["DOCKER_HOST"] ?? "unix:///var/run/docker.sock";
    return new DockerClientConfiguration(new Uri(dockerHost)).CreateClient();
});

// Register services
builder.Services.AddHttpClient<NginxProxyManagerClient>();
builder.Services.AddSingleton<LabelParser>();
builder.Services.AddSingleton<DockerNetworkService>();
builder.Services.AddSingleton<CertificateService>();
builder.Services.AddSingleton<InstanceIdentifier>();
builder.Services.AddSingleton<SyncOrchestrator>();
builder.Services.AddSingleton<NpmMirrorSyncService>();
builder.Services.AddHostedService<DockerMonitorService>();
builder.Services.AddHostedService<NpmMirrorSyncService>();

var host = builder.Build();

try
{
    Log.Information("Starting NPM Docker Sync");
    await host.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application terminated unexpectedly");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
}

// Enricher to extract just the class name from SourceContext
class ShortSourceContextEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        if (logEvent.Properties.TryGetValue("SourceContext", out var sourceContext) &&
            sourceContext is ScalarValue { Value: string context })
        {
            var lastDot = context.LastIndexOf('.');
            var shortName = lastDot >= 0 ? context.Substring(lastDot + 1) : context;
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("ShortContext", shortName));
        }
        else
        {
            logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("ShortContext", string.Empty));
        }
    }
}
