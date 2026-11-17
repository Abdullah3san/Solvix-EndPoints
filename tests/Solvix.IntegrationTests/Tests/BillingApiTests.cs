namespace Solvix.IntegrationTests.Tests;

using System.Net;
using System.Net.Http.Json;
using Npgsql;
using Solvix.Agent.Contracts;
using Solvix.IntegrationTests.Fixtures;
using Solvix.Modules.Billing.Application;
using Solvix.Modules.Billing.Domain;
using Solvix.Modules.Devices.Application.Devices;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Results;

/// <summary>Subscriptions, plan limits enforced on the server, invoices, tenant isolation of billing data.</summary>
[Collection(ApiCollection.Name)]
public sealed class BillingApiTests(ApiFixture fixture)
{
    [Fact]
    public async Task New_workspaces_start_a_trial_on_the_starter_plan()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var client = fixture.CreateClient(tenant.Token);
        var subscription = await HttpAssert.OkAsync<SubscriptionDto>(await client.GetAsync("/api/v1/billing/subscription"));
        Assert.Equal("starter", subscription.PlanCode);
        Assert.Equal(SubscriptionStatus.Trialing, subscription.Status);
        Assert.Equal(25, subscription.DeviceLimit);
        Assert.True(subscription.CanEnrollDevices);
        Assert.NotNull(subscription.TrialEndsAtUtc);
    }

    [Fact]
    public async Task Plan_catalog_lists_the_four_plans_with_the_agreed_device_limits()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var client = fixture.CreateClient(tenant.Token);
        var plans = await HttpAssert.OkAsync<List<PlanDto>>(await client.GetAsync("/api/v1/billing/plans"));
        Assert.Equal(["starter", "business", "professional", "enterprise"], plans.Select(p => p.Code).ToArray());
        Assert.Equal([25, 75, 200], plans.Take(3).Select(p => p.DeviceLimit!.Value).ToArray());
        var enterprise = plans[3];
        Assert.True(enterprise.IsCustom);
        Assert.Null(enterprise.DeviceLimit);
        Assert.All(plans.Take(3), p => Assert.True(p.MonthlyPriceMinor > 0 && p.AnnualPriceMinor > 0));
    }

    [Fact]
    public async Task The_device_limit_is_enforced_by_the_server_at_enrollment()
    {
        var tenant = await fixture.RegisterTenantAsync();
        await fixture.SetDeviceLimitAsync(tenant, 2);

        await fixture.EnrollNewDeviceAsync(tenant);
        await fixture.EnrollNewDeviceAsync(tenant);

        var token = await fixture.CreateTokenAsync(tenant);
        var denied = await fixture.PostEnrollAsync(token, System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256), DeviceTestKit.Identity());
        var problem = await HttpAssert.ProblemAsync(denied, HttpStatusCode.PaymentRequired, "device_limit_reached");
        Assert.Contains("2 of 2", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);

        // The denied attempt did not consume the token (the whole enrollment transaction rolled back).
        using var admin = fixture.CreateClient(tenant.Token);
        var tokens = await admin.GetStringAsync("/api/v1/enrollment-tokens");
        Assert.Contains("\"useCount\":0", tokens, StringComparison.Ordinal);

        var usage = await HttpAssert.OkAsync<UsageDto>(await admin.GetAsync("/api/v1/billing/usage"));
        Assert.Equal(2, usage.DevicesUsed);
        Assert.Equal(2, usage.DeviceLimit);
        Assert.Equal("limit", usage.Level);
        Assert.False(usage.CanEnrollDevices);
    }

    [Fact]
    public async Task Denied_enrollments_and_issued_invoices_are_counted_in_the_exported_metrics()
    {
        var measurements = new System.Collections.Concurrent.ConcurrentBag<(string Instrument, string? Reason)>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == Solvix.SharedKernel.Telemetry.SolvixMetrics.MeterName)
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            measurements.Add((instrument.Name, tags.ToArray().FirstOrDefault(t => t.Key == "reason").Value as string)));
        listener.Start();

        var tenant = await fixture.RegisterTenantAsync();
        await fixture.SetDeviceLimitAsync(tenant, 1);
        await fixture.EnrollNewDeviceAsync(tenant);
        var denied = await fixture.PostEnrollAsync(await fixture.CreateTokenAsync(tenant), System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256), DeviceTestKit.Identity());
        Assert.Equal(HttpStatusCode.PaymentRequired, denied.StatusCode);
        using var owner = fixture.CreateClient(tenant.Token);
        await owner.PostAsJsonAsync("/api/v1/billing/subscription/change", new ChangePlanRequest("business", BillingInterval.Monthly), ApiFixture.Json);

        Assert.Contains(measurements, m => m.Instrument == "solvix.devices.enrollments_denied" && m.Reason == "device_limit_reached");
        Assert.Contains(measurements, m => m.Instrument == "solvix.billing.invoices_issued");
    }

    [Fact]
    public async Task Re_enrolling_an_existing_device_does_not_need_a_free_slot_and_retiring_frees_one()
    {
        var tenant = await fixture.RegisterTenantAsync();
        await fixture.SetDeviceLimitAsync(tenant, 1);
        var identity = DeviceTestKit.Identity();
        var first = await fixture.EnrollAsync(await fixture.CreateTokenAsync(tenant), identity);

        // Same hardware again: allowed even though the plan is full.
        var again = await fixture.EnrollAsync(await fixture.CreateTokenAsync(tenant), identity);
        Assert.Equal(first.DeviceId, again.DeviceId);

        // A different machine is refused...
        var denied = await fixture.PostEnrollAsync(await fixture.CreateTokenAsync(tenant), System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256), DeviceTestKit.Identity());
        Assert.Equal(HttpStatusCode.PaymentRequired, denied.StatusCode);

        // ...until the first is retired.
        using var admin = fixture.CreateClient(tenant.Token);
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/v1/devices/{first.DeviceId}/retire", null)).StatusCode);
        await fixture.EnrollNewDeviceAsync(tenant);
    }

    [Fact]
    public async Task Concurrent_enrollments_can_never_exceed_the_limit()
    {
        var tenant = await fixture.RegisterTenantAsync();
        await fixture.SetDeviceLimitAsync(tenant, 3);

        var tokens = new List<string>();
        for (var i = 0; i < 8; i++)
        {
            tokens.Add(await fixture.CreateTokenAsync(tenant));
        }

        var responses = await Task.WhenAll(tokens.Select(t => fixture.PostEnrollAsync(t, System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256), DeviceTestKit.Identity())));
        Assert.Equal(3, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        Assert.Equal(5, responses.Count(r => r.StatusCode == HttpStatusCode.PaymentRequired));

        using var admin = fixture.CreateClient(tenant.Token);
        var devices = await HttpAssert.OkAsync<PagedResult<DeviceSummaryDto>>(await admin.GetAsync("/api/v1/devices"));
        Assert.Equal(3, devices.TotalCount);
    }

    [Fact]
    public async Task A_lapsed_subscription_blocks_new_devices_even_if_no_job_has_run()
    {
        var tenant = await fixture.RegisterTenantAsync();
        // Simulate an expired trial directly in the database; the worker is disabled in tests.
        await fixture.ExecutePlatformSqlAsync("UPDATE subscriptions SET trial_ends_at_utc = now() - interval '1 day', current_period_end_utc = now() - interval '1 day' WHERE tenant_id = @t", ("t", tenant.TenantId));

        var denied = await fixture.PostEnrollAsync(await fixture.CreateTokenAsync(tenant), System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256), DeviceTestKit.Identity());
        await HttpAssert.ProblemAsync(denied, HttpStatusCode.PaymentRequired, "subscription_expired");

        using var client = fixture.CreateClient(tenant.Token);
        var subscription = await HttpAssert.OkAsync<SubscriptionDto>(await client.GetAsync("/api/v1/billing/subscription"));
        Assert.False(subscription.CanEnrollDevices);
        Assert.Equal("subscription_expired", subscription.RestrictionCode);
    }

    [Fact]
    public async Task Existing_devices_keep_working_when_the_subscription_lapses()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var device = await fixture.EnrollNewDeviceAsync(tenant);
        await fixture.ExecutePlatformSqlAsync("UPDATE subscriptions SET status = 'Expired' WHERE tenant_id = @t", ("t", tenant.TenantId));
        using var agent = fixture.CreateDeviceClient(device.Certificate);
        Assert.Equal(HttpStatusCode.OK, (await agent.HeartbeatAsync()).StatusCode);
    }

    [Fact]
    public async Task Suspension_blocks_enrollment_and_reactivation_restores_it()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var op = fixture.OperatorClient();
        Assert.Equal(HttpStatusCode.NoContent, (await op.PostAsJsonAsync($"/api/v1/platform/billing/tenants/{tenant.TenantId}/suspend", new OperatorSuspendRequest("chargeback"), ApiFixture.Json)).StatusCode);
        var denied = await fixture.PostEnrollAsync(await fixture.CreateTokenAsync(tenant), System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256), DeviceTestKit.Identity());
        await HttpAssert.ProblemAsync(denied, HttpStatusCode.PaymentRequired, "subscription_suspended");

        Assert.Equal(HttpStatusCode.NoContent, (await op.PostAsync($"/api/v1/platform/billing/tenants/{tenant.TenantId}/reactivate", null)).StatusCode);
        await fixture.EnrollNewDeviceAsync(tenant);
    }

    [Fact]
    public async Task Buying_a_plan_issues_an_invoice_and_the_operator_confirming_payment_activates_it()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var owner = fixture.CreateClient(tenant.Token);
        var result = await HttpAssert.OkAsync<ChangePlanResult>(await owner.PostAsJsonAsync("/api/v1/billing/subscription/change", new ChangePlanRequest("business", BillingInterval.Annual), ApiFixture.Json));
        Assert.Equal("invoice_issued", result.Outcome);
        Assert.Null(result.CheckoutUrl); // manual provider
        Assert.Equal(InvoiceStatus.Open, result.Invoice!.Status);
        Assert.Equal(129000, result.Invoice.TotalMinor);
        Assert.Equal("INV-000001", result.Invoice.Number);
        Assert.Equal(SubscriptionStatus.Trialing, result.Subscription.Status); // not active until paid

        using var op = fixture.OperatorClient();
        var paid = await HttpAssert.OkAsync<InvoiceDto>(await op.PostAsJsonAsync($"/api/v1/platform/billing/tenants/{tenant.TenantId}/invoices/{result.Invoice.Id}/mark-paid", new OperatorMarkPaidRequest("bank-123"), ApiFixture.Json));
        Assert.Equal(InvoiceStatus.Paid, paid.Status);

        var subscription = await HttpAssert.OkAsync<SubscriptionDto>(await owner.GetAsync("/api/v1/billing/subscription"));
        Assert.Equal(SubscriptionStatus.Active, subscription.Status);
        Assert.Equal("business", subscription.PlanCode);
        Assert.Equal(75, subscription.DeviceLimit);
        Assert.Equal(BillingInterval.Annual, subscription.Interval);
        Assert.InRange((subscription.CurrentPeriodEndUtc - subscription.CurrentPeriodStartUtc).TotalDays, 364, 366);

        // Settling twice is idempotent.
        var second = await op.PostAsJsonAsync($"/api/v1/platform/billing/tenants/{tenant.TenantId}/invoices/{result.Invoice.Id}/mark-paid", new OperatorMarkPaidRequest("bank-123"), ApiFixture.Json);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var history = await HttpAssert.OkAsync<List<BillingHistoryItemDto>>(await owner.GetAsync("/api/v1/billing/history"));
        Assert.Contains(history, h => h.Kind == "payment" && h.Status == "Succeeded");
        Assert.Contains(history, h => h.Kind == "invoice");
        Assert.Contains(history, h => h.Kind == "subscription" && h.Title == "Activated");
    }

    [Fact]
    public async Task An_upgrade_is_prorated_and_a_downgrade_waits_for_the_period_end_and_the_fleet_size()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var owner = fixture.CreateClient(tenant.Token);
        using var op = fixture.OperatorClient();

        async Task PayAsync(ChangePlanResult r) =>
            Assert.Equal(HttpStatusCode.OK, (await op.PostAsJsonAsync($"/api/v1/platform/billing/tenants/{tenant.TenantId}/invoices/{r.Invoice!.Id}/mark-paid", new OperatorMarkPaidRequest("ref"), ApiFixture.Json)).StatusCode);

        await PayAsync(await HttpAssert.OkAsync<ChangePlanResult>(await owner.PostAsJsonAsync("/api/v1/billing/subscription/change", new ChangePlanRequest("business", BillingInterval.Monthly), ApiFixture.Json)));

        // Upgrade straight away: the unused part of the business period is credited.
        var upgrade = await HttpAssert.OkAsync<ChangePlanResult>(await owner.PostAsJsonAsync("/api/v1/billing/subscription/change", new ChangePlanRequest("professional", BillingInterval.Monthly), ApiFixture.Json));
        Assert.Equal("invoice_issued", upgrade.Outcome);
        Assert.Equal(InvoicePurpose.PlanChange, upgrade.Invoice!.Purpose);
        Assert.InRange(upgrade.Invoice.TotalMinor, 29900 - 12900, 29900 - 12800);
        var details = await HttpAssert.OkAsync<InvoiceDetailsDto>(await owner.GetAsync($"/api/v1/billing/invoices/{upgrade.Invoice.Id}"));
        Assert.Contains(details.Lines, l => l.AmountMinor < 0);
        await PayAsync(upgrade);
        Assert.Equal(200, (await HttpAssert.OkAsync<SubscriptionDto>(await owner.GetAsync("/api/v1/billing/subscription"))).DeviceLimit);

        // Downgrade: scheduled, not charged, limit unchanged until the period ends.
        var down = await HttpAssert.OkAsync<ChangePlanResult>(await owner.PostAsJsonAsync("/api/v1/billing/subscription/change", new ChangePlanRequest("starter", BillingInterval.Monthly), ApiFixture.Json));
        Assert.Equal("scheduled", down.Outcome);
        Assert.Null(down.Invoice);
        Assert.Equal("starter", down.Subscription.PendingPlanCode);
        Assert.Equal(200, down.Subscription.DeviceLimit);

        // Re-selecting the current plan cancels the scheduled change.
        var cleared = await HttpAssert.OkAsync<ChangePlanResult>(await owner.PostAsJsonAsync("/api/v1/billing/subscription/change", new ChangePlanRequest("professional", BillingInterval.Monthly), ApiFixture.Json));
        Assert.Equal("unchanged", cleared.Outcome);
        Assert.Null(cleared.Subscription.PendingPlanCode);
    }

    [Fact]
    public async Task A_downgrade_below_current_usage_is_refused()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var owner = fixture.CreateClient(tenant.Token);
        using var op = fixture.OperatorClient();
        var buy = await HttpAssert.OkAsync<ChangePlanResult>(await owner.PostAsJsonAsync("/api/v1/billing/subscription/change", new ChangePlanRequest("professional", BillingInterval.Monthly), ApiFixture.Json));
        await op.PostAsJsonAsync($"/api/v1/platform/billing/tenants/{tenant.TenantId}/invoices/{buy.Invoice!.Id}/mark-paid", new OperatorMarkPaidRequest("r"), ApiFixture.Json);

        // 26 devices do not fit the 25-device Starter plan.
        var token = await fixture.CreateTokenAsync(tenant, maxUses: 30);
        for (var i = 0; i < 26; i++)
        {
            await fixture.EnrollAsync(token);
        }

        var refused = await owner.PostAsJsonAsync("/api/v1/billing/subscription/change", new ChangePlanRequest("starter", BillingInterval.Monthly), ApiFixture.Json);
        await HttpAssert.ProblemAsync(refused, HttpStatusCode.Conflict, "usage_exceeds_plan");
    }

    [Fact]
    public async Task Enterprise_cannot_be_self_served_and_unknown_plans_are_not_found()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var owner = fixture.CreateClient(tenant.Token);
        await HttpAssert.ProblemAsync(await owner.PostAsJsonAsync("/api/v1/billing/subscription/change", new ChangePlanRequest("enterprise", BillingInterval.Monthly), ApiFixture.Json), HttpStatusCode.Conflict, "plan_requires_sales");
        await HttpAssert.ProblemAsync(await owner.PostAsJsonAsync("/api/v1/billing/subscription/change", new ChangePlanRequest("platinum", BillingInterval.Monthly), ApiFixture.Json), HttpStatusCode.NotFound);
        await HttpAssert.ProblemAsync(await owner.PostAsJsonAsync("/api/v1/billing/subscription/change", new ChangePlanRequest("Bad Code!", BillingInterval.Monthly), ApiFixture.Json), HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task Cancel_and_resume_toggle_cancellation_at_period_end()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var owner = fixture.CreateClient(tenant.Token);
        var cancelled = await HttpAssert.OkAsync<SubscriptionDto>(await owner.PostAsync("/api/v1/billing/subscription/cancel", null));
        Assert.True(cancelled.CancelAtPeriodEnd);
        Assert.True(cancelled.CanEnrollDevices);
        var resumed = await HttpAssert.OkAsync<SubscriptionDto>(await owner.PostAsync("/api/v1/billing/subscription/resume", null));
        Assert.False(resumed.CancelAtPeriodEnd);
        await HttpAssert.ProblemAsync(await owner.PostAsync("/api/v1/billing/subscription/resume", null), HttpStatusCode.Conflict, "subscription_not_cancelling");
    }

    [Fact]
    public async Task Billing_data_is_owner_only_while_plan_and_usage_are_visible_to_members()
    {
        var tenant = await fixture.RegisterTenantAsync();
        foreach (var role in new[] { Phase12Role.Member, Phase12Role.Admin })
        {
            var member = await fixture.AddRoleMemberAsync(tenant, role);
            using var client = fixture.CreateClient(member.Token);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/billing/subscription")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/billing/usage")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/billing/plans")).StatusCode);
            foreach (var path in new[] { "/api/v1/billing/invoices", "/api/v1/billing/history", "/api/v1/billing/profile" })
            {
                Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path)).StatusCode);
            }

            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/billing/subscription/change", new ChangePlanRequest("business", BillingInterval.Monthly), ApiFixture.Json)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/v1/billing/subscription/cancel", null)).StatusCode);
        }

        using var anonymous = fixture.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/billing/subscription")).StatusCode);
    }

    [Fact]
    public async Task Invoices_and_billing_profiles_are_isolated_between_tenants()
    {
        var a = await fixture.RegisterTenantAsync();
        var b = await fixture.RegisterTenantAsync();
        using var ownerA = fixture.CreateClient(a.Token);
        using var ownerB = fixture.CreateClient(b.Token);

        await ownerA.PutAsJsonAsync("/api/v1/billing/profile", new UpdateBillingProfileRequest("Acme Ltd", "ap@acme.test", "1 Main St", null, "Springfield", null, "12345", "us", "TAX-A"), ApiFixture.Json);
        var invoiceA = (await HttpAssert.OkAsync<ChangePlanResult>(await ownerA.PostAsJsonAsync("/api/v1/billing/subscription/change", new ChangePlanRequest("business", BillingInterval.Monthly), ApiFixture.Json))).Invoice!;

        // B cannot see, render, pay or list A's invoice, and has no profile of its own.
        await HttpAssert.ProblemAsync(await ownerB.GetAsync($"/api/v1/billing/invoices/{invoiceA.Id}"), HttpStatusCode.NotFound);
        Assert.Equal(HttpStatusCode.NotFound, (await ownerB.GetAsync($"/api/v1/billing/invoices/{invoiceA.Id}/html")).StatusCode);
        await HttpAssert.ProblemAsync(await ownerB.PostAsync($"/api/v1/billing/invoices/{invoiceA.Id}/checkout", null), HttpStatusCode.NotFound);
        Assert.Equal(0, (await HttpAssert.OkAsync<PagedResult<InvoiceDto>>(await ownerB.GetAsync("/api/v1/billing/invoices"))).TotalCount);
        Assert.Equal(HttpStatusCode.NoContent, (await ownerB.GetAsync("/api/v1/billing/profile")).StatusCode);

        // An operator addressing tenant B cannot settle tenant A's invoice either.
        using var op = fixture.OperatorClient();
        await HttpAssert.ProblemAsync(
            await op.PostAsJsonAsync($"/api/v1/platform/billing/tenants/{b.TenantId}/invoices/{invoiceA.Id}/mark-paid", new OperatorMarkPaidRequest("x"), ApiFixture.Json),
            HttpStatusCode.NotFound);
        var still = await HttpAssert.OkAsync<InvoiceDetailsDto>(await ownerA.GetAsync($"/api/v1/billing/invoices/{invoiceA.Id}"));
        Assert.Equal(InvoiceStatus.Open, still.Invoice.Status);

        // Tenant B's own numbering starts at 1 (counters are per tenant).
        var invoiceB = (await HttpAssert.OkAsync<ChangePlanResult>(await ownerB.PostAsJsonAsync("/api/v1/billing/subscription/change", new ChangePlanRequest("starter", BillingInterval.Monthly), ApiFixture.Json))).Invoice!;
        Assert.Equal("INV-000001", invoiceB.Number);
        Assert.Equal(invoiceA.Number, invoiceB.Number);
        Assert.NotEqual(invoiceA.Id, invoiceB.Id);
    }

    [Fact]
    public async Task The_runtime_database_role_sees_no_billing_rows_without_a_tenant_and_cannot_edit_plans()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var owner = fixture.CreateClient(tenant.Token);
        await owner.PostAsJsonAsync("/api/v1/billing/subscription/change", new ChangePlanRequest("business", BillingInterval.Monthly), ApiFixture.Json);

        await using var connection = new NpgsqlConnection(fixture.Database.AppConnectionString);
        await connection.OpenAsync();
        foreach (var table in new[] { "subscriptions", "invoices", "invoice_lines", "payments", "billing_profiles", "subscription_events", "usage_snapshots", "invoice_counters" })
        {
            await using var count = new NpgsqlCommand($"SELECT count(*) FROM {table}", connection);
            Assert.Equal(0L, (long)(await count.ExecuteScalarAsync())!);
        }

        // Plans are readable but immutable for the runtime role.
        await using (var read = new NpgsqlCommand("SELECT count(*) FROM plans", connection))
        {
            Assert.Equal(4L, (long)(await read.ExecuteScalarAsync())!);
        }

        await using var update = new NpgsqlCommand("UPDATE plans SET monthly_price_minor = 1 WHERE code = 'business'", connection);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => update.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }

    [Fact]
    public async Task Invoice_html_is_encoded_and_served_with_a_locked_down_policy()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var owner = fixture.CreateClient(tenant.Token);
        await owner.PutAsJsonAsync("/api/v1/billing/profile", new UpdateBillingProfileRequest("<script>alert(1)</script> & Co", "a@b.test", "<img src=x onerror=alert(2)>", null, null, null, null, "US", null), ApiFixture.Json);
        var invoice = (await HttpAssert.OkAsync<ChangePlanResult>(await owner.PostAsJsonAsync("/api/v1/billing/subscription/change", new ChangePlanRequest("starter", BillingInterval.Monthly), ApiFixture.Json))).Invoice!;

        var response = await owner.GetAsync($"/api/v1/billing/invoices/{invoice.Id}/html");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType!.MediaType);
        Assert.Contains("default-src 'none'", response.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        var html = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<script>alert", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<img", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        Assert.Contains(invoice.Number, html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tax_comes_from_the_operator_and_cannot_be_set_by_the_tenant()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var op = fixture.OperatorClient();
        Assert.Equal(HttpStatusCode.NoContent, (await op.PutAsJsonAsync($"/api/v1/platform/billing/tenants/{tenant.TenantId}/tax-rate", new OperatorTaxRateRequest(2000), ApiFixture.Json)).StatusCode);

        using var owner = fixture.CreateClient(tenant.Token);
        // A profile payload carrying a tax rate is simply ignored.
        var body = new StringContent("{\"legalName\":\"X\",\"billingEmail\":\"x@y.test\",\"taxRateBasisPoints\":0}", System.Text.Encoding.UTF8, "application/json");
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsync("/api/v1/billing/profile", body)).StatusCode);
        var invoice = (await HttpAssert.OkAsync<ChangePlanResult>(await owner.PostAsJsonAsync("/api/v1/billing/subscription/change", new ChangePlanRequest("starter", BillingInterval.Monthly), ApiFixture.Json))).Invoice!;
        Assert.Equal(4900, invoice.SubtotalMinor);
        Assert.Equal(980, invoice.TaxMinor);
        Assert.Equal(5880, invoice.TotalMinor);
    }

    [Fact]
    public async Task The_operator_api_is_closed_without_the_correct_key()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var url = $"/api/v1/platform/billing/tenants/{tenant.TenantId}/suspend";
        foreach (var key in new string?[] { null, "wrong-key", string.Empty, SolvixApiFactory.OperatorKey + "x" })
        {
            using var op = fixture.OperatorClient(key);
            Assert.Equal(HttpStatusCode.Unauthorized, (await op.PostAsJsonAsync(url, new OperatorSuspendRequest("x"), ApiFixture.Json)).StatusCode);
        }

        // A tenant owner's JWT is not an operator credential.
        using var owner = fixture.CreateClient(tenant.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await owner.PostAsJsonAsync(url, new OperatorSuspendRequest("x"), ApiFixture.Json)).StatusCode);

        using var legit = fixture.OperatorClient();
        await HttpAssert.ProblemAsync(await legit.PostAsJsonAsync($"/api/v1/platform/billing/tenants/{Guid.NewGuid()}/suspend", new OperatorSuspendRequest("x"), ApiFixture.Json), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Usage_history_records_the_daily_peak()
    {
        var tenant = await fixture.RegisterTenantAsync();
        await fixture.EnrollNewDeviceAsync(tenant);
        await fixture.EnrollNewDeviceAsync(tenant);
        using var owner = fixture.CreateClient(tenant.Token);
        var usage = await HttpAssert.OkAsync<UsageDto>(await owner.GetAsync("/api/v1/billing/usage"));
        Assert.Equal(2, usage.DevicesUsed);
        var today = Assert.Single(usage.History);
        Assert.Equal(2, today.PeakDevices);
        Assert.Equal(25, today.DeviceLimit);
        Assert.Equal("ok", usage.Level);
    }
}
