namespace Solvix.IntegrationTests.Tests;

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Solvix.Api.Infrastructure;
using Solvix.IntegrationTests.Fixtures;
using Solvix.Modules.Identity.Application.Account;
using Solvix.Modules.Identity.Application.Auth;

/// <summary>Account lockout and per-client rate limiting on the authentication endpoints.</summary>
[Collection(ApiCollection.Name)]
public sealed class BruteForceProtectionTests(ApiFixture fixture)
{
    private const int MaxAttempts = 5;

    [Fact]
    public async Task Account_locks_after_repeated_failures_and_even_the_right_password_is_then_refused()
    {
        var t = await fixture.RegisterTenantAsync();

        for (var i = 0; i < MaxAttempts; i++)
        {
            await HttpAssert.ProblemAsync(await fixture.TryLoginAsync(t.OwnerEmail, $"Wrong-Passw0rd-{i}"), HttpStatusCode.Unauthorized, "invalid_credentials");
        }

        // Locked. The response is identical to any other failure, so lockout state cannot be probed.
        var locked = await HttpAssert.ProblemAsync(await fixture.TryLoginAsync(t.OwnerEmail, t.OwnerPassword), HttpStatusCode.Unauthorized, "invalid_credentials");
        var unknown = await HttpAssert.ProblemAsync(await fixture.TryLoginAsync(fixture.NewEmail("ghost"), "whatever-1A"), HttpStatusCode.Unauthorized, "invalid_credentials");
        Assert.Equal(unknown.GetProperty("detail").GetString(), locked.GetProperty("detail").GetString());

        var lockoutEnd = await fixture.ScalarAsOwnerAsync<DateTime>("SELECT lockout_end_utc FROM users WHERE id = @u", ("u", t.OwnerUserId));
        Assert.InRange(lockoutEnd - DateTime.UtcNow, TimeSpan.FromMinutes(13), TimeSpan.FromMinutes(16));

        // The owner is told, and the trail records it.
        await fixture.Emails.WaitForAsync(t.OwnerEmail, "temporarily locked");
        Assert.Equal(1L, await fixture.ScalarAsOwnerAsync<long>(
            "SELECT count(*) FROM auth_audit_events WHERE event_type = 'account.locked' AND user_id = @u", ("u", t.OwnerUserId)));

        // Sessions that already exist are not affected by a lockout of new sign-ins.
        using var existing = fixture.CreateClient(t.Token);
        (await existing.GetAsync("/api/v1/auth/me")).EnsureSuccessStatusCode();

        // Once the lockout has run out, the right password works again.
        await fixture.ExecuteAsOwnerAsync("UPDATE users SET lockout_end_utc = now() - interval '1 second' WHERE id = @u", ("u", t.OwnerUserId));
        await fixture.LoginAsync(t.OwnerEmail, t.OwnerPassword);
        Assert.Equal(0, await fixture.ScalarAsOwnerAsync<int>("SELECT failed_access_count + lockout_count FROM users WHERE id = @u", ("u", t.OwnerUserId)));
    }

    [Fact]
    public async Task Each_consecutive_lockout_lasts_longer()
    {
        var t = await fixture.RegisterTenantAsync();
        async Task LockAsync()
        {
            for (var i = 0; i < MaxAttempts; i++)
            {
                await fixture.TryLoginAsync(t.OwnerEmail, $"Wrong-Passw0rd-{i}");
            }
        }

        await LockAsync();
        var first = await fixture.ScalarAsOwnerAsync<DateTime>("SELECT lockout_end_utc FROM users WHERE id = @u", ("u", t.OwnerUserId)) - DateTime.UtcNow;

        await fixture.ExecuteAsOwnerAsync("UPDATE users SET lockout_end_utc = now() - interval '1 second' WHERE id = @u", ("u", t.OwnerUserId));
        await LockAsync();
        var second = await fixture.ScalarAsOwnerAsync<DateTime>("SELECT lockout_end_utc FROM users WHERE id = @u", ("u", t.OwnerUserId)) - DateTime.UtcNow;

        Assert.InRange(first, TimeSpan.FromMinutes(13), TimeSpan.FromMinutes(16));
        Assert.InRange(second, TimeSpan.FromMinutes(28), TimeSpan.FromMinutes(31));
    }

