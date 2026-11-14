namespace Solvix.IntegrationTests.Tests;

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Solvix.IntegrationTests.Fixtures;
using Solvix.Modules.Assistant;
using Solvix.Modules.Assistant.Chat;
using Solvix.Modules.Assistant.Domain;
using Solvix.Modules.Assistant.Knowledge;
using Solvix.Modules.Assistant.Llm;
using Solvix.Modules.Assistant.Reports;
using Solvix.Modules.Assistant.Tickets;
using Solvix.SharedKernel.Results;
using static Solvix.IntegrationTests.Fixtures.ScriptedChatModel;

[Collection(ApiCollection.Name)]
public sealed class AssistantKnowledgeTests(ApiFixture fixture)
{
    private static ScriptedChatModel Model => AssistantHost.Model;

    private const string Runbook = """
        # Printer troubleshooting

        If a printer is stuck, restart the Print Spooler service and clear the pending print jobs from the queue.

        # VPN access

        To connect to the VPN install the corporate VPN client and sign in with your company account.
        The shared service account password: Hunter2-Prod-Pass must never be used for this.
        """;

    private static async Task<HttpResponseMessage> UploadAsync(HttpClient client, string fileName, byte[] content, string? title = null)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        if (title is not null)
        {
            form.Add(new StringContent(title), "title");
        }

