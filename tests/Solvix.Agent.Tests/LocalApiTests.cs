namespace Solvix.Agent.Tests;

using System.IO.Pipes;
using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Solvix.Agent.Contracts;
using Solvix.Agent.Core.Communication;
using Solvix.Agent.Core.Configuration;
using Solvix.Agent.Core.LocalApi;

/// <summary>The path from a person at the computer (tickets, chat) to the server, through the agent service.</summary>
public sealed class LocalApiTests : IDisposable
{
    private readonly FakeAgentApi _api = new();
    private readonly LocalApiServer _server;
    private readonly string _pipe = "solvix-test-" + Guid.NewGuid().ToString("N");

    public LocalApiTests()
    {
        _server = new LocalApiServer(_api, Options.Create(new AgentOptions { LocalPipeName = _pipe }), NullLogger<LocalApiServer>.Instance);
    }

    public void Dispose()
    {
        _server.Dispose();
        _api.Dispose();
    }

    [Fact]
    public async Task Ticket_submission_uses_the_operating_system_identity_not_the_callers_claim()
    {
        var response = await _server.DispatchAsync(
            new LocalRequest { Op = LocalOps.SubmitTicket, Ticket = new SubmitTicketRequest { Subject = "No wifi", RequesterName = "Someone Else" } },
            "CONTOSO\\dana",
            CancellationToken.None);

        Assert.True(response.Ok);
        Assert.Equal(1, response.Ticket!.Number);
        var sent = Assert.Single(_api.Tickets);
        Assert.Equal("CONTOSO\\dana", sent.RequesterName);
        Assert.Equal("No wifi", sent.Subject);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Blank_subjects_never_reach_the_server(string subject)
    {
        var response = await _server.DispatchAsync(
            new LocalRequest { Op = LocalOps.SubmitTicket, Ticket = new SubmitTicketRequest { Subject = subject } }, "u", CancellationToken.None);
        Assert.False(response.Ok);
        Assert.Empty(_api.Tickets);
    }

    [Fact]
    public async Task Chat_is_sent_as_the_local_user_and_polling_is_bounded()
    {
        var sent = await _server.DispatchAsync(new LocalRequest { Op = LocalOps.SendChat, Body = "hello" }, "dana", CancellationToken.None);
        Assert.True(sent.Ok);
        Assert.Equal("dana", Assert.Single(_api.ChatSent).SenderName);

        await _server.DispatchAsync(new LocalRequest { Op = LocalOps.PollChat, After = -5, WaitSeconds = 9999 }, "dana", CancellationToken.None);
        Assert.Equal((0L, 25), Assert.Single(_api.ChatPolls));

        var tooLong = await _server.DispatchAsync(new LocalRequest { Op = LocalOps.SendChat, Body = new string('x', ChatMessage.BodyMaxLength + 1) }, "dana", CancellationToken.None);
        Assert.False(tooLong.Ok);
        Assert.Single(_api.ChatSent);
    }

    [Fact]
    public async Task Unknown_operations_are_refused()
    {
        Assert.False((await _server.DispatchAsync(new LocalRequest { Op = "run.command" }, "u", CancellationToken.None)).Ok);
        Assert.False((await _server.DispatchAsync(new LocalRequest { Op = LocalOps.SubmitTicket }, "u", CancellationToken.None)).Ok);
        Assert.False((await _server.DispatchAsync(new LocalRequest { Op = LocalOps.SendChat, Body = " " }, "u", CancellationToken.None)).Ok);
        Assert.Empty(_api.Calls);
    }

    [Theory]
    [InlineData(AgentApiFailure.Transient, null, "cannot be reached")]
    [InlineData(AgentApiFailure.Authentication, null, "not authorized")]
    [InlineData(AgentApiFailure.Rejected, "ticket_rate_limited", "wait a few minutes")]
    public async Task Server_failures_become_friendly_messages(AgentApiFailure failure, string? code, string expected)
    {
        _api.Failures.Enqueue(new AgentApiException(failure, HttpStatusCode.BadGateway, code, "boom"));
        var response = await _server.DispatchAsync(
            new LocalRequest { Op = LocalOps.SubmitTicket, Ticket = new SubmitTicketRequest { Subject = "x" } }, "u", CancellationToken.None);
        Assert.False(response.Ok);
        Assert.Contains(expected, response.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("boom", response.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Requests_travel_over_the_pipe_end_to_end()
    {
        await _server.StartAsync(CancellationToken.None);
        try
        {
            var client = new LocalApiClient(_pipe);
            var created = await client.CallAsync(
                new LocalRequest { Op = LocalOps.SubmitTicket, Ticket = new SubmitTicketRequest { Subject = "Screen flickers", Priority = TicketPriority.Urgent } },
                CancellationToken.None);
            Assert.True(created.Ok, created.Error);
            Assert.Equal(TicketPriority.Urgent, created.Ticket!.Priority);

            var list = await client.CallAsync(new LocalRequest { Op = LocalOps.ListTickets }, CancellationToken.None);
            Assert.True(list.Ok);
            Assert.Empty(list.Tickets!);
        }
        finally
        {
            await _server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Garbage_and_oversized_requests_are_rejected_without_calling_the_server()
    {
        await _server.StartAsync(CancellationToken.None);
        try
        {
            foreach (var payload in new[] { "not json\n", "{\"op\":\n", new string('a', LocalApiServer.MaxRequestBytes + 10) + "\n" })
            {
                await using var pipe = new NamedPipeClientStream(".", _pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
                await pipe.ConnectAsync(3000);
                await pipe.WriteAsync(Encoding.UTF8.GetBytes(payload));
                await pipe.FlushAsync();
                using var reader = new StreamReader(pipe);
                var line = await reader.ReadLineAsync();
                Assert.Contains("\"ok\":false", line ?? string.Empty, StringComparison.Ordinal);
            }

            Assert.Empty(_api.Calls);
        }
        finally
        {
            await _server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Client_reports_a_stopped_service_clearly()
    {
        var response = await new LocalApiClient("solvix-not-running-" + Guid.NewGuid().ToString("N")).CallAsync(new LocalRequest { Op = LocalOps.ListTickets }, CancellationToken.None);
        Assert.False(response.Ok);
        Assert.Contains("not running", response.Error, StringComparison.OrdinalIgnoreCase);
    }
}
