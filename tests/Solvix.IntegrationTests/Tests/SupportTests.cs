namespace Solvix.IntegrationTests.Tests;

using System.Net;
using System.Net.Http.Json;
using Solvix.Agent.Contracts;
using Solvix.IntegrationTests.Fixtures;
using Solvix.Modules.Support.Application;
using Solvix.Modules.Support.Domain;
using Solvix.SharedKernel.Results;

/// <summary>Tickets and chat: submitted from the laptop through the agent, handled in the web console.</summary>
[Collection(ApiCollection.Name)]
public sealed class SupportTests(ApiFixture fixture)
{
    private static SubmitTicketRequest Ticket(string subject = "Printer offline") => new()
    {
        Subject = subject,
        Description = "Cannot print since this morning.",
        Priority = TicketPriority.High,
        RequesterName = "Dana Smith",
    };

    private async Task<(TenantSession Tenant, EnrolledDevice Device, HttpClient Agent, HttpClient Admin)> SetUpAsync()
    {
        var tenant = await fixture.RegisterTenantAsync();
        var device = await fixture.EnrollAsync(await fixture.CreateTokenAsync(tenant));
        return (tenant, device, fixture.CreateDeviceClient(device.Certificate), fixture.CreateClient(tenant.Token));
    }

    [Fact]
    public async Task Ticket_submitted_by_the_agent_reaches_the_console_and_status_flows_back()
    {
        var (_, device, agent, admin) = await SetUpAsync();

        var receipt = await HttpAssert.OkAsync<AgentTicket>(
            await agent.PostAsJsonAsync("/" + AgentRoutes.Tickets, Ticket(), ApiFixture.Json), HttpStatusCode.Created);
        Assert.True(receipt.Number > 0);
        Assert.Equal(TicketStatus.Open, receipt.Status);

        var list = await HttpAssert.OkAsync<PagedResult<TicketSummaryDto>>(await admin.GetAsync("/api/v1/tickets"));
        var summary = Assert.Single(list.Items);
        Assert.Equal(receipt.Id, summary.Id);
        Assert.Equal(TicketSource.Agent, summary.Source);
        Assert.Equal(device.DeviceId, summary.DeviceId);
        Assert.Equal(device.Identity.Hostname, summary.DeviceName);
        Assert.Equal("Dana Smith", summary.RequesterName);

        // Technician replies (moves the ticket to in progress) and adds an internal note.
        var details = await HttpAssert.OkAsync<TicketDetailsDto>(
            await admin.PostAsJsonAsync($"/api/v1/tickets/{receipt.Id}/comments", new AddTicketCommentRequest("Restart the spooler."), ApiFixture.Json));
        Assert.Equal(TicketStatus.InProgress, details.Summary.Status);
        await HttpAssert.OkAsync<TicketDetailsDto>(
            await admin.PostAsJsonAsync($"/api/v1/tickets/{receipt.Id}/comments", new AddTicketCommentRequest("Driver issue, ordering a cable.", IsInternal: true), ApiFixture.Json));
        details = await HttpAssert.OkAsync<TicketDetailsDto>(
            await admin.PatchAsJsonAsync($"/api/v1/tickets/{receipt.Id}", new UpdateTicketRequest(TicketStatus.Resolved, null, null), ApiFixture.Json));
        Assert.Equal(TicketStatus.Resolved, details.Summary.Status);
        Assert.NotNull(details.ResolvedAtUtc);
        Assert.Equal(2, details.Comments.Count);

        // The agent sees the new status, and only its own device's tickets.
        var mine = await HttpAssert.OkAsync<AgentTicketList>(await agent.GetAsync("/" + AgentRoutes.Tickets));
        Assert.Equal(TicketStatus.Resolved, Assert.Single(mine.Tickets).Status);

        var filtered = await HttpAssert.OkAsync<PagedResult<TicketSummaryDto>>(await admin.GetAsync("/api/v1/tickets?open=true"));
        Assert.Empty(filtered.Items);
    }