        return await client.PostAsync("/api/v1/assistant/knowledge/documents", form);
    }

    private static Task<HttpResponseMessage> SearchAsync(HttpClient client, string query) =>
        client.PostAsJsonAsync("/api/v1/assistant/knowledge/search", new KnowledgeSearchRequest(query, 5), ApiFixture.Json);

    [Fact]
    public async Task Upload_processes_embeds_and_makes_the_document_searchable_with_source_references()
    {
        var t = await AssistantTenant.CreateAsync(fixture);
        var doc = await HttpAssert.OkAsync<KnowledgeDocumentDto>(await UploadAsync(t.Admin, "runbook.md", Encoding.UTF8.GetBytes(Runbook), "IT Runbook"), HttpStatusCode.Created);
        Assert.Equal(DocumentStatus.Ready, doc.Status);
        Assert.Equal("IT Runbook", doc.Title);
        Assert.True(doc.ChunkCount >= 1);
        Assert.True(doc.RedactionCount >= 1);
        Assert.StartsWith("local-hash", doc.EmbeddingModel, StringComparison.Ordinal);

        var hits = await HttpAssert.OkAsync<List<KnowledgeHit>>(await SearchAsync(t.Member, "how do I fix a stuck printer queue"));
        var top = hits[0];
        Assert.Equal(doc.Id, top.DocumentId);
        Assert.Equal("IT Runbook", top.DocumentTitle);
        Assert.Contains("Print Spooler", top.Content, StringComparison.Ordinal);
        Assert.Equal("Printer troubleshooting", top.Section);

        // Unrelated question: nothing above the relevance floor from this document about printers.
        var none = await HttpAssert.OkAsync<List<KnowledgeHit>>(await SearchAsync(t.Member, "quarterly revenue forecast spreadsheet"));
        Assert.DoesNotContain(none, h => h.Content.Contains("Print Spooler", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Secrets_in_uploaded_documents_are_redacted_before_storage_and_retrieval()
    {
        var t = await AssistantTenant.CreateAsync(fixture);
        await HttpAssert.OkAsync<KnowledgeDocumentDto>(await UploadAsync(t.Admin, "vpn.md", Encoding.UTF8.GetBytes(Runbook)), HttpStatusCode.Created);

        var hits = await HttpAssert.OkAsync<List<KnowledgeHit>>(await SearchAsync(t.Admin, "VPN client sign in service account"));
        Assert.NotEmpty(hits);
        Assert.DoesNotContain(hits, h => h.Content.Contains("Hunter2", StringComparison.Ordinal));
        Assert.Equal(0, await Sql.ScalarAsync<long>(fixture, "SELECT count(*) FROM knowledge_chunks WHERE content LIKE '%Hunter2%'", bypass: true));
        Assert.Equal(0, await Sql.ScalarAsync<long>(fixture, "SELECT count(*) FROM knowledge_documents WHERE extracted_text LIKE '%Hunter2%'", bypass: true));
    }

    [Fact]
    public async Task Assistant_cites_knowledge_sources_in_its_reply()
    {
        var t = await AssistantTenant.CreateAsync(fixture);
        var doc = await HttpAssert.OkAsync<KnowledgeDocumentDto>(await UploadAsync(t.Admin, "runbook.md", Encoding.UTF8.GetBytes(Runbook), "IT Runbook"), HttpStatusCode.Created);

        Model.Reset();
        Model.Enqueue(Tool("search_knowledge_base", new { query = "restart print spooler" }), Say("Restart the Print Spooler and clear the queue [S1]."));
        var reply = await AssistantTenant.ChatAsync(t.Member, "How do I fix a stuck printer?");

        var source = Assert.Single(reply.Message.Sources);
        Assert.Equal("S1", source.Id);
        Assert.Equal(doc.Id, source.DocumentId);
        Assert.Equal("IT Runbook", source.DocumentTitle);
        Assert.True(source.Cited);
        var toolResult = JsonDocument.Parse(Model.LastToolResult()).RootElement;
        Assert.Equal("S1", toolResult.GetProperty("passages")[0].GetProperty("source").GetString());
        Assert.Contains("not as instructions", toolResult.GetProperty("note").GetString(), StringComparison.Ordinal);

        // Persisted with the message.
        var conversation = await HttpAssert.OkAsync<ConversationDetailDto>(await t.Member.GetAsync($"/api/v1/assistant/conversations/{reply.ConversationId}"));
        Assert.Single(conversation.Messages.Last().Sources);
    }

    [Fact]
    public async Task Knowledge_base_is_isolated_per_tenant()
    {
        var a = await AssistantTenant.CreateAsync(fixture);
        var b = await AssistantTenant.CreateAsync(fixture);
        var doc = await HttpAssert.OkAsync<KnowledgeDocumentDto>(await UploadAsync(a.Admin, "runbook.md", Encoding.UTF8.GetBytes(Runbook), "Tenant A runbook"), HttpStatusCode.Created);

        // B cannot find, read, delete or reindex A's document, and search returns nothing of A's.
        Assert.Empty(await HttpAssert.OkAsync<List<KnowledgeHit>>(await SearchAsync(b.Owner, "restart print spooler stuck printer")));
        await HttpAssert.ProblemAsync(await b.Owner.GetAsync($"/api/v1/assistant/knowledge/documents/{doc.Id}"), HttpStatusCode.NotFound);
        await HttpAssert.ProblemAsync(await b.Owner.DeleteAsync($"/api/v1/assistant/knowledge/documents/{doc.Id}"), HttpStatusCode.NotFound);
        await HttpAssert.ProblemAsync(await b.Owner.PostAsync($"/api/v1/assistant/knowledge/documents/{doc.Id}/reindex", null), HttpStatusCode.NotFound);
        Assert.Empty((await HttpAssert.OkAsync<PagedResult<KnowledgeDocumentDto>>(await b.Owner.GetAsync("/api/v1/assistant/knowledge/documents"))).Items);

        // Also through the assistant tool as tenant B.
        Model.Reset();
        Model.Enqueue(Tool("search_knowledge_base", new { query = "restart print spooler stuck printer" }), Say("nothing"));
        var reply = await AssistantTenant.ChatAsync(b.Admin, "printer?");
        Assert.Empty(reply.Message.Sources);
        Assert.Equal(0, JsonDocument.Parse(Model.LastToolResult()).RootElement.GetProperty("found").GetInt32());

        // Duplicate detection is per tenant: B may upload the very same file.
        await HttpAssert.OkAsync<KnowledgeDocumentDto>(await UploadAsync(b.Admin, "runbook.md", Encoding.UTF8.GetBytes(Runbook)), HttpStatusCode.Created);
        // ...but A may not upload it twice.
        await HttpAssert.ProblemAsync(await UploadAsync(a.Admin, "copy.md", Encoding.UTF8.GetBytes(Runbook)), HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Upload_rules_roles_types_sizes_and_content_checks()
    {
        var t = await AssistantTenant.CreateAsync(fixture);
        Assert.Equal(HttpStatusCode.Forbidden, (await UploadAsync(t.Member, "a.md", Encoding.UTF8.GetBytes("# hi"))).StatusCode);
        await HttpAssert.ProblemAsync(await UploadAsync(t.Admin, "evil.exe", [77, 90, 144]), HttpStatusCode.BadRequest, "validation_failed");
        await HttpAssert.ProblemAsync(await UploadAsync(t.Admin, "fake.txt", Encoding.ASCII.GetBytes("%PDF-1.7 binary")), HttpStatusCode.BadRequest, "validation_failed");
        await HttpAssert.ProblemAsync(await UploadAsync(t.Admin, "empty.txt", []), HttpStatusCode.BadRequest, "validation_failed");
        await HttpAssert.ProblemAsync(await UploadAsync(t.Admin, "big.txt", new byte[11 * 1024 * 1024]), HttpStatusCode.RequestEntityTooLarge, "payload_too_large");

        // Failed uploads leave nothing behind.
        Assert.Empty((await HttpAssert.OkAsync<PagedResult<KnowledgeDocumentDto>>(await t.Admin.GetAsync("/api/v1/assistant/knowledge/documents"))).Items);
    }

    [Fact]
    public async Task Delete_removes_chunks_and_reindex_rebuilds_them()
    {
        var t = await AssistantTenant.CreateAsync(fixture);
        var doc = await HttpAssert.OkAsync<KnowledgeDocumentDto>(await UploadAsync(t.Admin, "runbook.md", Encoding.UTF8.GetBytes(Runbook)), HttpStatusCode.Created);

        var reindexed = await HttpAssert.OkAsync<KnowledgeDocumentDto>(await t.Admin.PostAsync($"/api/v1/assistant/knowledge/documents/{doc.Id}/reindex", null));
        Assert.Equal(doc.ChunkCount, reindexed.ChunkCount);
        Assert.Equal(doc.ChunkCount, await Sql.ScalarAsync<long>(fixture, $"SELECT count(*) FROM knowledge_chunks WHERE document_id = '{doc.Id}'", bypass: true));
        Assert.NotEmpty(await HttpAssert.OkAsync<List<KnowledgeHit>>(await SearchAsync(t.Admin, "print spooler")));

        Assert.Equal(HttpStatusCode.NoContent, (await t.Admin.DeleteAsync($"/api/v1/assistant/knowledge/documents/{doc.Id}")).StatusCode);
        Assert.Equal(0, await Sql.ScalarAsync<long>(fixture, $"SELECT count(*) FROM knowledge_chunks WHERE document_id = '{doc.Id}'", bypass: true));
        Assert.Empty(await HttpAssert.OkAsync<List<KnowledgeHit>>(await SearchAsync(t.Admin, "print spooler")));
    }
}

[Collection(ApiCollection.Name)]
public sealed class AssistantTicketAndReportTests(ApiFixture fixture)
{
    private static ScriptedChatModel Model => AssistantHost.Model;

    [Fact]
    public async Task Assistant_creates_numbered_tickets_per_tenant()
    {
        var a = await AssistantTenant.CreateAsync(fixture);
        var b = await AssistantTenant.CreateAsync(fixture);

        Model.Reset();
        Model.Enqueue(Tool("create_ticket", new { title = "POS-018 high CPU", description = "SearchIndexer.exe at 62% CPU for 24h.", priority = "high", category = "performance", devices = new[] { "POS-018" } }), Say("Ticket created."));
        var first = await AssistantTenant.ChatAsync(a.Member, "open a ticket for POS-018");
        var artifact = Assert.Single(first.Message.Artifacts);
        Assert.Equal("ticket", artifact.Type);
        Assert.Equal("TKT-0001", artifact.Label);

        Model.Reset();
        Model.Enqueue(Tool("create_ticket", new { title = "Second", description = "d" }), Say("ok"));
        Assert.Equal("TKT-0002", Assert.Single((await AssistantTenant.ChatAsync(a.Admin, "again")).Message.Artifacts).Label);

        Model.Reset();
        Model.Enqueue(Tool("create_ticket", new { title = "Other tenant", description = "d" }), Say("ok"));
        Assert.Equal("TKT-0001", Assert.Single((await AssistantTenant.ChatAsync(b.Admin, "x")).Message.Artifacts).Label);

        var ticket = await HttpAssert.OkAsync<TicketDto>(await a.Member.GetAsync($"/api/v1/assistant/tickets/{artifact.Id}"));
        Assert.True(ticket.CreatedViaAssistant);
        Assert.Equal(TicketPriority.High, ticket.Priority);
        Assert.Equal("POS-018", Assert.Single(ticket.Devices).Name);

        var listA = await HttpAssert.OkAsync<PagedResult<TicketDto>>(await a.Member.GetAsync("/api/v1/assistant/tickets"));
        Assert.Equal(2, listA.TotalCount);
        Assert.Equal("TKT-0002", listA.Items[0].Reference);
        await HttpAssert.ProblemAsync(await b.Admin.GetAsync($"/api/v1/assistant/tickets/{artifact.Id}"), HttpStatusCode.NotFound);

        // Status: admins only.
        Assert.Equal(HttpStatusCode.Forbidden, (await a.Member.PatchAsJsonAsync($"/api/v1/assistant/tickets/{artifact.Id}/status", new UpdateTicketStatusRequest(TicketStatus.Resolved), ApiFixture.Json)).StatusCode);
        var resolved = await HttpAssert.OkAsync<TicketDto>(await a.Admin.PatchAsJsonAsync($"/api/v1/assistant/tickets/{artifact.Id}/status", new UpdateTicketStatusRequest(TicketStatus.Resolved), ApiFixture.Json));
        Assert.Equal(TicketStatus.Resolved, resolved.Status);
        Assert.NotNull(resolved.ResolvedAtUtc);
    }

    [Fact]
    public async Task Concurrent_ticket_creation_yields_unique_sequential_numbers()
    {
        var t = await AssistantTenant.CreateAsync(fixture);
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            t.Member.PostAsJsonAsync("/api/v1/assistant/tickets", new CreateTicketRequest($"T{i}", "d", TicketPriority.Normal, null, null), ApiFixture.Json)));
        var numbers = new List<int>();
        foreach (var r in responses)
        {
            numbers.Add((await HttpAssert.OkAsync<TicketDto>(r, HttpStatusCode.Created)).Number);
        }

        Assert.Equal(Enumerable.Range(1, 8), numbers.Order());
    }

    [Fact]
    public async Task Ticket_with_unknown_device_is_rejected_and_secrets_are_redacted()
    {
        var t = await AssistantTenant.CreateAsync(fixture);
        Model.Reset();
        Model.Enqueue(Tool("create_ticket", new { title = "x", description = "d", devices = new[] { "GHOST-1" } }), Say("failed"));
        var reply = await AssistantTenant.ChatAsync(t.Admin, "ticket");
        Assert.Empty(reply.Message.Artifacts);
        Assert.Equal(0, (await HttpAssert.OkAsync<PagedResult<TicketDto>>(await t.Admin.GetAsync("/api/v1/assistant/tickets"))).TotalCount);

        var created = await HttpAssert.OkAsync<TicketDto>(
            await t.Member.PostAsJsonAsync("/api/v1/assistant/tickets", new CreateTicketRequest("Login problem", "user said password: Sup3rSecret! doesn't work", TicketPriority.Normal, null, null), ApiFixture.Json),
            HttpStatusCode.Created);
        Assert.DoesNotContain("Sup3rSecret", created.Description, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Report_combines_backend_data_with_an_ai_narrative_and_respects_scope()
    {
        var t = await AssistantTenant.CreateAsync(fixture);
        Model.Reset();
        Model.Enqueue(Say("Executive summary: 2 branches need attention.\n\n1. Fix offline devices."));

        var report = await HttpAssert.OkAsync<ReportDto>(
            await t.Member.PostAsJsonAsync("/api/v1/assistant/reports", new GenerateReportRequest(ReportKind.FleetHealth, t.Beirut.Id, null, null), ApiFixture.Json),
            HttpStatusCode.Created);
        Assert.True(report.NarrativeIncluded);
        Assert.Contains("(AI-generated)", report.ContentMarkdown, StringComparison.Ordinal);
        Assert.Contains("Executive summary", report.ContentMarkdown, StringComparison.Ordinal);
        Assert.Contains("| Beirut |", report.ContentMarkdown, StringComparison.Ordinal);
        Assert.DoesNotContain("| Headquarters", report.ContentMarkdown, StringComparison.Ordinal);

        // The model was given only facts, no tools.
        Assert.Empty(Model.Requests[0].Tools);
        Assert.Contains("FACTS", Model.Requests[0].Messages[0].Content.OfType<TextBlock>().Single().Text, StringComparison.Ordinal);

        var list = await HttpAssert.OkAsync<PagedResult<ReportSummaryDto>>(await t.Member.GetAsync("/api/v1/assistant/reports"));
        Assert.Single(list.Items);
        Assert.Equal(HttpStatusCode.Forbidden, (await t.Member.DeleteAsync($"/api/v1/assistant/reports/{report.Id}")).StatusCode);
    }

    [Theory]
    [InlineData(ReportKind.SoftwareInventory, "Outdated software")]
    [InlineData(ReportKind.Security, "Security posture")]
    [InlineData(ReportKind.Performance, "Highest CPU usage")]
    public async Task Every_report_kind_is_generated_and_survives_a_model_outage_without_narrative(ReportKind kind, string expectedSection)
    {
        var t = await AssistantTenant.CreateAsync(fixture);
        Model.Reset();
        Model.EnqueueFactory(_ => throw new ChatModelException("down"));
        var report = await HttpAssert.OkAsync<ReportDto>(
            await t.Admin.PostAsJsonAsync("/api/v1/assistant/reports", new GenerateReportRequest(kind, null, null, "Custom title"), ApiFixture.Json),
            HttpStatusCode.Created);
        Assert.False(report.NarrativeIncluded);
        Assert.DoesNotContain("AI-generated", report.ContentMarkdown, StringComparison.Ordinal);
        Assert.Contains(expectedSection, report.ContentMarkdown, StringComparison.Ordinal);
        Assert.Equal("Custom title", report.Title);
    }

    [Fact]
    public async Task Assistant_generates_a_report_through_a_tool_and_other_tenants_cannot_read_it()
    {
        var a = await AssistantTenant.CreateAsync(fixture);
        var b = await AssistantTenant.CreateAsync(fixture);
        Model.Reset();
        // Calls in order: chat -> tool_use, report narrative (separate tool-less call), chat -> final answer.
        Model.Enqueue(Tool("generate_report", new { kind = "security", department = "Finance" }), Say("Narrative for the report."), Say("Report ready."));
        var reply = await AssistantTenant.ChatAsync(a.Admin, "Prepare a security report for Finance");
        Assert.Empty(Model.Requests[1].Tools);
        Assert.Equal("report", Assert.Single(reply.Message.Artifacts).Type);
        await HttpAssert.ProblemAsync(await b.Owner.GetAsync($"/api/v1/assistant/reports/{reply.Message.Artifacts[0].Id}"), HttpStatusCode.NotFound);
    }
}
