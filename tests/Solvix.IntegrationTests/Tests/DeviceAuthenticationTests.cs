namespace Solvix.IntegrationTests.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Solvix.Agent.Contracts;
using Solvix.IntegrationTests.Fixtures;
using Solvix.Modules.Devices.Application.Devices;
using Solvix.Modules.Devices.Domain;
using Solvix.Modules.Tenancy.Application.Directory;
using Solvix.SharedKernel.Results;

/// <summary>Invalid, expired, revoked and forged device credentials; separation of user and device identities.</summary>
[Collection(ApiCollection.Name)]
public sealed class DeviceAuthenticationTests(ApiFixture fixture)
{
    [Fact]
    public async Task Requests_without_a_certificate_are_unauthorized()
    {
        using var client = fixture.CreateDeviceClient(null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.HeartbeatAsync()).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/" + AgentRoutes.Updates)).StatusCode);
    }

    [Fact]
    public async Task Self_signed_certificate_with_a_valid_looking_identity_is_rejected()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var device = await fixture.EnrollAsync(await fixture.CreateTokenAsync(tenant));

        // Attacker copies the identity of a real device into a self-made certificate.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={device.DeviceId}", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddUri(new Uri(DeviceIdentityUri.Format(tenant.TenantId, device.DeviceId)));
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.2")], false));
        using var forged = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));

        using var client = fixture.CreateDeviceClient(forged);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.HeartbeatAsync()).StatusCode);
    }

    [Fact]
    public async Task Certificate_from_a_foreign_ca_is_rejected()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var device = await fixture.EnrollAsync(await fixture.CreateTokenAsync(tenant));

        using var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var caRequest = new CertificateRequest("CN=Evil CA", caKey, HashAlgorithmName.SHA384);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var evilCa = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var leaf = new CertificateRequest($"CN={device.DeviceId}", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddUri(new Uri(DeviceIdentityUri.Format(tenant.TenantId, device.DeviceId)));
        leaf.CertificateExtensions.Add(san.Build());
        using var cert = leaf.Create(evilCa, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(10), [1, 2, 3, 4]);

        using var client = fixture.CreateDeviceClient(cert);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.HeartbeatAsync()).StatusCode);
    }

    [Fact]
    public async Task Ca_signed_certificate_that_was_never_issued_through_enrollment_is_rejected()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var device = await fixture.EnrollAsync(await fixture.CreateTokenAsync(tenant));

        // Valid chain and identity, but no credential row: e.g. a leaked CA-signed cert from another environment.
        using var unknown = fixture.IssueDirect(tenant.TenantId, device.DeviceId, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(10), out _);
        using var client = fixture.CreateDeviceClient(unknown);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.HeartbeatAsync()).StatusCode);
    }

    [Fact]
    public async Task Expired_and_not_yet_valid_certificates_are_rejected()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var device = await fixture.EnrollAsync(await fixture.CreateTokenAsync(tenant));

        using var expired = fixture.IssueDirect(tenant.TenantId, device.DeviceId, DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow.AddDays(-1), out _);
        using var future = fixture.IssueDirect(tenant.TenantId, device.DeviceId, DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow.AddDays(30), out _);
        foreach (var cert in new[] { expired, future })
        {
            // Register them as credentials so only the validity period can be the reason for rejection.
            await fixture.ExecutePlatformSqlAsync(
                """
                INSERT INTO device_credentials (id, tenant_id, device_id, thumbprint, serial_number, not_before_utc, not_after_utc, created_at_utc, updated_at_utc)
                VALUES (gen_random_uuid(), @tenant, @device, @thumbprint, @serial, @nb, @na, now(), now())
                """,
                ("tenant", tenant.TenantId),
                ("device", device.DeviceId),
                ("thumbprint", cert.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant()),
                ("serial", cert.SerialNumber),
                ("nb", new DateTimeOffset(cert.NotBefore.ToUniversalTime())),
                ("na", new DateTimeOffset(cert.NotAfter.ToUniversalTime())));
            using var client = fixture.CreateDeviceClient(cert);
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.HeartbeatAsync()).StatusCode);
        }
    }

    [Fact]
    public async Task Revoked_credential_and_deleted_device_are_rejected()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var token = await fixture.CreateTokenAsync(tenant, maxUses: 2);
        var revoked = await fixture.EnrollAsync(token);
        var deleted = await fixture.EnrollAsync(token);

        await fixture.ExecutePlatformSqlAsync(
            "UPDATE device_credentials SET revoked_at_utc = now(), revocation_reason = 'Revoked' WHERE device_id = @d", ("d", revoked.DeviceId));
        using (var client = fixture.CreateDeviceClient(revoked.Certificate))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.HeartbeatAsync()).StatusCode);
        }

        using var admin = fixture.CreateClient(tenant.Token);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/devices/{deleted.DeviceId}")).StatusCode);
        using (var client = fixture.CreateDeviceClient(deleted.Certificate))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.HeartbeatAsync()).StatusCode);
        }
    }

    [Fact]
    public async Task Suspended_tenant_devices_are_rejected()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var device = await fixture.EnrollAsync(await fixture.CreateTokenAsync(tenant));
        await fixture.ExecutePlatformSqlAsync("UPDATE tenants SET status = 'Suspended' WHERE id = @t", ("t", tenant.TenantId));
        await FlushTenantCacheAsync(tenant.TenantId);

        using var client = fixture.CreateDeviceClient(device.Certificate);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.HeartbeatAsync()).StatusCode);
    }

    [Fact]
    public async Task User_tokens_cannot_call_agent_endpoints_and_device_certificates_cannot_call_user_endpoints()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var device = await fixture.EnrollAsync(await fixture.CreateTokenAsync(tenant));

        using var user = fixture.CreateClient(tenant.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.HeartbeatAsync()).StatusCode);

        using var agent = fixture.CreateDeviceClient(device.Certificate);
        Assert.Equal(HttpStatusCode.Unauthorized, (await agent.GetAsync("/api/v1/devices")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await agent.GetAsync("/api/v1/branches")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await agent.GetAsync("/api/v1/enrollment-tokens")).StatusCode);
    }

    [Fact]
    public async Task Certificate_renewal_issues_a_new_certificate_and_retires_the_old_one_on_first_use()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var device = await fixture.EnrollAsync(await fixture.CreateTokenAsync(tenant));
        using var oldClient = fixture.CreateDeviceClient(device.Certificate);
        await HttpAssert.OkAsync<HeartbeatResponse>(await oldClient.HeartbeatAsync());

        using var newKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var renewal = await HttpAssert.OkAsync<CertificateRenewalResponse>(await oldClient.PostAsJsonAsync(
            "/" + AgentRoutes.RenewCertificate,
            new CertificateRenewalRequest { CertificateSigningRequestPem = DeviceTestKit.CreateCsr(newKey) },
            ApiFixture.Json));
        using var renewed = X509Certificate2.CreateFromPem(renewal.CertificatePem).CopyWithPrivateKey(newKey);

        // Until the new certificate is used, the old one keeps working (no lock-out if the agent crashes).
        await HttpAssert.OkAsync<HeartbeatResponse>(await oldClient.HeartbeatAsync());

        using var newClient = fixture.CreateDeviceClient(renewed);
        await HttpAssert.OkAsync<HeartbeatResponse>(await newClient.HeartbeatAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await oldClient.HeartbeatAsync()).StatusCode);

        using var admin = fixture.CreateClient(tenant.Token);
        var details = await HttpAssert.OkAsync<DeviceDetailsDto>(await admin.GetAsync($"/api/v1/devices/{device.DeviceId}"));
        Assert.Contains(details.Credentials, c => c.RevocationReason == CredentialRevocationReason.Superseded);
        Assert.Single(details.Credentials, c => c.RevokedAtUtc is null);
    }

    [Fact]
    public async Task Heartbeat_recommends_renewal_inside_the_renewal_window()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var device = await fixture.EnrollAsync(await fixture.CreateTokenAsync(tenant));
        using var shortLived = fixture.IssueDirect(tenant.TenantId, device.DeviceId, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(5), out _);
        await fixture.ExecutePlatformSqlAsync(
            """
            INSERT INTO device_credentials (id, tenant_id, device_id, thumbprint, serial_number, not_before_utc, not_after_utc, created_at_utc, updated_at_utc)
            VALUES (gen_random_uuid(), @tenant, @device, @thumbprint, @serial, now() - interval '5 minutes', now() + interval '5 days', now(), now())
            """,
            ("tenant", tenant.TenantId),
            ("device", device.DeviceId),
            ("thumbprint", shortLived.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant()),
            ("serial", shortLived.SerialNumber));
        using var client = fixture.CreateDeviceClient(shortLived);
        var heartbeat = await HttpAssert.OkAsync<HeartbeatResponse>(await client.HeartbeatAsync());
        Assert.True(heartbeat.CertificateRenewalRecommended);
    }

    [Fact]
    public async Task Agent_version_is_tracked_and_update_checks_compare_against_the_catalog()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var device = await fixture.EnrollAsync(await fixture.CreateTokenAsync(tenant));
        using var agent = fixture.CreateDeviceClient(device.Certificate);

        var update = await HttpAssert.OkAsync<UpdateCheckResponse>(await agent.GetAsync($"/{AgentRoutes.Updates}?channel=stable&currentVersion=1.0.0"));
        Assert.True(update.UpdateAvailable);
        Assert.Equal(SolvixApiFactory.LatestAgentVersion, update.Release!.Version);
        Assert.Equal(Uri.UriSchemeHttps, update.Release.DownloadUrl.Scheme);
        Assert.Equal(64, update.Release.Sha256.Length);

        await HttpAssert.OkAsync<HeartbeatResponse>(await agent.HeartbeatAsync(SolvixApiFactory.LatestAgentVersion));
        var none = await HttpAssert.OkAsync<UpdateCheckResponse>(await agent.GetAsync($"/{AgentRoutes.Updates}?currentVersion={SolvixApiFactory.LatestAgentVersion}"));
        Assert.False(none.UpdateAvailable);
        Assert.Null(none.Release);
        var unknownChannel = await HttpAssert.OkAsync<UpdateCheckResponse>(await agent.GetAsync($"/{AgentRoutes.Updates}?channel=beta&currentVersion=0.1.0"));
        Assert.False(unknownChannel.UpdateAvailable);

        using var admin = fixture.CreateClient(tenant.Token);
        var details = await HttpAssert.OkAsync<DeviceDetailsDto>(await admin.GetAsync($"/api/v1/devices/{device.DeviceId}"));
        Assert.Equal(SolvixApiFactory.LatestAgentVersion, details.Summary.AgentVersion);
        Assert.False(details.Summary.AgentUpdateAvailable);
        Assert.NotNull(details.AgentVersionChangedAtUtc);

        var other = await fixture.EnrollAsync(await fixture.CreateTokenAsync(tenant));
        var stats = await HttpAssert.OkAsync<DeviceStatsDto>(await admin.GetAsync("/api/v1/devices/stats"));
        Assert.Equal(SolvixApiFactory.LatestAgentVersion, stats.LatestAgentVersion);
        Assert.Equal(1, stats.UpdateAvailable);
        Assert.Contains(stats.AgentVersions, v => v.Version == DeviceTestKit.AgentVersion && v.UpdateAvailable && v.DeviceCount == 1);
        Assert.Contains(stats.AgentVersions, v => v.Version == SolvixApiFactory.LatestAgentVersion && v.IsLatest);
        var outdated = await HttpAssert.OkAsync<PagedResult<DeviceSummaryDto>>(await admin.GetAsync($"/api/v1/devices?agentVersion={DeviceTestKit.AgentVersion}"));
        Assert.Equal(other.DeviceId, Assert.Single(outdated.Items).Id);
    }

    [Fact]
    public async Task Invalid_heartbeat_payloads_are_rejected()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var device = await fixture.EnrollAsync(await fixture.CreateTokenAsync(tenant));
        using var agent = fixture.CreateDeviceClient(device.Certificate);
        await HttpAssert.ProblemAsync(await agent.HeartbeatAsync("not-a-version"), HttpStatusCode.BadRequest, "validation_failed");
    }

    private async Task FlushTenantCacheAsync(Guid tenantId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var cache = scope.ServiceProvider.GetRequiredService<ITenantDirectoryCache>();
        await cache.InvalidateAsync(tenantId, string.Empty, CancellationToken.None);
    }
}
