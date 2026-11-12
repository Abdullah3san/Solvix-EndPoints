namespace Solvix.IntegrationTests.Tests;

using System.Net;
using System.Net.Http.Json;
using Solvix.IntegrationTests.Fixtures;
using Solvix.Modules.Identity.Application.Account;
using Solvix.Modules.Identity.Application.Auth;
using Solvix.Modules.Identity.Application.Security;
using Solvix.Modules.Identity.Domain;

[Collection(ApiCollection.Name)]
public sealed class MultiFactorAuthenticationTests(ApiFixture fixture)
{
    private async Task<(Authenticator Authenticator, IReadOnlyList<string> RecoveryCodes)> EnrollAsync(HttpClient client, string password)
    {
        var setup = await HttpAssert.OkAsync<MfaSetupDto>(
            await client.PostAsJsonAsync("/api/v1/auth/mfa/totp/setup", new PasswordConfirmationRequest(password), ApiFixture.Json));
        Assert.StartsWith("otpauth://totp/Solvix:", setup.OtpAuthUri, StringComparison.Ordinal);
        var authenticator = new Authenticator(setup.Secret);
        var codes = await HttpAssert.OkAsync<RecoveryCodesDto>(
            await client.PostAsJsonAsync("/api/v1/auth/mfa/totp/confirm", new MfaConfirmRequest(await authenticator.NextCodeAsync()), ApiFixture.Json));
        return (authenticator, codes.RecoveryCodes);
    }

    private async Task<MfaChallengeDto> ChallengeAsync(string email, string password)
    {
        var login = await HttpAssert.OkAsync<LoginResponse>(await fixture.TryLoginAsync(email, password));
        Assert.True(login.MfaRequired);
        Assert.Null(login.Session);
        return login.Challenge!;
    }

