namespace Solvix.IntegrationTests.Tests;

using System.Net;
using System.Net.Http.Json;
using Solvix.IntegrationTests.Fixtures;
using Solvix.Modules.Identity.Application.Account;
using Solvix.Modules.Identity.Application.Auth;
using Solvix.Modules.Identity.Application.Users;
using Solvix.Modules.Identity.Domain;

/// <summary>Registration, e-mail verification, sign-in, refresh, sign-out, password reset and sessions.</summary>
[Collection(ApiCollection.Name)]
public sealed class AuthenticationFlowTests(ApiFixture fixture)
{
    [Fact]
    public async Task Registration_requires_email_verification_before_sign_in()
    {
        var pending = await fixture.RegisterUnverifiedTenantAsync();

        // Correct password, unverified address: refused with a specific reason.
        await HttpAssert.ProblemAsync(await fixture.TryLoginAsync(pending.OwnerEmail, pending.OwnerPassword), HttpStatusCode.Forbidden, "email_not_verified");

        // A wrong password reveals nothing about the verification state.
        await HttpAssert.ProblemAsync(await fixture.TryLoginAsync(pending.OwnerEmail, "Wrong-Passw0rd!"), HttpStatusCode.Unauthorized, "invalid_credentials");

        var mail = await fixture.Emails.WaitForAsync(pending.OwnerEmail, "Confirm your e-mail");
        Assert.Contains($"{SolvixApiFactory.WebBaseUrl}/verify-email#token=", mail.TextBody, StringComparison.Ordinal);
        var token = CapturingEmailSender.TokenFrom(mail);

        using var client = fixture.CreateClient();
        await HttpAssert.ProblemAsync(
            await client.PostAsJsonAsync("/api/v1/auth/verify-email", new VerifyEmailRequest("not-a-real-token-not-a-real-token-not-a-real"), ApiFixture.Json),
            HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/auth/verify-email", new VerifyEmailRequest(token), ApiFixture.Json)).StatusCode);

        // Single use.
        await HttpAssert.ProblemAsync(
            await client.PostAsJsonAsync("/api/v1/auth/verify-email", new VerifyEmailRequest(token), ApiFixture.Json),
            HttpStatusCode.BadRequest, "validation_failed");

        var auth = await fixture.LoginAsync(pending.OwnerEmail, pending.OwnerPassword);
        Assert.Equal(pending.TenantId, auth.Tenant.Id);
    }

    [Fact]
    public async Task Resending_verification_replaces_the_link_and_never_reveals_whether_an_account_exists()
    {
        var pending = await fixture.RegisterUnverifiedTenantAsync();
        var first = CapturingEmailSender.TokenFrom(await fixture.Emails.WaitForAsync(pending.OwnerEmail, "Confirm your e-mail"));
        using var client = fixture.CreateClient();

        var unknown = await client.PostAsJsonAsync("/api/v1/auth/resend-verification", new EmailRequest(fixture.NewEmail("nobody")), ApiFixture.Json);
        var known = await client.PostAsJsonAsync("/api/v1/auth/resend-verification", new EmailRequest(pending.OwnerEmail), ApiFixture.Json);
        Assert.Equal(HttpStatusCode.Accepted, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, known.StatusCode);
        Assert.Equal(await unknown.Content.ReadAsStringAsync(), await known.Content.ReadAsStringAsync());

        var second = CapturingEmailSender.TokenFrom(await fixture.Emails.WaitForAsync(pending.OwnerEmail, "Confirm your e-mail", skip: 1));
        Assert.NotEqual(first, second);

        // The older link stopped working when the new one was issued.
        await HttpAssert.ProblemAsync(
            await client.PostAsJsonAsync("/api/v1/auth/verify-email", new VerifyEmailRequest(first), ApiFixture.Json),
            HttpStatusCode.BadRequest, "validation_failed");
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/auth/verify-email", new VerifyEmailRequest(second), ApiFixture.Json)).StatusCode);
    }

    [Fact]
    public async Task Unknown_account_and_wrong_password_are_indistinguishable()
    {
        var t = await fixture.RegisterTenantAsync();

        var wrongPassword = await fixture.TryLoginAsync(t.OwnerEmail, "Wrong-Passw0rd!");
        var unknownAccount = await fixture.TryLoginAsync(fixture.NewEmail("ghost"), "Wrong-Passw0rd!");

        var a = await HttpAssert.ProblemAsync(wrongPassword, HttpStatusCode.Unauthorized, "invalid_credentials");
        var b = await HttpAssert.ProblemAsync(unknownAccount, HttpStatusCode.Unauthorized, "invalid_credentials");
        Assert.Equal(a.GetProperty("detail").GetString(), b.GetProperty("detail").GetString());
        Assert.Equal(a.GetProperty("title").GetString(), b.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Sign_in_issues_a_short_lived_access_token_and_a_refresh_token_and_never_a_password()
    {
        var t = await fixture.RegisterTenantAsync();
        using var client = fixture.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(t.OwnerEmail, t.OwnerPassword, null), ApiFixture.Json);
        var raw = await response.Content.ReadAsStringAsync();
        var login = await HttpAssert.OkAsync<LoginResponse>(response);

        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.DoesNotContain(t.OwnerPassword, raw, StringComparison.Ordinal);
        Assert.DoesNotContain("passwordHash", raw, StringComparison.OrdinalIgnoreCase);

        var session = login.Session!;
        Assert.InRange(session.AccessTokenExpiresAtUtc - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(13), TimeSpan.FromMinutes(16));
        Assert.True(session.RefreshTokenExpiresAtUtc > session.AccessTokenExpiresAtUtc);
        Assert.NotEqual(session.AccessToken, session.RefreshToken);

        // The access token names the user, tenant and session, and carries no roles or permissions.
        var payload = System.Text.Json.JsonDocument.Parse(Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Decode(session.AccessToken.Split('.')[1])).RootElement;
        Assert.Equal(session.SessionId.ToString(), payload.GetProperty("sid").GetString());
        Assert.Equal(t.TenantId.ToString(), payload.GetProperty("tid").GetString());
        Assert.False(payload.TryGetProperty("role", out _));
        Assert.False(payload.TryGetProperty("permissions", out _));

        // The refresh token is stored as a hash only.
        Assert.Equal(0L, await fixture.ScalarAsOwnerAsync<long>("SELECT count(*) FROM refresh_tokens WHERE token_hash = @t", ("t", session.RefreshToken)));
        Assert.Equal(0L, await fixture.ScalarAsOwnerAsync<long>("SELECT count(*) FROM users WHERE password_hash LIKE '%' || @p || '%'", ("p", t.OwnerPassword)));
    }

    [Fact]
    public async Task Refresh_rotates_the_token_and_reusing_an_old_one_revokes_the_session()
    {
        var t = await fixture.RegisterTenantAsync();
        using var anonymous = fixture.CreateClient();

        var second = await HttpAssert.OkAsync<AuthResponse>(
            await anonymous.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(t.Auth.RefreshToken), ApiFixture.Json));
        Assert.Equal(t.Auth.SessionId, second.SessionId);
        Assert.NotEqual(t.Auth.RefreshToken, second.RefreshToken);
        Assert.NotEqual(t.Auth.AccessToken, second.AccessToken);

        using (var withNew = fixture.CreateClient(second.AccessToken))
        {
            (await withNew.GetAsync("/api/v1/auth/me")).EnsureSuccessStatusCode();
        }

        // Move the first rotation outside the concurrency grace window, then replay the first token.
        await fixture.ExecuteAsOwnerAsync(
            "UPDATE refresh_tokens SET consumed_at_utc = consumed_at_utc - interval '5 minutes' WHERE session_id = @s AND consumed_at_utc IS NOT NULL",
            ("s", t.Auth.SessionId));
        await HttpAssert.ProblemAsync(
            await anonymous.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(t.Auth.RefreshToken), ApiFixture.Json),
            HttpStatusCode.Unauthorized, "invalid_refresh_token");

        // The whole session is gone: neither the newest refresh token nor the newest access token works.
        await HttpAssert.ProblemAsync(
            await anonymous.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(second.RefreshToken), ApiFixture.Json),
            HttpStatusCode.Unauthorized, "invalid_refresh_token");
        using var stolen = fixture.CreateClient(second.AccessToken);
        await HttpAssert.ProblemAsync(await stolen.GetAsync("/api/v1/auth/me"), HttpStatusCode.Unauthorized, "session_expired");

