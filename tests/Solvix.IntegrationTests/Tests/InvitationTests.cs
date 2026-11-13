namespace Solvix.IntegrationTests.Tests;

using System.Net;
using System.Net.Http.Json;
using Solvix.IntegrationTests.Fixtures;
using Solvix.Modules.Identity.Application.Auth;
using Solvix.Modules.Identity.Application.Invitations;
using Solvix.Modules.Identity.Application.Users;
using Solvix.Modules.Identity.Domain;
using Solvix.SharedKernel.Results;

[Collection(ApiCollection.Name)]
public sealed class InvitationTests(ApiFixture fixture)
{
    [Fact]
    public async Task Inviting_a_new_person_creates_their_account_on_acceptance_with_exactly_the_invited_roles()
    {
        var t = await fixture.RegisterTenantAsync("Acme Invites");
        using var owner = fixture.CreateClient(t.Token);
        var roles = await ApiFixture.RolesAsync(owner);
        var email = fixture.NewEmail("invitee");

        var response = await owner.PostAsJsonAsync(
            "/api/v1/invitations",
            new CreateInvitationRequest(email, "Invited Person", [roles[SystemRoles.ItTechnician.Name].Id, roles[SystemRoles.AssetManager.Name].Id]),
            ApiFixture.Json);
        var raw = await response.Content.ReadAsStringAsync();
        var invitation = await HttpAssert.OkAsync<InvitationDto>(response, HttpStatusCode.Created);
        Assert.Equal(InvitationStatus.Pending, invitation.Status);

        // The link only travels by e-mail; the API response does not contain the token.
        var mail = await fixture.Emails.WaitForAsync(email, "invited");
        var token = CapturingEmailSender.TokenFrom(mail);
        Assert.Contains("/accept-invitation#token=", mail.TextBody, StringComparison.Ordinal);
        Assert.Contains("Acme Invites", mail.Subject, StringComparison.Ordinal);
        Assert.DoesNotContain(token, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("token", raw, StringComparison.OrdinalIgnoreCase);

        using var anonymous = fixture.CreateClient();
        var preview = await HttpAssert.OkAsync<InvitationPreviewDto>(
            await anonymous.PostAsJsonAsync("/api/v1/auth/invitations/preview", new InvitationTokenRequest(token), ApiFixture.Json));
        Assert.Equal("Acme Invites", preview.TenantName);
        Assert.Equal(email, preview.Email);
        Assert.False(preview.AccountExists);
        Assert.Equal(new[] { "Asset Manager", "IT Technician" }, preview.Roles);

        // A weak password is refused and the invitation stays usable.
        var weak = await HttpAssert.ProblemAsync(
            await anonymous.PostAsJsonAsync("/api/v1/auth/invitations/accept", new AcceptInvitationRequest(token, null, "short"), ApiFixture.Json),
            HttpStatusCode.BadRequest, "validation_failed");
        Assert.True(weak.GetProperty("errors").TryGetProperty("password", out _));
        Assert.True(weak.GetProperty("errors").TryGetProperty("displayName", out _));

        // The invitee tries to smuggle in more roles and another tenant: ignored, they are not part of the contract.
        var accepted = await HttpAssert.OkAsync<InvitationAcceptedDto>(await anonymous.PostAsJsonAsync(
            "/api/v1/auth/invitations/accept",
            new
            {
                token,
                displayName = "Invited Person",
                password = ApiFixture.DefaultPassword,
                roleIds = new[] { roles[SystemRoles.SuperAdministrator.Name].Id },
                roles = new[] { "Super Administrator" },
                tenantId = Guid.NewGuid(),
                isActive = true,
            },
            ApiFixture.Json));
        Assert.True(accepted.AccountCreated);
        Assert.Equal(t.Slug, accepted.TenantSlug);

        // Accepting proves the address, so no separate verification is needed.
        var auth = await fixture.LoginAsync(email, ApiFixture.DefaultPassword);
        using var invitee = fixture.CreateClient(auth.AccessToken);
        var me = await HttpAssert.OkAsync<CurrentUserDto>(await invitee.GetAsync("/api/v1/auth/me"));
        Assert.Equal(new[] { "Asset Manager", "IT Technician" }, me.Roles);
        Assert.Equal(
            SystemRoles.ItTechnician.Permissions.Union(SystemRoles.AssetManager.Permissions).Order(StringComparer.Ordinal),
            me.Permissions);

        // Single use.
        await HttpAssert.ProblemAsync(
            await anonymous.PostAsJsonAsync("/api/v1/auth/invitations/accept", new AcceptInvitationRequest(token, "X", ApiFixture.DefaultPassword), ApiFixture.Json),
            HttpStatusCode.BadRequest, "validation_failed");

        var list = await HttpAssert.OkAsync<PagedResult<InvitationDto>>(await owner.GetAsync("/api/v1/invitations"));
        Assert.Equal(InvitationStatus.Accepted, Assert.Single(list.Items).Status);
    }

    [Fact]
    public async Task Accepting_as_an_existing_account_never_changes_its_credentials()
    {
        var a = await fixture.RegisterTenantAsync();
        var b = await fixture.RegisterTenantAsync();
        using var ownerB = fixture.CreateClient(b.Token);
        var rolesB = await ApiFixture.RolesAsync(ownerB);

        // B invites A's owner, who already has an account.
        await HttpAssert.OkAsync<InvitationDto>(
            await ownerB.PostAsJsonAsync("/api/v1/invitations", new CreateInvitationRequest(a.OwnerEmail, null, [rolesB[SystemRoles.ReadOnlyAuditor.Name].Id]), ApiFixture.Json),
            HttpStatusCode.Created);
        var token = CapturingEmailSender.TokenFrom(await fixture.Emails.WaitForAsync(a.OwnerEmail, "invited"));

        using var anonymous = fixture.CreateClient();
        var preview = await HttpAssert.OkAsync<InvitationPreviewDto>(
            await anonymous.PostAsJsonAsync("/api/v1/auth/invitations/preview", new InvitationTokenRequest(token), ApiFixture.Json));
        Assert.True(preview.AccountExists);

        // Whoever holds the link supplies a password of their choosing: it must be ignored.
        var accepted = await HttpAssert.OkAsync<InvitationAcceptedDto>(await anonymous.PostAsJsonAsync(
            "/api/v1/auth/invitations/accept", new AcceptInvitationRequest(token, "Mallory", "Attacker-Chosen-Passw0rd!"), ApiFixture.Json));
        Assert.False(accepted.AccountCreated);

        await HttpAssert.ProblemAsync(await fixture.TryLoginAsync(a.OwnerEmail, "Attacker-Chosen-Passw0rd!"), HttpStatusCode.Unauthorized, "invalid_credentials");
        var inB = await fixture.LoginAsync(a.OwnerEmail, a.OwnerPassword, b.Slug);
        Assert.Equal(b.TenantId, inB.Tenant.Id);
        Assert.NotEqual("Mallory", inB.User.DisplayName);

        // Their existing session in A was untouched.
        using var stillA = fixture.CreateClient(a.Token);
        (await stillA.GetAsync("/api/v1/auth/me")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Revoked_expired_and_replaced_invitation_links_do_not_work()
    {
        var t = await fixture.RegisterTenantAsync();
        using var owner = fixture.CreateClient(t.Token);
        using var anonymous = fixture.CreateClient();
        var roles = await ApiFixture.RolesAsync(owner);
        var roleIds = new[] { roles[SystemRoles.ReadOnlyAuditor.Name].Id };

        async Task<(InvitationDto Invitation, string Token, string Email)> InviteAsync()
        {
            var email = fixture.NewEmail("invitee");
            var invitation = await HttpAssert.OkAsync<InvitationDto>(
                await owner.PostAsJsonAsync("/api/v1/invitations", new CreateInvitationRequest(email, null, roleIds), ApiFixture.Json), HttpStatusCode.Created);
            return (invitation, CapturingEmailSender.TokenFrom(await fixture.Emails.WaitForAsync(email, "invited")), email);
        }

        Task<HttpResponseMessage> AcceptAsync(string token) => anonymous.PostAsJsonAsync(
            "/api/v1/auth/invitations/accept", new AcceptInvitationRequest(token, "Invited Person", ApiFixture.DefaultPassword), ApiFixture.Json);

        // Revoked.
        var revoked = await InviteAsync();
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/v1/invitations/{revoked.Invitation.Id}")).StatusCode);
        await HttpAssert.ProblemAsync(await AcceptAsync(revoked.Token), HttpStatusCode.BadRequest, "validation_failed");
        await HttpAssert.ProblemAsync(
            await anonymous.PostAsJsonAsync("/api/v1/auth/invitations/preview", new InvitationTokenRequest(revoked.Token), ApiFixture.Json),
            HttpStatusCode.BadRequest, "validation_failed");

        // Expired.
        var expired = await InviteAsync();
        await fixture.ExecuteAsOwnerAsync("UPDATE user_invitations SET expires_at_utc = now() - interval '1 minute' WHERE id = @i", ("i", expired.Invitation.Id));
        await HttpAssert.ProblemAsync(await AcceptAsync(expired.Token), HttpStatusCode.BadRequest, "validation_failed");

        // Re-sent: the first link dies, the second works.
        var resent = await InviteAsync();
        await HttpAssert.OkAsync<InvitationDto>(await owner.PostAsync($"/api/v1/invitations/{resent.Invitation.Id}/resend", null));
        var newToken = CapturingEmailSender.TokenFrom(await fixture.Emails.WaitForAsync(resent.Email, "invited", skip: 1));
        Assert.NotEqual(resent.Token, newToken);
        await HttpAssert.ProblemAsync(await AcceptAsync(resent.Token), HttpStatusCode.BadRequest, "validation_failed");
        await HttpAssert.OkAsync<InvitationAcceptedDto>(await AcceptAsync(newToken));

        // No account was created by any of the failed attempts.
        Assert.Equal(0L, await fixture.ScalarAsOwnerAsync<long>(
            "SELECT count(*) FROM users WHERE normalized_email = ANY(@e)", ("e", new[] { revoked.Email.ToUpperInvariant(), expired.Email.ToUpperInvariant() })));
    }

    [Fact]
    public async Task Members_cannot_be_invited_twice_and_only_one_open_invitation_exists_per_address()
    {
        var t = await fixture.RegisterTenantAsync();
        using var owner = fixture.CreateClient(t.Token);
        var roles = await ApiFixture.RolesAsync(owner);
        var auditor = new[] { roles[SystemRoles.ReadOnlyAuditor.Name].Id };
        var technician = new[] { roles[SystemRoles.ItTechnician.Name].Id };

        await HttpAssert.ProblemAsync(
            await owner.PostAsJsonAsync("/api/v1/invitations", new CreateInvitationRequest(t.OwnerEmail, null, auditor), ApiFixture.Json),
            HttpStatusCode.Conflict, "already_member");

        // Inviting the same address again replaces the roles and the link instead of stacking invitations.
        var email = fixture.NewEmail("invitee");
        var first = await HttpAssert.OkAsync<InvitationDto>(
            await owner.PostAsJsonAsync("/api/v1/invitations", new CreateInvitationRequest(email, null, auditor), ApiFixture.Json), HttpStatusCode.Created);
        var second = await HttpAssert.OkAsync<InvitationDto>(
            await owner.PostAsJsonAsync("/api/v1/invitations", new CreateInvitationRequest(email.ToUpperInvariant(), null, technician), ApiFixture.Json), HttpStatusCode.Created);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal("IT Technician", Assert.Single(second.Roles).Name);

        var member = await fixture.AcceptInvitationAsync(email, skip: 1);
        Assert.True(member.AccountCreated);
        var auth = await fixture.LoginAsync(email, ApiFixture.DefaultPassword);
        using var invitee = fixture.CreateClient(auth.AccessToken);
        Assert.Equal(new[] { "IT Technician" }, (await HttpAssert.OkAsync<CurrentUserDto>(await invitee.GetAsync("/api/v1/auth/me"))).Roles);

        // A deactivated member is reactivated, not re-invited.
        (await owner.PostAsync($"/api/v1/users/{auth.User.Id}/deactivate", null)).EnsureSuccessStatusCode();
        await HttpAssert.ProblemAsync(
            await owner.PostAsJsonAsync("/api/v1/invitations", new CreateInvitationRequest(email, null, auditor), ApiFixture.Json),
            HttpStatusCode.Conflict, "member_deactivated");

        var problem = await HttpAssert.ProblemAsync(
            await owner.PostAsJsonAsync("/api/v1/invitations", new CreateInvitationRequest("not-an-email", null, []), ApiFixture.Json),
            HttpStatusCode.BadRequest, "validation_failed");
        Assert.True(problem.GetProperty("errors").TryGetProperty("email", out _));
        Assert.True(problem.GetProperty("errors").TryGetProperty("roleIds", out _));
    }
}
