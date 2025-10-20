using Docker.DotNet;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using NpmDockerSync.Services;

var builder = Host.CreateApplicationBuilder(args);

// Add configuration sources (order matters: later sources override earlier ones)
builder.Configuration.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables();

// Configure logging - must clear providers FIRST, then add formatter, then console with formatter name
builder.Logging.ClearProviders();
builder.Logging.AddConsoleFormatter<SimpleConsoleFormatter, CustomConsoleFormatterOptions>(options =>
{
    options.ColorBehavior = LoggerColorBehavior.Enabled;
});
builder.Logging.AddConsole(options =>
{
    options.FormatterName = "simple";
});
// Apply log level filters from appsettings.json AFTER adding console provider
builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));

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

await host.RunAsync();