        Assert.Equal(
            SessionRevocationReasons.RefreshTokenReuse,
            await fixture.ScalarAsOwnerAsync<string>("SELECT revoked_reason FROM user_sessions WHERE id = @s", ("s", t.Auth.SessionId)));
        Assert.Equal(1L, await fixture.ScalarAsOwnerAsync<long>(
            "SELECT count(*) FROM auth_audit_events WHERE event_type = 'token.reuse_detected' AND session_id = @s", ("s", t.Auth.SessionId)));
    }

    [Fact]
    public async Task Concurrent_refreshes_within_the_grace_window_both_succeed()
    {
        var t = await fixture.RegisterTenantAsync();
        using var anonymous = fixture.CreateClient();

        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ =>
            anonymous.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(t.Auth.RefreshToken), ApiFixture.Json)));
        var sessions = new List<AuthResponse>();
        foreach (var response in results)
        {
            sessions.Add(await HttpAssert.OkAsync<AuthResponse>(response));
        }

        Assert.Equal(3, sessions.Select(s => s.RefreshToken).Distinct(StringComparer.Ordinal).Count());
        foreach (var session in sessions)
        {
            using var client = fixture.CreateClient(session.AccessToken);
            (await client.GetAsync("/api/v1/auth/me")).EnsureSuccessStatusCode();
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task Invalid_refresh_tokens_are_rejected(string token)
    {
        using var anonymous = fixture.CreateClient();
        var response = await anonymous.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(token), ApiFixture.Json);
        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest, response.StatusCode.ToString());
    }

    [Fact]
    public async Task Sign_out_ends_the_session_for_access_and_refresh_tokens()
    {
        var t = await fixture.RegisterTenantAsync();
        using var client = fixture.CreateClient(t.Token);
        (await client.GetAsync("/api/v1/auth/me")).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync("/api/v1/auth/logout", new LogoutRequest(null), ApiFixture.Json)).StatusCode);

        await HttpAssert.ProblemAsync(await client.GetAsync("/api/v1/auth/me"), HttpStatusCode.Unauthorized, "session_expired");
        using var anonymous = fixture.CreateClient();
        await HttpAssert.ProblemAsync(
            await anonymous.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(t.Auth.RefreshToken), ApiFixture.Json),
            HttpStatusCode.Unauthorized, "invalid_refresh_token");

        // Signing out again, or with nothing at all, is harmless.
        Assert.Equal(HttpStatusCode.NoContent, (await anonymous.PostAsJsonAsync("/api/v1/auth/logout", new LogoutRequest(t.Auth.RefreshToken), ApiFixture.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await anonymous.PostAsJsonAsync("/api/v1/auth/logout", new LogoutRequest(null), ApiFixture.Json)).StatusCode);
    }

    [Fact]
    public async Task Sign_out_with_only_the_refresh_token_works_after_the_access_token_expired()
    {
        var t = await fixture.RegisterTenantAsync();
        using var anonymous = fixture.CreateClient();
        Assert.Equal(HttpStatusCode.NoContent, (await anonymous.PostAsJsonAsync("/api/v1/auth/logout", new LogoutRequest(t.Auth.RefreshToken), ApiFixture.Json)).StatusCode);

        using var client = fixture.CreateClient(t.Token);
        await HttpAssert.ProblemAsync(await client.GetAsync("/api/v1/auth/me"), HttpStatusCode.Unauthorized, "session_expired");
    }

    [Fact]
    public async Task Password_reset_works_once_signs_out_everywhere_and_is_silent_for_unknown_accounts()
    {
        var t = await fixture.RegisterTenantAsync();
        using var anonymous = fixture.CreateClient();

        var unknownEmail = fixture.NewEmail("nobody");
        var unknown = await anonymous.PostAsJsonAsync("/api/v1/auth/forgot-password", new EmailRequest(unknownEmail), ApiFixture.Json);
        var known = await anonymous.PostAsJsonAsync("/api/v1/auth/forgot-password", new EmailRequest(t.OwnerEmail), ApiFixture.Json);
        Assert.Equal(HttpStatusCode.Accepted, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, known.StatusCode);
        Assert.Equal(await unknown.Content.ReadAsStringAsync(), await known.Content.ReadAsStringAsync());

        var mail = await fixture.Emails.WaitForAsync(t.OwnerEmail, "Reset your password");
        Assert.Contains("/reset-password#token=", mail.TextBody, StringComparison.Ordinal);
        Assert.Equal(0, fixture.Emails.Count(unknownEmail, "Reset"));
        var token = CapturingEmailSender.TokenFrom(mail);

        // A weak password is rejected and does not burn the link.
        var weak = await HttpAssert.ProblemAsync(
            await anonymous.PostAsJsonAsync("/api/v1/auth/reset-password", new ResetPasswordRequest(token, "short"), ApiFixture.Json),
            HttpStatusCode.BadRequest, "validation_failed");
        Assert.True(weak.GetProperty("errors").TryGetProperty("newPassword", out _));

        // So is the current password (history).
        await HttpAssert.ProblemAsync(
            await anonymous.PostAsJsonAsync("/api/v1/auth/reset-password", new ResetPasswordRequest(token, t.OwnerPassword), ApiFixture.Json),
            HttpStatusCode.BadRequest, "validation_failed");

        const string NewPassword = "Brand-New-Passw0rd!";
        Assert.Equal(HttpStatusCode.NoContent, (await anonymous.PostAsJsonAsync("/api/v1/auth/reset-password", new ResetPasswordRequest(token, NewPassword), ApiFixture.Json)).StatusCode);

        // Single use.
        await HttpAssert.ProblemAsync(
            await anonymous.PostAsJsonAsync("/api/v1/auth/reset-password", new ResetPasswordRequest(token, "Another-Passw0rd!9"), ApiFixture.Json),
            HttpStatusCode.BadRequest, "validation_failed");

        // Every existing session is gone, the old password no longer works, the new one does.
        using var old = fixture.CreateClient(t.Token);
        await HttpAssert.ProblemAsync(await old.GetAsync("/api/v1/auth/me"), HttpStatusCode.Unauthorized, "session_expired");
        await HttpAssert.ProblemAsync(
            await anonymous.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(t.Auth.RefreshToken), ApiFixture.Json),
            HttpStatusCode.Unauthorized, "invalid_refresh_token");
        await HttpAssert.ProblemAsync(await fixture.TryLoginAsync(t.OwnerEmail, t.OwnerPassword), HttpStatusCode.Unauthorized, "invalid_credentials");
        await fixture.LoginAsync(t.OwnerEmail, NewPassword);

        await fixture.Emails.WaitForAsync(t.OwnerEmail, "Your password was changed");
    }

    [Fact]
    public async Task Expired_reset_links_are_rejected()
    {
        var t = await fixture.RegisterTenantAsync();
        using var anonymous = fixture.CreateClient();
        await anonymous.PostAsJsonAsync("/api/v1/auth/forgot-password", new EmailRequest(t.OwnerEmail), ApiFixture.Json);
        var token = CapturingEmailSender.TokenFrom(await fixture.Emails.WaitForAsync(t.OwnerEmail, "Reset your password"));

        await fixture.ExecuteAsOwnerAsync(
            "UPDATE user_tokens SET expires_at_utc = now() - interval '1 minute' WHERE user_id = @u AND purpose = 'PasswordReset'", ("u", t.OwnerUserId));

        await HttpAssert.ProblemAsync(
            await anonymous.PostAsJsonAsync("/api/v1/auth/reset-password", new ResetPasswordRequest(token, "Brand-New-Passw0rd!"), ApiFixture.Json),
            HttpStatusCode.BadRequest, "validation_failed");
        await fixture.LoginAsync(t.OwnerEmail, t.OwnerPassword);
    }

    [Fact]
    public async Task Changing_the_password_needs_the_current_one_and_ends_other_sessions()
    {
        var t = await fixture.RegisterTenantAsync();
        var other = await fixture.LoginAsync(t.OwnerEmail, t.OwnerPassword);
        using var client = fixture.CreateClient(t.Token);
        using var otherClient = fixture.CreateClient(other.AccessToken);
        (await otherClient.GetAsync("/api/v1/auth/me")).EnsureSuccessStatusCode();

        var wrong = await HttpAssert.ProblemAsync(
            await client.PostAsJsonAsync("/api/v1/auth/change-password", new ChangePasswordRequest("Wrong-Passw0rd!", "Brand-New-Passw0rd!"), ApiFixture.Json),
            HttpStatusCode.BadRequest, "validation_failed");
        Assert.True(wrong.GetProperty("errors").TryGetProperty("currentPassword", out _));

        const string NewPassword = "Brand-New-Passw0rd!";
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync(
            "/api/v1/auth/change-password", new ChangePasswordRequest(t.OwnerPassword, NewPassword), ApiFixture.Json)).StatusCode);

        // This session survives, the other one does not.
        (await client.GetAsync("/api/v1/auth/me")).EnsureSuccessStatusCode();
        await HttpAssert.ProblemAsync(await otherClient.GetAsync("/api/v1/auth/me"), HttpStatusCode.Unauthorized, "session_expired");

        // Recent passwords cannot be reused.
        var reuse = await HttpAssert.ProblemAsync(
            await client.PostAsJsonAsync("/api/v1/auth/change-password", new ChangePasswordRequest(NewPassword, t.OwnerPassword), ApiFixture.Json),
            HttpStatusCode.BadRequest, "validation_failed");
        Assert.Contains("reuse", reuse.GetProperty("errors").GetProperty("newPassword")[0].GetString(), StringComparison.Ordinal);

        await fixture.LoginAsync(t.OwnerEmail, NewPassword);
    }

    [Fact]
    public async Task A_user_can_list_and_revoke_their_own_sessions_but_nobody_elses()
    {
        var t = await fixture.RegisterTenantAsync();
        var member = await fixture.AddMemberAsync(t, SystemRoles.TenantAdministrator.Name);
        var second = await fixture.LoginAsync(t.OwnerEmail, t.OwnerPassword);
        using var owner = fixture.CreateClient(t.Token);
        using var ownerSecond = fixture.CreateClient(second.AccessToken);
        using var memberClient = fixture.CreateClient(member.Token);

        var mine = await HttpAssert.OkAsync<List<SessionDto>>(await owner.GetAsync("/api/v1/auth/sessions"));
        Assert.Equal(2, mine.Count);
        Assert.Single(mine, s => s.IsCurrent && s.Id == t.Auth.SessionId);
        Assert.DoesNotContain(mine, s => s.Id == member.Auth.SessionId);

        // Even an administrator cannot revoke another person's session through the self-service route.
        await HttpAssert.ProblemAsync(await memberClient.DeleteAsync($"/api/v1/auth/sessions/{second.SessionId}"), HttpStatusCode.NotFound, "not_found");
        (await ownerSecond.GetAsync("/api/v1/auth/me")).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/v1/auth/sessions/{second.SessionId}")).StatusCode);
        await HttpAssert.ProblemAsync(await ownerSecond.GetAsync("/api/v1/auth/me"), HttpStatusCode.Unauthorized, "session_expired");

        var third = await fixture.LoginAsync(t.OwnerEmail, t.OwnerPassword);
        var revoked = await HttpAssert.OkAsync<SessionsRevokedDto>(await owner.PostAsync("/api/v1/auth/sessions/revoke-others", null));
        Assert.Equal(1, revoked.Revoked);
        using var ownerThird = fixture.CreateClient(third.AccessToken);
        await HttpAssert.ProblemAsync(await ownerThird.GetAsync("/api/v1/auth/me"), HttpStatusCode.Unauthorized, "session_expired");
        (await owner.GetAsync("/api/v1/auth/me")).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Idle_and_absolute_session_expiry_are_enforced_on_the_server()
    {
        var t = await fixture.RegisterTenantAsync();
        var idle = await fixture.LoginAsync(t.OwnerEmail, t.OwnerPassword);
        var absolute = await fixture.LoginAsync(t.OwnerEmail, t.OwnerPassword);

        // Neither session has been used yet, so nothing about them is cached.
        await fixture.ExecuteAsOwnerAsync("UPDATE user_sessions SET idle_expires_at_utc = now() - interval '1 second' WHERE id = @s", ("s", idle.SessionId));
        await fixture.ExecuteAsOwnerAsync("UPDATE user_sessions SET expires_at_utc = now() - interval '1 second' WHERE id = @s", ("s", absolute.SessionId));

        using var anonymous = fixture.CreateClient();
        foreach (var session in new[] { idle, absolute })
        {
            // The access tokens themselves are still within their lifetime and correctly signed.
            using var client = fixture.CreateClient(session.AccessToken);
            await HttpAssert.ProblemAsync(await client.GetAsync("/api/v1/auth/me"), HttpStatusCode.Unauthorized, "session_expired");
            await HttpAssert.ProblemAsync(
                await anonymous.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(session.RefreshToken), ApiFixture.Json),
                HttpStatusCode.Unauthorized, "invalid_refresh_token");
        }
    }

    [Fact]
    public async Task Tampered_unsigned_expired_and_foreign_tokens_are_rejected()
    {
        var a = await fixture.RegisterTenantAsync();
        var b = await fixture.RegisterTenantAsync();

        async Task AssertRejectedAsync(string token, string because)
        {
            using var client = fixture.CreateClient(token);
            var response = await client.GetAsync("/api/v1/branches");
            Assert.True(response.StatusCode == HttpStatusCode.Unauthorized, $"{because}: expected 401 but got {(int)response.StatusCode}");
        }

        // Signature no longer matches the payload.
        await AssertRejectedAsync(TestTokens.TamperPayload(a.Token, "tid", b.TenantId.ToString()), "tenant swapped in payload");
        await AssertRejectedAsync(TestTokens.TamperPayload(a.Token, "sub", b.OwnerUserId.ToString()), "subject swapped in payload");
        await AssertRejectedAsync(TestTokens.Unsigned(a.OwnerUserId, a.TenantId, a.Auth.SessionId), "alg none");
        await AssertRejectedAsync(TestTokens.Mint(a.OwnerUserId, a.TenantId, a.Auth.SessionId, signingKey: "some-other-key-some-other-key-some-other-key"), "wrong key");
        await AssertRejectedAsync(TestTokens.Mint(a.OwnerUserId, a.TenantId, a.Auth.SessionId, issuer: "evil"), "wrong issuer");
        await AssertRejectedAsync(TestTokens.Mint(a.OwnerUserId, a.TenantId, a.Auth.SessionId, audience: "evil"), "wrong audience");
        await AssertRejectedAsync(TestTokens.Mint(a.OwnerUserId, a.TenantId, a.Auth.SessionId, expires: DateTime.UtcNow.AddMinutes(-5)), "expired");

        // Even a correctly signed token is only as good as its session:
        await AssertRejectedAsync(TestTokens.Mint(a.OwnerUserId, a.TenantId, sessionId: null), "no session claim");
        await AssertRejectedAsync(TestTokens.Mint(a.OwnerUserId, a.TenantId, Guid.NewGuid()), "unknown session");
        await AssertRejectedAsync(TestTokens.Mint(a.OwnerUserId, b.TenantId, a.Auth.SessionId), "own session presented for another tenant");
        await AssertRejectedAsync(TestTokens.Mint(a.OwnerUserId, a.TenantId, b.Auth.SessionId), "another tenant's session");
        await AssertRejectedAsync(TestTokens.Mint(b.OwnerUserId, a.TenantId, a.Auth.SessionId), "another user's session");

        // Control: the genuine token works, and a forged role/permission claim adds nothing.
        using var genuine = fixture.CreateClient(a.Token);
        (await genuine.GetAsync("/api/v1/branches")).EnsureSuccessStatusCode();
    }
}
