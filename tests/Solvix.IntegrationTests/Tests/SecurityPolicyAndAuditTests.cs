namespace Solvix.IntegrationTests.Tests;

using System.Net;
using System.Net.Http.Json;
using Solvix.IntegrationTests.Fixtures;
using Solvix.Modules.Identity.Application.Audit;
using Solvix.Modules.Identity.Application.Roles;
using Solvix.Modules.Identity.Application.Security;
using Solvix.Modules.Identity.Application.Users;
using Solvix.Modules.Identity.Domain;
using Solvix.SharedKernel.Authorization;
using Solvix.SharedKernel.Results;

[Collection(ApiCollection.Name)]
public sealed class SecurityPolicyAndAuditTests(ApiFixture fixture)
{
    [Fact]
    public async Task Policy_starts_at_the_platform_baseline_and_can_be_tightened_but_not_loosened()
    {
        var t = await fixture.RegisterTenantAsync();
        using var owner = fixture.CreateClient(t.Token);

        var initial = await HttpAssert.OkAsync<SecurityPolicyDto>(await owner.GetAsync("/api/v1/security/policy"));
        Assert.Equal(initial.Baseline, initial.Policy);
        Assert.Equal(10, initial.Policy.PasswordMinLength);
        Assert.False(initial.Policy.RequireMfa);

        var tightened = initial.Policy with { PasswordMinLength = 14, PasswordRequireUppercase = true, PasswordHistoryCount = 10, PasswordMaxAgeDays = 90 };
        Assert.Equal(tightened, (await HttpAssert.OkAsync<SecurityPolicyDto>(await owner.PutAsJsonAsync("/api/v1/security/policy", tightened, ApiFixture.Json))).Policy);

        var problem = await HttpAssert.ProblemAsync(
            await owner.PutAsJsonAsync(
                "/api/v1/security/policy",
                tightened with { PasswordMinLength = 4, PasswordRequireDigit = false, SessionIdleTimeoutMinutes = 0, SessionAbsoluteLifetimeHours = 99999 },
                ApiFixture.Json),
            HttpStatusCode.BadRequest, "validation_failed");
        foreach (var field in new[] { "passwordMinLength", "passwordRequireDigit", "sessionIdleTimeoutMinutes", "sessionAbsoluteLifetimeHours" })
        {
            Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), $"missing error for {field}");
        }

        Assert.Equal(tightened, (await HttpAssert.OkAsync<SecurityPolicyDto>(await owner.GetAsync("/api/v1/security/policy"))).Policy);
    }

    [Fact]
    public async Task Only_holders_of_the_security_permissions_can_read_or_change_the_policy()
    {
        var t = await fixture.RegisterTenantAsync();
        using var owner = fixture.CreateClient(t.Token);
        var policy = (await HttpAssert.OkAsync<SecurityPolicyDto>(await owner.GetAsync("/api/v1/security/policy"))).Policy;

        var technician = await fixture.AddMemberAsync(t, SystemRoles.ItTechnician.Name);
        var auditor = await fixture.AddMemberAsync(t, SystemRoles.ReadOnlyAuditor.Name);
        var securityAdmin = await fixture.AddMemberAsync(t, SystemRoles.SecurityAdministrator.Name);
        using var technicianClient = fixture.CreateClient(technician.Token);
        using var auditorClient = fixture.CreateClient(auditor.Token);
        using var securityClient = fixture.CreateClient(securityAdmin.Token);

        await HttpAssert.ProblemAsync(await technicianClient.GetAsync("/api/v1/security/policy"), HttpStatusCode.Forbidden, "permission_denied");
        await HttpAssert.ProblemAsync(await technicianClient.GetAsync("/api/v1/security/audit-events"), HttpStatusCode.Forbidden, "permission_denied");

        (await auditorClient.GetAsync("/api/v1/security/policy")).EnsureSuccessStatusCode();
        (await auditorClient.GetAsync("/api/v1/security/audit-events")).EnsureSuccessStatusCode();
        await HttpAssert.ProblemAsync(
            await auditorClient.PutAsJsonAsync("/api/v1/security/policy", policy with { RequireMfa = true }, ApiFixture.Json), HttpStatusCode.Forbidden, "permission_denied");

        await HttpAssert.OkAsync<SecurityPolicyDto>(await securityClient.PutAsJsonAsync("/api/v1/security/policy", policy with { PasswordMinLength = 12 }, ApiFixture.Json));

        // Separation of duties: the security administrator sets policy but cannot change who holds which role.
        await HttpAssert.ProblemAsync(
            await securityClient.PutAsJsonAsync($"/api/v1/users/{technician.UserId}/roles", new SetUserRolesRequest([Guid.NewGuid()]), ApiFixture.Json),
            HttpStatusCode.Forbidden, "permission_denied");
    }

    [Fact]
    public async Task Expired_passwords_must_be_replaced_through_the_reset_flow()
    {
        var t = await fixture.RegisterTenantAsync();
        using var owner = fixture.CreateClient(t.Token);
        var policy = (await HttpAssert.OkAsync<SecurityPolicyDto>(await owner.GetAsync("/api/v1/security/policy"))).Policy;
        await HttpAssert.OkAsync<SecurityPolicyDto>(await owner.PutAsJsonAsync("/api/v1/security/policy", policy with { PasswordMaxAgeDays = 30 }, ApiFixture.Json));

        await fixture.LoginAsync(t.OwnerEmail, t.OwnerPassword);
        await fixture.ExecuteAsOwnerAsync("UPDATE users SET password_changed_at_utc = now() - interval '31 days' WHERE id = @u", ("u", t.OwnerUserId));

        await HttpAssert.ProblemAsync(await fixture.TryLoginAsync(t.OwnerEmail, t.OwnerPassword), HttpStatusCode.Forbidden, "password_expired");

        // A wrong password still looks like any other failure.
        await HttpAssert.ProblemAsync(await fixture.TryLoginAsync(t.OwnerEmail, "Wrong-Passw0rd!"), HttpStatusCode.Unauthorized, "invalid_credentials");
    }

    [Fact]
    public async Task Security_relevant_actions_are_recorded_with_actor_subject_and_origin()
    {
        var t = await fixture.RegisterTenantAsync();
        using var owner = fixture.CreateClient(t.Token);
        owner.DefaultRequestHeaders.UserAgent.ParseAdd("SolvixTests/1.0");
        owner.DefaultRequestHeaders.Add("X-Correlation-Id", "audit-corr-1");
        var roles = await ApiFixture.RolesAsync(owner);

        var member = await fixture.AddMemberAsync(t, SystemRoles.ReadOnlyAuditor.Name);
        await fixture.TryLoginAsync(member.Email, "Wrong-Passw0rd!");
        (await owner.PutAsJsonAsync($"/api/v1/users/{member.UserId}/roles", new SetUserRolesRequest([roles[SystemRoles.ItManager.Name].Id]), ApiFixture.Json)).EnsureSuccessStatusCode();
        var role = await HttpAssert.OkAsync<RoleDto>(
            await owner.PostAsJsonAsync("/api/v1/roles", new SaveRoleRequest("Audited", null, [Permissions.Reports.View]), ApiFixture.Json), HttpStatusCode.Created);
        (await owner.DeleteAsync($"/api/v1/roles/{role.Id}")).EnsureSuccessStatusCode();
        var policy = (await HttpAssert.OkAsync<SecurityPolicyDto>(await owner.GetAsync("/api/v1/security/policy"))).Policy;
        (await owner.PutAsJsonAsync("/api/v1/security/policy", policy with { PasswordMinLength = 12 }, ApiFixture.Json)).EnsureSuccessStatusCode();
        (await owner.PostAsync($"/api/v1/users/{member.UserId}/sessions/revoke", null)).EnsureSuccessStatusCode();
        (await owner.PostAsync($"/api/v1/users/{member.UserId}/deactivate", null)).EnsureSuccessStatusCode();
        (await owner.PostAsync($"/api/v1/users/{member.UserId}/activate", null)).EnsureSuccessStatusCode();
        (await owner.DeleteAsync($"/api/v1/users/{member.UserId}")).EnsureSuccessStatusCode();

        var events = (await HttpAssert.OkAsync<PagedResult<AuditEventDto>>(await owner.GetAsync("/api/v1/security/audit-events?pageSize=200"))).Items;
        var types = events.Select(e => e.EventType).ToHashSet(StringComparer.Ordinal);
        foreach (var expected in new[]
        {
            AuditEvents.TenantRegistered, AuditEvents.EmailVerified, AuditEvents.LoginSucceeded, AuditEvents.LoginFailed,
            AuditEvents.UserInvited, AuditEvents.InvitationAccepted, AuditEvents.UserRolesChanged, AuditEvents.RoleCreated,
            AuditEvents.RoleDeleted, AuditEvents.SecurityPolicyUpdated, AuditEvents.SessionsRevokedByAdministrator,
            AuditEvents.UserDeactivated, AuditEvents.UserActivated, AuditEvents.UserRemoved,
        })
        {
            Assert.True(types.Contains(expected), $"no '{expected}' event recorded; have: {string.Join(", ", types.Order(StringComparer.Ordinal))}");
        }

        var roleChange = events.Single(e => e.EventType == AuditEvents.UserRolesChanged);
        Assert.Equal(member.UserId, roleChange.UserId);
        Assert.Equal(t.OwnerUserId, roleChange.ActorUserId);
        Assert.Equal(AuditOutcome.Success, roleChange.Outcome);
        Assert.Equal("from=[Read-Only Auditor] to=[IT Manager]", roleChange.Detail);
        Assert.Equal("SolvixTests/1.0", roleChange.UserAgent);
        Assert.Equal("audit-corr-1", roleChange.CorrelationId);

        var failed = events.Single(e => e.EventType == AuditEvents.LoginFailed);
        Assert.Equal(AuditOutcome.Failure, failed.Outcome);
        Assert.Equal("wrong_password", failed.Detail);
        Assert.Contains("passwordMinLength:10->12", events.Single(e => e.EventType == AuditEvents.SecurityPolicyUpdated).Detail, StringComparison.Ordinal);

        // No secret ever lands in the trail.
        Assert.All(events, e =>
        {
            Assert.DoesNotContain(member.Password, e.Detail ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain("Wrong-Passw0rd", e.Detail ?? string.Empty, StringComparison.Ordinal);
        });

        // Filters.
        var logins = await HttpAssert.OkAsync<PagedResult<AuditEventDto>>(await owner.GetAsync("/api/v1/security/audit-events?eventType=login."));
        Assert.All(logins.Items, e => Assert.StartsWith("login.", e.EventType, StringComparison.Ordinal));
        var failures = await HttpAssert.OkAsync<PagedResult<AuditEventDto>>(await owner.GetAsync("/api/v1/security/audit-events?outcome=Failure"));
        Assert.All(failures.Items, e => Assert.Equal(AuditOutcome.Failure, e.Outcome));
        var aboutMember = await HttpAssert.OkAsync<PagedResult<AuditEventDto>>(await owner.GetAsync($"/api/v1/security/audit-events?userId={member.UserId}"));
        Assert.All(aboutMember.Items, e => Assert.True(e.UserId == member.UserId || e.ActorUserId == member.UserId));
        var future = await HttpAssert.OkAsync<PagedResult<AuditEventDto>>(await owner.GetAsync(
            $"/api/v1/security/audit-events?from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddHours(1).ToString("O"))}"));
        Assert.Empty(future.Items);
    }
}
