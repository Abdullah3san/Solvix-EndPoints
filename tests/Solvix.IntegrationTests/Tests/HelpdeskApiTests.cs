namespace Solvix.IntegrationTests.Tests;

using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Solvix.IntegrationTests.Fixtures;
using Solvix.Modules.Helpdesk.Application;
using Solvix.Modules.Helpdesk.Domain;
using Solvix.SharedKernel.Identity;
using Solvix.SharedKernel.Results;

[Collection(ApiCollection.Name)]
public sealed class HelpdeskApiTests(ApiFixture fixture)
{
    private const string Base = "/api/v1/helpdesk";

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5];

    private static async Task<TicketDetailsDto> CreateTicketAsync(HttpClient client, string subject = "Cannot print", string description = "The printer is offline.", TicketPriority? priority = null, Guid? categoryId = null, Guid? deviceId = null)
    {
        var response = await client.PostAsJsonAsync($"{Base}/tickets", new CreateTicketRequest(subject, description, priority, categoryId, deviceId, null, null), ApiFixture.Json);
        return await HttpAssert.OkAsync<TicketDetailsDto>(response, HttpStatusCode.Created);
    }

    private async Task<(TenantSession Tenant, MemberSession Tech, TechnicianDto Technician, HttpClient TechClient)> SetupWithTechnicianAsync()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var member = await fixture.AddMemberAsync(tenant);
        using var owner = fixture.CreateClient(tenant.Token);
        var technician = await HttpAssert.OkAsync<TechnicianDto>(
            await owner.PostAsJsonAsync($"{Base}/technicians", new CreateTechnicianRequest(member.UserId, 10, null, "tier 1"), ApiFixture.Json), HttpStatusCode.Created);
        return (tenant, member, technician, fixture.CreateClient(member.Token));
    }

    private async Task<T> RunScopedAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }

    [Fact]
    public async Task New_workspaces_get_default_categories_and_a_default_sla_policy()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var owner = fixture.CreateClient(tenant.Token);
        var categories = await HttpAssert.OkAsync<List<CategoryDto>>(await owner.GetAsync($"{Base}/categories"));
        Assert.Equal(["Access & Accounts", "Hardware", "Network", "Other", "Security", "Software"], categories.Select(c => c.Name).Order().ToArray());
        var policies = await HttpAssert.OkAsync<List<SlaPolicyDto>>(await owner.GetAsync($"{Base}/sla-policies"));
        var standard = Assert.Single(policies);
        Assert.True(standard.IsDefault);
        Assert.Equal(4, standard.Targets.Count);
    }

    [Fact]
    public async Task A_member_can_raise_a_ticket_and_it_gets_a_number_sla_and_audit_trail()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var member = await fixture.AddMemberAsync(tenant);
        using var client = fixture.CreateClient(member.Token);

        var first = await CreateTicketAsync(client, priority: TicketPriority.Critical); // members cannot choose priority
        var second = await CreateTicketAsync(client, "Second");
        Assert.Equal(1, first.Summary.Number);
        Assert.Equal(2, second.Summary.Number);
        Assert.Equal(TicketPriority.Medium, first.Summary.Priority); // non-staff priority is ignored
        Assert.Equal(TicketStatus.New, first.Summary.Status);
        Assert.True(first.Summary.NeedsTriage);
        Assert.NotNull(first.Summary.FirstResponseDueAtUtc);
        Assert.NotNull(first.Summary.ResolutionDueAtUtc);
        Assert.Equal("ok", first.Summary.SlaState);
        Assert.Equal(member.Email, first.Summary.RequesterEmail);

        var history = await HttpAssert.OkAsync<List<TicketEventDto>>(await client.GetAsync($"{Base}/tickets/{first.Summary.Id}/history"));
        Assert.Contains(history, e => e.Type == TicketEventType.Created);
    }

    [Fact]
    public async Task The_queue_is_ordered_by_severity_not_alphabetically()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var owner = fixture.CreateClient(tenant.Token);
        foreach (var (subject, priority) in new[] { ("low", TicketPriority.Low), ("medium", TicketPriority.Medium), ("critical", TicketPriority.Critical), ("high", TicketPriority.High) })
        {
            await CreateTicketAsync(owner, subject, "d", priority);
        }

        var list = await HttpAssert.OkAsync<PagedResult<TicketSummaryDto>>(await owner.GetAsync($"{Base}/tickets"));
        Assert.Equal(["critical", "high", "medium", "low"], list.Items.Select(t => t.Subject).ToArray());
    }

    [Fact]
    public async Task Validation_rejects_bad_tickets()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var owner = fixture.CreateClient(tenant.Token);
        await HttpAssert.ProblemAsync(await owner.PostAsJsonAsync($"{Base}/tickets", new CreateTicketRequest("", "d", null, null, null, null, null), ApiFixture.Json), HttpStatusCode.BadRequest, "validation_failed");
        await HttpAssert.ProblemAsync(await owner.PostAsJsonAsync($"{Base}/tickets", new CreateTicketRequest("s", new string('x', 20_001), null, null, null, null, null), ApiFixture.Json), HttpStatusCode.BadRequest, "validation_failed");
        await HttpAssert.ProblemAsync(await owner.PostAsJsonAsync($"{Base}/tickets", new CreateTicketRequest("s", "d", null, Guid.NewGuid(), null, null, null), ApiFixture.Json), HttpStatusCode.BadRequest, "validation_failed");
        await HttpAssert.ProblemAsync(await owner.PostAsJsonAsync($"{Base}/tickets", new CreateTicketRequest("s", "d", null, null, Guid.NewGuid(), null, null), ApiFixture.Json), HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task Members_only_see_their_own_tickets_while_staff_see_all()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var alice = await fixture.AddMemberAsync(tenant);
        var bob = await fixture.AddMemberAsync(tenant);
        using var aliceClient = fixture.CreateClient(alice.Token);
        using var bobClient = fixture.CreateClient(bob.Token);
        using var owner = fixture.CreateClient(tenant.Token);

        var aliceTicket = await CreateTicketAsync(aliceClient, "Alice's laptop");
        await CreateTicketAsync(bobClient, "Bob's monitor");

        var aliceList = await HttpAssert.OkAsync<PagedResult<TicketSummaryDto>>(await aliceClient.GetAsync($"{Base}/tickets"));
        Assert.Equal("Alice's laptop", Assert.Single(aliceList.Items).Subject);
        Assert.Equal(2, (await HttpAssert.OkAsync<PagedResult<TicketSummaryDto>>(await owner.GetAsync($"{Base}/tickets"))).TotalCount);

        // Bob cannot read, comment on, attach to, change or even detect Alice's ticket.
        var id = aliceTicket.Summary.Id;
        Assert.Equal(HttpStatusCode.NotFound, (await bobClient.GetAsync($"{Base}/tickets/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bobClient.GetAsync($"{Base}/tickets/{id}/history")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bobClient.PostAsJsonAsync($"{Base}/tickets/{id}/comments", new AddCommentRequest("hi", false), ApiFixture.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await bobClient.PostAsJsonAsync($"{Base}/tickets/{id}/status", new ChangeStatusRequest(TicketStatus.Closed, null), ApiFixture.Json)).StatusCode);

        // Searching or filtering can't widen a member's scope either.
        var sneaky = await HttpAssert.OkAsync<PagedResult<TicketSummaryDto>>(await bobClient.GetAsync($"{Base}/tickets?search=Alice&triage=true&unassigned=true"));
        Assert.Empty(sneaky.Items);
    }

    [Fact]
    public async Task Members_cannot_use_staff_operations()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var member = await fixture.AddMemberAsync(tenant);
        using var client = fixture.CreateClient(member.Token);
        var ticket = await CreateTicketAsync(client);
        var id = ticket.Summary.Id;

        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"{Base}/tickets/{id}/assign", new AssignTicketRequest(null), ApiFixture.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"{Base}/tickets/{id}/priority", new ChangePriorityRequest(TicketPriority.Critical), ApiFixture.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"{Base}/tickets/{id}/category", new CategorizeTicketRequest(null), ApiFixture.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync($"{Base}/tickets/{id}/summary", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"{Base}/tickets/{id}/comments", new AddCommentRequest("internal", true), ApiFixture.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{Base}/tickets/stats")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"{Base}/categories", new SaveCategoryRequest("X", null, null, null, null), ApiFixture.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"{Base}/technicians", new CreateTechnicianRequest(Guid.NewGuid(), null, null, null), ApiFixture.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{Base}/sla-policies")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{Base}/email-channels")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"{Base}/email-channels", new SaveInboundChannelRequest("x", null, null, null, null), ApiFixture.Json)).StatusCode);
    }

    [Fact]
    public async Task A_technician_works_a_ticket_from_assignment_to_resolution_and_the_history_records_it()
    {
        var (tenant, tech, technician, techClient) = await SetupWithTechnicianAsync();
        using var _ = techClient;
        var requester = await fixture.AddMemberAsync(tenant);
        using var requesterClient = fixture.CreateClient(requester.Token);
        var ticket = await CreateTicketAsync(requesterClient, "VPN broken");
        var id = ticket.Summary.Id;

        // The technician (a plain member with a technician profile) now sees everything.
        Assert.Equal(1, (await HttpAssert.OkAsync<PagedResult<TicketSummaryDto>>(await techClient.GetAsync($"{Base}/tickets"))).TotalCount);

        var assigned = await HttpAssert.OkAsync<TicketDetailsDto>(await techClient.PostAsJsonAsync($"{Base}/tickets/{id}/assign", new AssignTicketRequest(technician.Id), ApiFixture.Json));
        Assert.Equal(TicketStatus.Open, assigned.Summary.Status);
        Assert.Equal(technician.Id, assigned.Summary.AssigneeTechnicianId);
        Assert.Equal(tech.Email.Length > 0, assigned.Summary.AssigneeName is not null);

        var replied = await HttpAssert.OkAsync<TicketDetailsDto>(await techClient.PostAsJsonAsync($"{Base}/tickets/{id}/comments", new AddCommentRequest("Please restart the client.", false), ApiFixture.Json));
        Assert.NotNull(replied.Summary.SlaState);
        await techClient.PostAsJsonAsync($"{Base}/tickets/{id}/comments", new AddCommentRequest("Looks like the gateway cert expired.", true), ApiFixture.Json);

        // The requester sees the public reply but not the internal note.
        var asRequester = await HttpAssert.OkAsync<TicketDetailsDto>(await requesterClient.GetAsync($"{Base}/tickets/{id}"));
        Assert.Single(asRequester.Comments);
        Assert.DoesNotContain(asRequester.Comments, c => c.IsInternal);
        var asTech = await HttpAssert.OkAsync<TicketDetailsDto>(await techClient.GetAsync($"{Base}/tickets/{id}"));
        Assert.Equal(2, asTech.Comments.Count);

        // Resolving needs a resolution note.
        await HttpAssert.ProblemAsync(await techClient.PostAsJsonAsync($"{Base}/tickets/{id}/status", new ChangeStatusRequest(TicketStatus.Resolved, null), ApiFixture.Json), HttpStatusCode.BadRequest, "validation_failed");
        var resolved = await HttpAssert.OkAsync<TicketDetailsDto>(await techClient.PostAsJsonAsync($"{Base}/tickets/{id}/status", new ChangeStatusRequest(TicketStatus.Resolved, "Renewed the gateway certificate."), ApiFixture.Json));
        Assert.Equal(TicketStatus.Resolved, resolved.Summary.Status);
        var resolution = Assert.Single(resolved.Resolutions);
        Assert.Equal("Renewed the gateway certificate.", resolution.Notes);
        Assert.NotNull(resolved.AiSummary); // generated on resolution (built-in summariser)
        Assert.Equal("extractive", resolved.AiSummarySource);

        // History (resolution history) for staff includes routing and notes; for the requester only the public lifecycle.
        var staffHistory = await HttpAssert.OkAsync<List<TicketEventDto>>(await techClient.GetAsync($"{Base}/tickets/{id}/history"));
        Assert.Contains(staffHistory, e => e.Type == TicketEventType.Assigned);
        Assert.Contains(staffHistory, e => e.Type == TicketEventType.InternalNote);
        Assert.Contains(staffHistory, e => e.Type == TicketEventType.Resolved && e.Detail == "Renewed the gateway certificate.");
        var publicHistory = await HttpAssert.OkAsync<List<TicketEventDto>>(await requesterClient.GetAsync($"{Base}/tickets/{id}/history"));
        Assert.DoesNotContain(publicHistory, e => e.Type is TicketEventType.InternalNote or TicketEventType.Assigned);
        Assert.Contains(publicHistory, e => e.Type == TicketEventType.Resolved);

        // The requester answers: the ticket reopens and the resolution cycle is closed off.
        var reopened = await HttpAssert.OkAsync<TicketDetailsDto>(await requesterClient.PostAsJsonAsync($"{Base}/tickets/{id}/comments", new AddCommentRequest("Still broken.", false), ApiFixture.Json));
        Assert.Equal(TicketStatus.Open, reopened.Summary.Status);
        Assert.Equal(1, reopened.ReopenCount);
        Assert.NotNull(Assert.Single(reopened.Resolutions).ReopenedAtUtc);

        // A requester may close their own resolved ticket but not meddle with open ones.
        await HttpAssert.ProblemAsync(await requesterClient.PostAsJsonAsync($"{Base}/tickets/{id}/status", new ChangeStatusRequest(TicketStatus.Closed, null), ApiFixture.Json), HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Waiting_on_requester_pauses_the_sla_and_a_requester_reply_resumes_it()
    {
        var (tenant, _, technician, techClient) = await SetupWithTechnicianAsync();
        using var _ = techClient;
        var requester = await fixture.AddMemberAsync(tenant);
        using var requesterClient = fixture.CreateClient(requester.Token);
        var ticket = await CreateTicketAsync(requesterClient);
        var id = ticket.Summary.Id;
        await techClient.PostAsJsonAsync($"{Base}/tickets/{id}/assign", new AssignTicketRequest(technician.Id), ApiFixture.Json);

        var waiting = await HttpAssert.OkAsync<TicketDetailsDto>(await techClient.PostAsJsonAsync($"{Base}/tickets/{id}/status", new ChangeStatusRequest(TicketStatus.WaitingOnRequester, null), ApiFixture.Json));
        Assert.Equal("paused", waiting.Summary.SlaState);

        var back = await HttpAssert.OkAsync<TicketDetailsDto>(await requesterClient.PostAsJsonAsync($"{Base}/tickets/{id}/comments", new AddCommentRequest("Here are the details.", false), ApiFixture.Json));
        Assert.Equal(TicketStatus.Open, back.Summary.Status);
        Assert.NotEqual("paused", back.Summary.SlaState);
        Assert.True(back.Summary.ResolutionDueAtUtc >= ticket.Summary.ResolutionDueAtUtc);
    }

    [Fact]
    public async Task Category_defaults_auto_assign_and_least_loaded_technician_is_picked()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var m1 = await fixture.AddMemberAsync(tenant);
        var m2 = await fixture.AddMemberAsync(tenant);
        using var owner = fixture.CreateClient(tenant.Token);
        var t1 = await HttpAssert.OkAsync<TechnicianDto>(await owner.PostAsJsonAsync($"{Base}/technicians", new CreateTechnicianRequest(m1.UserId, 5, null, null), ApiFixture.Json), HttpStatusCode.Created);
        var t2 = await HttpAssert.OkAsync<TechnicianDto>(await owner.PostAsJsonAsync($"{Base}/technicians", new CreateTechnicianRequest(m2.UserId, 5, null, null), ApiFixture.Json), HttpStatusCode.Created);
        var network = (await HttpAssert.OkAsync<List<CategoryDto>>(await owner.GetAsync($"{Base}/categories"))).Single(c => c.Name == "Network");
        var hardware = (await HttpAssert.OkAsync<List<CategoryDto>>(await owner.GetAsync($"{Base}/categories"))).Single(c => c.Name == "Hardware");

        // Network always goes to technician 1.
        await owner.PutAsJsonAsync($"{Base}/categories/{network.Id}", new SaveCategoryRequest("Network", null, t1.Id, ["vpn", "wifi"], true), ApiFixture.Json);
        var net = await CreateTicketAsync(owner, categoryId: network.Id);
        Assert.Equal(t1.Id, net.Summary.AssigneeTechnicianId);
        Assert.Equal(TicketStatus.Open, net.Summary.Status);

        // Hardware has no default: balanced by load. t1 already has one ticket, so the next two go t2, then alternate.
        var h1 = await CreateTicketAsync(owner, categoryId: hardware.Id);
        Assert.Equal(t2.Id, h1.Summary.AssigneeTechnicianId);
        var h2 = await CreateTicketAsync(owner, categoryId: hardware.Id);
        Assert.NotNull(h2.Summary.AssigneeTechnicianId);

        // An unavailable technician is skipped.
        await owner.PutAsJsonAsync($"{Base}/technicians/{t2.Id}", new UpdateTechnicianRequest(true, false, 5, null, null), ApiFixture.Json);
        var h3 = await CreateTicketAsync(owner, categoryId: hardware.Id);
        Assert.Equal(t1.Id, h3.Summary.AssigneeTechnicianId);

        // No category means no auto-assignment: the ticket waits in triage.
        var untriaged = await CreateTicketAsync(owner);
        Assert.Null(untriaged.Summary.AssigneeTechnicianId);

        // Triage: categorising assigns by the category rules.
        var triaged = await HttpAssert.OkAsync<TicketDetailsDto>(await owner.PostAsJsonAsync($"{Base}/tickets/{untriaged.Summary.Id}/category", new CategorizeTicketRequest(network.Id), ApiFixture.Json));
        Assert.False(triaged.Summary.NeedsTriage);
        Assert.Equal(t1.Id, triaged.Summary.AssigneeTechnicianId);
    }

    [Fact]
    public async Task Capacity_limits_are_respected_and_duplicates_rejected()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var m = await fixture.AddMemberAsync(tenant);
        using var owner = fixture.CreateClient(tenant.Token);
        var tech = await HttpAssert.OkAsync<TechnicianDto>(await owner.PostAsJsonAsync($"{Base}/technicians", new CreateTechnicianRequest(m.UserId, 1, null, null), ApiFixture.Json), HttpStatusCode.Created);
        await HttpAssert.ProblemAsync(await owner.PostAsJsonAsync($"{Base}/technicians", new CreateTechnicianRequest(m.UserId, 1, null, null), ApiFixture.Json), HttpStatusCode.Conflict);
        await HttpAssert.ProblemAsync(await owner.PostAsJsonAsync($"{Base}/technicians", new CreateTechnicianRequest(Guid.NewGuid(), 1, null, null), ApiFixture.Json), HttpStatusCode.BadRequest, "validation_failed");

        var hardware = (await HttpAssert.OkAsync<List<CategoryDto>>(await owner.GetAsync($"{Base}/categories"))).Single(c => c.Name == "Hardware");
        var first = await CreateTicketAsync(owner, categoryId: hardware.Id);
        var second = await CreateTicketAsync(owner, categoryId: hardware.Id);
        Assert.Equal(tech.Id, first.Summary.AssigneeTechnicianId);
        Assert.Null(second.Summary.AssigneeTechnicianId); // capacity 1 reached
        await HttpAssert.ProblemAsync(await owner.PostAsJsonAsync($"{Base}/categories", new SaveCategoryRequest("hardware", null, null, null, null), ApiFixture.Json), HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Sla_policies_can_be_managed_with_exactly_one_default()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var owner = fixture.CreateClient(tenant.Token);
        var targets = new[] { new SlaTargetDto(TicketPriority.High, 30, 120), new SlaTargetDto(TicketPriority.Low, 240, 2880) };
        var created = await HttpAssert.OkAsync<SlaPolicyDto>(await owner.PostAsJsonAsync($"{Base}/sla-policies",
            new SaveSlaPolicyRequest("Business hours", false, null, true, "Europe/London", 540, 1020, 62, targets), ApiFixture.Json), HttpStatusCode.Created);
        Assert.False(created.IsDefault);

        var promoted = await HttpAssert.OkAsync<SlaPolicyDto>(await owner.PutAsJsonAsync($"{Base}/sla-policies/{created.Id}",
            new SaveSlaPolicyRequest("Business hours", true, true, true, "Europe/London", 540, 1020, 62, [new SlaTargetDto(TicketPriority.Medium, 60, 600)]), ApiFixture.Json));
        Assert.True(promoted.IsDefault);
        Assert.Equal(TicketPriority.Medium, Assert.Single(promoted.Targets).Priority);

        var all = await HttpAssert.OkAsync<List<SlaPolicyDto>>(await owner.GetAsync($"{Base}/sla-policies"));
        Assert.Single(all, p => p.IsDefault);
        await HttpAssert.ProblemAsync(await owner.DeleteAsync($"{Base}/sla-policies/{created.Id}"), HttpStatusCode.Conflict, "default_required");

        // New tickets now follow the business-hours default.
        var ticket = await CreateTicketAsync(owner);
        Assert.NotNull(ticket.Summary.ResolutionDueAtUtc);
        await HttpAssert.ProblemAsync(await owner.PostAsJsonAsync($"{Base}/sla-policies", new SaveSlaPolicyRequest("Bad", false, null, true, "Mars/Olympus", 540, 1020, 62, targets), ApiFixture.Json), HttpStatusCode.BadRequest, "validation_failed");
        await HttpAssert.ProblemAsync(await owner.PostAsJsonAsync($"{Base}/sla-policies", new SaveSlaPolicyRequest("Bad2", false, null, false, "UTC", 0, 1440, 127, [new SlaTargetDto(TicketPriority.Low, 100, 50)]), ApiFixture.Json), HttpStatusCode.BadRequest, "validation_failed");
    }

    [Fact]
    public async Task Escalation_rules_flag_reassign_and_raise_priority_exactly_once()
    {
        var (tenant, _, lead, techClient) = await SetupWithTechnicianAsync();
        using var _ = techClient;
        using var owner = fixture.CreateClient(tenant.Token);
        await owner.PostAsJsonAsync($"{Base}/escalation-rules", new SaveEscalationRuleRequest("Warn at 50%", true, EscalationTrigger.FirstResponseElapsedPercent, 50, EscalationAction.FlagOnly, null, null), ApiFixture.Json);
        await owner.PostAsJsonAsync($"{Base}/escalation-rules", new SaveEscalationRuleRequest("Raise at 80%", true, EscalationTrigger.FirstResponseElapsedPercent, 80, EscalationAction.RaisePriority, null, null), ApiFixture.Json);
        await owner.PostAsJsonAsync($"{Base}/escalation-rules", new SaveEscalationRuleRequest("Lead at 100%", true, EscalationTrigger.FirstResponseElapsedPercent, 100, EscalationAction.Reassign, lead.Id, null), ApiFixture.Json);

        var young = await CreateTicketAsync(owner, "young");
        var late = await CreateTicketAsync(owner, "late");
        var answered = await CreateTicketAsync(owner, "answered");
        await owner.PostAsJsonAsync($"{Base}/tickets/{answered.Summary.Id}/comments", new AddCommentRequest("on it", false), ApiFixture.Json);

        // Backdate: "late" is past its first-response window, "answered" too but it had a response.
        await fixture.ExecutePlatformSqlAsync(
            "UPDATE tickets SET created_at_utc = now() - interval '10 hours', first_response_due_at_utc = now() - interval '2 hours' WHERE id IN (@a, @b)",
            ("a", late.Summary.Id), ("b", answered.Summary.Id));

        var changed = await RunScopedAsync(async sp => await sp.GetRequiredService<IHelpdeskJobRunner>().RunAsync(default));
        Assert.True(changed >= 1);
        // Running again changes nothing: each rule applies once per ticket.
        await RunScopedAsync(async sp => await sp.GetRequiredService<IHelpdeskJobRunner>().RunAsync(default));

        var lateAfter = await HttpAssert.OkAsync<TicketDetailsDto>(await owner.GetAsync($"{Base}/tickets/{late.Summary.Id}"));
        Assert.Equal(3, lateAfter.Summary.EscalationLevel);
        Assert.Equal(TicketPriority.High, lateAfter.Summary.Priority);          // Medium raised once
        Assert.Equal(lead.Id, lateAfter.Summary.AssigneeTechnicianId);           // reassigned to the lead
        Assert.True(lateAfter.Summary.FirstResponseBreached);
        Assert.Equal("breached", lateAfter.Summary.SlaState);

        var history = await HttpAssert.OkAsync<List<TicketEventDto>>(await owner.GetAsync($"{Base}/tickets/{late.Summary.Id}/history"));
        Assert.Equal(3, history.Count(e => e.Type == TicketEventType.Escalated));
        Assert.Single(history, e => e.Type == TicketEventType.SlaBreached && e.Detail!.Contains("first response", StringComparison.Ordinal));

        var answeredAfter = await HttpAssert.OkAsync<TicketDetailsDto>(await owner.GetAsync($"{Base}/tickets/{answered.Summary.Id}"));
        Assert.Equal(0, answeredAfter.Summary.EscalationLevel);
        Assert.Equal(0, (await HttpAssert.OkAsync<TicketDetailsDto>(await owner.GetAsync($"{Base}/tickets/{young.Summary.Id}"))).Summary.EscalationLevel);

        var escalated = await HttpAssert.OkAsync<PagedResult<TicketSummaryDto>>(await owner.GetAsync($"{Base}/tickets?escalated=true"));
        Assert.Equal(late.Summary.Id, Assert.Single(escalated.Items).Id);
        var overdue = await HttpAssert.OkAsync<PagedResult<TicketSummaryDto>>(await owner.GetAsync($"{Base}/tickets?overdue=true"));
        Assert.Contains(overdue.Items, t => t.Id == late.Summary.Id);
        Assert.DoesNotContain(overdue.Items, t => t.Id == young.Summary.Id);
    }

    [Fact]
    public async Task Attachments_are_validated_stored_and_served_as_downloads_only()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var member = await fixture.AddMemberAsync(tenant);
        var other = await fixture.AddMemberAsync(tenant);
        using var client = fixture.CreateClient(member.Token);
        using var otherClient = fixture.CreateClient(other.Token);
        var ticket = await CreateTicketAsync(client);
        var url = $"{Base}/tickets/{ticket.Summary.Id}/attachments";

        static MultipartFormDataContent Form(string name, byte[] data)
        {
            var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(data);
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            form.Add(file, "file", name);
            return form;
        }

        var created = await HttpAssert.OkAsync<AttachmentDto>(await client.PostAsync(url, Form("../../screen shot.png", Png)), HttpStatusCode.Created);
        Assert.Equal("screen shot.png", created.FileName);
        Assert.Equal("image/png", created.ContentType);
        Assert.Equal(Png.Length, created.SizeBytes);

        await HttpAssert.ProblemAsync(await client.PostAsync(url, Form("malware.exe", Png)), HttpStatusCode.BadRequest, "validation_failed");
        await HttpAssert.ProblemAsync(await client.PostAsync(url, Form("fake.png", "MZ not an image"u8.ToArray())), HttpStatusCode.BadRequest, "validation_failed");
        await HttpAssert.ProblemAsync(await client.PostAsync(url, Form("page.html", "<script>alert(1)</script>"u8.ToArray())), HttpStatusCode.BadRequest, "validation_failed");
        await HttpAssert.ProblemAsync(await client.PostAsync(url, Form("big.txt", new byte[11 * 1024 * 1024])), HttpStatusCode.BadRequest, "validation_failed");

        var download = await client.GetAsync($"{url}/{created.Id}");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(Png, await download.Content.ReadAsByteArrayAsync());
        Assert.Equal("attachment", download.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("nosniff", download.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.StartsWith("sandbox", download.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);

        // Another member cannot download or upload to it.
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.GetAsync($"{url}/{created.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await otherClient.PostAsync(url, Form("a.png", Png))).StatusCode);
    }

    [Fact]
    public async Task Tickets_and_catalogs_are_isolated_between_tenants()
    {
        var a = await fixture.RegisterTenantAsync();
        var b = await fixture.RegisterTenantAsync();
        using var ownerA = fixture.CreateClient(a.Token);
        using var ownerB = fixture.CreateClient(b.Token);
        var ticketA = await CreateTicketAsync(ownerA, "A's secret");
        var categoryA = (await HttpAssert.OkAsync<List<CategoryDto>>(await ownerA.GetAsync($"{Base}/categories"))).First();

        Assert.Equal(HttpStatusCode.NotFound, (await ownerB.GetAsync($"{Base}/tickets/{ticketA.Summary.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await ownerB.PostAsJsonAsync($"{Base}/tickets/{ticketA.Summary.Id}/comments", new AddCommentRequest("x", false), ApiFixture.Json)).StatusCode);
        Assert.Equal(0, (await HttpAssert.OkAsync<PagedResult<TicketSummaryDto>>(await ownerB.GetAsync($"{Base}/tickets"))).TotalCount);

        // B cannot reference A's category, technician or device in its own tickets.
        await HttpAssert.ProblemAsync(await ownerB.PostAsJsonAsync($"{Base}/tickets", new CreateTicketRequest("s", "d", null, categoryA.Id, null, null, null), ApiFixture.Json), HttpStatusCode.BadRequest, "validation_failed");
        var deviceA = await fixture.EnrollNewDeviceAsync(a);
        await HttpAssert.ProblemAsync(await ownerB.PostAsJsonAsync($"{Base}/tickets", new CreateTicketRequest("s", "d", null, null, deviceA.DeviceId, null, null), ApiFixture.Json), HttpStatusCode.BadRequest, "validation_failed");
        var ownTicket = await CreateTicketAsync(ownerB);
        await HttpAssert.ProblemAsync(await ownerB.PostAsJsonAsync($"{Base}/tickets/{ownTicket.Summary.Id}/assign", new AssignTicketRequest(Guid.NewGuid()), ApiFixture.Json), HttpStatusCode.NotFound);

        // Each tenant counts its own tickets from 1.
        Assert.Equal(1, ticketA.Summary.Number);
        Assert.Equal(1, ownTicket.Summary.Number);

        // A device of the same tenant can be linked and shows its name.
        var linked = await HttpAssert.OkAsync<TicketDetailsDto>(await ownerA.PostAsJsonAsync($"{Base}/tickets/{ticketA.Summary.Id}/device", new LinkDeviceRequest(deviceA.DeviceId), ApiFixture.Json));
        Assert.Equal(deviceA.Identity.Hostname, linked.DeviceName);
    }

    [Fact]
    public async Task Ai_summary_is_available_on_demand_and_rate_limited_per_ticket()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var owner = fixture.CreateClient(tenant.Token);
        var ticket = await CreateTicketAsync(owner, "Slow laptop", "The laptop takes ten minutes to boot. It started after the last update.");
        await owner.PostAsJsonAsync($"{Base}/tickets/{ticket.Summary.Id}/comments", new AddCommentRequest("Checked startup items.", false), ApiFixture.Json);
        await owner.PostAsJsonAsync($"{Base}/tickets/{ticket.Summary.Id}/comments", new AddCommentRequest("Contains a SECRET-INTERNAL-NOTE", true), ApiFixture.Json);

        var summary = await HttpAssert.OkAsync<TicketDetailsDto>(await owner.PostAsync($"{Base}/tickets/{ticket.Summary.Id}/summary", null));
        Assert.Contains("boot", summary.AiSummary, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-INTERNAL-NOTE", summary.AiSummary, StringComparison.Ordinal);
        Assert.NotNull(summary.AiSummaryGeneratedAtUtc);
        await HttpAssert.ProblemAsync(await owner.PostAsync($"{Base}/tickets/{ticket.Summary.Id}/summary", null), HttpStatusCode.Conflict, "summary_cooldown");
    }

    [Fact]
    public async Task Staff_dashboard_numbers_reflect_the_queue()
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var owner = fixture.CreateClient(tenant.Token);
        await CreateTicketAsync(owner, "one");
        var two = await CreateTicketAsync(owner, "two");
        await owner.PostAsJsonAsync($"{Base}/tickets/{two.Summary.Id}/status", new ChangeStatusRequest(TicketStatus.Resolved, "done"), ApiFixture.Json);
        var stats = await HttpAssert.OkAsync<TicketStatsDto>(await owner.GetAsync($"{Base}/tickets/stats"));
        Assert.Equal(1, stats.Open);
        Assert.Equal(1, stats.Unassigned);
        Assert.Equal(1, stats.NeedsTriage);
        Assert.Equal(1, stats.ResolvedLast7Days);
    }
}
