namespace Solvix.IntegrationTests.Tests;

using System.Net.Http.Json;
using System.Text;
using Npgsql;
using Solvix.IntegrationTests.Fixtures;
using Solvix.Modules.Assistant.Domain;
using Solvix.Modules.Assistant.Reports;
using Solvix.Modules.Assistant.Tickets;
using static Solvix.IntegrationTests.Fixtures.ScriptedChatModel;

/// <summary>Row-Level Security on every Phase 11 table, proven with raw SQL that bypasses EF and the API.</summary>
[Collection(ApiCollection.Name)]
public sealed class AssistantIsolationTests(ApiFixture fixture)
{
    [Fact]
    public async Task Every_assistant_table_is_invisible_and_unwritable_across_tenants()
    {
        var a = await AssistantTenant.CreateAsync(fixture);
        var b = await AssistantTenant.CreateAsync(fixture);
        var model = AssistantHost.Model;

        // Populate every table for tenant A through the real API.
        model.Reset();
        model.Enqueue(
            Tool("propose_remediation", new { action = "refresh_inventory", devices = new[] { "POS-001" }, title = "Refresh", rationale = "r" }),
            Tool("create_ticket", new { title = "t", description = "d" }, "toolu_2"),
            Say("done"));
        await AssistantTenant.ChatAsync(a.Admin, "do things");
        using (var form = new MultipartFormDataContent())
        {
            form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("# Doc\n\nHello isolation test document.")), "file", "iso.md");
            (await a.Admin.PostAsync("/api/v1/assistant/knowledge/documents", form)).EnsureSuccessStatusCode();
        }

        model.Reset();
        model.Enqueue(Say("narrative"));
        (await a.Admin.PostAsJsonAsync("/api/v1/assistant/reports", new GenerateReportRequest(ReportKind.FleetHealth, null, null, null), ApiFixture.Json)).EnsureSuccessStatusCode();

        var tables = Solvix.Infrastructure.Persistence.Migrations.AddAssistant.TenantTables;
        foreach (var table in tables)
        {
            Assert.True(await Sql.ScalarAsync<long>(fixture, $"SELECT count(*) FROM {table}", a.TenantId) > 0, $"{table}: tenant A should see its own rows");
            Assert.Equal(0, await Sql.ScalarAsync<long>(fixture, $"SELECT count(*) FROM {table}", b.TenantId));
            Assert.Equal(0, await Sql.ScalarAsync<long>(fixture, $"SELECT count(*) FROM {table}", tenantId: null));
        }

        // Writes: tenant B's session cannot insert rows owned by A, nor touch A's rows.
        var ex = await Assert.ThrowsAnyAsync<PostgresException>(() => Sql.ExecuteAsync(
            fixture,
            $"INSERT INTO assistant_conversations (id, tenant_id, user_id, title, last_message_at_utc, is_archived, created_at_utc, updated_at_utc) VALUES (gen_random_uuid(), '{a.TenantId}', gen_random_uuid(), 'x', now(), false, now(), now())",
            b.TenantId));
        Assert.Equal("42501", ex.SqlState);
        Assert.Equal(0, await Sql.ExecuteAsync(fixture, "UPDATE assistant_tickets SET title = 'hijacked'", b.TenantId));
        Assert.Equal(0, await Sql.ExecuteAsync(fixture, "UPDATE assistant_proposals SET status = 'Succeeded'", b.TenantId));
        Assert.Equal(0, await Sql.ExecuteAsync(fixture, "DELETE FROM knowledge_chunks", b.TenantId));
        Assert.Equal(0, await Sql.ExecuteAsync(fixture, "DELETE FROM knowledge_documents", b.TenantId));
    }

    [Fact]
    public async Task Ticket_counter_is_tenant_scoped_at_the_database_level()
    {
        var a = await AssistantTenant.CreateAsync(fixture);
        await a.Member.PostAsJsonAsync("/api/v1/assistant/tickets", new CreateTicketRequest("t", "d", TicketPriority.Low, null, null), ApiFixture.Json);
        Assert.Equal(1, await Sql.ScalarAsync<long>(fixture, "SELECT last_number FROM assistant_ticket_counters", a.TenantId));
    }
}
