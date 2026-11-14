namespace Solvix.IntegrationTests.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.DependencyInjection;
using Solvix.Agent.Contracts;
using Solvix.IntegrationTests.Fixtures;
using Solvix.Modules.Devices.Application.Devices;
using Solvix.Modules.Devices.Application.Enrollment;
using Solvix.Modules.Devices.Domain;
using Solvix.Modules.Devices.Security;
using Solvix.SharedKernel.Results;

/// <summary>Enrollment, duplicate devices, invalid and expired enrollment credentials.</summary>
[Collection(ApiCollection.Name)]
public sealed class DeviceEnrollmentTests(ApiFixture fixture)
{
    [Fact]
    public async Task Enrollment_issues_a_ca_signed_client_certificate_bound_to_the_device()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var token = await fixture.CreateTokenAsync(tenant);
        Assert.StartsWith(EnrollmentTokenCodec.Prefix, token, StringComparison.Ordinal);

        var device = await fixture.EnrollAsync(token);

        Assert.Equal(tenant.TenantId, device.Enrollment.TenantId);
        Assert.False(device.Enrollment.ReEnrolled);
        Assert.True(device.Enrollment.Configuration.HeartbeatIntervalSeconds > 0);

        // The certificate chains to the returned CA and carries the device identity.
        var ca = X509Certificate2.CreateFromPem(Assert.Single(device.Enrollment.CaCertificatesPem));
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        Assert.True(chain.Build(device.Certificate));
        Assert.True(DeviceCertificateIdentity.TryRead(device.Certificate, out var tenantId, out var deviceId));
        Assert.Equal(tenant.TenantId, tenantId);
        Assert.Equal(device.DeviceId, deviceId);
        Assert.Contains(device.Certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages.Cast<Oid>(), o => o.Value == "1.3.6.1.5.5.7.3.2");
        Assert.InRange(device.Enrollment.CertificateNotAfterUtc, DateTimeOffset.UtcNow.AddDays(80), DateTimeOffset.UtcNow.AddDays(91));

        // The device is visible to the tenant and its credential works.
        using var agent = fixture.CreateDeviceClient(device.Certificate);
        var heartbeat = await HttpAssert.OkAsync<HeartbeatResponse>(await agent.HeartbeatAsync());
        Assert.False(heartbeat.CertificateRenewalRecommended);

