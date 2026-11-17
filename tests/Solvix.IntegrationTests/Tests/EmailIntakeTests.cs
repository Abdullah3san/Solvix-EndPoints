namespace Solvix.IntegrationTests.Tests;

using System.Net;
using System.Net.Http.Json;
using Solvix.IntegrationTests.Fixtures;
using Solvix.Modules.Helpdesk.Application;
using Solvix.Modules.Helpdesk.Domain;
using Solvix.SharedKernel.Results;

/// <summary>Tickets arriving by e-mail: no category, triage queue, threading, and abuse resistance.</summary>
[Collection(ApiCollection.Name)]
public sealed class EmailIntakeTests(ApiFixture fixture)
{
    private const string Base = "/api/v1/helpdesk";

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4, 5];

    private static InboundEmailRequest Mail(string from, string subject, string body, string? messageId = null, string? inReplyTo = null, IReadOnlyDictionary<string, string>? headers = null, IReadOnlyList<InboundAttachmentDto>? attachments = null) =>
        new(messageId ?? $"<{Guid.NewGuid():N}@mail.test>", inReplyTo, null, new EmailAddressDto(from, "Sender"), subject, body, null, headers, attachments);

    private async Task<(TenantSession Tenant, string Token, InboundChannelDto Channel)> SetupChannelAsync(string? domains = null)
    {
        var tenant = await fixture.RegisterTenantAsync();
        using var owner = fixture.CreateClient(tenant.Token);
        var created = await HttpAssert.OkAsync<CreatedInboundChannelDto>(
            await owner.PostAsJsonAsync($"{Base}/email-channels", new SaveInboundChannelRequest("Support", "support@acme.test", null, TicketPriority.High, domains is null ? null : [domains]), ApiFixture.Json), HttpStatusCode.Created);
        return (tenant, created.Token, created.Channel);
    }

    private static async Task<HttpResponseMessage> SendAsync(ApiFixture f, string? token, InboundEmailRequest request)
    {
        using var client = f.CreateClient();
        using var message = new HttpRequestMessage(HttpMethod.Post, $"{Base}/inbound-email") { Content = JsonContent.Create(request, options: ApiFixture.Json) };
        if (token is not null)
        {
            message.Headers.Add("X-Channel-Token", token);
        }

        return await client.SendAsync(message);
    }

    [Fact]
    public async Task An_email_becomes_an_uncategorised_ticket_in_the_triage_queue()
    {
        var (tenant, token, channel) = await SetupChannelAsync();
        using var owner = fixture.CreateClient(tenant.Token);
        var network = (await HttpAssert.OkAsync<List<CategoryDto>>(await owner.GetAsync($"{Base}/categories"))).Single(c => c.Name == "Network");
        await owner.PutAsJsonAsync($"{Base}/categories/{network.Id}", new SaveCategoryRequest("Network", null, null, ["vpn", "wifi"], true), ApiFixture.Json);

        var result = await HttpAssert.OkAsync<InboundEmailResult>(await SendAsync(fixture, token, Mail("Jane.Doe@Client.test", "Re: VPN keeps dropping", "Since this morning my VPN disconnects every few minutes.")));
        Assert.Equal("created", result.Outcome);
        Assert.Equal(1, result.TicketNumber);

        var triage = await HttpAssert.OkAsync<PagedResult<TicketSummaryDto>>(await owner.GetAsync($"{Base}/tickets?triage=true"));
        var ticket = Assert.Single(triage.Items);
        Assert.Equal("VPN keeps dropping", ticket.Subject);          // "Re:" stripped
        Assert.Null(ticket.CategoryId);                               // never auto-categorised
        Assert.True(ticket.NeedsTriage);
        Assert.Equal(network.Id, ticket.SuggestedCategoryId);        // but a suggestion is offered
        Assert.Equal(TicketSource.Email, ticket.Source);
        Assert.Equal("jane.doe@client.test", ticket.RequesterEmail);
        Assert.Equal(TicketPriority.High, ticket.Priority);           // channel default
        Assert.NotNull(ticket.ResolutionDueAtUtc);                    // SLA clock runs from receipt

        // Accepting the suggestion completes triage.
        var triaged = await HttpAssert.OkAsync<TicketDetailsDto>(await owner.PostAsJsonAsync($"{Base}/tickets/{ticket.Id}/category", new CategorizeTicketRequest(ticket.SuggestedCategoryId), ApiFixture.Json));
        Assert.False(triaged.Summary.NeedsTriage);
        Assert.Null(triaged.Summary.SuggestedCategoryId);
        Assert.Empty((await HttpAssert.OkAsync<PagedResult<TicketSummaryDto>>(await owner.GetAsync($"{Base}/tickets?triage=true"))).Items);

        var channels = await HttpAssert.OkAsync<List<InboundChannelDto>>(await owner.GetAsync($"{Base}/email-channels"));
        Assert.NotNull(Assert.Single(channels, c => c.Id == channel.Id).LastReceivedAtUtc);
        Assert.DoesNotContain(token, await owner.GetStringAsync($"{Base}/email-channels"), StringComparison.Ordinal); // secret is shown once only
    }

    [Fact]
    public async Task Replies_thread_onto_the_ticket_only_for_the_original_requester()
    {
        var (tenant, token, _) = await SetupChannelAsync();
        using var owner = fixture.CreateClient(tenant.Token);
        var first = await HttpAssert.OkAsync<InboundEmailResult>(await SendAsync(fixture, token, Mail("alice@client.test", "Printer down", "It is dead.", "<m1@mail.test>")));
        var number = first.TicketNumber!.Value;

        // By ticket reference in the subject.
        var byRef = await HttpAssert.OkAsync<InboundEmailResult>(await SendAsync(fixture, token, Mail("alice@client.test", $"Re: [SVX-{number}] Printer down", "Any news?")));
        Assert.Equal("replied", byRef.Outcome);
        // By In-Reply-To header.
        var byHeader = await HttpAssert.OkAsync<InboundEmailResult>(await SendAsync(fixture, token, Mail("alice@client.test", "Re: Printer down", "Hello?", inReplyTo: "<m1@mail.test>")));
        Assert.Equal("replied", byHeader.Outcome);
        Assert.Equal(number, byHeader.TicketNumber);

        // A stranger who knows the number does NOT get to write into Alice's ticket: they open their own.
        var stranger = await HttpAssert.OkAsync<InboundEmailResult>(await SendAsync(fixture, token, Mail("mallory@evil.test", $"[SVX-{number}] hijack", "Add me.")));
        Assert.Equal("created", stranger.Outcome);
        Assert.NotEqual(number, stranger.TicketNumber);

        var list = await HttpAssert.OkAsync<PagedResult<TicketSummaryDto>>(await owner.GetAsync($"{Base}/tickets"));
        var ticket = await HttpAssert.OkAsync<TicketDetailsDto>(await owner.GetAsync($"{Base}/tickets/{list.Items.Single(t => t.Number == number).Id}"));
        Assert.Equal(2, ticket.Comments.Count);
        Assert.All(ticket.Comments, c => Assert.Equal(TicketSource.Email, c.Source));
        Assert.All(ticket.Comments, c => Assert.False(c.IsFromStaff));
    }

    [Fact]
    public async Task A_requester_replying_to_a_resolved_ticket_reopens_it()
    {
        var (tenant, token, _) = await SetupChannelAsync();
        using var owner = fixture.CreateClient(tenant.Token);
        var created = await HttpAssert.OkAsync<InboundEmailResult>(await SendAsync(fixture, token, Mail("alice@client.test", "Printer", "Down.")));
        var id = (await HttpAssert.OkAsync<PagedResult<TicketSummaryDto>>(await owner.GetAsync($"{Base}/tickets"))).Items.Single().Id;
        await owner.PostAsJsonAsync($"{Base}/tickets/{id}/status", new ChangeStatusRequest(TicketStatus.Resolved, "Replaced toner."), ApiFixture.Json);

        var reply = await HttpAssert.OkAsync<InboundEmailResult>(await SendAsync(fixture, token, Mail("alice@client.test", $"Re: [#{created.TicketNumber}] Printer", "Not fixed.")));
        Assert.Equal("replied", reply.Outcome);
        var after = await HttpAssert.OkAsync<TicketDetailsDto>(await owner.GetAsync($"{Base}/tickets/{id}"));
        Assert.Equal(TicketStatus.Open, after.Summary.Status);
        Assert.Equal(1, after.ReopenCount);
    }

    [Fact]
    public async Task Duplicate_deliveries_and_automatic_mail_do_not_create_tickets()
    {
        var (tenant, token, _) = await SetupChannelAsync();
        using var owner = fixture.CreateClient(tenant.Token);
        var mail = Mail("a@client.test", "Hello", "Body", "<dup@mail.test>");
        Assert.Equal("created", (await HttpAssert.OkAsync<InboundEmailResult>(await SendAsync(fixture, token, mail))).Outcome);
        Assert.Equal("duplicate", (await HttpAssert.OkAsync<InboundEmailResult>(await SendAsync(fixture, token, mail))).Outcome);

        foreach (var headers in new[]
        {
            new Dictionary<string, string> { ["Auto-Submitted"] = "auto-replied" },
            new Dictionary<string, string> { ["Precedence"] = "bulk" },
            new Dictionary<string, string> { ["X-Auto-Response-Suppress"] = "All" },
        })
        {
            Assert.Equal("ignored", (await HttpAssert.OkAsync<InboundEmailResult>(await SendAsync(fixture, token, Mail("robot@client.test", "Out of office", "I am away.", headers: headers)))).Outcome);
        }

        // Mail from the channel's own address (a mail loop) is dropped.
        Assert.Equal("ignored", (await HttpAssert.OkAsync<InboundEmailResult>(await SendAsync(fixture, token, Mail("support@acme.test", "loop", "loop")))).Outcome);
        Assert.Equal(1, (await HttpAssert.OkAsync<PagedResult<TicketSummaryDto>>(await owner.GetAsync($"{Base}/tickets"))).TotalCount);
    }

    [Fact]
    public async Task One_sender_cannot_flood_the_queue()
    {
        var (tenant, token, _) = await SetupChannelAsync();
        using var owner = fixture.CreateClient(tenant.Token);
        var outcomes = new List<string>();
        for (var i = 0; i < 23; i++)
        {
            outcomes.Add((await HttpAssert.OkAsync<InboundEmailResult>(await SendAsync(fixture, token, Mail("spammer@bulk.test", $"Buy now {i}", "spam")))).Outcome);
        }

        Assert.Equal(20, outcomes.Count(o => o == "created"));
        Assert.Equal(3, outcomes.Count(o => o == "ignored"));
        // Other senders are unaffected.
        Assert.Equal("created", (await HttpAssert.OkAsync<InboundEmailResult>(await SendAsync(fixture, token, Mail("legit@client.test", "Help", "Please")))).Outcome);
    }

    [Fact]
    public async Task The_sender_domain_allow_list_is_enforced()
    {
        var (tenant, token, _) = await SetupChannelAsync("client.test");
        using var owner = fixture.CreateClient(tenant.Token);
        Assert.Equal("created", (await HttpAssert.OkAsync<InboundEmailResult>(await SendAsync(fixture, token, Mail("ok@client.test", "Hi", "ok")))).Outcome);
        Assert.Equal("ignored", (await HttpAssert.OkAsync<InboundEmailResult>(await SendAsync(fixture, token, Mail("who@other.test", "Hi", "no")))).Outcome);
        Assert.Equal(1, (await HttpAssert.OkAsync<PagedResult<TicketSummaryDto>>(await owner.GetAsync($"{Base}/tickets"))).TotalCount);
    }

    [Fact]
    public async Task Bad_missing_cross_tenant_disabled_and_rotated_tokens_are_rejected()
    {
        var (tenantA, tokenA, channelA) = await SetupChannelAsync();
        var (_, tokenB, _) = await SetupChannelAsync();
        var mail = Mail("a@client.test", "Hi", "Body");

        foreach (var bad in new string?[] { null, string.Empty, "svxm_garbage", "Bearer " + tokenA, tokenA[..^2] + "AA", "svxe_" + tokenA[5..] })
        {
            await HttpAssert.ProblemAsync(await SendAsync(fixture, bad, mail), HttpStatusCode.Unauthorized, "channel_token_invalid");
        }

        // Splice tenant B's channel secret under tenant A's ids: must not open anything.
        Assert.True(ChannelTokenCodec.TryParse(tokenA, out var tA, out var cA, out _));
        Assert.True(ChannelTokenCodec.TryParse(tokenB, out _, out _, out var secretB));
        var spliced = "svxm_" + Convert.ToBase64String(tA.ToByteArray(bigEndian: true).Concat(cA.ToByteArray(bigEndian: true)).Concat(secretB).ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        await HttpAssert.ProblemAsync(await SendAsync(fixture, spliced, mail), HttpStatusCode.Unauthorized, "channel_token_invalid");

        // Tenant B's token creates tickets for B only.
        using var ownerA = fixture.CreateClient(tenantA.Token);
        Assert.Equal("created", (await HttpAssert.OkAsync<InboundEmailResult>(await SendAsync(fixture, tokenB, mail))).Outcome);
        Assert.Equal(0, (await HttpAssert.OkAsync<PagedResult<TicketSummaryDto>>(await ownerA.GetAsync($"{Base}/tickets"))).TotalCount);

        // Disable -> rejected; rotate -> old token dead, new token works.
        await ownerA.PutAsJsonAsync($"{Base}/email-channels/{channelA.Id}", new SaveInboundChannelRequest("Support", null, false, TicketPriority.Medium, null), ApiFixture.Json);
        await HttpAssert.ProblemAsync(await SendAsync(fixture, tokenA, mail), HttpStatusCode.Unauthorized, "channel_token_invalid");
        await ownerA.PutAsJsonAsync($"{Base}/email-channels/{channelA.Id}", new SaveInboundChannelRequest("Support", null, true, TicketPriority.Medium, null), ApiFixture.Json);
        var rotated = await HttpAssert.OkAsync<CreatedInboundChannelDto>(await ownerA.PostAsync($"{Base}/email-channels/{channelA.Id}/rotate", null));
        await HttpAssert.ProblemAsync(await SendAsync(fixture, tokenA, mail), HttpStatusCode.Unauthorized, "channel_token_invalid");
        Assert.Equal("created", (await HttpAssert.OkAsync<InboundEmailResult>(await SendAsync(fixture, rotated.Token, mail))).Outcome);
    }

    [Fact]
    public async Task Only_valid_attachments_are_kept_and_html_mail_is_reduced_to_text()
    {
        var (tenant, token, _) = await SetupChannelAsync();
        using var owner = fixture.CreateClient(tenant.Token);
        var request = new InboundEmailRequest("<att@mail.test>", null, null, new EmailAddressDto("a@client.test", null), "Screenshot",
            null, "<p>See <b>attached</b></p><script>alert(1)</script>", null,
            [
                new InboundAttachmentDto("ok.png", "image/png", Convert.ToBase64String(Png)),
                new InboundAttachmentDto("evil.exe", "application/octet-stream", Convert.ToBase64String(Png)),
                new InboundAttachmentDto("bad.png", "image/png", "!!!not base64!!!"),
            ]);
        Assert.Equal("created", (await HttpAssert.OkAsync<InboundEmailResult>(await SendAsync(fixture, token, request))).Outcome);

        var id = (await HttpAssert.OkAsync<PagedResult<TicketSummaryDto>>(await owner.GetAsync($"{Base}/tickets"))).Items.Single().Id;
        var ticket = await HttpAssert.OkAsync<TicketDetailsDto>(await owner.GetAsync($"{Base}/tickets/{id}"));
        Assert.Equal("See attached", ticket.Description);
        Assert.Equal("ok.png", Assert.Single(ticket.Attachments).FileName);
        var history = await HttpAssert.OkAsync<List<TicketEventDto>>(await owner.GetAsync($"{Base}/tickets/{id}/history"));
        Assert.Contains(history, e => e.Detail!.Contains("attachment skipped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Malformed_requests_are_rejected_without_creating_anything()
    {
        var (tenant, token, _) = await SetupChannelAsync();
        using var owner = fixture.CreateClient(tenant.Token);
        await HttpAssert.ProblemAsync(await SendAsync(fixture, token, Mail("not-an-email", "s", "b")), HttpStatusCode.BadRequest, "validation_failed");
        using var client = fixture.CreateClient();
        using var noFrom = new HttpRequestMessage(HttpMethod.Post, $"{Base}/inbound-email") { Content = new StringContent("{\"subject\":\"x\"}", System.Text.Encoding.UTF8, "application/json") };
        noFrom.Headers.Add("X-Channel-Token", token);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(noFrom)).StatusCode);
        Assert.Equal(0, (await HttpAssert.OkAsync<PagedResult<TicketSummaryDto>>(await owner.GetAsync($"{Base}/tickets"))).TotalCount);
    }
}
