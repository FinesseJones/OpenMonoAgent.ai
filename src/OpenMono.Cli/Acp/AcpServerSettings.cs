namespace OpenMono.Acp;

public sealed class AcpServerSettings
{
    public bool Enabled { get; set; } = true;
    public int Port { get; set; } = 7475;
    public int SessionTtlHours { get; set; } = 24;
    public int PendingUserResponseTimeoutMinutes { get; set; } = 10;

    /// <summary>
    /// Directory where session JSON files live. Inside the container the extension mounts
    /// a named volume at /data, so the default of /data/acp-sessions persists across
    /// container restarts. For native runs (outside Docker) the AcpSessionStore falls back
    /// to AppConfig.DataDirectory + "/acp-sessions" when this path is not writable.
    /// </summary>
    public string SessionsDirectory { get; set; } = "/data/acp-sessions";

    public TimeSpan PendingUserResponseTimeout => TimeSpan.FromMinutes(PendingUserResponseTimeoutMinutes);
}

/// <summary>
/// Thrown by AcpUserInteractionForwarder (T5) to unwind the ConversationLoop after a
/// permission_request or user_input_request SSE event has been emitted. AcpTurnRunner (T8)
/// catches it, closes the SSE response, and persists the pending TaskCompletionSource on the
/// session so the next /turn POST can resolve it.
/// </summary>
public sealed class PendingUserResponseException : Exception
{
    public string PauseId { get; }
    public PendingResponseKind Kind { get; }

    public PendingUserResponseException(string id, PendingResponseKind kind)
        : base($"Awaiting client {kind} response for pause id {id}")
    {
        PauseId = id;
        Kind = kind;
    }
}

public enum PendingResponseKind { Permission, UserInput }