    [Fact]
    public async Task Enrolment_needs_the_password_and_a_valid_code_and_then_sign_in_needs_the_second_factor()
    {
        var t = await fixture.RegisterTenantAsync();
        using var client = fixture.CreateClient(t.Token);

        var before = await HttpAssert.OkAsync<MfaStatusDto>(await client.GetAsync("/api/v1/auth/mfa"));
        Assert.False(before.Enabled);

        await HttpAssert.ProblemAsync(
            await client.PostAsJsonAsync("/api/v1/auth/mfa/totp/setup", new PasswordConfirmationRequest("Wrong-Passw0rd!"), ApiFixture.Json),
            HttpStatusCode.BadRequest, "validation_failed");

        var setup = await HttpAssert.OkAsync<MfaSetupDto>(
            await client.PostAsJsonAsync("/api/v1/auth/mfa/totp/setup", new PasswordConfirmationRequest(t.OwnerPassword), ApiFixture.Json));
        var authenticator = new Authenticator(setup.Secret);

        // Not active until a code proves the authenticator works; password-only sign-in still completes.
        await HttpAssert.ProblemAsync(
            await client.PostAsJsonAsync("/api/v1/auth/mfa/totp/confirm", new MfaConfirmRequest(authenticator.WrongCode()), ApiFixture.Json),
            HttpStatusCode.BadRequest, "validation_failed");
        await fixture.LoginAsync(t.OwnerEmail, t.OwnerPassword);

        var codes = await HttpAssert.OkAsync<RecoveryCodesDto>(
            await client.PostAsJsonAsync("/api/v1/auth/mfa/totp/confirm", new MfaConfirmRequest(await authenticator.NextCodeAsync()), ApiFixture.Json));
        Assert.Equal(10, codes.RecoveryCodes.Count);

        var after = await HttpAssert.OkAsync<MfaStatusDto>(await client.GetAsync("/api/v1/auth/mfa"));
        Assert.True(after.Enabled);
        Assert.Equal(10, after.RecoveryCodesRemaining);
        await fixture.Emails.WaitForAsync(t.OwnerEmail, "turned on");

        // The secret is encrypted at rest and recovery codes are stored as hashes.
        var stored = await fixture.ScalarAsOwnerAsync<string>("SELECT mfa_secret FROM users WHERE id = @u", ("u", t.OwnerUserId));
        Assert.StartsWith("v1.", stored, StringComparison.Ordinal);
        Assert.DoesNotContain(setup.Secret, stored, StringComparison.Ordinal);
        Assert.Equal(0L, await fixture.ScalarAsOwnerAsync<long>(
            "SELECT count(*) FROM user_recovery_codes WHERE code_hash = ANY(@c)", ("c", codes.RecoveryCodes.Select(c => c.Replace("-", string.Empty, StringComparison.Ordinal)).ToArray())));

        // From now on the password alone yields a challenge and no tokens of any kind.
        using var anonymous = fixture.CreateClient();
        var response = await fixture.TryLoginAsync(t.OwnerEmail, t.OwnerPassword);
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("accessToken", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("refreshToken", raw, StringComparison.Ordinal);
        var challenge = System.Text.Json.JsonSerializer.Deserialize<LoginResponse>(raw, ApiFixture.Json)!.Challenge!;

        // The challenge token is not an access token.
        using var withChallenge = fixture.CreateClient(challenge.MfaToken);
        Assert.Equal(HttpStatusCode.Unauthorized, (await withChallenge.GetAsync("/api/v1/auth/me")).StatusCode);

        var session = await HttpAssert.OkAsync<AuthResponse>(await anonymous.PostAsJsonAsync(
            "/api/v1/auth/mfa/verify", new MfaVerifyRequest(challenge.MfaToken, await authenticator.NextCodeAsync(), null), ApiFixture.Json));
        using var signedIn = fixture.CreateClient(session.AccessToken);
        var me = await HttpAssert.OkAsync<CurrentUserDto>(await signedIn.GetAsync("/api/v1/auth/me"));
        Assert.True(me.MfaEnabled);
        Assert.True(me.MfaVerified);
    }

    [Fact]
    public async Task A_code_or_challenge_cannot_be_replayed()
    {
        var t = await fixture.RegisterTenantAsync();
        using var client = fixture.CreateClient(t.Token);
        var (authenticator, _) = await EnrollAsync(client, t.OwnerPassword);
        using var anonymous = fixture.CreateClient();

        var challenge = await ChallengeAsync(t.OwnerEmail, t.OwnerPassword);
        var code = await authenticator.NextCodeAsync();
        await HttpAssert.OkAsync<AuthResponse>(await anonymous.PostAsJsonAsync(
            "/api/v1/auth/mfa/verify", new MfaVerifyRequest(challenge.MfaToken, code, null), ApiFixture.Json));

        // Same challenge again: spent.
        await HttpAssert.ProblemAsync(
            await anonymous.PostAsJsonAsync("/api/v1/auth/mfa/verify", new MfaVerifyRequest(challenge.MfaToken, await authenticator.NextCodeAsync(), null), ApiFixture.Json),
            HttpStatusCode.Unauthorized, "mfa_challenge_invalid");

        // A fresh challenge with the code that was already accepted (shoulder-surfed within its 30 seconds): refused.
        var second = await ChallengeAsync(t.OwnerEmail, t.OwnerPassword);
        await HttpAssert.ProblemAsync(
            await anonymous.PostAsJsonAsync("/api/v1/auth/mfa/verify", new MfaVerifyRequest(second.MfaToken, code, null), ApiFixture.Json),
            HttpStatusCode.Unauthorized, "mfa_code_invalid");
    }

    [Fact]
    public async Task Wrong_codes_exhaust_the_challenge_and_then_lock_the_account()
    {
        var t = await fixture.RegisterTenantAsync();
        using var client = fixture.CreateClient(t.Token);
        var (authenticator, _) = await EnrollAsync(client, t.OwnerPassword);
        using var anonymous = fixture.CreateClient();

        var challenge = await ChallengeAsync(t.OwnerEmail, t.OwnerPassword);
        for (var i = 0; i < 5; i++)
        {
            await HttpAssert.ProblemAsync(
                await anonymous.PostAsJsonAsync("/api/v1/auth/mfa/verify", new MfaVerifyRequest(challenge.MfaToken, authenticator.WrongCode(), null), ApiFixture.Json),
                HttpStatusCode.Unauthorized, "mfa_code_invalid");
        }

        // Five wrong codes: the challenge is dead even for the right code, and the account is locked,
        // so knowing the password does not allow unlimited guesses at the six digits.
        await HttpAssert.ProblemAsync(
            await anonymous.PostAsJsonAsync("/api/v1/auth/mfa/verify", new MfaVerifyRequest(challenge.MfaToken, await authenticator.NextCodeAsync(), null), ApiFixture.Json),
            HttpStatusCode.Unauthorized, "mfa_challenge_invalid");
        await HttpAssert.ProblemAsync(await fixture.TryLoginAsync(t.OwnerEmail, t.OwnerPassword), HttpStatusCode.Unauthorized, "invalid_credentials");
        Assert.NotNull(await fixture.ScalarAsOwnerAsync<object>("SELECT lockout_end_utc FROM users WHERE id = @u AND lockout_end_utc > now()", ("u", t.OwnerUserId)));
    }

    [Fact]
    public async Task Recovery_codes_work_once_each()
    {
        var t = await fixture.RegisterTenantAsync();
        using var client = fixture.CreateClient(t.Token);
        var (_, recoveryCodes) = await EnrollAsync(client, t.OwnerPassword);
        using var anonymous = fixture.CreateClient();

        var challenge = await ChallengeAsync(t.OwnerEmail, t.OwnerPassword);
        var session = await HttpAssert.OkAsync<AuthResponse>(await anonymous.PostAsJsonAsync(
            "/api/v1/auth/mfa/verify", new MfaVerifyRequest(challenge.MfaToken, null, recoveryCodes[0].ToLowerInvariant()), ApiFixture.Json));

        var again = await ChallengeAsync(t.OwnerEmail, t.OwnerPassword);
        await HttpAssert.ProblemAsync(
            await anonymous.PostAsJsonAsync("/api/v1/auth/mfa/verify", new MfaVerifyRequest(again.MfaToken, null, recoveryCodes[0]), ApiFixture.Json),
            HttpStatusCode.Unauthorized, "mfa_code_invalid");
        await HttpAssert.OkAsync<AuthResponse>(await anonymous.PostAsJsonAsync(
            "/api/v1/auth/mfa/verify", new MfaVerifyRequest(again.MfaToken, null, recoveryCodes[1]), ApiFixture.Json));

        using var signedIn = fixture.CreateClient(session.AccessToken);
        var status = await HttpAssert.OkAsync<MfaStatusDto>(await signedIn.GetAsync("/api/v1/auth/mfa"));
        Assert.Equal(8, status.RecoveryCodesRemaining);
        Assert.Equal(2L, await fixture.ScalarAsOwnerAsync<long>(
            "SELECT count(*) FROM auth_audit_events WHERE event_type = 'mfa.recovery_code_used' AND user_id = @u", ("u", t.OwnerUserId)));
    }

    [Fact]
    public async Task Password_reset_does_not_bypass_the_second_factor()
    {
        var t = await fixture.RegisterTenantAsync();
        using var client = fixture.CreateClient(t.Token);
        var (authenticator, _) = await EnrollAsync(client, t.OwnerPassword);
        using var anonymous = fixture.CreateClient();

        await anonymous.PostAsJsonAsync("/api/v1/auth/forgot-password", new EmailRequest(t.OwnerEmail), ApiFixture.Json);
        var token = CapturingEmailSender.TokenFrom(await fixture.Emails.WaitForAsync(t.OwnerEmail, "Reset your password"));
        (await anonymous.PostAsJsonAsync("/api/v1/auth/reset-password", new ResetPasswordRequest(token, "Brand-New-Passw0rd!"), ApiFixture.Json)).EnsureSuccessStatusCode();

        var challenge = await ChallengeAsync(t.OwnerEmail, "Brand-New-Passw0rd!");
        await HttpAssert.OkAsync<AuthResponse>(await anonymous.PostAsJsonAsync(
            "/api/v1/auth/mfa/verify", new MfaVerifyRequest(challenge.MfaToken, await authenticator.NextCodeAsync(), null), ApiFixture.Json));
    }

    [Fact]
    public async Task Turning_mfa_off_needs_password_and_code()
    {
        var t = await fixture.RegisterTenantAsync();
        using var client = fixture.CreateClient(t.Token);
        var (authenticator, _) = await EnrollAsync(client, t.OwnerPassword);

        await HttpAssert.ProblemAsync(
            await client.PostAsJsonAsync("/api/v1/auth/mfa/disable", new MfaProtectedRequest("Wrong-Passw0rd!", await authenticator.NextCodeAsync()), ApiFixture.Json),
            HttpStatusCode.BadRequest, "validation_failed");
        await HttpAssert.ProblemAsync(
            await client.PostAsJsonAsync("/api/v1/auth/mfa/disable", new MfaProtectedRequest(t.OwnerPassword, authenticator.WrongCode()), ApiFixture.Json),
            HttpStatusCode.BadRequest, "validation_failed");
        Assert.True((await HttpAssert.OkAsync<MfaStatusDto>(await client.GetAsync("/api/v1/auth/mfa"))).Enabled);

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync(
            "/api/v1/auth/mfa/disable", new MfaProtectedRequest(t.OwnerPassword, await authenticator.NextCodeAsync()), ApiFixture.Json)).StatusCode);
        Assert.False((await HttpAssert.OkAsync<MfaStatusDto>(await client.GetAsync("/api/v1/auth/mfa"))).Enabled);
        await fixture.LoginAsync(t.OwnerEmail, t.OwnerPassword);
        await fixture.Emails.WaitForAsync(t.OwnerEmail, "turned off");
    }

    [Fact]
    public async Task A_workspace_that_requires_mfa_blocks_members_until_they_enrol()
    {
        var t = await fixture.RegisterTenantAsync();
        var member = await fixture.AddMemberAsync(t, SystemRoles.TenantAdministrator.Name);
        using var owner = fixture.CreateClient(t.Token);
        using var memberClient = fixture.CreateClient(member.Token);
        (await memberClient.GetAsync("/api/v1/branches")).EnsureSuccessStatusCode();

        var policy = await HttpAssert.OkAsync<SecurityPolicyDto>(await owner.GetAsync("/api/v1/security/policy"));
        await HttpAssert.OkAsync<SecurityPolicyDto>(await owner.PutAsJsonAsync("/api/v1/security/policy", policy.Policy with { RequireMfa = true }, ApiFixture.Json));

        // Effective on the very next request of an existing session.
        var blocked = await HttpAssert.ProblemAsync(await memberClient.GetAsync("/api/v1/branches"), HttpStatusCode.Forbidden, "mfa_enrollment_required");
        Assert.False(blocked.TryGetProperty("requiredPermissions", out _));
        await HttpAssert.ProblemAsync(await memberClient.GetAsync("/api/v1/users"), HttpStatusCode.Forbidden, "mfa_enrollment_required");

        // Their own account pages stay reachable so they can comply.
        var me = await HttpAssert.OkAsync<CurrentUserDto>(await memberClient.GetAsync("/api/v1/auth/me"));
        Assert.True(me.MfaEnrollmentRequired);
        Assert.True((await HttpAssert.OkAsync<MfaStatusDto>(await memberClient.GetAsync("/api/v1/auth/mfa"))).RequiredByWorkspace);

        // A fresh password-only sign-in is in the same position.
        var fresh = await fixture.LoginAsync(member.Email, member.Password, t.Slug);
        using var freshClient = fixture.CreateClient(fresh.AccessToken);
        await HttpAssert.ProblemAsync(await freshClient.GetAsync("/api/v1/branches"), HttpStatusCode.Forbidden, "mfa_enrollment_required");

        // Enrolling in the first session restores it at once and ends the other password-only session.
        await EnrollAsync(memberClient, member.Password);
        (await memberClient.GetAsync("/api/v1/branches")).EnsureSuccessStatusCode();
        Assert.False((await HttpAssert.OkAsync<CurrentUserDto>(await memberClient.GetAsync("/api/v1/auth/me"))).MfaEnrollmentRequired);
        await HttpAssert.ProblemAsync(await freshClient.GetAsync("/api/v1/auth/me"), HttpStatusCode.Unauthorized, "session_expired");
    }
}
