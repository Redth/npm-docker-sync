using Docker.DotNet;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NpmDockerSync.Services;

var builder = Host.CreateApplicationBuilder(args);

// Add configuration
builder.Configuration.AddEnvironmentVariables();

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

// Add logging
builder.Services.AddLogging();

var host = builder.Build();

await host.RunAsync();
