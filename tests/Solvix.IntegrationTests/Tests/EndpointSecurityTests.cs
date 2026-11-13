namespace Solvix.IntegrationTests.Tests;

using System.Net;
using System.Net.Http.Json;
using Npgsql;
using Solvix.IntegrationTests.Fixtures;
using Solvix.Modules.Audit.Application;
using Solvix.Modules.EndpointManagement.Application.Patches;
using Solvix.SharedKernel.Auditing;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Results;

/// <summary>Audit history and database-level protection of the Phase 09 tables.</summary>
[Collection(ApiCollection.Name)]
public sealed class EndpointSecurityTests(ApiFixture fixture)
{
    [Fact]
    public async Task Administrative_actions_are_audited_with_actor_and_correlation_id()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var device = await fixture.EnrollDeviceAsync(await fixture.CreateEnrollmentTokenAsync(tenant), "AUDIT-PC");
        await device.Api.UploadInventoryAsync(EndpointTestKit.Inventory([EndpointTestKit.Update(Guid.NewGuid().ToString(), "KB5040001 Cumulative Update")]), CancellationToken.None);

        using var owner = fixture.CreateClient(tenant.Token);
        var patch = Assert.Single((await HttpAssert.OkAsync<PagedResult<PatchDto>>(await owner.GetAsync("/api/v1/patches"))).Items);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/patches/approve")
        {
            Content = JsonContent.Create(new PatchDecisionRequest([patch.Id], "CAB approved"), options: ApiFixture.Json),
        };
        request.Headers.Add("X-Correlation-Id", "change-4711");
        (await owner.SendAsync(request)).EnsureSuccessStatusCode();

        var entries = await HttpAssert.OkAsync<PagedResult<AuditEntryDto>>(await owner.GetAsync($"/api/v1/audit?action=patch&entityId={patch.Id}"));
        var approved = Assert.Single(entries.Items, e => e.Action == "patch.approved");
        Assert.Equal(AuditActorType.User, approved.ActorType);
        Assert.Equal(tenant.OwnerEmail, approved.ActorName);
        Assert.Equal("change-4711", approved.CorrelationId);
        Assert.Equal("CAB approved", approved.Details!.Value.GetProperty("note").GetString());

        var enrolled = await HttpAssert.OkAsync<PagedResult<AuditEntryDto>>(await owner.GetAsync($"/api/v1/audit?action=device.enrolled"));
        Assert.Equal(device.DeviceId, Assert.Single(enrolled.Items).EntityId);

        // Members cannot read the audit log; other tenants never see it.
        using var member = fixture.CreateClient(await fixture.AddUserAsync(tenant, TenantRole.Member));
        Assert.Equal(HttpStatusCode.Forbidden, (await member.GetAsync("/api/v1/audit")).StatusCode);
        var other = await fixture.RegisterTenantAsync();
        using var otherOwner = fixture.CreateClient(other.Token);
        Assert.DoesNotContain(
            (await HttpAssert.OkAsync<PagedResult<AuditEntryDto>>(await otherOwner.GetAsync("/api/v1/audit?pageSize=200"))).Items,
            e => e.EntityId == patch.Id || e.EntityId == device.DeviceId);
    }

    [Fact]
    public async Task Audit_log_is_append_only_even_for_the_schema_owner()
    {
        var tenant = await fixture.RegisterTenantAsync();
        await fixture.EnrollDeviceAsync(await fixture.CreateEnrollmentTokenAsync(tenant), "IMMUTABLE");

        foreach (var connectionString in new[] { fixture.Database.AppConnectionString, fixture.Database.MigratorConnectionString })
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await ExecuteAsync(connection, "SELECT set_config('app.tenant_id', @t, false)", ("t", tenant.TenantId.ToString()));
            Assert.True(await ScalarAsync(connection, "SELECT count(*) FROM audit_entries") > 0);

            foreach (var sql in new[] { "UPDATE audit_entries SET summary = 'tampered'", "DELETE FROM audit_entries", "TRUNCATE audit_entries" })
            {
                var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, sql));
                Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
            }
        }
    }

    [Fact]
    public async Task Raw_sql_cannot_read_or_write_another_tenants_devices_patches_or_policies()
    {
        var a = await fixture.RegisterTenantAsync();
        var b = await fixture.RegisterTenantAsync();
        var deviceA = await fixture.EnrollDeviceAsync(await fixture.CreateEnrollmentTokenAsync(a), "RLS-A");
        await deviceA.Api.UploadInventoryAsync(EndpointTestKit.Inventory([EndpointTestKit.Update(Guid.NewGuid().ToString(), "KB1 Update")]), CancellationToken.None);
        await fixture.EnrollDeviceAsync(await fixture.CreateEnrollmentTokenAsync(b), "RLS-B");

        await using var connection = new NpgsqlConnection(fixture.Database.AppConnectionString);
        await connection.OpenAsync();
        Assert.Equal(0L, await ScalarAsync(connection, "SELECT count(*) FROM devices"));

        await ExecuteAsync(connection, "SELECT set_config('app.tenant_id', @t, false)", ("t", b.TenantId.ToString()));
        Assert.Equal(1L, await ScalarAsync(connection, "SELECT count(*) FROM devices"));
        foreach (var table in new[] { "patches", "device_patch_states", "enrollment_tokens" })
        {
            Assert.Equal(0L, await ScalarAsync(connection, $"SELECT count(*) FROM {table} WHERE tenant_id = @t", ("t", a.TenantId)));
        }

        Assert.Equal(0, await ExecuteAsync(connection, "UPDATE devices SET hostname = 'pwned' WHERE id = @id", ("id", deviceA.DeviceId)));
        var insert = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(
            connection,
            "INSERT INTO device_groups (id, tenant_id, name, created_at_utc, updated_at_utc) VALUES (gen_random_uuid(), @t, 'evil', now(), now())",
            ("t", a.TenantId)));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, insert.SqlState);
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

    private static async Task<long> ScalarAsync(NpgsqlConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
