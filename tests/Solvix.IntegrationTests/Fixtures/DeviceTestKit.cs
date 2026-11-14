namespace Solvix.IntegrationTests.Fixtures;

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Solvix.Agent.Contracts;
using Solvix.Modules.Devices.Application.Enrollment;
using Solvix.Modules.Devices.Security;

/// <summary>Simulates agents against the in-process API.</summary>
public static class DeviceTestKit
{
    public const string AgentVersion = "1.0.0";

    public static async Task<string> CreateTokenAsync(this ApiFixture fixture, TenantSession tenant, int maxUses = 1, int? lifetimeHours = null, Guid? groupId = null, Guid? branchId = null, Guid? departmentId = null)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentNullException.ThrowIfNull(tenant);
        using var client = fixture.CreateClient(tenant.Token);
        var created = await HttpAssert.OkAsync<CreatedEnrollmentTokenDto>(
            await client.PostAsJsonAsync("/api/v1/enrollment-tokens", new CreateEnrollmentTokenRequest("test", lifetimeHours, maxUses, groupId, branchId, departmentId), ApiFixture.Json),
            HttpStatusCode.Created);
        return created.Token;
    }

    public static DeviceIdentityInfo Identity(string? hardwareId = null, string? serial = null, string? hostname = null) => new()
    {
        HardwareId = hardwareId ?? Guid.NewGuid().ToString(),
        SerialNumber = serial ?? "SN-" + Guid.NewGuid().ToString("N")[..10],
        Hostname = hostname ?? "PC-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(),
        Manufacturer = "Contoso",
        Model = "Book 15",
        OsName = "Windows 11 Pro",
        OsVersion = "10.0.22631",
        OsBuild = "22631",
        Architecture = "x64",
        AgentVersion = AgentVersion,
    };

    public static string CreateCsr(ECDsa key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var request = new CertificateRequest("CN=ignored-by-server", key, HashAlgorithmName.SHA256);
        return request.CreateSigningRequestPem();
    }

    public static Task<HttpResponseMessage> PostEnrollAsync(this ApiFixture fixture, string token, ECDsa key, DeviceIdentityInfo identity)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        var client = fixture.CreateClient();
        return client.PostAsJsonAsync(
            "/" + AgentRoutes.Enroll,
            new EnrollmentRequest { EnrollmentToken = token, CertificateSigningRequestPem = CreateCsr(key), Device = identity },
            ApiFixture.Json);
    }

    public static async Task<EnrolledDevice> EnrollAsync(this ApiFixture fixture, string token, DeviceIdentityInfo? identity = null)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        identity ??= Identity();
        var response = await fixture.PostEnrollAsync(token, key, identity);
        var enrollment = await HttpAssert.OkAsync<EnrollmentResponse>(response);
        var certificate = X509Certificate2.CreateFromPem(enrollment.CertificatePem).CopyWithPrivateKey(key);
        return new EnrolledDevice(enrollment, certificate, key, identity);
    }

    /// <summary>A client that presents <paramref name="certificate"/> the way a TLS-terminating proxy forwards it.</summary>
    public static HttpClient CreateDeviceClient(this ApiFixture fixture, X509Certificate2? certificate)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-Proto", "https");
        if (certificate is not null)
        {
            client.DefaultRequestHeaders.Add(SolvixApiFactory.ForwardedCertificateHeader, ClientCertificateHeader.Encode(certificate));
        }

        return client;
    }

    public static Task<HttpResponseMessage> HeartbeatAsync(this HttpClient client, string agentVersion = AgentVersion) =>
        client.PostAsJsonAsync(
            "/" + AgentRoutes.Heartbeat,
            new HeartbeatRequest
            {
                AgentVersion = agentVersion,
                Health = new AgentHealthReport { Status = AgentHealthStatus.Healthy, StartedAtUtc = DateTimeOffset.UtcNow, UptimeSeconds = 5 },
            },
            ApiFixture.Json);

    /// <summary>Issues a certificate directly from the device CA (bypassing enrollment) to craft forgeries.</summary>
    public static X509Certificate2 IssueDirect(this ApiFixture fixture, Guid tenantId, Guid deviceId, DateTimeOffset notBefore, DateTimeOffset notAfter, out ECDsa key)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        var ca = fixture.Factory.Services.GetRequiredService<IDeviceCertificateAuthority>();
        key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=x", key, HashAlgorithmName.SHA256);
        var issued = ca.Issue(request.PublicKey, tenantId, deviceId, notBefore, notAfter);
        return issued.Certificate.CopyWithPrivateKey(key);
    }

    /// <summary>Runs SQL as the schema owner with RLS bypassed (test set-up only).</summary>
    public static async Task ExecutePlatformSqlAsync(this ApiFixture fixture, string sql, params (string Name, object Value)[] parameters)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        await using var connection = new NpgsqlConnection(fixture.Database.MigratorConnectionString);
        await connection.OpenAsync();
        await using (var bypass = new NpgsqlCommand("SELECT set_config('app.rls_bypass', 'on', false)", connection))
        {
            await bypass.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync();
    }
}

public sealed record EnrolledDevice(EnrollmentResponse Enrollment, X509Certificate2 Certificate, ECDsa Key, DeviceIdentityInfo Identity)
{
    public Guid DeviceId => Enrollment.DeviceId;
}
