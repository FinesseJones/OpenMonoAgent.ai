using System.Text.Json.Serialization;

namespace OpenMono.Setup;

public sealed class RelayConfig
{
    [JsonPropertyName("email")]
    public string Email { get; set; } = "";

    [JsonPropertyName("relayToken")]
    public string RelayToken { get; set; } = "";

    [JsonPropertyName("remotePort")]
    public int RemotePort { get; set; }

    [JsonPropertyName("proxyPrefix")]
    public string ProxyPrefix { get; set; } = "";

    [JsonPropertyName("frpsAddress")]
    public string FrpsAddress { get; set; } = "";

    [JsonPropertyName("frpsPort")]
    public int FrpsPort { get; set; }

    [JsonPropertyName("activatedAt")]
    public DateTimeOffset ActivatedAt { get; set; }
}