        using var admin = fixture.CreateClient(tenant.Token);
        var details = await HttpAssert.OkAsync<DeviceDetailsDto>(await admin.GetAsync($"/api/v1/devices/{device.DeviceId}"));
        Assert.Equal(device.Identity.Hostname, details.Summary.Hostname);
        Assert.Equal(DeviceConnectivity.Online, details.Summary.Connectivity);
        Assert.Equal(DeviceStatus.Active, details.Summary.Status);
        var credential = Assert.Single(details.Credentials);
        Assert.Equal(device.Certificate.GetCertHashString(HashAlgorithmName.SHA256).ToLowerInvariant(), credential.Thumbprint);
        Assert.NotNull(credential.FirstUsedAtUtc);
    }

    [Fact]
    public async Task Token_secret_is_stored_only_as_a_hash_and_single_use_tokens_are_consumed()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var token = await fixture.CreateTokenAsync(tenant, maxUses: 1);
        await fixture.EnrollAsync(token);

        var second = await fixture.PostEnrollAsync(token, ECDsa.Create(ECCurve.NamedCurves.nistP256), DeviceTestKit.Identity());
        await HttpAssert.ProblemAsync(second, HttpStatusCode.Unauthorized, "enrollment_token_invalid");

        using var admin = fixture.CreateClient(tenant.Token);
        var all = await HttpAssert.OkAsync<PagedResult<EnrollmentTokenDto>>(await admin.GetAsync("/api/v1/enrollment-tokens?includeInactive=true"));
        var listed = Assert.Single(all.Items);
        Assert.Equal(EnrollmentTokenState.Exhausted, listed.State);
        Assert.Equal(1, listed.UseCount);
        var raw = await admin.GetStringAsync("/api/v1/enrollment-tokens?includeInactive=true");
        Assert.DoesNotContain(token, raw, StringComparison.Ordinal);

        var active = await HttpAssert.OkAsync<PagedResult<EnrollmentTokenDto>>(await admin.GetAsync("/api/v1/enrollment-tokens"));
        Assert.Empty(active.Items);
    }

    [Fact]
    public async Task Multi_use_token_enrolls_several_devices_and_adds_them_to_its_group()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        var group = await HttpAssert.OkAsync<Solvix.Modules.Devices.Application.Groups.DeviceGroupDto>(
            await admin.PostAsJsonAsync("/api/v1/device-groups", new Solvix.Modules.Devices.Application.Groups.CreateDeviceGroupRequest("Laptops", null), ApiFixture.Json),
            HttpStatusCode.Created);
        var token = await fixture.CreateTokenAsync(tenant, maxUses: 3, groupId: group.Id);

        var a = await fixture.EnrollAsync(token);
        var b = await fixture.EnrollAsync(token);
        Assert.NotEqual(a.DeviceId, b.DeviceId);

        var inGroup = await HttpAssert.OkAsync<PagedResult<DeviceSummaryDto>>(await admin.GetAsync($"/api/v1/devices?groupId={group.Id}"));
        Assert.Equal(2, inGroup.TotalCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("svxe_AAAA")]
    [InlineData("svxe_!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!")]
    public async Task Malformed_tokens_are_rejected(string token)
    {
        var response = await fixture.PostEnrollAsync(token, ECDsa.Create(ECCurve.NamedCurves.nistP256), DeviceTestKit.Identity());
        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest, response.StatusCode.ToString());
    }

    [Fact]
    public async Task Token_with_wrong_secret_or_wrong_tenant_is_rejected()
    {
        var a = await fixture.RegisterTenantAsync();
        var b = await fixture.RegisterTenantAsync();
        var token = await fixture.CreateTokenAsync(a, maxUses: 5);
        Assert.True(EnrollmentTokenCodec.TryParse(token, out _, out var tokenId, out _));

        // Same token id, fresh random secret.
        var (forgedSecret, _) = EnrollmentTokenCodec.Create(a.TenantId, tokenId);
        await HttpAssert.ProblemAsync(
            await fixture.PostEnrollAsync(forgedSecret, ECDsa.Create(ECCurve.NamedCurves.nistP256), DeviceTestKit.Identity()),
            HttpStatusCode.Unauthorized, "enrollment_token_invalid");

        // Valid secret, but re-targeted at tenant B: B has no such token (RLS-scoped lookup).
        Assert.True(EnrollmentTokenCodec.TryParse(token, out _, out _, out var secret));
        var payload = new byte[64];
        b.TenantId.TryWriteBytes(payload.AsSpan(0, 16));
        tokenId.TryWriteBytes(payload.AsSpan(16, 16));
        secret.CopyTo(payload.AsSpan(32));
        var retargeted = EnrollmentTokenCodec.Prefix + System.Buffers.Text.Base64Url.EncodeToString(payload);
        await HttpAssert.ProblemAsync(
            await fixture.PostEnrollAsync(retargeted, ECDsa.Create(ECCurve.NamedCurves.nistP256), DeviceTestKit.Identity()),
            HttpStatusCode.Unauthorized, "enrollment_token_invalid");

        // Nothing was enrolled anywhere and the genuine token still works.
        using var clientB = fixture.CreateClient(b.Token);
        Assert.Equal(0, (await HttpAssert.OkAsync<PagedResult<DeviceSummaryDto>>(await clientB.GetAsync("/api/v1/devices"))).TotalCount);
        await fixture.EnrollAsync(token);
    }

    [Fact]
    public async Task Expired_token_is_rejected_with_a_specific_code()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var token = await fixture.CreateTokenAsync(tenant, maxUses: 5);
        Assert.True(EnrollmentTokenCodec.TryParse(token, out _, out var tokenId, out _));
        await fixture.ExecutePlatformSqlAsync(
            "UPDATE enrollment_tokens SET expires_at_utc = now() - interval '1 minute' WHERE id = @id", ("id", tokenId));

        await HttpAssert.ProblemAsync(
            await fixture.PostEnrollAsync(token, ECDsa.Create(ECCurve.NamedCurves.nistP256), DeviceTestKit.Identity()),
            HttpStatusCode.Unauthorized, "enrollment_token_expired");
    }

    [Fact]
    public async Task Revoked_token_is_rejected()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        var created = await HttpAssert.OkAsync<CreatedEnrollmentTokenDto>(
            await admin.PostAsJsonAsync("/api/v1/enrollment-tokens", new CreateEnrollmentTokenRequest("revoke-me", 1, 10, null), ApiFixture.Json),
            HttpStatusCode.Created);
        var revoked = await HttpAssert.OkAsync<EnrollmentTokenDto>(await admin.PostAsync($"/api/v1/enrollment-tokens/{created.EnrollmentToken.Id}/revoke", null));
        Assert.Equal(EnrollmentTokenState.Revoked, revoked.State);

        await HttpAssert.ProblemAsync(
            await fixture.PostEnrollAsync(created.Token, ECDsa.Create(ECCurve.NamedCurves.nistP256), DeviceTestKit.Identity()),
            HttpStatusCode.Unauthorized, "enrollment_token_invalid");
    }

    [Fact]
    public async Task Token_lifetime_and_uses_are_bounded()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        await HttpAssert.ProblemAsync(
            await admin.PostAsJsonAsync("/api/v1/enrollment-tokens", new CreateEnrollmentTokenRequest("too-long", 24 * 365, 1, null), ApiFixture.Json),
            HttpStatusCode.BadRequest, "validation_failed");
        await HttpAssert.ProblemAsync(
            await admin.PostAsJsonAsync("/api/v1/enrollment-tokens", new CreateEnrollmentTokenRequest("no-uses", 1, 0, null), ApiFixture.Json),
            HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task Invalid_csr_is_rejected_without_consuming_the_token()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var token = await fixture.CreateTokenAsync(tenant, maxUses: 1);
        using var client = fixture.CreateClient();

        // Tampered CSR: signature no longer matches.
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var der = new CertificateRequest("CN=x", key, HashAlgorithmName.SHA256).CreateSigningRequest();
        der[^5] ^= 0xFF;
        var tampered = PemEncoding.WriteString("CERTIFICATE REQUEST", der);
        await HttpAssert.ProblemAsync(
            await client.PostAsJsonAsync("/" + AgentRoutes.Enroll, new EnrollmentRequest { EnrollmentToken = token, CertificateSigningRequestPem = tampered, Device = DeviceTestKit.Identity() }, ApiFixture.Json),
            HttpStatusCode.BadRequest, "validation_failed");

        // Weak RSA key.
        using var weak = RSA.Create(1024);
        var weakCsr = new CertificateRequest("CN=x", weak, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSigningRequestPem();
        await HttpAssert.ProblemAsync(
            await client.PostAsJsonAsync("/" + AgentRoutes.Enroll, new EnrollmentRequest { EnrollmentToken = token, CertificateSigningRequestPem = weakCsr, Device = DeviceTestKit.Identity() }, ApiFixture.Json),
            HttpStatusCode.BadRequest, "validation_failed");

        // Token still usable.
        await fixture.EnrollAsync(token);
    }

    [Fact]
    public async Task Duplicate_device_re_enrolls_into_the_same_record_and_revokes_old_credentials()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var token = await fixture.CreateTokenAsync(tenant, maxUses: 5);
        var identity = DeviceTestKit.Identity();

        var first = await fixture.EnrollAsync(token, identity);
        using (var firstAgent = fixture.CreateDeviceClient(first.Certificate))
        {
            await HttpAssert.OkAsync<HeartbeatResponse>(await firstAgent.HeartbeatAsync());
        }

        // Same hardware (e.g. OS reinstall), different hostname and key pair.
        var second = await fixture.EnrollAsync(token, identity with { Hostname = "REINSTALLED-PC" });
        Assert.Equal(first.DeviceId, second.DeviceId);
        Assert.True(second.Enrollment.ReEnrolled);

        using var oldAgent = fixture.CreateDeviceClient(first.Certificate);
        Assert.Equal(HttpStatusCode.Unauthorized, (await oldAgent.HeartbeatAsync()).StatusCode);
        using var newAgent = fixture.CreateDeviceClient(second.Certificate);
        await HttpAssert.OkAsync<HeartbeatResponse>(await newAgent.HeartbeatAsync());

        using var admin = fixture.CreateClient(tenant.Token);
        var devices = await HttpAssert.OkAsync<PagedResult<DeviceSummaryDto>>(await admin.GetAsync("/api/v1/devices"));
        var only = Assert.Single(devices.Items);
        Assert.Equal("REINSTALLED-PC", only.Hostname);
        var details = await HttpAssert.OkAsync<DeviceDetailsDto>(await admin.GetAsync($"/api/v1/devices/{first.DeviceId}"));
        Assert.Equal(2, details.EnrollmentCount);
        Assert.Contains(details.Credentials, c => c.RevocationReason == CredentialRevocationReason.ReEnrolled);
    }

    [Fact]
    public async Task Placeholder_hardware_ids_never_merge_distinct_machines()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var token = await fixture.CreateTokenAsync(tenant, maxUses: 5);
        var a = await fixture.EnrollAsync(token, DeviceTestKit.Identity("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF", "To be filled by O.E.M."));
        var b = await fixture.EnrollAsync(token, DeviceTestKit.Identity("FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF", "To be filled by O.E.M."));
        Assert.NotEqual(a.DeviceId, b.DeviceId);
        Assert.False(b.Enrollment.ReEnrolled);
    }

    [Fact]
    public async Task Same_hardware_in_two_tenants_yields_two_independent_devices()
    {
        var a = await fixture.RegisterTenantAsync();
        var b = await fixture.RegisterTenantAsync();
        var identity = DeviceTestKit.Identity();
        var inA = await fixture.EnrollAsync(await fixture.CreateTokenAsync(a), identity);
        var inB = await fixture.EnrollAsync(await fixture.CreateTokenAsync(b), identity);
        Assert.NotEqual(inA.DeviceId, inB.DeviceId);
        Assert.False(inB.Enrollment.ReEnrolled);

        // A's credential keeps working: B's enrollment did not touch A's record.
        using var agentA = fixture.CreateDeviceClient(inA.Certificate);
        await HttpAssert.OkAsync<HeartbeatResponse>(await agentA.HeartbeatAsync());
    }

    [Fact]
    public async Task Retired_device_cannot_re_enroll_until_deleted()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var token = await fixture.CreateTokenAsync(tenant, maxUses: 5);
        var identity = DeviceTestKit.Identity();
        var device = await fixture.EnrollAsync(token, identity);
        using var admin = fixture.CreateClient(tenant.Token);

        var retired = await HttpAssert.OkAsync<DeviceDetailsDto>(await admin.PostAsync($"/api/v1/devices/{device.DeviceId}/retire", null));
        Assert.Equal(DeviceStatus.Retired, retired.Summary.Status);
        Assert.All(retired.Credentials, c => Assert.NotNull(c.RevokedAtUtc));

        using var agent = fixture.CreateDeviceClient(device.Certificate);
        Assert.Equal(HttpStatusCode.Unauthorized, (await agent.HeartbeatAsync()).StatusCode);

        await HttpAssert.ProblemAsync(
            await fixture.PostEnrollAsync(token, ECDsa.Create(ECCurve.NamedCurves.nistP256), identity),
            HttpStatusCode.Conflict, "device_retired");

        // The failed attempt did not consume a use.
        var tokens = await HttpAssert.OkAsync<PagedResult<EnrollmentTokenDto>>(await admin.GetAsync("/api/v1/enrollment-tokens"));
        Assert.Equal(1, Assert.Single(tokens.Items).UseCount);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/devices/{device.DeviceId}")).StatusCode);
        var again = await fixture.EnrollAsync(token, identity);
        Assert.NotEqual(device.DeviceId, again.DeviceId);
    }

    [Fact]
    public async Task Members_cannot_manage_enrollment_tokens()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var owner = fixture.CreateClient(tenant.Token);
        var email = $"member-{Guid.NewGuid():N}@example.test";
        var add = await owner.PostAsJsonAsync(
            "/api/v1/tenant/members",
            new Solvix.Modules.Identity.Application.Members.AddMemberRequest(email, "Member", "Passw0rd!Secure", Solvix.SharedKernel.Identity.TenantRole.Member),
            ApiFixture.Json);
        Assert.True(add.IsSuccessStatusCode, await add.Content.ReadAsStringAsync());
        var member = await fixture.LoginAsync(email, "Passw0rd!Secure", tenant.Slug);

        using var client = fixture.CreateClient(member.AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/enrollment-tokens")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/enrollment-tokens", new CreateEnrollmentTokenRequest("x", 1, 1, null), ApiFixture.Json)).StatusCode);
        // ...but can read devices.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/devices")).StatusCode);
    }

    [Fact]
    public async Task Enrollment_uses_the_tenant_from_the_token_only()
    {
        // Anonymous request with a bogus tenant header: the header is irrelevant for enrollment.
        var tenant = await fixture.RegisterTenantAsync();
        var other = await fixture.RegisterTenantAsync();
        var token = await fixture.CreateTokenAsync(tenant);
        using var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Add("X-Tenant-Id", other.TenantId.ToString());
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var response = await HttpAssert.OkAsync<EnrollmentResponse>(await client.PostAsJsonAsync(
            "/" + AgentRoutes.Enroll,
            new EnrollmentRequest { EnrollmentToken = token, CertificateSigningRequestPem = DeviceTestKit.CreateCsr(key), Device = DeviceTestKit.Identity() },
            ApiFixture.Json));
        Assert.Equal(tenant.TenantId, response.TenantId);
        _ = fixture.Factory.Services.GetRequiredService<IDeviceCertificateAuthority>();
    }
}
