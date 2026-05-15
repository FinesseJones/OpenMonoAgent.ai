using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace OpenMono.Acp;

public static class AcpServer
{
    public static async Task StartAsync(AcpServerSettings settings,
                                        IServiceProvider host,    // for ConversationLoop factory, AppConfig, etc.
                                        CancellationToken ct)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(o => o.ListenLocalhost(settings.Port));
        builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
            p.WithOrigins(settings.CorsOrigins).AllowAnyHeader().AllowAnyMethod()));
        builder.Services.AddSingleton(settings);
        // Wire AcpSessionStore, conversation-loop factory, etc.

        var app = builder.Build();
        app.UseCors();
        // TODO(T8): AcpEndpoints.Map(app);
        await app.RunAsync(ct);
    }
}
