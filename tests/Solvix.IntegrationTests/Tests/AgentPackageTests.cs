namespace Solvix.IntegrationTests.Tests;

using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Solvix.Agent.Contracts;
using Solvix.IntegrationTests.Fixtures;
using Solvix.Modules.Devices.Application.Enrollment;
using Solvix.Modules.Organization.Application.Branches;
using Solvix.Modules.Organization.Application.Departments;

/// <summary>The downloadable agent package: signed exe + enrollment.json, so nothing is pasted on a command line.</summary>
[Collection(ApiCollection.Name)]
public sealed class AgentPackageTests(ApiFixture fixture)
{
    [Fact]
    public async Task Package_contains_the_unmodified_exe_and_an_enrollment_file_for_that_token()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        var branch = await HttpAssert.OkAsync<BranchDto>(await admin.PostAsJsonAsync("/api/v1/branches", new CreateBranchRequest("Berlin store", "BER", false, null, null, null, null), ApiFixture.Json), HttpStatusCode.Created);
        var tills = await HttpAssert.OkAsync<DepartmentDto>(await admin.PostAsJsonAsync("/api/v1/departments", new CreateDepartmentRequest(branch.Id, null, "Tills", "TILL", null), ApiFixture.Json), HttpStatusCode.Created);
        var created = await HttpAssert.OkAsync<CreatedEnrollmentTokenDto>(
            await admin.PostAsJsonAsync("/api/v1/enrollment-tokens", new CreateEnrollmentTokenRequest("Berlin tills", 12, 3, null, null, tills.Id), ApiFixture.Json), HttpStatusCode.Created);

        var response = await admin.PostAsJsonAsync("/api/v1/enrollment-tokens/package", new AgentPackageRequest(created.Token), ApiFixture.Json);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        Assert.Contains("SolvixAgent-", response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName, StringComparison.Ordinal);
        Assert.EndsWith("Berlin-tills.zip", (response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName)!, StringComparison.Ordinal);