    [Fact]
    public async Task Invalid_tickets_are_rejected_and_submission_is_throttled()
    {
        var (_, _, agent, _) = await SetUpAsync();
        await HttpAssert.ProblemAsync(
            await agent.PostAsJsonAsync("/" + AgentRoutes.Tickets, Ticket(" "), ApiFixture.Json), HttpStatusCode.BadRequest, "validation_failed");
        await HttpAssert.ProblemAsync(
            await agent.PostAsJsonAsync("/" + AgentRoutes.Tickets, Ticket(new string('x', SubmitTicketRequest.SubjectMaxLength + 1)), ApiFixture.Json), HttpStatusCode.BadRequest, "validation_failed");

        for (var i = 0; i < AgentSupportService.MaxTicketsPerWindow; i++)
        {
            Assert.Equal(HttpStatusCode.Created, (await agent.PostAsJsonAsync("/" + AgentRoutes.Tickets, Ticket($"Issue {i}"), ApiFixture.Json)).StatusCode);
        }

        await HttpAssert.ProblemAsync(
            await agent.PostAsJsonAsync("/" + AgentRoutes.Tickets, Ticket("One too many"), ApiFixture.Json), HttpStatusCode.Conflict, "ticket_rate_limited");
    }

    [Fact]
    public async Task Chat_messages_flow_in_both_directions()
    {
        var (_, device, agent, admin) = await SetUpAsync();

        var sent = await HttpAssert.OkAsync<ChatMessageDto>(
            await admin.PostAsJsonAsync($"/api/v1/devices/{device.DeviceId}/chat", new SendChatRequest("Hi Dana, I'm looking at your laptop."), ApiFixture.Json), HttpStatusCode.Created);
        Assert.Equal(ChatSender.Technician, sent.Sender);

        var polled = await HttpAssert.OkAsync<ChatMessageList>(await agent.GetAsync($"/{AgentRoutes.Chat}?after=0"));
        var first = Assert.Single(polled.Messages);
        Assert.Equal("Hi Dana, I'm looking at your laptop.", first.Body);

        var reply = await HttpAssert.OkAsync<ChatMessage>(
            await agent.PostAsJsonAsync("/" + AgentRoutes.Chat, new SendChatMessageRequest { Body = "Thanks!", SenderName = "Dana Smith" }, ApiFixture.Json), HttpStatusCode.Created);
        Assert.Equal(ChatSender.User, reply.Sender);

        var thread = await HttpAssert.OkAsync<List<ChatMessageDto>>(await admin.GetAsync($"/api/v1/devices/{device.DeviceId}/chat"));
        Assert.Equal(["Hi Dana, I'm looking at your laptop.", "Thanks!"], thread.Select(m => m.Body));
        Assert.Equal("Dana Smith", thread[1].SenderName);

        // Only newer messages after the cursor.
        var after = await HttpAssert.OkAsync<ChatMessageList>(await agent.GetAsync($"/{AgentRoutes.Chat}?after={first.Sequence}"));
        Assert.Equal("Thanks!", Assert.Single(after.Messages).Body);

        var conversations = await HttpAssert.OkAsync<List<ChatConversationDto>>(await admin.GetAsync("/api/v1/chat/conversations"));
        var conversation = Assert.Single(conversations);
        Assert.Equal(device.DeviceId, conversation.DeviceId);
        Assert.Equal("Thanks!", conversation.LastMessage);
    }

    [Fact]
    public async Task Long_poll_returns_as_soon_as_a_technician_message_arrives()
    {
        var (_, device, agent, admin) = await SetUpAsync();
        var started = DateTimeOffset.UtcNow;
        var poll = agent.GetAsync($"/{AgentRoutes.Chat}?after=0&wait=20");
        await Task.Delay(1500);
        await admin.PostAsJsonAsync($"/api/v1/devices/{device.DeviceId}/chat", new SendChatRequest("ping"), ApiFixture.Json);

        var result = await HttpAssert.OkAsync<ChatMessageList>(await poll);
        Assert.Equal("ping", Assert.Single(result.Messages).Body);
        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(10));

