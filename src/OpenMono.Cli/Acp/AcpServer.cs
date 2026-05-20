using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace OpenMono.Acp;

/// <summary>
/// Builds the Kestrel host for the ACP server. Returns a WebApplication that the caller
/// (AcpHostedService in T11, or Program.cs until then) is responsible for starting,
/// running, and stopping. Endpoint registration happens via AcpEndpoints.Map (T7).
/// </summary>
public static class AcpServer
{
    public static WebApplication Build(AcpServerSettings settings, IServiceCollection services)
    {
        var builder = WebApplication.CreateBuilder();

        foreach (var d in services)
            builder.Services.Add(d);

        // Bind 0.0.0.0 because the agent runs inside a Docker container whose port is
        // mapped to 127.0.0.1:<host_port> by the extension (DockerManager launches with
        // `-p 127.0.0.1:<P>:7475`). Localhost-only access is enforced by Docker's
        // port-publishing on the host, not by Kestrel inside the container — calling
        // ListenLocalhost here would only bind 127.0.0.1 in the container's network
        // namespace and break the port mapping.
        //
        // For native-mode runs (outside Docker, via --acp-only on the host), export
        // ASPNETCORE_URLS=http://127.0.0.1:7475 before launching to restrict access
        // to localhost; ASPNETCORE_URLS overrides this binding.
        builder.WebHost.ConfigureKestrel(o => o.ListenAnyIP(settings.Port));

        builder.Services.AddSingleton(settings);

        var app = builder.Build();
        AcpEndpoints.Map(app);   // populated in T7
        return app;
    }
}