        using var zip = new ZipArchive(await response.Content.ReadAsStreamAsync(), ZipArchiveMode.Read);
        Assert.Equal(new[] { "README.txt", "Solvix.Agent.exe", "appsettings.json", "enrollment.json" }.Order(StringComparer.Ordinal), zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));

        // Byte-for-byte: a changed exe would lose its Authenticode signature.
        using (var exe = zip.GetEntry("Solvix.Agent.exe")!.Open())
        using (var copy = new MemoryStream())
        {
            await exe.CopyToAsync(copy);
            Assert.Equal(SolvixApiFactory.FakeAgentExe, copy.ToArray());
        }

        using var entry = zip.GetEntry(AgentEnrollmentFile.FileName)!.Open();
        var file = (await JsonSerializer.DeserializeAsync(entry, AgentJsonContext.Default.AgentEnrollmentFile))!;
        Assert.Equal(created.Token, file.EnrollmentToken);
        Assert.Equal(SolvixApiFactory.AgentPublicUrl, file.ServerUrl);
        Assert.Equal(new string('d', 64).ToUpperInvariant(), file.ServerCertificateSha256);
        Assert.Equal(created.EnrollmentToken.ExpiresAtUtc, file.TokenExpiresAtUtc!.Value, TimeSpan.FromSeconds(1));
        Assert.Equal(tenant.Name, file.WorkspaceName);
        Assert.Equal("Berlin store / Tills", file.Placement);

        using var readme = new StreamReader(zip.GetEntry("README.txt")!.Open());
        var text = await readme.ReadToEndAsync();
        Assert.Contains("Solvix.Agent.exe", text, StringComparison.Ordinal);
        Assert.DoesNotContain(created.Token, text, StringComparison.Ordinal); // the token appears only in enrollment.json
    }

    [Fact]
    public async Task The_downloaded_enrollment_file_enrolls_an_agent_with_the_placement_of_its_token()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        var branch = await HttpAssert.OkAsync<BranchDto>(await admin.PostAsJsonAsync("/api/v1/branches", new CreateBranchRequest("Paris store", "PAR", false, null, null, null, null), ApiFixture.Json), HttpStatusCode.Created);
        var created = await HttpAssert.OkAsync<CreatedEnrollmentTokenDto>(
            await admin.PostAsJsonAsync("/api/v1/enrollment-tokens", new CreateEnrollmentTokenRequest("Paris", 24, 1, null, branch.Id), ApiFixture.Json), HttpStatusCode.Created);

        var package = await (await admin.PostAsJsonAsync("/api/v1/enrollment-tokens/package", new AgentPackageRequest(created.Token), ApiFixture.Json)).Content.ReadAsByteArrayAsync();
        using var zip = new ZipArchive(new MemoryStream(package), ZipArchiveMode.Read);
        var directory = Path.Combine(Path.GetTempPath(), "solvix-pkg-" + Guid.NewGuid().ToString("N"));
        try
        {
            zip.ExtractToDirectory(directory);
            var settings = Solvix.Agent.Core.Enrollment.EnrollmentRequestSettings.TryLoad(Path.Combine(directory, AgentEnrollmentFile.FileName), DateTimeOffset.UtcNow, out var error);
            Assert.NotNull(settings);
            Assert.Null(error);
            Assert.Equal(created.Token, settings.Token);
            Assert.Equal(Uri.UriSchemeHttps, settings.ServerUrl.Scheme);
        }
        finally
        {
            Directory.Delete(directory, true);
        }

        // The token from the package enrolls a device in the Paris branch.
        var device = await fixture.EnrollAsync(created.Token);
        var details = await HttpAssert.OkAsync<Solvix.Modules.Devices.Application.Devices.DeviceDetailsDto>(await admin.GetAsync($"/api/v1/devices/{device.DeviceId}"));
        Assert.Equal("Paris store", details.Assignment.Branch!.Name);
    }

    [Fact]
    public async Task A_package_can_only_be_built_for_a_valid_token_of_the_callers_own_tenant()
    {
        var a = await fixture.RegisterTenantAsync();
        var b = await fixture.RegisterTenantAsync();
        using var clientA = fixture.CreateClient(a.Token);
        using var clientB = fixture.CreateClient(b.Token);
        var token = (await HttpAssert.OkAsync<CreatedEnrollmentTokenDto>(
            await clientA.PostAsJsonAsync("/api/v1/enrollment-tokens", new CreateEnrollmentTokenRequest("a", 24, 5, null), ApiFixture.Json), HttpStatusCode.Created)).Token;

        // Another tenant's admin cannot turn A's token into a package.
        await HttpAssert.ProblemAsync(await clientB.PostAsJsonAsync("/api/v1/enrollment-tokens/package", new AgentPackageRequest(token), ApiFixture.Json), HttpStatusCode.NotFound, "not_found");

        // A forged secret for a real token id, and garbage.
        Assert.True(Solvix.Modules.Devices.Security.EnrollmentTokenCodec.TryParse(token, out var tenantId, out var tokenId, out _));
        var (forged, _) = Solvix.Modules.Devices.Security.EnrollmentTokenCodec.Create(tenantId, tokenId);
        await HttpAssert.ProblemAsync(await clientA.PostAsJsonAsync("/api/v1/enrollment-tokens/package", new AgentPackageRequest(forged), ApiFixture.Json), HttpStatusCode.NotFound, "not_found");
        await HttpAssert.ProblemAsync(await clientA.PostAsJsonAsync("/api/v1/enrollment-tokens/package", new AgentPackageRequest("svxe_garbage"), ApiFixture.Json), HttpStatusCode.BadRequest, "validation_failed");
        await HttpAssert.ProblemAsync(await clientA.PostAsJsonAsync("/api/v1/enrollment-tokens/package", new AgentPackageRequest(""), ApiFixture.Json), HttpStatusCode.BadRequest, "validation_failed");

        // Unauthenticated, and agents' device certificates, are refused.
        using var anonymous = fixture.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/api/v1/enrollment-tokens/package", new AgentPackageRequest(token), ApiFixture.Json)).StatusCode);
        var device = await fixture.EnrollAsync(token);
        using var agentClient = fixture.CreateDeviceClient(device.Certificate);
        Assert.Equal(HttpStatusCode.Unauthorized, (await agentClient.PostAsJsonAsync("/api/v1/enrollment-tokens/package", new AgentPackageRequest(token), ApiFixture.Json)).StatusCode);
    }

    [Fact]
    public async Task Members_cannot_download_packages()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var owner = fixture.CreateClient(tenant.Token);
        var token = (await HttpAssert.OkAsync<CreatedEnrollmentTokenDto>(
            await owner.PostAsJsonAsync("/api/v1/enrollment-tokens", new CreateEnrollmentTokenRequest("x", 24, 1, null), ApiFixture.Json), HttpStatusCode.Created)).Token;
        var email = $"member-{Guid.NewGuid():N}@example.test";
        Assert.True((await owner.PostAsJsonAsync("/api/v1/tenant/members", new Solvix.Modules.Identity.Application.Members.AddMemberRequest(email, "Member", "Passw0rd!Secure", Solvix.SharedKernel.Identity.TenantRole.Member), ApiFixture.Json)).IsSuccessStatusCode);
        using var member = fixture.CreateClient((await fixture.LoginAsync(email, "Passw0rd!Secure", tenant.Slug)).AccessToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await member.PostAsJsonAsync("/api/v1/enrollment-tokens/package", new AgentPackageRequest(token), ApiFixture.Json)).StatusCode);
    }

    [Fact]
    public async Task Revoked_expired_and_used_up_tokens_cannot_be_packaged()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        async Task<CreatedEnrollmentTokenDto> NewToken(int uses) => await HttpAssert.OkAsync<CreatedEnrollmentTokenDto>(
            await admin.PostAsJsonAsync("/api/v1/enrollment-tokens", new CreateEnrollmentTokenRequest("t", 24, uses, null), ApiFixture.Json), HttpStatusCode.Created);

        var revoked = await NewToken(2);
        await admin.PostAsync($"/api/v1/enrollment-tokens/{revoked.EnrollmentToken.Id}/revoke", null);
        await HttpAssert.ProblemAsync(await admin.PostAsJsonAsync("/api/v1/enrollment-tokens/package", new AgentPackageRequest(revoked.Token), ApiFixture.Json), HttpStatusCode.Conflict, "enrollment_token_not_active");

        var exhausted = await NewToken(1);
        await fixture.EnrollAsync(exhausted.Token);
        await HttpAssert.ProblemAsync(await admin.PostAsJsonAsync("/api/v1/enrollment-tokens/package", new AgentPackageRequest(exhausted.Token), ApiFixture.Json), HttpStatusCode.Conflict, "enrollment_token_not_active");

        var expired = await NewToken(2);
        await fixture.ExecutePlatformSqlAsync("UPDATE enrollment_tokens SET expires_at_utc = now() - interval '1 minute' WHERE id = @id", ("id", expired.EnrollmentToken.Id));
        await HttpAssert.ProblemAsync(await admin.PostAsJsonAsync("/api/v1/enrollment-tokens/package", new AgentPackageRequest(expired.Token), ApiFixture.Json), HttpStatusCode.Conflict, "enrollment_token_not_active");
    }

    [Fact]
    public async Task A_server_without_a_published_agent_reports_it_clearly()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        var token = (await HttpAssert.OkAsync<CreatedEnrollmentTokenDto>(
            await admin.PostAsJsonAsync("/api/v1/enrollment-tokens", new CreateEnrollmentTokenRequest("t", 24, 1, null), ApiFixture.Json), HttpStatusCode.Created)).Token;

        var exe = Path.Combine(SolvixApiFactory.AgentPackageDirectory, "Solvix.Agent.exe");
        var moved = exe + ".moved";
        File.Move(exe, moved);
        try
        {
            await HttpAssert.ProblemAsync(await admin.PostAsJsonAsync("/api/v1/enrollment-tokens/package", new AgentPackageRequest(token), ApiFixture.Json), HttpStatusCode.Conflict, "agent_package_unavailable");
        }
        finally
        {
            File.Move(moved, exe);
        }
    }
}
