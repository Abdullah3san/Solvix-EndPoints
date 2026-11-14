namespace Solvix.Modules.Mdm.Providers.Android;

using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

public interface IGoogleAccessTokenProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken);
}

/// <summary>
/// OAuth 2.0 service-account flow (JWT bearer grant, RFC 7523) for the Android Management API.
/// The assertion is signed locally with the service account key; no Google client library is needed.
/// </summary>
internal sealed class GoogleServiceAccountTokenProvider(HttpClient http, IOptions<MdmOptions> options, TimeProvider time) : IGoogleAccessTokenProvider
{
    private const string Scope = "https://www.googleapis.com/auth/androidmanagement";
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _expires;

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (_token is not null && time.GetUtcNow() < _expires - TimeSpan.FromMinutes(2))
        {
            return _token;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_token is not null && time.GetUtcNow() < _expires - TimeSpan.FromMinutes(2))
            {
                return _token;
            }

            var android = options.Value.Android;
            if (string.IsNullOrWhiteSpace(android.ServiceAccountJson))
            {
                throw new InvalidOperationException("Android service account is not configured.");
            }

            using var key = JsonDocument.Parse(android.ServiceAccountJson);
            var clientEmail = key.RootElement.GetProperty("client_email").GetString()!;
            var privateKey = key.RootElement.GetProperty("private_key").GetString()!;
            var assertion = BuildAssertion(clientEmail, privateKey, android.TokenUrl, time.GetUtcNow());

            using var response = await http.PostAsync(
                android.TokenUrl,
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                    ["assertion"] = assertion,
                }),
                cancellationToken);
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            _token = body.GetProperty("access_token").GetString()!;
            _expires = time.GetUtcNow().AddSeconds(body.GetProperty("expires_in").GetInt32());
            return _token;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static string BuildAssertion(string clientEmail, string privateKeyPem, string audience, DateTimeOffset now)
    {
        var header = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
        var claims = Base64Url(JsonSerializer.SerializeToUtf8Bytes(new
        {
            iss = clientEmail,
            scope = Scope,
            aud = audience,
            iat = now.ToUnixTimeSeconds(),
            exp = now.AddMinutes(55).ToUnixTimeSeconds(),
        }));
        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);
        var signature = rsa.SignData(Encoding.ASCII.GetBytes($"{header}.{claims}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{header}.{claims}.{Base64Url(signature)}";
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
