using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenMono.Setup;

public sealed class RelayApiClient : IDisposable
{
    private readonly HttpClient _http;
    private const string BaseUrl = "https://app.openmonoagent.ai";

    public RelayApiClient()
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(BaseUrl),
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    public async Task<OtpRequestResult> RequestOtpAsync(string email, CancellationToken ct)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync("/api/cli/otp", new { email }, ct);
            if (response.IsSuccessStatusCode) return OtpRequestResult.Sent;
            if ((int)response.StatusCode == 429) return OtpRequestResult.RateLimited;
            return OtpRequestResult.Error;
        }
        catch
        {
            return OtpRequestResult.NetworkError;
        }
    }

    public async Task<(RelayConfig? Config, OtpVerifyError Error)> VerifyOtpAsync(
        string email, string otp, CancellationToken ct)
    {
        try
        {
            using var response = await _http.PostAsJsonAsync(
                "/api/cli/otp/verify", new { email, otp }, ct);

            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                var result = JsonSerializer.Deserialize<VerifyResponse>(body);
                if (result is null || string.IsNullOrEmpty(result.RelayToken))
                    return (null, OtpVerifyError.InvalidResponse);

                return (new RelayConfig
                {
                    Email = email,
                    RelayToken = result.RelayToken,
                    RemotePort = result.RemotePort,
                    ProxyPrefix = result.ProxyPrefix,
                    FrpsAddress = result.FrpsAddress,
                    FrpsPort = result.FrpsPort,
                    ActivatedAt = DateTimeOffset.UtcNow,
                }, OtpVerifyError.None);
            }

            if ((int)response.StatusCode == 429) return (null, OtpVerifyError.MaxAttempts);
            return (null, OtpVerifyError.InvalidCode);
        }
        catch
        {
            return (null, OtpVerifyError.NetworkError);
        }
    }

    public void Dispose() => _http.Dispose();

    private sealed class VerifyResponse
    {
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
    }
}

public enum OtpRequestResult { Sent, RateLimited, Error, NetworkError }
public enum OtpVerifyError { None, InvalidCode, MaxAttempts, InvalidResponse, NetworkError }
