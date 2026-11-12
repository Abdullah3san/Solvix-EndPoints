namespace Solvix.IntegrationTests.Tests;

using System.Net;
using System.Net.Http.Json;
using Npgsql;
using Solvix.Infrastructure.Tenancy;
using Solvix.IntegrationTests.Fixtures;
using Solvix.Modules.Identity.Application.Account;
using Solvix.Modules.Identity.Application.Audit;
using Solvix.Modules.Identity.Application.Auth;
using Solvix.Modules.Identity.Application.Invitations;
using Solvix.Modules.Identity.Application.Roles;
using Solvix.Modules.Identity.Application.Security;
using Solvix.Modules.Identity.Application.Users;
using Solvix.Modules.Identity.Domain;
using Solvix.SharedKernel.Authorization;
using Solvix.SharedKernel.Results;

/// <summary>
/// Identity and access data of one tenant must be unreachable from another:
/// roles, users, invitations, sessions, policies and audit events. Checked
/// through the API and directly against PostgreSQL.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class CrossTenantAccessTests(ApiFixture fixture)
{
    [Fact]
    public async Task Roles_of_another_tenant_cannot_be_read_changed_deleted_or_assigned()
    {
        var a = await fixture.RegisterTenantAsync();
        var b = await fixture.RegisterTenantAsync();
        using var ownerA = fixture.CreateClient(a.Token);
        using var ownerB = fixture.CreateClient(b.Token);
        var rolesA = await ApiFixture.RolesAsync(ownerA);
        var rolesB = await ApiFixture.RolesAsync(ownerB);
        var customB = await HttpAssert.OkAsync<RoleDto>(
            await ownerB.PostAsJsonAsync("/api/v1/roles", new SaveRoleRequest("B only", null, [Permissions.Reports.View]), ApiFixture.Json), HttpStatusCode.Created);

        // Same role names, different rows per tenant.
        Assert.Empty(rolesA.Values.Select(r => r.Id).Intersect(rolesB.Values.Select(r => r.Id)));
        Assert.DoesNotContain("B only", (await ApiFixture.RolesAsync(ownerA)).Keys);

        await HttpAssert.ProblemAsync(await ownerA.GetAsync($"/api/v1/roles/{customB.Id}"), HttpStatusCode.NotFound, "not_found");
        await HttpAssert.ProblemAsync(
            await ownerA.PutAsJsonAsync($"/api/v1/roles/{customB.Id}", new SaveRoleRequest("Hijacked", null, [.. PermissionCatalog.AllKeys]), ApiFixture.Json),
            HttpStatusCode.NotFound, "not_found");
        await HttpAssert.ProblemAsync(await ownerA.DeleteAsync($"/api/v1/roles/{customB.Id}"), HttpStatusCode.NotFound, "not_found");

        // Assigning B's Super Administrator role to a member of A, directly or by invitation.
        var memberA = await fixture.AddMemberAsync(a, SystemRoles.ReadOnlyAuditor.Name);
        var foreignSuperAdmin = rolesB[SystemRoles.SuperAdministrator.Name].Id;
        await HttpAssert.ProblemAsync(
            await ownerA.PutAsJsonAsync($"/api/v1/users/{memberA.UserId}/roles", new SetUserRolesRequest([foreignSuperAdmin]), ApiFixture.Json),
            HttpStatusCode.BadRequest, "validation_failed");
        await HttpAssert.ProblemAsync(
            await ownerA.PostAsJsonAsync("/api/v1/invitations", new CreateInvitationRequest(fixture.NewEmail("x"), null, [foreignSuperAdmin]), ApiFixture.Json),
            HttpStatusCode.BadRequest, "validation_failed");

        var stillB = await HttpAssert.OkAsync<RoleDto>(await ownerB.GetAsync($"/api/v1/roles/{customB.Id}"));
        Assert.Equal("B only", stillB.Name);
        Assert.Equal(new[] { "reports.view" }, stillB.Permissions);
    }

    [Fact]
    public async Task Users_and_sessions_of_another_tenant_cannot_be_seen_or_managed()
    {
        var a = await fixture.RegisterTenantAsync();
        var b = await fixture.RegisterTenantAsync();
        var memberB = await fixture.AddMemberAsync(b, SystemRoles.ItTechnician.Name);
        using var ownerA = fixture.CreateClient(a.Token);
        using var memberBClient = fixture.CreateClient(memberB.Token);
        var rolesA = await ApiFixture.RolesAsync(ownerA);

        var users = await HttpAssert.OkAsync<PagedResult<TenantUserDto>>(await ownerA.GetAsync("/api/v1/users"));
        Assert.Equal(a.OwnerEmail, Assert.Single(users.Items).Email);
        Assert.Empty((await HttpAssert.OkAsync<PagedResult<TenantUserDto>>(await ownerA.GetAsync($"/api/v1/users?search={Uri.EscapeDataString(memberB.Email)}"))).Items);

        foreach (var userId in new[] { memberB.UserId, b.OwnerUserId })
        {
            await HttpAssert.ProblemAsync(await ownerA.GetAsync($"/api/v1/users/{userId}"), HttpStatusCode.NotFound, "not_found");
            await HttpAssert.ProblemAsync(
                await ownerA.PutAsJsonAsync($"/api/v1/users/{userId}/roles", new SetUserRolesRequest([rolesA[SystemRoles.ReadOnlyAuditor.Name].Id]), ApiFixture.Json),
                HttpStatusCode.NotFound, "not_found");
            await HttpAssert.ProblemAsync(await ownerA.PostAsync($"/api/v1/users/{userId}/deactivate", null), HttpStatusCode.NotFound, "not_found");
            await HttpAssert.ProblemAsync(await ownerA.PostAsync($"/api/v1/users/{userId}/activate", null), HttpStatusCode.NotFound, "not_found");
            await HttpAssert.ProblemAsync(await ownerA.DeleteAsync($"/api/v1/users/{userId}"), HttpStatusCode.NotFound, "not_found");
            await HttpAssert.ProblemAsync(await ownerA.GetAsync($"/api/v1/users/{userId}/sessions"), HttpStatusCode.NotFound, "not_found");
            await HttpAssert.ProblemAsync(await ownerA.PostAsync($"/api/v1/users/{userId}/sessions/revoke", null), HttpStatusCode.NotFound, "not_found");
        }

        // A's owner cannot end B's member's session through the self-service route either.
        await HttpAssert.ProblemAsync(await ownerA.DeleteAsync($"/api/v1/auth/sessions/{memberB.Auth.SessionId}"), HttpStatusCode.NotFound, "not_found");

        var me = await HttpAssert.OkAsync<CurrentUserDto>(await memberBClient.GetAsync("/api/v1/auth/me"));
        Assert.Equal([SystemRoles.ItTechnician.Name], me.Roles);
    }

    [Fact]
    public async Task Invitations_of_another_tenant_cannot_be_listed_resent_or_revoked()
    {
        var a = await fixture.RegisterTenantAsync();
        var b = await fixture.RegisterTenantAsync();
        using var ownerA = fixture.CreateClient(a.Token);
        using var ownerB = fixture.CreateClient(b.Token);
        var rolesB = await ApiFixture.RolesAsync(ownerB);
        var email = fixture.NewEmail("invitee");
        var invitation = await HttpAssert.OkAsync<InvitationDto>(
            await ownerB.PostAsJsonAsync("/api/v1/invitations", new CreateInvitationRequest(email, null, [rolesB[SystemRoles.SuperAdministrator.Name].Id]), ApiFixture.Json),
            HttpStatusCode.Created);

        Assert.Empty((await HttpAssert.OkAsync<PagedResult<InvitationDto>>(await ownerA.GetAsync("/api/v1/invitations"))).Items);
        await HttpAssert.ProblemAsync(await ownerA.PostAsync($"/api/v1/invitations/{invitation.Id}/resend", null), HttpStatusCode.NotFound, "not_found");
        await HttpAssert.ProblemAsync(await ownerA.DeleteAsync($"/api/v1/invitations/{invitation.Id}"), HttpStatusCode.NotFound, "not_found");

        // The invitation is still pending and, once accepted, joins B and only B.
        Assert.Equal(1, fixture.Emails.Count(email, "invited"));
        await fixture.AcceptInvitationAsync(email);
        var auth = await fixture.LoginAsync(email, ApiFixture.DefaultPassword);
        Assert.Equal(b.TenantId, auth.Tenant.Id);
        await HttpAssert.ProblemAsync(await fixture.TryLoginAsync(email, ApiFixture.DefaultPassword, a.Slug), HttpStatusCode.Forbidden, "forbidden");
        Assert.Single((await HttpAssert.OkAsync<PagedResult<TenantUserDto>>(await ownerA.GetAsync("/api/v1/users"))).Items);
    }

    [Fact]
    public async Task A_person_in_two_tenants_has_separate_permissions_sessions_and_administration_in_each()
    {
        var a = await fixture.RegisterTenantAsync();
        var b = await fixture.RegisterTenantAsync();
        using var ownerA = fixture.CreateClient(a.Token);
        using var ownerB = fixture.CreateClient(b.Token);

        // Dana is a Tenant Administrator in A and only an auditor in B.
        var dana = await fixture.AddMemberAsync(a, SystemRoles.TenantAdministrator.Name);
        var inB = await fixture.AddMemberAsync(b, SystemRoles.ReadOnlyAuditor.Name, dana.Email, dana.Password);
        Assert.False(inB.AccountCreated);
        Assert.Equal(dana.UserId, inB.UserId);

        using var danaA = fixture.CreateClient(dana.Token);
        using var danaB = fixture.CreateClient(inB.Token);

        var meA = await HttpAssert.OkAsync<CurrentUserDto>(await danaA.GetAsync("/api/v1/auth/me"));
        var meB = await HttpAssert.OkAsync<CurrentUserDto>(await danaB.GetAsync("/api/v1/auth/me"));
        Assert.Contains(Permissions.Users.Manage, meA.Permissions);
        Assert.DoesNotContain(Permissions.Users.Manage, meB.Permissions);

        // Admin rights in A mean nothing in B.
        await HttpAssert.ProblemAsync(await danaB.PostAsJsonAsync("/api/v1/invitations", new { }, ApiFixture.Json), HttpStatusCode.Forbidden, "permission_denied");
        await HttpAssert.ProblemAsync(await danaB.PostAsync($"/api/v1/users/{b.OwnerUserId}/deactivate", null), HttpStatusCode.Forbidden, "permission_denied");

        // Nor can the A token be pointed at B, by header or by guessing B's ids.
        danaA.DefaultRequestHeaders.Add(TenantResolutionMiddleware.TenantHeader, b.TenantId.ToString());
        await HttpAssert.ProblemAsync(await danaA.GetAsync("/api/v1/users"), HttpStatusCode.Forbidden, "cross_tenant_access");
        danaA.DefaultRequestHeaders.Remove(TenantResolutionMiddleware.TenantHeader);
        await HttpAssert.ProblemAsync(await danaA.PostAsync($"/api/v1/users/{b.OwnerUserId}/deactivate", null), HttpStatusCode.NotFound, "not_found");

        // Switching workspace issues a session for B with B's (lesser) permissions and ends the A session.
        var workspaces = await HttpAssert.OkAsync<List<AuthTenantDto>>(await danaA.GetAsync("/api/v1/auth/workspaces"));
        Assert.Equal(new[] { a.TenantId, b.TenantId }.Order(), workspaces.Select(w => w.Id).Order());
        var switched = await HttpAssert.OkAsync<AuthResponse>(await danaA.PostAsJsonAsync("/api/v1/auth/switch-tenant", new SwitchTenantRequest(b.Slug), ApiFixture.Json));
        Assert.Equal(b.TenantId, switched.Tenant.Id);
        using var danaSwitched = fixture.CreateClient(switched.AccessToken);
        Assert.Equal([SystemRoles.ReadOnlyAuditor.Name], (await HttpAssert.OkAsync<CurrentUserDto>(await danaSwitched.GetAsync("/api/v1/auth/me"))).Roles);
        await HttpAssert.ProblemAsync(await danaA.GetAsync("/api/v1/auth/me"), HttpStatusCode.Unauthorized, "session_expired");

        // B's administrator sees and controls only Dana's presence in B.
        var again = await fixture.LoginAsync(dana.Email, dana.Password, a.Slug);
        using var danaAgain = fixture.CreateClient(again.AccessToken);
        var sessionsSeenByB = await HttpAssert.OkAsync<List<SessionDto>>(await ownerB.GetAsync($"/api/v1/users/{dana.UserId}/sessions"));
        Assert.All(sessionsSeenByB, s => Assert.Equal(b.TenantId, s.TenantId));
        Assert.DoesNotContain(sessionsSeenByB, s => s.Id == again.SessionId);

        var revoked = await HttpAssert.OkAsync<SessionsRevokedDto>(await ownerB.PostAsync($"/api/v1/users/{dana.UserId}/sessions/revoke", null));
        Assert.Equal(sessionsSeenByB.Count, revoked.Revoked);
        await HttpAssert.ProblemAsync(await danaB.GetAsync("/api/v1/auth/me"), HttpStatusCode.Unauthorized, "session_expired");
        (await danaAgain.GetAsync("/api/v1/auth/me")).EnsureSuccessStatusCode();

        // Removing Dana from B leaves the account and the membership in A alone.
        (await ownerB.DeleteAsync($"/api/v1/users/{dana.UserId}")).EnsureSuccessStatusCode();
        (await danaAgain.GetAsync("/api/v1/users")).EnsureSuccessStatusCode();
        await HttpAssert.ProblemAsync(await fixture.TryLoginAsync(dana.Email, dana.Password, b.Slug), HttpStatusCode.Forbidden, "forbidden");
        Assert.Contains((await HttpAssert.OkAsync<PagedResult<TenantUserDto>>(await ownerA.GetAsync("/api/v1/users"))).Items, u => u.UserId == dana.UserId);
    }

    [Fact]
    public async Task Security_policies_are_per_tenant_and_a_shared_account_must_satisfy_the_strictest()
    {
        var a = await fixture.RegisterTenantAsync();
        var b = await fixture.RegisterTenantAsync();
        using var ownerA = fixture.CreateClient(a.Token);
        using var ownerB = fixture.CreateClient(b.Token);

        var before = await HttpAssert.OkAsync<SecurityPolicyDto>(await ownerB.GetAsync("/api/v1/security/policy"));
        var policyA = await HttpAssert.OkAsync<SecurityPolicyDto>(await ownerA.GetAsync("/api/v1/security/policy"));
        await HttpAssert.OkAsync<SecurityPolicyDto>(await ownerA.PutAsJsonAsync(
            "/api/v1/security/policy",
            policyA.Policy with { PasswordMinLength = 20, PasswordRequireSymbol = true, RequireMfa = false, SessionIdleTimeoutMinutes = 15 },
            ApiFixture.Json));

        // B is unaffected.
        var after = await HttpAssert.OkAsync<SecurityPolicyDto>(await ownerB.GetAsync("/api/v1/security/policy"));
        Assert.Equal(before.Policy, after.Policy);
        Assert.Equal(10, (await HttpAssert.OkAsync<PasswordRulesDto>(await ownerB.GetAsync("/api/v1/auth/me/password-rules"))).MinLength);

        // Someone who belongs to both must meet A's stricter rule, whichever workspace they are signed in to.
        var shared = await fixture.AddMemberAsync(b, SystemRoles.ReadOnlyAuditor.Name);
        await fixture.AddMemberAsync(a, SystemRoles.ReadOnlyAuditor.Name, shared.Email, shared.Password);
        var viaB = await fixture.LoginAsync(shared.Email, shared.Password, b.Slug);
        using var sharedB = fixture.CreateClient(viaB.AccessToken);
        Assert.Equal(20, (await HttpAssert.OkAsync<PasswordRulesDto>(await sharedB.GetAsync("/api/v1/auth/me/password-rules"))).MinLength);
        await HttpAssert.ProblemAsync(
            await sharedB.PostAsJsonAsync("/api/v1/auth/change-password", new ChangePasswordRequest(shared.Password, "Only-15-chars-1!"), ApiFixture.Json),
            HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal(HttpStatusCode.NoContent, (await sharedB.PostAsJsonAsync(
            "/api/v1/auth/change-password", new ChangePasswordRequest(shared.Password, "A-much-longer-passphrase-42!"), ApiFixture.Json)).StatusCode);

        // New sessions in A get A's shorter lifetime; B keeps the default.
        var inA = await fixture.LoginAsync(shared.Email, "A-much-longer-passphrase-42!", a.Slug);
        var inB = await fixture.LoginAsync(shared.Email, "A-much-longer-passphrase-42!", b.Slug);
        Assert.InRange(inA.RefreshTokenExpiresAtUtc - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(13), TimeSpan.FromMinutes(16));
        Assert.InRange(inB.RefreshTokenExpiresAtUtc - DateTimeOffset.UtcNow, TimeSpan.FromHours(7), TimeSpan.FromHours(9));
    }

    [Fact]
    public async Task Audit_events_are_only_visible_to_their_own_tenant()
    {
        var a = await fixture.RegisterTenantAsync();
        var b = await fixture.RegisterTenantAsync();
        await fixture.TryLoginAsync(b.OwnerEmail, "Wrong-Passw0rd!");
        await fixture.TryLoginAsync(fixture.NewEmail("ghost"), "Wrong-Passw0rd!");
        using var ownerA = fixture.CreateClient(a.Token);
        using var ownerB = fixture.CreateClient(b.Token);

        var eventsA = await HttpAssert.OkAsync<PagedResult<AuditEventDto>>(await ownerA.GetAsync("/api/v1/security/audit-events?pageSize=200"));
        var eventsB = await HttpAssert.OkAsync<PagedResult<AuditEventDto>>(await ownerB.GetAsync("/api/v1/security/audit-events?pageSize=200"));

        Assert.NotEmpty(eventsA.Items);
        Assert.All(eventsA.Items, e => Assert.True(e.UserId is null || e.UserId == a.OwnerUserId, $"A sees an event about {e.Email}"));
        Assert.DoesNotContain(eventsA.Items, e => string.Equals(e.Email, b.OwnerEmail, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(eventsB.Items, e => e.EventType == AuditEvents.LoginFailed && e.UserId == b.OwnerUserId);

        // Filtering by another tenant's user returns nothing rather than their events.
        var probe = await HttpAssert.OkAsync<PagedResult<AuditEventDto>>(await ownerA.GetAsync($"/api/v1/security/audit-events?userId={b.OwnerUserId}"));
        Assert.Empty(probe.Items);

        // Attempts against unknown addresses belong to no tenant and are visible to none.
        Assert.DoesNotContain(eventsA.Items.Concat(eventsB.Items), e => e.Detail == "unknown_account");
        Assert.True(await fixture.ScalarAsOwnerAsync<long>("SELECT count(*) FROM auth_audit_events WHERE tenant_id IS NULL AND detail = 'unknown_account'") >= 1);
    }

    [Fact]
    public async Task Row_level_security_isolates_identity_tables_for_raw_sql()
    {
        var a = await fixture.RegisterTenantAsync();
        var b = await fixture.RegisterTenantAsync();
        await fixture.TryLoginAsync(fixture.NewEmail("ghost"), "Wrong-Passw0rd!");
        var tables = new[]
        {
            "roles", "role_permissions", "membership_roles", "user_sessions", "refresh_tokens",
            "tenant_security_policies", "auth_audit_events",
        };

        await using var connection = new NpgsqlConnection(fixture.Database.AppConnectionString);
        await connection.OpenAsync();

        // No tenant context: nothing, including the platform-level audit rows.
        foreach (var table in tables)
        {
            Assert.Equal(0L, await ScalarAsync(connection, $"SELECT count(*) FROM {table}"));
        }

        await SetTenantAsync(connection, a.TenantId);
        foreach (var table in tables)
        {
            Assert.True(await ScalarAsync(connection, $"SELECT count(*) FROM {table}") > 0, $"{table}: tenant A sees none of its own rows");
            Assert.Equal(0L, await ScalarAsync(connection, $"SELECT count(*) FROM {table} WHERE tenant_id IS DISTINCT FROM @t", ("t", a.TenantId)));
        }

        Assert.Equal(7L, await ScalarAsync(connection, "SELECT count(*) FROM roles WHERE system_key IS NOT NULL"));

        // Writing into another tenant is rejected by the policy.
        var roleOfB = await fixture.ScalarAsOwnerAsync<Guid>("SELECT id FROM roles WHERE tenant_id = @t AND system_key = 'read_only_auditor'", ("t", b.TenantId));
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            connection,
            "INSERT INTO role_permissions (id, tenant_id, role_id, permission, created_at_utc, updated_at_utc) VALUES (gen_random_uuid(), @t, @r, 'billing.manage', now(), now())",
            ("t", b.TenantId), ("r", roleOfB)));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
        Assert.Equal(0, await ExecuteAsync(connection, "UPDATE user_sessions SET revoked_at_utc = now() WHERE tenant_id = @t", ("t", b.TenantId)));
        Assert.Equal(0, await ExecuteAsync(connection, "DELETE FROM membership_roles WHERE tenant_id = @t", ("t", b.TenantId)));
        Assert.Equal(0, await ExecuteAsync(connection, "UPDATE tenant_security_policies SET require_mfa = TRUE WHERE tenant_id = @t", ("t", b.TenantId)));

        // A tenant cannot forge a platform-level audit row either.
        ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            connection,
            "INSERT INTO auth_audit_events (id, occurred_at_utc, event_type, outcome, tenant_id) VALUES (gen_random_uuid(), now(), 'forged', 'Success', NULL)"));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact]
    public async Task The_database_itself_refuses_role_assignments_that_cross_tenants()
    {
        var a = await fixture.RegisterTenantAsync();
        var b = await fixture.RegisterTenantAsync();
        var membershipA = await fixture.ScalarAsOwnerAsync<Guid>("SELECT id FROM tenant_memberships WHERE tenant_id = @t", ("t", a.TenantId));
        var superAdminB = await fixture.ScalarAsOwnerAsync<Guid>("SELECT id FROM roles WHERE tenant_id = @t AND system_key = 'super_admin'", ("t", b.TenantId));

        // Even with Row-Level Security bypassed (schema owner, platform scope), the composite
        // foreign keys make "A's member holds B's role" unrepresentable, under either tenant id.
        foreach (var tenantId in new[] { a.TenantId, b.TenantId })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecuteAsOwnerAsync(
                "INSERT INTO membership_roles (id, tenant_id, membership_id, role_id, created_at_utc, updated_at_utc) VALUES (gen_random_uuid(), @t, @m, @r, now(), now())",
                ("t", tenantId), ("m", membershipA), ("r", superAdminB)));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ex.SqlState);
        }
    }

    [Fact]
    public async Task The_audit_trail_is_append_only_for_every_database_role()
    {
        var a = await fixture.RegisterTenantAsync();

        foreach (var sql in new[]
        {
            "UPDATE auth_audit_events SET outcome = 'Success', detail = 'tampered' WHERE tenant_id = @t",
            "DELETE FROM auth_audit_events WHERE tenant_id = @t",
        })
        {
            var ex = await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecuteAsOwnerAsync(sql, ("t", a.TenantId)));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
            Assert.Contains("append-only", ex.MessageText, StringComparison.Ordinal);
        }

        var truncate = await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecuteAsOwnerAsync("TRUNCATE auth_audit_events"));
        Assert.Contains("append-only", truncate.MessageText, StringComparison.Ordinal);

        // Through the runtime role and inside the tenant's own context as well.
        await using var connection = new NpgsqlConnection(fixture.Database.AppConnectionString);
        await connection.OpenAsync();
        await SetTenantAsync(connection, a.TenantId);
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, "DELETE FROM auth_audit_events"));
        Assert.True(await ScalarAsync(connection, "SELECT count(*) FROM auth_audit_events") > 0);
    }

    private static async Task SetTenantAsync(NpgsqlConnection connection, Guid tenantId)
    {
        await using var command = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, false)", connection);
        command.Parameters.AddWithValue("t", tenantId.ToString());
        await command.ExecuteScalarAsync();
    }

    private static async Task<long> ScalarAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<int> ExecuteAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return await command.ExecuteNonQueryAsync();
    }
}
