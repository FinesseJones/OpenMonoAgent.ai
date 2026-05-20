using System.Text;
using System.Text.Json;

namespace OpenMono.Acp;

/// <summary>
/// Writes SSE-framed events to a response body. A semaphore serializes concurrent writes
/// from IAcpEventSink callbacks (which can fire from worker threads during LLM streaming)
/// and IAcpUserInteraction callbacks (which fire from the tool dispatcher) so the bytes
/// for one event are never interleaved with another.
/// </summary>
public sealed class SseWriter
{
    private readonly Stream _body;
    private readonly CancellationToken _ct;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = false };

    public SseWriter(Stream body, CancellationToken ct)
    {
        _body = body;
        _ct = ct;
    }

    public async Task WriteEventAsync(string eventName, object payload)
    {
        var data = JsonSerializer.Serialize(payload, Opts);
        var bytes = Encoding.UTF8.GetBytes($"event: {eventName}\ndata: {data}\n\n");

        await _gate.WaitAsync(_ct);
        try
        {
            await _body.WriteAsync(bytes, _ct);
            await _body.FlushAsync(_ct);
        }
        finally
        {
            _gate.Release();
        }
    }
}