        // Nothing new: an idle poll honours its wait and returns empty.
        var idle = await HttpAssert.OkAsync<ChatMessageList>(await agent.GetAsync($"/{AgentRoutes.Chat}?after={result.Messages[0].Sequence}&wait=1"));
        Assert.Empty(idle.Messages);
    }

    [Fact]
    public async Task Tenants_cannot_see_each_others_tickets_or_chats()
    {
        var (_, deviceA, agentA, adminA) = await SetUpAsync();
        var (_, _, agentB, adminB) = await SetUpAsync();
        var ticket = await HttpAssert.OkAsync<AgentTicket>(await agentA.PostAsJsonAsync("/" + AgentRoutes.Tickets, Ticket(), ApiFixture.Json), HttpStatusCode.Created);
        await adminA.PostAsJsonAsync($"/api/v1/devices/{deviceA.DeviceId}/chat", new SendChatRequest("secret"), ApiFixture.Json);

        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"/api/v1/tickets/{ticket.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.PatchAsJsonAsync($"/api/v1/tickets/{ticket.Id}", new UpdateTicketRequest(TicketStatus.Closed, null, null), ApiFixture.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.GetAsync($"/api/v1/devices/{deviceA.DeviceId}/chat")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await adminB.PostAsJsonAsync($"/api/v1/devices/{deviceA.DeviceId}/chat", new SendChatRequest("x"), ApiFixture.Json)).StatusCode);
        Assert.Empty((await HttpAssert.OkAsync<PagedResult<TicketSummaryDto>>(await adminB.GetAsync("/api/v1/tickets"))).Items);
        Assert.Empty((await HttpAssert.OkAsync<AgentTicketList>(await agentB.GetAsync("/" + AgentRoutes.Tickets))).Tickets);
        Assert.Empty((await HttpAssert.OkAsync<ChatMessageList>(await agentB.GetAsync($"/{AgentRoutes.Chat}?after=0"))).Messages);

        // Linking a ticket to another tenant's device is refused.
        await HttpAssert.ProblemAsync(
            await adminB.PostAsJsonAsync("/api/v1/tickets", new CreateTicketRequest("x", null, TicketPriority.Low, deviceA.DeviceId), ApiFixture.Json), HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Agent_endpoints_require_a_device_certificate_and_console_endpoints_a_user()
    {
        var (tenant, _, _, admin) = await SetUpAsync();
        using var anonymous = fixture.CreateDeviceClient(null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync("/" + AgentRoutes.Tickets, Ticket(), ApiFixture.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync($"/{AgentRoutes.Chat}?after=0")).StatusCode);

        // A user token is not a device identity.
        using var userOnAgentRoute = fixture.CreateClient(tenant.Token);
        Assert.True((await userOnAgentRoute.GetAsync("/" + AgentRoutes.Tickets)).StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);

        using var noToken = fixture.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await noToken.GetAsync("/api/v1/tickets")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/v1/tickets")).StatusCode);
    }

    [Fact]
    public async Task Ticket_history_survives_device_deletion_and_chat_does_not()
    {
        var (_, device, agent, admin) = await SetUpAsync();
        var ticket = await HttpAssert.OkAsync<AgentTicket>(await agent.PostAsJsonAsync("/" + AgentRoutes.Tickets, Ticket(), ApiFixture.Json), HttpStatusCode.Created);
        await admin.PostAsJsonAsync($"/api/v1/devices/{device.DeviceId}/chat", new SendChatRequest("hello"), ApiFixture.Json);

        await admin.PostAsync($"/api/v1/devices/{device.DeviceId}/retire", null);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync($"/api/v1/devices/{device.DeviceId}/chat", new SendChatRequest("late"), ApiFixture.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/devices/{device.DeviceId}")).StatusCode);

        var details = await HttpAssert.OkAsync<TicketDetailsDto>(await admin.GetAsync($"/api/v1/tickets/{ticket.Id}"));
        Assert.Null(details.Summary.DeviceId);
        Assert.Null(details.Summary.DeviceName);
    }

    [Fact]
    public async Task Web_tickets_can_be_assigned_to_members_only()
    {
        var (tenant, device, _, admin) = await SetUpAsync();
        var created = await HttpAssert.OkAsync<TicketDetailsDto>(
            await admin.PostAsJsonAsync("/api/v1/tickets", new CreateTicketRequest("Laptop slow", "Fans loud", TicketPriority.Normal, device.DeviceId), ApiFixture.Json), HttpStatusCode.Created);
        Assert.Equal(TicketSource.Web, created.Summary.Source);

        var me = tenant.Auth.User.Id;
        var assigned = await HttpAssert.OkAsync<TicketDetailsDto>(
            await admin.PatchAsJsonAsync($"/api/v1/tickets/{created.Summary.Id}", new UpdateTicketRequest(null, TicketPriority.Urgent, me), ApiFixture.Json));
        Assert.Equal(me, assigned.Summary.AssignedUserId);
        Assert.Equal(TicketPriority.Urgent, assigned.Summary.Priority);

        await HttpAssert.ProblemAsync(
            await admin.PatchAsJsonAsync($"/api/v1/tickets/{created.Summary.Id}", new UpdateTicketRequest(null, null, Guid.NewGuid()), ApiFixture.Json), HttpStatusCode.NotFound);
        var cleared = await HttpAssert.OkAsync<TicketDetailsDto>(
            await admin.PatchAsJsonAsync($"/api/v1/tickets/{created.Summary.Id}", new UpdateTicketRequest(null, null, null, Unassign: true), ApiFixture.Json));
        Assert.Null(cleared.Summary.AssignedUserId);
    }
}