    [Fact]
    public async Task Parallel_guesses_cannot_outrun_the_attempt_counter()
    {
        var t = await fixture.RegisterTenantAsync();

        // 40 wrong guesses at the same instant.
        var responses = await Task.WhenAll(Enumerable.Range(0, 40).Select(i => fixture.TryLoginAsync(t.OwnerEmail, $"Guess-Passw0rd-{i}")));
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));

        // Only MaxAttempts of them were ever compared against the hash: each attempt is reserved
        // atomically before verification, so a flood buys an attacker nothing over five tries.
        var verified = await fixture.ScalarAsOwnerAsync<long>(
            "SELECT count(*) FROM auth_audit_events WHERE user_id = @u AND event_type = 'login.failed' AND detail = 'wrong_password'", ("u", t.OwnerUserId));
        var refused = await fixture.ScalarAsOwnerAsync<long>(
            "SELECT count(*) FROM auth_audit_events WHERE user_id = @u AND event_type = 'login.failed' AND detail = 'locked_out'", ("u", t.OwnerUserId));
        Assert.Equal(MaxAttempts, verified);
        Assert.Equal(40 - MaxAttempts, refused);

        // And the account is now locked, even for the right password.
        await HttpAssert.ProblemAsync(await fixture.TryLoginAsync(t.OwnerEmail, t.OwnerPassword), HttpStatusCode.Unauthorized, "invalid_credentials");
    }

    [Fact]
    public async Task A_successful_sign_in_resets_the_failure_counter()
    {
        var t = await fixture.RegisterTenantAsync();
        for (var round = 0; round < 3; round++)
        {
            for (var i = 0; i < MaxAttempts - 1; i++)
            {
                await HttpAssert.ProblemAsync(await fixture.TryLoginAsync(t.OwnerEmail, "Wrong-Passw0rd!"), HttpStatusCode.Unauthorized, "invalid_credentials");
            }

            await fixture.LoginAsync(t.OwnerEmail, t.OwnerPassword);
        }
    }

    [Fact]
    public async Task Password_reset_lifts_a_lockout()
    {
        var t = await fixture.RegisterTenantAsync();
        for (var i = 0; i < MaxAttempts; i++)
        {
            await fixture.TryLoginAsync(t.OwnerEmail, "Wrong-Passw0rd!");
        }

        await HttpAssert.ProblemAsync(await fixture.TryLoginAsync(t.OwnerEmail, t.OwnerPassword), HttpStatusCode.Unauthorized, "invalid_credentials");

        using var anonymous = fixture.CreateClient();
        await anonymous.PostAsJsonAsync("/api/v1/auth/forgot-password", new EmailRequest(t.OwnerEmail), ApiFixture.Json);
        var token = CapturingEmailSender.TokenFrom(await fixture.Emails.WaitForAsync(t.OwnerEmail, "Reset your password"));
        (await anonymous.PostAsJsonAsync("/api/v1/auth/reset-password", new ResetPasswordRequest(token, "Brand-New-Passw0rd!"), ApiFixture.Json)).EnsureSuccessStatusCode();

        await fixture.LoginAsync(t.OwnerEmail, "Brand-New-Passw0rd!");
    }

    [Fact]
    public async Task Authentication_endpoints_are_rate_limited_per_client()
    {
        // A second host on the same database with production-like limits.
        await using var strict = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.PostConfigure<RateLimitingOptions>(options =>
            {
                options.Credentials = new RateLimitRule { PermitLimit = 3, WindowSeconds = 60 };
                options.Email = new RateLimitRule { PermitLimit = 2, WindowSeconds = 60 };
            })));
        using var client = strict.CreateClient();

        for (var i = 0; i < 3; i++)
        {
            var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(fixture.NewEmail("ghost"), "Wrong-Passw0rd!", null), ApiFixture.Json);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }

        var limited = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(fixture.NewEmail("ghost"), "Wrong-Passw0rd!", null), ApiFixture.Json);
        var problem = await HttpAssert.ProblemAsync(limited, HttpStatusCode.TooManyRequests, "too_many_requests");
        Assert.True(limited.Headers.Contains("Retry-After"), problem.ToString());

        // The limit is shared by every credential-checking endpoint, so switching endpoints does not help...
        await HttpAssert.ProblemAsync(
            await client.PostAsJsonAsync("/api/v1/auth/mfa/verify", new MfaVerifyRequest(new string('a', 43), "123456", null), ApiFixture.Json),
            HttpStatusCode.TooManyRequests, "too_many_requests");
        await HttpAssert.ProblemAsync(
            await client.PostAsJsonAsync("/api/v1/auth/reset-password", new ResetPasswordRequest(new string('a', 43), "Brand-New-Passw0rd!"), ApiFixture.Json),
            HttpStatusCode.TooManyRequests, "too_many_requests");

        // ...and mail-sending endpoints have their own, tighter budget.
        for (var i = 0; i < 2; i++)
        {
            Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new EmailRequest(fixture.NewEmail("x")), ApiFixture.Json)).StatusCode);
        }

        await HttpAssert.ProblemAsync(
            await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new EmailRequest(fixture.NewEmail("x")), ApiFixture.Json),
            HttpStatusCode.TooManyRequests, "too_many_requests");

        // Ordinary API traffic is not throttled by these policies.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live")).StatusCode);
    }

    [Fact]
    public async Task Repeated_reset_requests_for_one_account_send_one_mail_within_the_cooldown()
    {
        await using var cooled = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.PostConfigure<Solvix.Modules.Identity.Configuration.IdentitySecurityOptions>(o => o.EmailCooldownSeconds = 60)));
        var t = await fixture.RegisterTenantAsync();
        using var client = cooled.CreateClient();

        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new EmailRequest(t.OwnerEmail), ApiFixture.Json)).StatusCode);
        }

        await fixture.Emails.WaitForAsync(t.OwnerEmail, "Reset your password");
        await Task.Delay(300);
        Assert.Equal(1, fixture.Emails.Count(t.OwnerEmail, "Reset your password"));
    }
}
