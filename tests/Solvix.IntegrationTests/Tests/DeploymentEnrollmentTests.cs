namespace Solvix.IntegrationTests.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Npgsql;
using Solvix.Agent.Contracts;
using Solvix.IntegrationTests.Fixtures;
using Solvix.Modules.Devices.Application.Devices;
using Solvix.Modules.Devices.Application.Enrollment;
using Solvix.Modules.Devices.Application.Groups;
using Solvix.Modules.Devices.Domain;
using Solvix.Modules.Devices.Security;
using Solvix.Modules.Organization.Application.Branches;
using Solvix.Modules.Organization.Application.Departments;
using Solvix.SharedKernel.Authorization;
using Solvix.SharedKernel.Results;

/// <summary>
/// ManageEngine-style enrollment: a generic installer carrying a deployment profile code, whose computers wait in
/// an approval queue until an administrator accepts them.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class DeploymentEnrollmentTests(ApiFixture fixture)
{
    private sealed record Submitted(HttpResponseMessage Response, ECDsa Key, DeviceIdentityInfo Identity);

    [Fact]
    public async Task A_workspace_always_has_a_default_profile_to_download()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        var profiles = await HttpAssert.OkAsync<List<DeploymentProfileDto>>(await admin.GetAsync("/api/v1/deployment-profiles"));
        var profile = Assert.Single(profiles);
        Assert.Equal("Default", profile.Name);
        Assert.True(profile.IsEnabled);
        Assert.Equal(0, profile.PendingCount);

        // Listing again does not create a second one.
        Assert.Single(await HttpAssert.OkAsync<List<DeploymentProfileDto>>(await admin.GetAsync("/api/v1/deployment-profiles")));
    }

    [Fact]
    public async Task Profiles_can_be_created_updated_validated_and_deleted()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        var berlin = await CreateBranchAsync(admin, "Berlin store", "BER");
        var tills = await CreateDepartmentAsync(admin, berlin.Id, "Tills", "TILL");
        var group = await HttpAssert.OkAsync<DeviceGroupDto>(
            await admin.PostAsJsonAsync("/api/v1/device-groups", new CreateDeviceGroupRequest("POS terminals", null), ApiFixture.Json), HttpStatusCode.Created);

        var created = await HttpAssert.OkAsync<DeploymentProfileDto>(
            await admin.PostAsJsonAsync("/api/v1/deployment-profiles", new SaveDeploymentProfileRequest("Berlin tills", "POS", null, tills.Id, group.Id), ApiFixture.Json),
            HttpStatusCode.Created);
        Assert.Equal(berlin.Id, created.BranchId); // implied by the department
        Assert.Equal("Berlin store", created.BranchName);
        Assert.Equal("Tills", created.DepartmentName);
        Assert.Equal("POS terminals", created.DeviceGroupName);

        await HttpAssert.ProblemAsync(
            await admin.PostAsJsonAsync("/api/v1/deployment-profiles", new SaveDeploymentProfileRequest("BERLIN TILLS", null, null, null, null), ApiFixture.Json),
            HttpStatusCode.Conflict);
        var paris = await CreateBranchAsync(admin, "Paris store", "PAR");
        await HttpAssert.ProblemAsync(
            await admin.PostAsJsonAsync("/api/v1/deployment-profiles", new SaveDeploymentProfileRequest("Mismatch", null, paris.Id, tills.Id, null), ApiFixture.Json),
            HttpStatusCode.BadRequest, "validation_failed");
        await HttpAssert.ProblemAsync(
            await admin.PostAsJsonAsync("/api/v1/deployment-profiles", new SaveDeploymentProfileRequest("", null, null, null, null), ApiFixture.Json),
            HttpStatusCode.BadRequest, "validation_failed");

        var updated = await HttpAssert.OkAsync<DeploymentProfileDto>(
            await admin.PutAsJsonAsync($"/api/v1/deployment-profiles/{created.Id}", new SaveDeploymentProfileRequest("Berlin POS", null, paris.Id, null, null), ApiFixture.Json));
        Assert.Equal("Paris store", updated.BranchName);
        Assert.Null(updated.DepartmentId);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/deployment-profiles/{created.Id}")).StatusCode);
        await HttpAssert.ProblemAsync(await admin.DeleteAsync($"/api/v1/deployment-profiles/{created.Id}"), HttpStatusCode.NotFound, "not_found");
    }

    [Fact]
    public async Task The_installer_is_one_exe_carrying_the_profile_code_and_nothing_secret()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        var profile = await DefaultProfileAsync(admin);

        var response = await admin.GetAsync($"/api/v1/deployment-profiles/{profile.Id}/installer");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/vnd.microsoft.portable-executable", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        var name = response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName;
        Assert.EndsWith(".exe", name, StringComparison.Ordinal);

        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.True(bytes.AsSpan(0, SolvixApiFactory.FakeAgentExe.Length).SequenceEqual(SolvixApiFactory.FakeAgentExe), "the program bytes must be unchanged");
        using var stream = new MemoryStream(bytes);
        Assert.True(EnrollmentTrailer.TryRead(stream, out var file, out var clean));
        Assert.Equal(SolvixApiFactory.FakeAgentExe.Length, clean);
        Assert.StartsWith(DeploymentTokens.ProfilePrefix, file!.EnrollmentToken, StringComparison.Ordinal);
        Assert.Equal(SolvixApiFactory.AgentPublicUrl, file.ServerUrl);
        Assert.Equal(tenant.Name, file.WorkspaceName);
        Assert.Null(file.TokenExpiresAtUtc); // a profile code does not expire; approval is the control

        // The code is the same on every download (the secret is stored), until it is rotated.
        var again = await (await admin.GetAsync($"/api/v1/deployment-profiles/{profile.Id}/installer")).Content.ReadAsByteArrayAsync();
        using var stream2 = new MemoryStream(again);
        Assert.True(EnrollmentTrailer.TryRead(stream2, out var file2, out _));
        Assert.Equal(file.EnrollmentToken, file2!.EnrollmentToken);

        // The list API never exposes the code or its secret.
        var raw = await admin.GetStringAsync("/api/v1/deployment-profiles");
        Assert.DoesNotContain(file.EnrollmentToken, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("codeSecret", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_computer_that_installs_the_generic_installer_waits_until_an_administrator_approves_it()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        var berlin = await CreateBranchAsync(admin, "Berlin store", "BER");
        var tills = await CreateDepartmentAsync(admin, berlin.Id, "Tills", "TILL");
        var group = await HttpAssert.OkAsync<DeviceGroupDto>(
            await admin.PostAsJsonAsync("/api/v1/device-groups", new CreateDeviceGroupRequest("POS terminals", null), ApiFixture.Json), HttpStatusCode.Created);
        var profile = await HttpAssert.OkAsync<DeploymentProfileDto>(
            await admin.PostAsJsonAsync("/api/v1/deployment-profiles", new SaveDeploymentProfileRequest("Berlin tills", null, null, tills.Id, group.Id), ApiFixture.Json),
            HttpStatusCode.Created);
        var code = await CodeAsync(admin, profile.Id);

        // 1. The agent submits; nothing is issued.
        var submitted = await SubmitAsync(code);
        Assert.Equal(HttpStatusCode.Accepted, submitted.Response.StatusCode);
        var pending = (await submitted.Response.Content.ReadFromJsonAsync<EnrollmentPendingResponse>(ApiFixture.Json))!;
        Assert.StartsWith(DeploymentTokens.PollPrefix, pending.PollToken, StringComparison.Ordinal);
        Assert.Equal(PollStatus.Pending, (await PollAsync(pending.PollToken)).Status);

        // 2. It is in the admin's queue with enough facts to decide, and no device exists yet.
        var queue = await HttpAssert.OkAsync<PagedResult<PendingEnrollmentDto>>(await admin.GetAsync("/api/v1/pending-enrollments"));
        var item = Assert.Single(queue.Items);
        Assert.Equal(submitted.Identity.Hostname, item.Hostname);
        Assert.Equal("Berlin tills", item.ProfileName);
        Assert.Equal("Berlin store", item.BranchName);
        Assert.Equal("Tills", item.DepartmentName);
        Assert.Equal("POS terminals", item.DeviceGroupName);
        Assert.Equal(1, (await HttpAssert.OkAsync<PendingEnrollmentCountDto>(await admin.GetAsync("/api/v1/pending-enrollments/count"))).Pending);
        Assert.Equal(0, (await HttpAssert.OkAsync<PagedResult<DeviceSummaryDto>>(await admin.GetAsync("/api/v1/devices"))).TotalCount);

        // 3. A pending computer has no credential: it cannot talk to the agent API.
        using (var anonymous = fixture.CreateDeviceClient(null))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.HeartbeatAsync()).StatusCode);
        }

        // 4. Approve: the device is created with the profile's placement.
        var approved = await HttpAssert.OkAsync<PendingEnrollmentDto>(
            await admin.PostAsJsonAsync($"/api/v1/pending-enrollments/{item.Id}/approve", new DecideEnrollmentRequest("Till 3"), ApiFixture.Json));
        Assert.Equal(PendingEnrollmentStatus.Approved, approved.Status);
        Assert.NotNull(approved.DeviceId);
        var device = await HttpAssert.OkAsync<DeviceDetailsDto>(await admin.GetAsync($"/api/v1/devices/{approved.DeviceId}"));
        Assert.Equal("Berlin store", device.Assignment.Branch!.Name);
        Assert.Equal("Tills", device.Assignment.Department!.Name);
        Assert.Equal("POS terminals", Assert.Single(device.Groups).Name);
        Assert.Equal(0, (await HttpAssert.OkAsync<PendingEnrollmentCountDto>(await admin.GetAsync("/api/v1/pending-enrollments/count"))).Pending);

        // 5. The agent's next poll returns the certificate; it chains to the device CA and works.
        var poll = await PollAsync(pending.PollToken);
        Assert.Equal(PollStatus.Approved, poll.Status);
        var enrollment = poll.Enrollment!;
        Assert.Equal(approved.DeviceId, enrollment.DeviceId);
        Assert.Equal(tenant.TenantId, enrollment.TenantId);
        var ca = X509Certificate2.CreateFromPem(Assert.Single(enrollment.CaCertificatesPem));
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        using var certificate = X509Certificate2.CreateFromPem(enrollment.CertificatePem).CopyWithPrivateKey(submitted.Key);
        Assert.True(chain.Build(certificate));
        Assert.True(DeviceCertificateIdentity.TryRead(certificate, out var tenantId, out var deviceId));
        Assert.Equal((tenant.TenantId, approved.DeviceId!.Value), (tenantId, deviceId));
        using var agent = fixture.CreateDeviceClient(certificate);
        Assert.Equal(HttpStatusCode.OK, (await agent.HeartbeatAsync()).StatusCode);

        // 6. A response lost on the way can be fetched again with the same poll token.
        Assert.Equal(PollStatus.Approved, (await PollAsync(pending.PollToken)).Status);

        // 7. The decision is audited, with who and what.
        Assert.True(await AuditCountAsync(tenant.TenantId, "enrollment.approved") >= 1);
        Assert.True(await AuditCountAsync(tenant.TenantId, "deployment_profile.created") >= 1);
    }

    [Fact]
    public async Task A_rejected_computer_is_told_so_and_never_gets_a_certificate()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        var code = await CodeAsync(admin, (await DefaultProfileAsync(admin)).Id);
        var pending = await SubmitPendingAsync(code);

        var item = Assert.Single((await HttpAssert.OkAsync<PagedResult<PendingEnrollmentDto>>(await admin.GetAsync("/api/v1/pending-enrollments"))).Items);
        var rejected = await HttpAssert.OkAsync<PendingEnrollmentDto>(
            await admin.PostAsJsonAsync($"/api/v1/pending-enrollments/{item.Id}/reject", new DecideEnrollmentRequest("Not one of ours"), ApiFixture.Json));
        Assert.Equal(PendingEnrollmentStatus.Rejected, rejected.Status);

        var poll = await PollAsync(pending.PollToken);
        Assert.Equal(PollStatus.Rejected, poll.Status);
        Assert.Equal("Not one of ours", poll.Message);
        Assert.Null(poll.Enrollment);
        Assert.Equal(0, (await HttpAssert.OkAsync<PagedResult<DeviceSummaryDto>>(await admin.GetAsync("/api/v1/devices"))).TotalCount);

        // A decided request cannot be decided again.
        await HttpAssert.ProblemAsync(await admin.PostAsJsonAsync($"/api/v1/pending-enrollments/{item.Id}/approve", new DecideEnrollmentRequest(null), ApiFixture.Json), HttpStatusCode.Conflict, "enrollment_request_decided");
    }

    [Fact]
    public async Task Forged_stale_disabled_and_rotated_codes_are_refused()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        var profile = await DefaultProfileAsync(admin);
        var code = await CodeAsync(admin, profile.Id);
        Assert.True(TenantScopedToken.TryParse(DeploymentTokens.ProfilePrefix, code, out var tenantId, out var profileId, out _));

        // Right profile, guessed secret.
        var (forged, _) = TenantScopedToken.Create(DeploymentTokens.ProfilePrefix, tenantId, profileId);
        await HttpAssert.ProblemAsync((await SubmitAsync(forged)).Response, HttpStatusCode.Unauthorized, "enrollment_code_invalid");
        await HttpAssert.ProblemAsync((await SubmitAsync("svxp_garbage")).Response, HttpStatusCode.Unauthorized, "enrollment_code_invalid");

        // A one-time token or a poll token is not a profile code and vice versa.
        var poll = await SubmitPendingAsync(code);
        await HttpAssert.ProblemAsync((await SubmitAsync(poll.PollToken)).Response, HttpStatusCode.Unauthorized);
        using var client = fixture.CreateClient();
        await HttpAssert.ProblemAsync(await client.PostAsJsonAsync("/" + AgentRoutes.EnrollPoll, new EnrollmentPollRequest { PollToken = code }, ApiFixture.Json), HttpStatusCode.Unauthorized, "enrollment_code_invalid");

        // Disabled profile: refused; enabled again: accepted.
        Assert.False((await HttpAssert.OkAsync<DeploymentProfileDto>(await admin.PostAsync($"/api/v1/deployment-profiles/{profile.Id}/disable", null))).IsEnabled);
        await HttpAssert.ProblemAsync((await SubmitAsync(code)).Response, HttpStatusCode.Unauthorized, "enrollment_code_invalid");
        await HttpAssert.ProblemAsync(await admin.GetAsync($"/api/v1/deployment-profiles/{profile.Id}/installer"), HttpStatusCode.Conflict, "deployment_profile_disabled");
        await admin.PostAsync($"/api/v1/deployment-profiles/{profile.Id}/enable", null);
        Assert.Equal(HttpStatusCode.Accepted, (await SubmitAsync(code)).Response.StatusCode);

        // Rotation invalidates installers downloaded before; a new download works.
        var rotated = await HttpAssert.OkAsync<DeploymentProfileDto>(await admin.PostAsync($"/api/v1/deployment-profiles/{profile.Id}/rotate-code", null));
        Assert.Equal(2, rotated.CodeVersion);
        await HttpAssert.ProblemAsync((await SubmitAsync(code)).Response, HttpStatusCode.Unauthorized, "enrollment_code_invalid");
        var fresh = await CodeAsync(admin, profile.Id);
        Assert.NotEqual(code, fresh);
        Assert.Equal(HttpStatusCode.Accepted, (await SubmitAsync(fresh)).Response.StatusCode);

        // Deleting the profile discards its waiting requests.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/deployment-profiles/{profile.Id}")).StatusCode);
        Assert.Equal(0, (await HttpAssert.OkAsync<PendingEnrollmentCountDto>(await admin.GetAsync("/api/v1/pending-enrollments/count"))).Pending);
        await HttpAssert.ProblemAsync((await SubmitAsync(fresh)).Response, HttpStatusCode.Unauthorized, "enrollment_code_invalid");
    }

    [Fact]
    public async Task Poll_tokens_are_bound_to_the_request_and_weak_keys_are_rejected()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        var code = await CodeAsync(admin, (await DefaultProfileAsync(admin)).Id);
        var pending = await SubmitPendingAsync(code);
        Assert.True(TenantScopedToken.TryParse(DeploymentTokens.PollPrefix, pending.PollToken, out var tenantId, out var pendingId, out _));

        var (wrongSecret, _) = TenantScopedToken.Create(DeploymentTokens.PollPrefix, tenantId, pendingId);
        using var client = fixture.CreateClient();
        await HttpAssert.ProblemAsync(await client.PostAsJsonAsync("/" + AgentRoutes.EnrollPoll, new EnrollmentPollRequest { PollToken = wrongSecret }, ApiFixture.Json), HttpStatusCode.Unauthorized, "enrollment_code_invalid");
        await HttpAssert.ProblemAsync(await client.PostAsJsonAsync("/" + AgentRoutes.EnrollPoll, new EnrollmentPollRequest { PollToken = "svxw_x" }, ApiFixture.Json), HttpStatusCode.Unauthorized, "enrollment_code_invalid");
        await HttpAssert.ProblemAsync(await client.PostAsJsonAsync("/" + AgentRoutes.EnrollPoll, new EnrollmentPollRequest { PollToken = "" }, ApiFixture.Json), HttpStatusCode.BadRequest, "validation_failed");

        // A weak RSA key never enters the queue.
        using var weak = RSA.Create(1024);
        var csr = new CertificateRequest("CN=x", weak, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSigningRequestPem();
        await HttpAssert.ProblemAsync(
            await client.PostAsJsonAsync("/" + AgentRoutes.Enroll, new EnrollmentRequest { EnrollmentToken = code, CertificateSigningRequestPem = csr, Device = DeviceTestKit.Identity() }, ApiFixture.Json),
            HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal(1, (await HttpAssert.OkAsync<PendingEnrollmentCountDto>(await admin.GetAsync("/api/v1/pending-enrollments/count"))).Pending);
    }

    [Fact]
    public async Task Another_workspace_cannot_see_decide_or_use_the_requests_and_profiles_of_this_one()
    {
        var a = await fixture.RegisterTenantAsync();
        var b = await fixture.RegisterTenantAsync();
        using var adminA = fixture.CreateClient(a.Token);
        using var adminB = fixture.CreateClient(b.Token);
        var profileA = await DefaultProfileAsync(adminA);
        var codeA = await CodeAsync(adminA, profileA.Id);
        await SubmitPendingAsync(codeA);
        var request = Assert.Single((await HttpAssert.OkAsync<PagedResult<PendingEnrollmentDto>>(await adminA.GetAsync("/api/v1/pending-enrollments"))).Items);

        Assert.Empty((await HttpAssert.OkAsync<PagedResult<PendingEnrollmentDto>>(await adminB.GetAsync("/api/v1/pending-enrollments"))).Items);
        await HttpAssert.ProblemAsync(await adminB.PostAsJsonAsync($"/api/v1/pending-enrollments/{request.Id}/approve", new DecideEnrollmentRequest(null), ApiFixture.Json), HttpStatusCode.NotFound, "not_found");
        await HttpAssert.ProblemAsync(await adminB.PostAsJsonAsync($"/api/v1/pending-enrollments/{request.Id}/reject", new DecideEnrollmentRequest(null), ApiFixture.Json), HttpStatusCode.NotFound, "not_found");
        await HttpAssert.ProblemAsync(await adminB.GetAsync($"/api/v1/deployment-profiles/{profileA.Id}/installer"), HttpStatusCode.NotFound, "not_found");
        await HttpAssert.ProblemAsync(await adminB.PostAsync($"/api/v1/deployment-profiles/{profileA.Id}/rotate-code", null), HttpStatusCode.NotFound, "not_found");
        await HttpAssert.ProblemAsync(await adminB.DeleteAsync($"/api/v1/deployment-profiles/{profileA.Id}"), HttpStatusCode.NotFound, "not_found");

        // A's code re-targeted at B finds no profile.
        Assert.True(TenantScopedToken.TryParse(DeploymentTokens.ProfilePrefix, codeA, out _, out var profileId, out var secret));
        var (retargeted, _) = TenantScopedToken.Create(DeploymentTokens.ProfilePrefix, b.TenantId, profileId, secret);
        await HttpAssert.ProblemAsync((await SubmitAsync(retargeted)).Response, HttpStatusCode.Unauthorized, "enrollment_code_invalid");
        Assert.Equal(1, (await HttpAssert.OkAsync<PendingEnrollmentCountDto>(await adminA.GetAsync("/api/v1/pending-enrollments/count"))).Pending);
    }

    [Fact]
    public async Task Only_people_who_may_enroll_devices_can_manage_profiles_and_the_queue()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        var profile = await DefaultProfileAsync(admin);
        var code = await CodeAsync(admin, profile.Id);
        await SubmitPendingAsync(code);
        var request = Assert.Single((await HttpAssert.OkAsync<PagedResult<PendingEnrollmentDto>>(await admin.GetAsync("/api/v1/pending-enrollments"))).Items);

        var viewer = await fixture.AddMemberWithPermissionsAsync(tenant, Permissions.Devices.View);
        using var viewerClient = fixture.CreateClient(viewer.Token);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewerClient.GetAsync("/api/v1/deployment-profiles")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewerClient.GetAsync($"/api/v1/deployment-profiles/{profile.Id}/installer")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewerClient.PostAsJsonAsync("/api/v1/deployment-profiles", new SaveDeploymentProfileRequest("x", null, null, null, null), ApiFixture.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewerClient.GetAsync("/api/v1/pending-enrollments")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewerClient.PostAsJsonAsync($"/api/v1/pending-enrollments/{request.Id}/approve", new DecideEnrollmentRequest(null), ApiFixture.Json)).StatusCode);

        var enroller = await fixture.AddMemberWithPermissionsAsync(tenant, Permissions.Devices.Enroll);
        using var enrollerClient = fixture.CreateClient(enroller.Token);
        Assert.Equal(HttpStatusCode.OK, (await enrollerClient.GetAsync("/api/v1/pending-enrollments")).StatusCode);

        using var anonymous = fixture.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/pending-enrollments")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/api/v1/deployment-profiles/{profile.Id}/installer")).StatusCode);
    }

    [Fact]
    public async Task Reinstalling_a_waiting_computer_replaces_its_request_and_approving_a_known_machine_re_enrolls_it()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        var berlin = await CreateBranchAsync(admin, "Berlin", "BER");
        var profile = await HttpAssert.OkAsync<DeploymentProfileDto>(
            await admin.PostAsJsonAsync("/api/v1/deployment-profiles", new SaveDeploymentProfileRequest("Berlin", null, berlin.Id, null, null), ApiFixture.Json), HttpStatusCode.Created);
        var code = await CodeAsync(admin, profile.Id);
        var identity = DeviceTestKit.Identity();

        // The same computer asks twice (reinstall): one request stays.
        await SubmitPendingAsync(code, identity);
        await SubmitPendingAsync(code, identity);
        var queue = await HttpAssert.OkAsync<PagedResult<PendingEnrollmentDto>>(await admin.GetAsync("/api/v1/pending-enrollments"));
        var request = Assert.Single(queue.Items);
        Assert.Null(request.ExistingDeviceId);
        await admin.PostAsJsonAsync($"/api/v1/pending-enrollments/{request.Id}/approve", new DecideEnrollmentRequest(null), ApiFixture.Json);

        // The machine is now known. Asking again is flagged, and approving re-enrolls the same record.
        var again = await SubmitPendingAsync(code, identity);
        var second = Assert.Single((await HttpAssert.OkAsync<PagedResult<PendingEnrollmentDto>>(await admin.GetAsync("/api/v1/pending-enrollments"))).Items);
        Assert.NotNull(second.ExistingDeviceId);
        Assert.NotNull(second.ExistingDeviceHostname);
        var approved = await HttpAssert.OkAsync<PendingEnrollmentDto>(await admin.PostAsJsonAsync($"/api/v1/pending-enrollments/{second.Id}/approve", new DecideEnrollmentRequest(null), ApiFixture.Json));
        Assert.Equal(second.ExistingDeviceId, approved.DeviceId);
        Assert.Equal(1, (await HttpAssert.OkAsync<PagedResult<DeviceSummaryDto>>(await admin.GetAsync("/api/v1/devices"))).TotalCount);
        var details = await HttpAssert.OkAsync<DeviceDetailsDto>(await admin.GetAsync($"/api/v1/devices/{approved.DeviceId}"));
        Assert.Equal(2, details.EnrollmentCount);
        Assert.Contains(details.Credentials, c => c.RevocationReason == CredentialRevocationReason.ReEnrolled);
        Assert.Equal(PollStatus.Approved, (await PollAsync(again.PollToken)).Status);
    }

    [Fact]
    public async Task Requests_expire_and_an_expired_request_cannot_be_approved()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        var pending = await SubmitPendingAsync(await CodeAsync(admin, (await DefaultProfileAsync(admin)).Id));
        var request = Assert.Single((await HttpAssert.OkAsync<PagedResult<PendingEnrollmentDto>>(await admin.GetAsync("/api/v1/pending-enrollments"))).Items);

        await fixture.ExecutePlatformSqlAsync("UPDATE pending_enrollments SET expires_at_utc = now() - interval '1 minute' WHERE id = @id", ("id", request.Id));
        Assert.Equal(PollStatus.Expired, (await PollAsync(pending.PollToken)).Status);
        Assert.Equal(0, (await HttpAssert.OkAsync<PendingEnrollmentCountDto>(await admin.GetAsync("/api/v1/pending-enrollments/count"))).Pending);
        await HttpAssert.ProblemAsync(
            await admin.PostAsJsonAsync($"/api/v1/pending-enrollments/{request.Id}/approve", new DecideEnrollmentRequest(null), ApiFixture.Json), HttpStatusCode.Conflict, "enrollment_request_expired");
        Assert.Equal(0, (await HttpAssert.OkAsync<PagedResult<DeviceSummaryDto>>(await admin.GetAsync("/api/v1/devices"))).TotalCount);
    }

    [Fact]
    public async Task Two_administrators_approving_at_once_issue_exactly_one_certificate()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin1 = fixture.CreateClient(tenant.Token);
        using var admin2 = fixture.CreateClient(tenant.Token);
        await SubmitPendingAsync(await CodeAsync(admin1, (await DefaultProfileAsync(admin1)).Id));
        var request = Assert.Single((await HttpAssert.OkAsync<PagedResult<PendingEnrollmentDto>>(await admin1.GetAsync("/api/v1/pending-enrollments"))).Items);

        var results = await Task.WhenAll(
            admin1.PostAsJsonAsync($"/api/v1/pending-enrollments/{request.Id}/approve", new DecideEnrollmentRequest(null), ApiFixture.Json),
            admin2.PostAsJsonAsync($"/api/v1/pending-enrollments/{request.Id}/approve", new DecideEnrollmentRequest(null), ApiFixture.Json));
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Single(results, r => r.StatusCode == HttpStatusCode.Conflict);

        var devices = await HttpAssert.OkAsync<PagedResult<DeviceSummaryDto>>(await admin1.GetAsync("/api/v1/devices"));
        var device = Assert.Single(devices.Items);
        var details = await HttpAssert.OkAsync<DeviceDetailsDto>(await admin1.GetAsync($"/api/v1/devices/{device.Id}"));
        Assert.Single(details.Credentials);
    }

    [Fact]
    public async Task Bulk_decisions_report_what_failed_and_still_process_the_rest()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        var code = await CodeAsync(admin, (await DefaultProfileAsync(admin)).Id);
        for (var i = 0; i < 4; i++)
        {
            await SubmitPendingAsync(code);
        }

        var items = (await HttpAssert.OkAsync<PagedResult<PendingEnrollmentDto>>(await admin.GetAsync("/api/v1/pending-enrollments"))).Items;
        Assert.Equal(4, items.Count);
        await fixture.ExecutePlatformSqlAsync("UPDATE pending_enrollments SET expires_at_utc = now() - interval '1 minute' WHERE id = @id", ("id", items[0].Id));

        var approve = await HttpAssert.OkAsync<BulkDecisionResultDto>(
            await admin.PostAsJsonAsync("/api/v1/pending-enrollments/bulk/approve", new BulkDecideEnrollmentsRequest([items[0].Id, items[1].Id, items[2].Id, Guid.NewGuid()], "batch"), ApiFixture.Json));
        Assert.Equal(2, approve.Succeeded);
        Assert.Equal(2, approve.Failed.Count);
        Assert.Contains(approve.Failed, f => f.Id == items[0].Id && f.Code == "enrollment_request_expired");
        Assert.Contains(approve.Failed, f => f.Code == "not_found");

        var reject = await HttpAssert.OkAsync<BulkDecisionResultDto>(
            await admin.PostAsJsonAsync("/api/v1/pending-enrollments/bulk/reject", new BulkDecideEnrollmentsRequest([items[3].Id], null), ApiFixture.Json));
        Assert.Equal(1, reject.Succeeded);
        Assert.Equal(2, (await HttpAssert.OkAsync<PagedResult<DeviceSummaryDto>>(await admin.GetAsync("/api/v1/devices"))).TotalCount);

        await HttpAssert.ProblemAsync(
            await admin.PostAsJsonAsync("/api/v1/pending-enrollments/bulk/approve", new BulkDecideEnrollmentsRequest([], null), ApiFixture.Json), HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task A_leaked_code_cannot_flood_the_queue_beyond_the_per_workspace_limit()
    {
        const string limit = "Solvix__Devices__Enrollment__MaxPendingPerTenant";
        var tenant = await fixture.RegisterTenantAsync();
        using var admin = fixture.CreateClient(tenant.Token);
        var code = await CodeAsync(admin, (await DefaultProfileAsync(admin)).Id);

        // A second host of the same application with a queue limit of 2 (the code and database are shared).
        await using var limited = new SolvixApiFactory(fixture.Database, fixture.RedisConnection, $"solvix-test-{Guid.NewGuid():N}:", null, new Dictionary<string, string> { [limit] = "2" });
        limited.StartServer();
        Environment.SetEnvironmentVariable(limit, null);

        using var client = limited.CreateClient();
        async Task<HttpResponseMessage> Submit()
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            return await client.PostAsJsonAsync(
                "/" + AgentRoutes.Enroll,
                new EnrollmentRequest { EnrollmentToken = code, CertificateSigningRequestPem = DeviceTestKit.CreateCsr(key), Device = DeviceTestKit.Identity() },
                ApiFixture.Json);
        }

        Assert.Equal(HttpStatusCode.Accepted, (await Submit()).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await Submit()).StatusCode);
        await HttpAssert.ProblemAsync(await Submit(), HttpStatusCode.Conflict, "enrollment_queue_full");

        // Deciding one frees a slot.
        var item = (await HttpAssert.OkAsync<PagedResult<PendingEnrollmentDto>>(await admin.GetAsync("/api/v1/pending-enrollments"))).Items[0];
        await admin.PostAsJsonAsync($"/api/v1/pending-enrollments/{item.Id}/reject", new DecideEnrollmentRequest(null), ApiFixture.Json);
        Assert.Equal(HttpStatusCode.Accepted, (await Submit()).StatusCode);
    }

    [Fact]
    public async Task Existing_one_time_token_enrollment_is_unchanged()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var device = await fixture.EnrollAsync(await fixture.CreateTokenAsync(tenant));
        using var agent = fixture.CreateDeviceClient(device.Certificate);
        Assert.Equal(HttpStatusCode.OK, (await agent.HeartbeatAsync()).StatusCode);
    }

    // ----- helpers -----------------------------------------------------------------------------------------------

    private static async Task<DeploymentProfileDto> DefaultProfileAsync(HttpClient admin) =>
        (await HttpAssert.OkAsync<List<DeploymentProfileDto>>(await admin.GetAsync("/api/v1/deployment-profiles"))).Single(p => p.Name == "Default");

    /// <summary>The code an administrator's downloaded installer carries.</summary>
    private static async Task<string> CodeAsync(HttpClient admin, Guid profileId)
    {
        var response = await admin.GetAsync($"/api/v1/deployment-profiles/{profileId}/installer");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var stream = new MemoryStream(await response.Content.ReadAsByteArrayAsync());
        Assert.True(EnrollmentTrailer.TryRead(stream, out var file, out _));
        return file!.EnrollmentToken;
    }

    private async Task<Submitted> SubmitAsync(string code, DeviceIdentityInfo? identity = null)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        identity ??= DeviceTestKit.Identity();
        using var client = fixture.CreateClient();
        var response = await client.PostAsJsonAsync(
            "/" + AgentRoutes.Enroll,
            new EnrollmentRequest { EnrollmentToken = code, CertificateSigningRequestPem = DeviceTestKit.CreateCsr(key), Device = identity },
            ApiFixture.Json);
        return new Submitted(response, key, identity);
    }

    private async Task<EnrollmentPendingResponse> SubmitPendingAsync(string code, DeviceIdentityInfo? identity = null)
    {
        var submitted = await SubmitAsync(code, identity);
        Assert.Equal(HttpStatusCode.Accepted, submitted.Response.StatusCode);
        return (await submitted.Response.Content.ReadFromJsonAsync<EnrollmentPendingResponse>(ApiFixture.Json))!;
    }

    private async Task<EnrollmentPollResponse> PollAsync(string pollToken)
    {
        using var client = fixture.CreateClient();
        return await HttpAssert.OkAsync<EnrollmentPollResponse>(
            await client.PostAsJsonAsync("/" + AgentRoutes.EnrollPoll, new EnrollmentPollRequest { PollToken = pollToken }, ApiFixture.Json));
    }

    private async Task<long> AuditCountAsync(Guid tenantId, string action)
    {
        await using var connection = new NpgsqlConnection(fixture.Database.MigratorConnectionString);
        await connection.OpenAsync();
        await using (var bypass = new NpgsqlCommand("SELECT set_config('app.rls_bypass', 'on', false)", connection))
        {
            await bypass.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand("SELECT count(*) FROM audit_entries WHERE tenant_id = @t AND action = @a", connection);
        command.Parameters.AddWithValue("t", tenantId);
        command.Parameters.AddWithValue("a", action);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<BranchDto> CreateBranchAsync(HttpClient client, string name, string code) =>
        await HttpAssert.OkAsync<BranchDto>(
            await client.PostAsJsonAsync("/api/v1/branches", new CreateBranchRequest(name, code, false, null, null, null, null), ApiFixture.Json), HttpStatusCode.Created);

    private static async Task<DepartmentDto> CreateDepartmentAsync(HttpClient client, Guid branchId, string name, string code) =>
        await HttpAssert.OkAsync<DepartmentDto>(
            await client.PostAsJsonAsync("/api/v1/departments", new CreateDepartmentRequest(branchId, null, name, code, null), ApiFixture.Json), HttpStatusCode.Created);

    private static class PollStatus
    {
        public const EnrollmentPollStatus Pending = EnrollmentPollStatus.Pending;
        public const EnrollmentPollStatus Approved = EnrollmentPollStatus.Approved;
        public const EnrollmentPollStatus Rejected = EnrollmentPollStatus.Rejected;
        public const EnrollmentPollStatus Expired = EnrollmentPollStatus.Expired;
    }
}
