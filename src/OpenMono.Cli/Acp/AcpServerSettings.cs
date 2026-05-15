using OpenMono.Session;

namespace OpenMono.Acp;

public sealed class AcpServerSettings
{
    public bool Enabled { get; set; } = true;
    public int Port { get; set; } = 7475;
    public string[] CorsOrigins { get; set; } = new[] { "vscode-webview://*" };
    public int SessionTtlHours { get; set; } = 24;
    public int PendingToolResultsTimeoutMinutes { get; set; } = 10;

    public TimeSpan PendingToolResultsTimeout => TimeSpan.FromMinutes(PendingToolResultsTimeoutMinutes);
}

/// <summary>
/// Thrown by AcpToolExecutor (caught by AcpTurnRunner) when a turn must pause to await tool results.
/// Carries the pending tool calls so the runner can emit them and the awaiting_tool_results sentinel.
/// </summary>
public sealed class PendingToolResultsException : Exception
{
    public IReadOnlyList<ToolCall> Pending { get; }
    public PendingToolResultsException(IReadOnlyList<ToolCall> pending) { Pending = pending; }
}
