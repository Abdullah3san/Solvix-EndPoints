namespace Solvix.IntegrationTests.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Solvix.IntegrationTests.Fixtures;
using Solvix.Modules.Identity.Application.Auth;
using Solvix.Modules.Identity.Application.Invitations;
using Solvix.Modules.Identity.Application.Roles;
using Solvix.Modules.Identity.Application.Users;
using Solvix.Modules.Identity.Domain;
using Solvix.Modules.Organization.Application.Branches;
using Solvix.Modules.Tenancy.Application.Management;
using Solvix.SharedKernel.Authorization;
using Solvix.SharedKernel.Results;

/// <summary>
/// Attempts to obtain more access than was granted. Every one of them must fail
/// on the server, whatever the client sends.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PrivilegeEscalationTests(ApiFixture fixture)
{
    private async Task<RoleDto> CreateRoleAsync(HttpClient admin, string name, params string[] permissions) =>
        await HttpAssert.OkAsync<RoleDto>(
            await admin.PostAsJsonAsync("/api/v1/roles", new SaveRoleRequest(name, null, permissions), ApiFixture.Json), HttpStatusCode.Created);

    public static TheoryData<string> SystemRoleKeys => [.. SystemRoles.All.Select(r => r.Key)];

    [Theory]
    [MemberData(nameof(SystemRoleKeys))]
    public async Task Each_built_in_role_grants_exactly_its_catalog_permissions(string roleKey)
    {
        var definition = SystemRoles.Find(roleKey)!;
        var t = await fixture.RegisterTenantAsync();
        var member = await fixture.AddMemberAsync(t, definition.Name);
        using var client = fixture.CreateClient(member.Token);

        var me = await HttpAssert.OkAsync<CurrentUserDto>(await client.GetAsync("/api/v1/auth/me"));
        Assert.Equal(definition.Permissions.Order(StringComparer.Ordinal), me.Permissions);

        // The decision is made per permission, on the server: probe one endpoint per permission family.
        async Task ExpectAsync(string permission, Func<Task<HttpResponseMessage>> call)
        {
            var response = await call();
            if (definition.Permissions.Contains(permission))
            {
                Assert.True(response.StatusCode != HttpStatusCode.Forbidden, $"{definition.Name} holds {permission} but was refused");
            }
            else
            {
                var problem = await HttpAssert.ProblemAsync(response, HttpStatusCode.Forbidden, "permission_denied");
                Assert.Equal(permission, problem.GetProperty("requiredPermissions")[0].GetString());
            }
        }

        await ExpectAsync(Permissions.Organization.View, () => client.GetAsync("/api/v1/branches"));
        await ExpectAsync(Permissions.Organization.Manage, () => client.PostAsJsonAsync("/api/v1/branches", new { }, ApiFixture.Json));
        await ExpectAsync(Permissions.Users.View, () => client.GetAsync("/api/v1/users"));
        await ExpectAsync(Permissions.Users.Manage, () => client.PostAsJsonAsync("/api/v1/invitations", new { }, ApiFixture.Json));
        await ExpectAsync(Permissions.Roles.View, () => client.GetAsync("/api/v1/roles"));
        await ExpectAsync(Permissions.Roles.Manage, () => client.PostAsJsonAsync("/api/v1/roles", new { }, ApiFixture.Json));
        await ExpectAsync(Permissions.Security.View, () => client.GetAsync("/api/v1/security/policy"));
        await ExpectAsync(Permissions.Security.Manage, () => client.PutAsJsonAsync("/api/v1/security/policy", new { }, ApiFixture.Json));
        await ExpectAsync(Permissions.Audit.View, () => client.GetAsync("/api/v1/security/audit-events"));
        await ExpectAsync(Permissions.Workspace.Manage, () => client.PutAsJsonAsync("/api/v1/tenant", new { }, ApiFixture.Json));
    }

    [Fact]
    public async Task Every_endpoint_declares_its_protection_and_rejects_anonymous_and_under_privileged_callers()
    {
        var t = await fixture.RegisterTenantAsync();

        // A member with a role that holds a single, unrelated permission.
        using var owner = fixture.CreateClient(t.Token);
        var reportsOnly = await CreateRoleAsync(owner, "Reports only", Permissions.Reports.View);
        var member = await fixture.AddMemberAsync(t, [reportsOnly.Id]);
        using var limited = fixture.CreateClient(member.Token);

        // Endpoints that need reports.view are probed with a caller holding a different permission.
        var auditOnly = await CreateRoleAsync(owner, "Audit only", Permissions.Audit.View);
        var otherMember = await fixture.AddMemberAsync(t, [auditOnly.Id]);
        using var limitedOther = fixture.CreateClient(otherMember.Token);
        using var anonymous = fixture.CreateClient();

        var endpoints = fixture.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("api/", StringComparison.Ordinal) == true)
            .ToList();
        Assert.True(endpoints.Count >= 50, $"only {endpoints.Count} API endpoints discovered");

        var anonymousCount = 0;
        var permissionCount = 0;
        foreach (var endpoint in endpoints)
        {
            var isAnonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
            var permissions = endpoint.Metadata.GetOrderedMetadata<RequirePermissionAttribute>();
            var authenticatedOnly = endpoint.Metadata.GetMetadata<AuthenticatedOnlyAttribute>() is not null;
            var deviceOnly = endpoint.Metadata.GetMetadata<DeviceAuthenticatedAttribute>() is not null;
            var name = $"{string.Join(",", endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods)} {endpoint.RoutePattern.RawText}";

            // Exactly one declaration (the host also refuses to start otherwise).
            Assert.True((isAnonymous ? 1 : 0) + (permissions.Count > 0 ? 1 : 0) + (authenticatedOnly ? 1 : 0) + (deviceOnly ? 1 : 0) == 1, $"{name}: ambiguous protection");
            if (isAnonymous)
            {
                anonymousCount++;
                continue;
            }

            var method = new HttpMethod(endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods[0]);
            var url = "/" + System.Text.RegularExpressions.Regex.Replace(endpoint.RoutePattern.RawText!, "\\{version[^}]*\\}", "1");
            url = System.Text.RegularExpressions.Regex.Replace(url, "\\{[^}]+\\}", Guid.NewGuid().ToString());

            HttpRequestMessage Request() => new(method, url)
            {
                Content = method == HttpMethod.Get || method == HttpMethod.Delete ? null : new StringContent("{}", Encoding.UTF8, "application/json"),
            };

            var unauthenticated = await anonymous.SendAsync(Request());
            Assert.True(unauthenticated.StatusCode == HttpStatusCode.Unauthorized, $"{name}: anonymous call returned {(int)unauthenticated.StatusCode}");

            if (deviceOnly)
            {
                // Agent endpoints: a valid user token is not a device certificate.
                var asUser = await limited.SendAsync(Request());
                Assert.True(asUser.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, $"{name}: user token accepted on a device endpoint ({(int)asUser.StatusCode})");
                continue;
            }

            if (permissions.Count > 0)
            {
                permissionCount++;
                var forbidden = await (permissions.Any(p => p.Permission == Permissions.Reports.View) ? limitedOther : limited).SendAsync(Request());
                Assert.True(forbidden.StatusCode == HttpStatusCode.Forbidden, $"{name}: caller without {permissions[0].Permission} got {(int)forbidden.StatusCode}");
            }
        }

        Assert.True(anonymousCount is > 5 and < 20, $"{anonymousCount} anonymous endpoints");
        Assert.True(permissionCount >= 35, $"{permissionCount} permission-protected endpoints");
    }

    [Fact]
    public async Task A_user_administrator_cannot_hand_out_permissions_they_do_not_hold()
    {
        var t = await fixture.RegisterTenantAsync();
        using var owner = fixture.CreateClient(t.Token);
        var roles = await ApiFixture.RolesAsync(owner);
        var userAdminRole = await CreateRoleAsync(owner, "User Admin", Permissions.Users.Manage, Permissions.Roles.View);
        var userAdmin = await fixture.AddMemberAsync(t, [userAdminRole.Id]);
        var target = await fixture.AddMemberAsync(t, [userAdminRole.Id]);
        using var client = fixture.CreateClient(userAdmin.Token);

        // Promote someone else to a role above their own level.
        foreach (var roleName in new[] { SystemRoles.SuperAdministrator.Name, SystemRoles.TenantAdministrator.Name, SystemRoles.SecurityAdministrator.Name, SystemRoles.ItTechnician.Name })
        {
            await HttpAssert.ProblemAsync(
                await client.PutAsJsonAsync($"/api/v1/users/{target.UserId}/roles", new SetUserRolesRequest([roles[roleName].Id]), ApiFixture.Json),
                HttpStatusCode.Forbidden, "insufficient_privilege");
        }

        // Same through an invitation, which would create an accomplice account with the higher role.
        await HttpAssert.ProblemAsync(
            await client.PostAsJsonAsync("/api/v1/invitations", new CreateInvitationRequest(fixture.NewEmail("accomplice"), null, [roles[SystemRoles.SuperAdministrator.Name].Id]), ApiFixture.Json),
            HttpStatusCode.Forbidden, "insufficient_privilege");

        // Hiding the powerful role among allowed ones does not help.
        await HttpAssert.ProblemAsync(
            await client.PutAsJsonAsync($"/api/v1/users/{target.UserId}/roles", new SetUserRolesRequest([userAdminRole.Id, roles[SystemRoles.SuperAdministrator.Name].Id]), ApiFixture.Json),
            HttpStatusCode.Forbidden, "insufficient_privilege");

        // What they can do: assign and invite at their own level.
        await HttpAssert.OkAsync<TenantUserDto>(
            await client.PutAsJsonAsync($"/api/v1/users/{target.UserId}/roles", new SetUserRolesRequest([userAdminRole.Id]), ApiFixture.Json));
        await HttpAssert.OkAsync<InvitationDto>(
            await client.PostAsJsonAsync("/api/v1/invitations", new CreateInvitationRequest(fixture.NewEmail("peer"), null, [userAdminRole.Id]), ApiFixture.Json),
            HttpStatusCode.Created);

        // Nothing leaked through: the target still has only the original role.
        var after = await HttpAssert.OkAsync<TenantUserDto>(await owner.GetAsync($"/api/v1/users/{target.UserId}"));
        Assert.Equal("User Admin", Assert.Single(after.Roles).Name);
    }

    [Fact]
    public async Task Nobody_can_change_their_own_roles_or_deactivate_or_remove_themselves()
    {
        var t = await fixture.RegisterTenantAsync();
        using var owner = fixture.CreateClient(t.Token);
        var roles = await ApiFixture.RolesAsync(owner);
        var admin = await fixture.AddMemberAsync(t, SystemRoles.TenantAdministrator.Name);
        using var client = fixture.CreateClient(admin.Token);

        await HttpAssert.ProblemAsync(
            await client.PutAsJsonAsync($"/api/v1/users/{admin.UserId}/roles", new SetUserRolesRequest([roles[SystemRoles.SuperAdministrator.Name].Id]), ApiFixture.Json),
            HttpStatusCode.Forbidden, "self_management_forbidden");
        await HttpAssert.ProblemAsync(await client.PostAsync($"/api/v1/users/{admin.UserId}/deactivate", null), HttpStatusCode.Forbidden, "self_management_forbidden");
        await HttpAssert.ProblemAsync(await client.DeleteAsync($"/api/v1/users/{admin.UserId}"), HttpStatusCode.Forbidden, "self_management_forbidden");

        // Even the owner cannot (they would be the first to lock the workspace out).
        await HttpAssert.ProblemAsync(
            await owner.PutAsJsonAsync($"/api/v1/users/{t.OwnerUserId}/roles", new SetUserRolesRequest([roles[SystemRoles.ReadOnlyAuditor.Name].Id]), ApiFixture.Json),
            HttpStatusCode.Forbidden, "self_management_forbidden");
    }

    [Fact]
    public async Task A_less_privileged_administrator_cannot_manage_a_more_privileged_user()
    {
        var t = await fixture.RegisterTenantAsync();
        using var owner = fixture.CreateClient(t.Token);
        var roles = await ApiFixture.RolesAsync(owner);
        var admin = await fixture.AddMemberAsync(t, SystemRoles.TenantAdministrator.Name);
        using var client = fixture.CreateClient(admin.Token);
        var auditorId = roles[SystemRoles.ReadOnlyAuditor.Name].Id;

        // The Tenant Administrator holds users.manage and security.manage, but the owner holds more.
        await HttpAssert.ProblemAsync(
            await client.PutAsJsonAsync($"/api/v1/users/{t.OwnerUserId}/roles", new SetUserRolesRequest([auditorId]), ApiFixture.Json),
            HttpStatusCode.Forbidden, "insufficient_privilege");
        await HttpAssert.ProblemAsync(await client.PostAsync($"/api/v1/users/{t.OwnerUserId}/deactivate", null), HttpStatusCode.Forbidden, "insufficient_privilege");
        await HttpAssert.ProblemAsync(await client.DeleteAsync($"/api/v1/users/{t.OwnerUserId}"), HttpStatusCode.Forbidden, "insufficient_privilege");
        await HttpAssert.ProblemAsync(await client.PostAsync($"/api/v1/users/{t.OwnerUserId}/sessions/revoke", null), HttpStatusCode.Forbidden, "insufficient_privilege");

        // The owner is untouched and still signed in.
        var me = await HttpAssert.OkAsync<CurrentUserDto>(await owner.GetAsync("/api/v1/auth/me"));
        Assert.Equal([SystemRoles.SuperAdministrator.Name], me.Roles);
    }

    [Fact]
    public async Task A_workspace_always_keeps_one_active_super_administrator()
    {
        var t = await fixture.RegisterTenantAsync();
        using var owner = fixture.CreateClient(t.Token);
        var roles = await ApiFixture.RolesAsync(owner);
        var second = await fixture.AddMemberAsync(t, SystemRoles.SuperAdministrator.Name);
        using var secondClient = fixture.CreateClient(second.Token);
        var auditor = new SetUserRolesRequest([roles[SystemRoles.ReadOnlyAuditor.Name].Id]);

        // With two Super Administrators one can be demoted...
        await HttpAssert.OkAsync<TenantUserDto>(await owner.PutAsJsonAsync($"/api/v1/users/{second.UserId}/roles", auditor, ApiFixture.Json));

        // ...but then the remaining one cannot be demoted, deactivated or removed by anyone.
        var third = await fixture.AddMemberAsync(t, SystemRoles.TenantAdministrator.Name);
        using var thirdClient = fixture.CreateClient(third.Token);
        await HttpAssert.ProblemAsync(await thirdClient.DeleteAsync($"/api/v1/users/{t.OwnerUserId}"), HttpStatusCode.Forbidden, "insufficient_privilege");

        // Promote the second back so that a peer exists who passes the privilege check, then race both demotions.
        await HttpAssert.OkAsync<TenantUserDto>(await owner.PutAsJsonAsync(
            $"/api/v1/users/{second.UserId}/roles", new SetUserRolesRequest([roles[SystemRoles.SuperAdministrator.Name].Id]), ApiFixture.Json));
        var race = await Task.WhenAll(
            owner.PutAsJsonAsync($"/api/v1/users/{second.UserId}/roles", auditor, ApiFixture.Json),
            secondClient.PutAsJsonAsync($"/api/v1/users/{t.OwnerUserId}/roles", auditor, ApiFixture.Json));
        Assert.Contains(race, r => r.StatusCode == HttpStatusCode.OK);
        Assert.Contains(race, r => r.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.Forbidden);

        var superAdministrators = await fixture.ScalarAsOwnerAsync<long>(
            """
            SELECT count(*) FROM tenant_memberships m
            JOIN membership_roles mr ON mr.membership_id = m.id
            JOIN roles r ON r.id = mr.role_id
            WHERE m.tenant_id = @t AND m.is_active AND r.system_key = 'super_admin'
            """, ("t", t.TenantId));
        Assert.Equal(1L, superAdministrators);
    }

    [Fact]
    public async Task The_last_super_administrator_cannot_be_deactivated_or_removed()
    {
        var t = await fixture.RegisterTenantAsync();
        var second = await fixture.AddMemberAsync(t, SystemRoles.SuperAdministrator.Name);
        using var owner = fixture.CreateClient(t.Token);
        using var secondClient = fixture.CreateClient(second.Token);

        (await owner.PostAsync($"/api/v1/users/{second.UserId}/deactivate", null)).EnsureSuccessStatusCode();

        // The deactivated one no longer counts: a third administrator with the same permissions cannot remove the owner.
        var third = await fixture.AddMemberAsync(t, SystemRoles.SuperAdministrator.Name);
        using var thirdClient = fixture.CreateClient(third.Token);
        (await owner.PostAsync($"/api/v1/users/{third.UserId}/deactivate", null)).EnsureSuccessStatusCode();
        (await owner.PostAsync($"/api/v1/users/{third.UserId}/activate", null)).EnsureSuccessStatusCode();
        var thirdAgain = await fixture.LoginAsync(third.Email, third.Password, t.Slug);
        using var thirdFresh = fixture.CreateClient(thirdAgain.AccessToken);
        (await thirdFresh.DeleteAsync($"/api/v1/users/{second.UserId}")).EnsureSuccessStatusCode();
        (await owner.DeleteAsync($"/api/v1/users/{third.UserId}")).EnsureSuccessStatusCode();

        // Now the owner is alone. Nobody is left who could remove them, and they cannot remove themselves.
        await HttpAssert.ProblemAsync(await owner.DeleteAsync($"/api/v1/users/{t.OwnerUserId}"), HttpStatusCode.Forbidden, "self_management_forbidden");
        await HttpAssert.ProblemAsync(await secondClient.GetAsync("/api/v1/auth/me"), HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_role_editor_cannot_create_or_widen_a_role_beyond_their_own_permissions()
    {
        var t = await fixture.RegisterTenantAsync();
        using var owner = fixture.CreateClient(t.Token);
        var roleEditorRole = await CreateRoleAsync(owner, "Role Editor", Permissions.Roles.Manage, Permissions.Users.Manage, Permissions.Reports.View);
        var editor = await fixture.AddMemberAsync(t, [roleEditorRole.Id]);
        using var client = fixture.CreateClient(editor.Token);

        // Create a role with a permission they lack, then assign it to themselves or a friend: blocked at step one.
        await HttpAssert.ProblemAsync(
            await client.PostAsJsonAsync("/api/v1/roles", new SaveRoleRequest("Backdoor", null, [Permissions.Billing.Manage]), ApiFixture.Json),
            HttpStatusCode.Forbidden, "insufficient_privilege");
        await HttpAssert.ProblemAsync(
            await client.PostAsJsonAsync("/api/v1/roles", new SaveRoleRequest("Backdoor", null, [.. PermissionCatalog.AllKeys]), ApiFixture.Json),
            HttpStatusCode.Forbidden, "insufficient_privilege");

        // Implied permissions count too: mdm.manage brings devices.view, which they do not hold.
        await HttpAssert.ProblemAsync(
            await client.PostAsJsonAsync("/api/v1/roles", new SaveRoleRequest("Sneaky", null, [Permissions.Reports.View, Permissions.Mdm.Manage]), ApiFixture.Json),
            HttpStatusCode.Forbidden, "insufficient_privilege");

        // Widen the role they already hold: blocked.
        await HttpAssert.ProblemAsync(
            await client.PutAsJsonAsync($"/api/v1/roles/{roleEditorRole.Id}", new SaveRoleRequest("Role Editor", null, [Permissions.Roles.Manage, Permissions.Security.Manage]), ApiFixture.Json),
            HttpStatusCode.Forbidden, "insufficient_privilege");

        // Edit or delete a role that holds more than they do: blocked, even when only removing from it.
        var powerful = await CreateRoleAsync(owner, "Powerful", Permissions.Security.Manage, Permissions.Reports.View);
        await HttpAssert.ProblemAsync(
            await client.PutAsJsonAsync($"/api/v1/roles/{powerful.Id}", new SaveRoleRequest("Powerful", null, [Permissions.Reports.View]), ApiFixture.Json),
            HttpStatusCode.Forbidden, "insufficient_privilege");
        await HttpAssert.ProblemAsync(await client.DeleteAsync($"/api/v1/roles/{powerful.Id}"), HttpStatusCode.Forbidden, "insufficient_privilege");

        // Within their own level everything works.
        var own = await HttpAssert.OkAsync<RoleDto>(
            await client.PostAsJsonAsync("/api/v1/roles", new SaveRoleRequest("Report Reader", "Reads reports", [Permissions.Reports.View]), ApiFixture.Json),
            HttpStatusCode.Created);
        Assert.Equal(new[] { "reports.view" }, own.Permissions);

        var unchanged = await HttpAssert.OkAsync<RoleDto>(await owner.GetAsync($"/api/v1/roles/{roleEditorRole.Id}"));
        Assert.DoesNotContain(Permissions.Security.Manage, unchanged.Permissions);
    }

    [Fact]
    public async Task Built_in_roles_cannot_be_modified_or_deleted_even_by_a_super_administrator()
    {
        var t = await fixture.RegisterTenantAsync();
        using var owner = fixture.CreateClient(t.Token);
        var roles = await ApiFixture.RolesAsync(owner);
        Assert.Equal(SystemRoles.All.Select(r => r.Name), roles.Values.Where(r => r.IsSystem).Select(r => r.Name));

        var auditor = roles[SystemRoles.ReadOnlyAuditor.Name];
        await HttpAssert.ProblemAsync(
            await owner.PutAsJsonAsync($"/api/v1/roles/{auditor.Id}", new SaveRoleRequest("Read-Only Auditor", null, [.. PermissionCatalog.AllKeys]), ApiFixture.Json),
            HttpStatusCode.Conflict, "system_role_immutable");
        await HttpAssert.ProblemAsync(await owner.DeleteAsync($"/api/v1/roles/{auditor.Id}"), HttpStatusCode.Conflict, "system_role_immutable");

        // A custom role cannot impersonate a built-in one, or be flagged as one through the request body.
        await HttpAssert.ProblemAsync(
            await owner.PostAsJsonAsync("/api/v1/roles", new SaveRoleRequest("super administrator", null, [Permissions.Reports.View]), ApiFixture.Json),
            HttpStatusCode.Conflict, "role_name_taken");
        var created = await HttpAssert.OkAsync<RoleDto>(await owner.PostAsJsonAsync(
            "/api/v1/roles",
            new { name = "Injected", permissions = new[] { Permissions.Reports.View }, isSystem = true, systemKey = "super_admin", tenantId = Guid.NewGuid(), id = auditor.Id },
            ApiFixture.Json), HttpStatusCode.Created);
        Assert.False(created.IsSystem);
        Assert.Null(created.SystemKey);
        Assert.NotEqual(auditor.Id, created.Id);
        Assert.Equal(new[] { "reports.view" }, created.Permissions);

        await HttpAssert.ProblemAsync(
            await owner.PostAsJsonAsync("/api/v1/roles", new SaveRoleRequest("Unknown", null, ["devices.view", "root.everything"]), ApiFixture.Json),
            HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task Role_changes_apply_to_tokens_that_were_issued_before_the_change()
    {
        var t = await fixture.RegisterTenantAsync();
        using var owner = fixture.CreateClient(t.Token);
        var roles = await ApiFixture.RolesAsync(owner);
        var custom = await CreateRoleAsync(owner, "Org Editor", Permissions.Organization.Manage);
        var member = await fixture.AddMemberAsync(t, [custom.Id]);
        using var client = fixture.CreateClient(member.Token);
        var branch = new CreateBranchRequest("North", "NTH", false, null, null, null, null);

        await HttpAssert.OkAsync<BranchDto>(await client.PostAsJsonAsync("/api/v1/branches", branch, ApiFixture.Json), HttpStatusCode.Created);

        // 1. The role itself loses the permission.
        await HttpAssert.OkAsync<RoleDto>(await owner.PutAsJsonAsync(
            $"/api/v1/roles/{custom.Id}", new SaveRoleRequest("Org Editor", null, [Permissions.Organization.View]), ApiFixture.Json));
        await HttpAssert.ProblemAsync(
            await client.PostAsJsonAsync("/api/v1/branches", branch with { Code = "N2" }, ApiFixture.Json), HttpStatusCode.Forbidden, "permission_denied");
        (await client.GetAsync("/api/v1/branches")).EnsureSuccessStatusCode();

        // 2. The member is moved to another role.
        await HttpAssert.OkAsync<TenantUserDto>(await owner.PutAsJsonAsync(
            $"/api/v1/users/{member.UserId}/roles", new SetUserRolesRequest([roles[SystemRoles.TenantAdministrator.Name].Id]), ApiFixture.Json));
        await HttpAssert.OkAsync<BranchDto>(await client.PostAsJsonAsync("/api/v1/branches", branch with { Code = "N3" }, ApiFixture.Json), HttpStatusCode.Created);
        await HttpAssert.ProblemAsync(await client.PutAsJsonAsync("/api/v1/tenant", new UpdateTenantRequest("Renamed"), ApiFixture.Json), HttpStatusCode.Forbidden, "permission_denied");

        // 3. Deactivated: the same token is dead at once; reactivation needs a new sign-in.
        (await owner.PostAsync($"/api/v1/users/{member.UserId}/deactivate", null)).EnsureSuccessStatusCode();
        await HttpAssert.ProblemAsync(await client.GetAsync("/api/v1/branches"), HttpStatusCode.Unauthorized, "session_expired");
        await HttpAssert.ProblemAsync(await fixture.TryLoginAsync(member.Email, member.Password, t.Slug), HttpStatusCode.Forbidden, "no_active_workspace");

        (await owner.PostAsync($"/api/v1/users/{member.UserId}/activate", null)).EnsureSuccessStatusCode();
        await HttpAssert.ProblemAsync(await client.GetAsync("/api/v1/branches"), HttpStatusCode.Unauthorized, "session_expired");
        await fixture.LoginAsync(member.Email, member.Password, t.Slug);
    }

    [Fact]
    public async Task Claims_added_to_a_token_grant_nothing()
    {
        var t = await fixture.RegisterTenantAsync();
        var member = await fixture.AddMemberAsync(t, SystemRoles.ReadOnlyAuditor.Name);

        // Suppose the signing key leaked and the auditor mints themselves a token full of role and permission claims.
        var forged = TestTokens.Mint(
            member.UserId, t.TenantId, member.Auth.SessionId,
            extraClaims:
            [
                new("role", "Owner"), new("role", "Super Administrator"), new("roles", "super_admin"),
                new("permissions", "users.manage"), new("permission", "tenant.manage"), new("scope", "users.manage roles.manage"),
                new("amr", "mfa"),
            ]);
        using var client = fixture.CreateClient(forged);

        var me = await HttpAssert.OkAsync<CurrentUserDto>(await client.GetAsync("/api/v1/auth/me"));
        Assert.Equal([SystemRoles.ReadOnlyAuditor.Name], me.Roles);
        Assert.False(me.MfaVerified);
        await HttpAssert.ProblemAsync(await client.PostAsJsonAsync("/api/v1/roles", new { }, ApiFixture.Json), HttpStatusCode.Forbidden, "permission_denied");
        await HttpAssert.ProblemAsync(await client.PutAsJsonAsync("/api/v1/tenant", new UpdateTenantRequest("Mine now"), ApiFixture.Json), HttpStatusCode.Forbidden, "permission_denied");
    }

    [Fact]
    public async Task Request_bodies_cannot_set_fields_that_are_not_part_of_the_contract()
    {
        var t = await fixture.RegisterTenantAsync();
        using var owner = fixture.CreateClient(t.Token);
        var roles = await ApiFixture.RolesAsync(owner);
        var member = await fixture.AddMemberAsync(t, SystemRoles.ReadOnlyAuditor.Name);
        var other = await fixture.RegisterTenantAsync();

        // Role assignment with extra fields that try to move the membership or flip flags.
        var updated = await HttpAssert.OkAsync<TenantUserDto>(await owner.PutAsJsonAsync(
            $"/api/v1/users/{member.UserId}/roles",
            new
            {
                roleIds = new[] { roles[SystemRoles.ItTechnician.Name].Id },
                tenantId = other.TenantId,
                userId = t.OwnerUserId,
                isActive = false,
                mfaEnabled = true,
                emailVerified = false,
                permissions = new[] { "billing.manage" },
            },
            ApiFixture.Json));
        Assert.Equal(member.UserId, updated.UserId);
        Assert.True(updated.IsActive);
        Assert.False(updated.MfaEnabled);
        Assert.Equal("IT Technician", Assert.Single(updated.Roles).Name);

        using var memberClient = fixture.CreateClient(member.Token);
        var me = await HttpAssert.OkAsync<CurrentUserDto>(await memberClient.GetAsync("/api/v1/auth/me"));
        Assert.Equal(t.TenantId, me.Tenant.Id);
        Assert.DoesNotContain(Permissions.Billing.Manage, me.Permissions);

        // The other tenant gained nobody.
        using var otherOwner = fixture.CreateClient(other.Token);
        Assert.Single((await HttpAssert.OkAsync<PagedResult<TenantUserDto>>(await otherOwner.GetAsync("/api/v1/users"))).Items);
    }

    [Fact]
    public async Task Roles_in_use_cannot_be_deleted_and_custom_roles_have_a_full_lifecycle()
    {
        var t = await fixture.RegisterTenantAsync();
        using var owner = fixture.CreateClient(t.Token);
        var role = await CreateRoleAsync(owner, "Help Desk", Permissions.Devices.RemoteAccess);
        Assert.Equal(new[] { "devices.remote_access", "devices.view" }, role.Permissions);

        var member = await fixture.AddMemberAsync(t, [role.Id]);
        await HttpAssert.ProblemAsync(await owner.DeleteAsync($"/api/v1/roles/{role.Id}"), HttpStatusCode.Conflict, "role_in_use");
        await HttpAssert.ProblemAsync(
            await owner.PostAsJsonAsync("/api/v1/roles", new SaveRoleRequest(" help desk ", null, [Permissions.Reports.View]), ApiFixture.Json),
            HttpStatusCode.Conflict, "role_name_taken");

        var list = await HttpAssert.OkAsync<List<RoleDto>>(await owner.GetAsync("/api/v1/roles"));
        Assert.Equal(1, list.Single(r => r.Id == role.Id).MemberCount);
        Assert.Equal(SystemRoles.All.Count + 1, list.Count);

        var catalog = await HttpAssert.OkAsync<List<PermissionDto>>(await owner.GetAsync("/api/v1/roles/permissions"));
        Assert.Equal(PermissionCatalog.All.Count, catalog.Count);

        (await owner.DeleteAsync($"/api/v1/users/{member.UserId}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/v1/roles/{role.Id}")).StatusCode);
        await HttpAssert.ProblemAsync(await owner.GetAsync($"/api/v1/roles/{role.Id}"), HttpStatusCode.NotFound, "not_found");
    }
}
