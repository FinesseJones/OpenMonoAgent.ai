using Microsoft.AspNetCore.Builder;

namespace OpenMono.Acp;

/// <summary>
/// Endpoint surface for the ACP server. T7 will implement /discovery, /sessions,
/// /sessions/{id}, /sessions/{id}/messages, /sessions/{id}/turn, and DELETE /sessions/{id}.
/// This file currently exposes only the entry-point that AcpServer.Build calls; the body
/// is intentionally empty so the project compiles between T2 and T7.
/// </summary>
public static class AcpEndpoints
{
    public static void Map(WebApplication app)
    {
        // Endpoints land in T7. Until then, AcpServer.Build still produces a working
        // ASP.NET Core host — it just answers 404 on any request.
    }
}
