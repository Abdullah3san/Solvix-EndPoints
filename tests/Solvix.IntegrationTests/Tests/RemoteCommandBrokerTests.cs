namespace Solvix.IntegrationTests.Tests;

using System.Net;
using System.Net.Http.Json;
using Npgsql;
using Solvix.Agent.Contracts;
using Solvix.Agent.Contracts.RemoteManagement;
using Solvix.IntegrationTests.Fixtures;
using Solvix.Modules.Identity.Application.Users;
using Solvix.Modules.RemoteManagement.Auditing;
using Solvix.Modules.RemoteManagement.Authorization;
using Solvix.Modules.RemoteManagement.CommandBroker;
using Solvix.Modules.RemoteManagement.Domain;
using Solvix.SharedKernel.Authorization;
using Solvix.SharedKernel.Results;

/// <summary>
/// Command broker end to end against the real API, PostgreSQL and Redis: signing, delivery,
/// acknowledgement, results, approvals, re-authorization, expiry, cancellation, tenant isolation
/// and the audit trail.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RemoteCommandBrokerTests(ApiFixture fixture)
{
    [Fact]
    public async Task Command_lifecycle_is_signed_delivered_acknowledged_completed_and_audited()
    {
        using var s = await fixture.SetUpAsync();
        var requested = await s.Owner.RequestAsync(s.DeviceId, "system-info");
        Assert.Equal(CommandExecutionStatus.Queued, requested.Status);
        Assert.Equal(1, requested.Sequence);

        // The heartbeat tells the agent there is work; the agent fetches signed commands.
        var heartbeat = await HttpAssert.OkAsync<HeartbeatResponse>(await s.Agent.HeartbeatAsync());
        Assert.Equal(1, heartbeat.PendingCommands);
        var signed = Assert.Single(await s.Agent.FetchAsync());
        var envelope = await s.Agent.VerifyAsync(signed);
        Assert.Equal(requested.Id, envelope.ExecutionId);
        Assert.Equal(s.Tenant.TenantId, envelope.TenantId);
        Assert.Equal(s.DeviceId, envelope.DeviceId);
        Assert.Equal(s.Tenant.Auth.User.Id, envelope.RequestedBy);
        Assert.Equal(CommandTypes.SystemInfo, envelope.CommandType);
        Assert.Equal(CommandAuthorization.Authorized, envelope.Authorization);
        Assert.Equal(CommandApprovalStatus.NotRequired, envelope.ApprovalStatus);
        Assert.True(envelope.ExpiresAtUtc > DateTimeOffset.UtcNow);
        Assert.Equal(22, envelope.Nonce.Length);

        Assert.Equal(HttpStatusCode.NoContent, (await s.Agent.AckAsync(requested.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Agent.ResultAsync(requested.Id, output: "{\"hostname\":\"x\"}")).StatusCode);

        var done = await s.Owner.GetCommandAsync(requested.Id);
        Assert.Equal(CommandExecutionStatus.Succeeded, done.Status);
        Assert.Equal("{\"hostname\":\"x\"}", done.Output);
        Assert.NotNull(done.DurationSeconds);
        Assert.Empty(await s.Agent.FetchAsync());

        var audit = await HttpAssert.OkAsync<PagedResult<RemoteAuditEventDto>>(await s.Owner.GetAsync($"/api/v1/remote/audit?executionId={requested.Id}"));
        Assert.Equal(
            [RemoteAuditEvents.CommandRequested, RemoteAuditEvents.CommandQueued, RemoteAuditEvents.CommandDelivered, RemoteAuditEvents.CommandAcknowledged, RemoteAuditEvents.CommandCompleted],
            audit.Items.OrderBy(e => e.Sequence).Select(e => e.EventType));
        Assert.All(audit.Items.Where(e => e.EventType == RemoteAuditEvents.CommandRequested), e => Assert.Equal(s.Tenant.Auth.User.Id, e.ActorUserId));
        Assert.All(audit.Items.Where(e => e.EventType == RemoteAuditEvents.CommandAcknowledged), e => Assert.Null(e.ActorUserId));
        Assert.True((await HttpAssert.OkAsync<AuditChainVerification>(await s.Owner.GetAsync("/api/v1/remote/audit/verify"))).Valid);
    }

    [Fact]
    public async Task A_result_is_recorded_once_and_acknowledgements_are_idempotent()
    {
        using var s = await fixture.SetUpAsync();
        var requested = await s.Owner.RequestAsync(s.DeviceId, "uptime");
        Assert.Single(await s.Agent.FetchAsync());

        // A result without acknowledgement is refused: the device must accept before running.
        await HttpAssert.ProblemAsync(await s.Agent.ResultAsync(requested.Id), HttpStatusCode.Conflict, "command_result_recorded");

        Assert.Equal(HttpStatusCode.NoContent, (await s.Agent.AckAsync(requested.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Agent.AckAsync(requested.Id)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Agent.ResultAsync(requested.Id, CommandOutcome.Succeeded, "first")).StatusCode);
        await HttpAssert.ProblemAsync(await s.Agent.ResultAsync(requested.Id, CommandOutcome.Failed, "second"), HttpStatusCode.Conflict, "command_result_recorded");

        var done = await s.Owner.GetCommandAsync(requested.Id);
        Assert.Equal(CommandExecutionStatus.Succeeded, done.Status);
        Assert.Equal("first", done.Output);
    }

    [Fact]
    public async Task Undelivered_and_unacknowledged_commands_are_redelivered_with_identical_signed_bytes()
    {
        using var s = await fixture.SetUpAsync();
        var requested = await s.Owner.RequestAsync(s.DeviceId, "processes");
        var first = Assert.Single(await s.Agent.FetchAsync());
        Assert.Empty(await s.Agent.FetchAsync()); // within the redelivery interval

        fixture.Factory.Clock.Advance(TimeSpan.FromSeconds(61));
        try
        {
            var again = Assert.Single(await s.Agent.FetchAsync());
            Assert.Equal(first, again);
            Assert.Equal(2, (await s.Owner.GetCommandAsync(requested.Id)).DeliveryAttempts);
        }
        finally
        {
            fixture.Factory.Clock.Reset();
        }
    }

    [Fact]
    public async Task Sequences_are_unique_and_increasing_under_concurrent_technicians()
    {
        using var s = await fixture.SetUpAsync();
        var (_, tech1) = await fixture.TechnicianAsync(s.Tenant, Permissions.Remote.View);
        var (_, tech2) = await fixture.TechnicianAsync(s.Tenant, Permissions.Remote.View, Permissions.Remote.Lock);
        using (tech1)
        using (tech2)
        {
            var requests = Enumerable.Range(0, 12).Select(i => (i % 2 == 0 ? tech1 : tech2).RequestAsync(s.DeviceId, i % 3 == 0 ? "uptime" : "services"));
            var executions = await Task.WhenAll(requests);
            Assert.Equal(Enumerable.Range(1, 12).Select(i => (long?)i), executions.Select(e => e.Sequence).Order());

            var delivered = new List<SignedCommand>();
            for (var i = 0; i < 3 && delivered.Count < 12; i++)
            {
                delivered.AddRange(await s.Agent.FetchAsync());
            }

            Assert.Equal(12, delivered.Select(c => RemoteTestKit.Peek(c).ExecutionId).Distinct().Count());
        }
    }

    [Fact]
    public async Task Technicians_without_the_operation_permission_are_refused_and_the_denial_is_audited()
    {
        using var s = await fixture.SetUpAsync();
        var (_, viewer) = await fixture.TechnicianAsync(s.Tenant, Permissions.Devices.View);
        var (_, readOnly) = await fixture.TechnicianAsync(s.Tenant, Permissions.Remote.View);
        using (viewer)
        using (readOnly)
        {
            await HttpAssert.ProblemAsync(await viewer.OperationAsync(s.DeviceId, "system-info"), HttpStatusCode.Forbidden, "permission_denied");
            foreach (var operation in new[] { "restart", "shutdown", "lock", "logoff" })
            {
                await HttpAssert.ProblemAsync(
                    await readOnly.OperationAsync(s.DeviceId, operation, new OperationRequest { ConfirmDeviceName = s.Hostname }),
                    HttpStatusCode.Forbidden, "permission_denied");
            }

            // Approvals are their own permission.
            await HttpAssert.ProblemAsync(await readOnly.GetAsync("/api/v1/remote/approvals"), HttpStatusCode.Forbidden, "permission_denied");
        }

        var page = await HttpAssert.OkAsync<PagedResult<CommandExecutionDto>>(await s.Owner.GetAsync($"/api/v1/remote/commands?deviceId={s.DeviceId}"));
        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task Restart_and_shutdown_require_typing_the_target_device_name()
    {
        using var s = await fixture.SetUpAsync(hostname: "POS-018");
        await HttpAssert.ProblemAsync(await s.Owner.OperationAsync(s.DeviceId, "restart"), HttpStatusCode.BadRequest);
        await HttpAssert.ProblemAsync(await s.Owner.OperationAsync(s.DeviceId, "shutdown", new OperationRequest { ConfirmDeviceName = "POS-019" }), HttpStatusCode.BadRequest);
        await HttpAssert.ProblemAsync(
            await s.Owner.OperationAsync(s.DeviceId, "restart", new OperationRequest { ConfirmDeviceName = "pos-018", DelaySeconds = 9999 }),
            HttpStatusCode.BadRequest);

        var restart = await s.Owner.RequestAsync(s.DeviceId, "restart", new OperationRequest { ConfirmDeviceName = "pos-018", DelaySeconds = 60, Message = "Updates" });
        Assert.Equal(CommandExecutionStatus.PendingApproval, restart.Status); // default policy
        Assert.Equal("60", restart.Parameters["delaySeconds"]);
    }

    [Fact]
    public async Task Sensitive_operations_wait_for_a_second_person_and_the_requester_cannot_approve()
    {
        using var s = await fixture.SetUpAsync();
        var (manager, managerClient) = await fixture.TechnicianAsync(s.Tenant, Permissions.Remote.Restart, Permissions.Remote.ApprovalsManage);
        var (_, technician) = await fixture.TechnicianAsync(s.Tenant, Permissions.Remote.ApprovalsManage, Permissions.Remote.View);
        using (managerClient)
        using (technician)
        {
            var restart = await managerClient.RequestAsync(s.DeviceId, "restart", new OperationRequest { ConfirmDeviceName = s.Hostname });
            Assert.Equal(CommandExecutionStatus.PendingApproval, restart.Status);
            Assert.Empty(await s.Agent.FetchAsync()); // nothing is signed before approval

            var approvalId = restart.ApprovalId!.Value;
            await HttpAssert.ProblemAsync(await managerClient.PostAsJsonAsync($"/api/v1/remote/approvals/{approvalId}/approve", new ApprovalDecisionRequest(null), ApiFixture.Json), HttpStatusCode.Forbidden, "approval_self_decision");

            // An approver must be allowed to do the operation themselves.
            await HttpAssert.ProblemAsync(await technician.PostAsJsonAsync($"/api/v1/remote/approvals/{approvalId}/approve", new ApprovalDecisionRequest(null), ApiFixture.Json), HttpStatusCode.Forbidden, "permission_denied");

            var approved = await HttpAssert.OkAsync<ApprovalDto>(await s.Owner.PostAsJsonAsync($"/api/v1/remote/approvals/{approvalId}/approve", new ApprovalDecisionRequest("Maintenance window"), ApiFixture.Json));
            Assert.Equal(ApprovalStatus.Approved, approved.Status);
            await HttpAssert.ProblemAsync(await s.Owner.PostAsJsonAsync($"/api/v1/remote/approvals/{approvalId}/reject", new ApprovalDecisionRequest(null), ApiFixture.Json), HttpStatusCode.Conflict, "approval_decided");

            var envelope = await s.Agent.VerifyAsync(Assert.Single(await s.Agent.FetchAsync()));
            Assert.Equal(CommandApprovalStatus.Approved, envelope.ApprovalStatus);
            Assert.Equal(approvalId, envelope.ApprovalId);
            Assert.Equal(s.Tenant.Auth.User.Id, envelope.ApprovedBy);
            Assert.Equal(manager.UserId, envelope.RequestedBy);

            var shutdown = await s.Owner.RequestAsync(s.DeviceId, "shutdown", new OperationRequest { ConfirmDeviceName = s.Hostname });
            await HttpAssert.OkAsync<ApprovalDto>(await managerClient.PostAsJsonAsync($"/api/v1/remote/approvals/{shutdown.ApprovalId}/reject", new ApprovalDecisionRequest("Not now"), ApiFixture.Json));
            Assert.Equal(CommandExecutionStatus.ApprovalRejected, (await s.Owner.GetCommandAsync(shutdown.Id)).Status);
            Assert.Empty(await s.Agent.FetchAsync());
        }
    }

    [Fact]
    public async Task A_permission_removed_after_the_request_stops_the_command_before_the_device_sees_it()
    {
        using var s = await fixture.SetUpAsync();
        var (tech, client) = await fixture.TechnicianAsync(s.Tenant, Permissions.Remote.Lock);
        using (client)
        {
            var locked = await client.RequestAsync(s.DeviceId, "lock");
            Assert.Equal(CommandExecutionStatus.Queued, locked.Status);

            var viewOnly = await fixture.AddMemberWithPermissionsAsync(s.Tenant, Permissions.Devices.View);
            var roles = await HttpAssert.OkAsync<TenantUserDto>(await s.Owner.GetAsync($"/api/v1/users/{viewOnly.UserId}"));
            await HttpAssert.OkAsync<TenantUserDto>(await s.Owner.PutAsJsonAsync($"/api/v1/users/{tech.UserId}/roles", new SetUserRolesRequest([.. roles.Roles.Select(r => r.Id)]), ApiFixture.Json));

            Assert.Empty(await s.Agent.FetchAsync());
            var rejected = await s.Owner.GetCommandAsync(locked.Id);
            Assert.Equal(CommandExecutionStatus.Rejected, rejected.Status);
            Assert.Equal(RemoteDenialCodes.PermissionDenied, rejected.ErrorCode);
        }
    }

    [Fact]
    public async Task Commands_for_offline_devices_expire_and_silent_executions_time_out()
    {
        using var s = await fixture.SetUpAsync();
        var offline = await s.Owner.RequestAsync(s.DeviceId, "services");
        var running = await s.Owner.RequestAsync(s.DeviceId, "uptime");
        var delivered = await s.Agent.FetchAsync();
        Assert.Equal(2, delivered.Count);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Agent.AckAsync(running.Id)).StatusCode);

        // The device goes offline: 'offline' was delivered but never acknowledged, 'running' never reports.
        var result = await fixture.SweepCommandsAsync(DateTimeOffset.UtcNow.AddMinutes(61));
        Assert.True(result.Expired >= 1 && result.TimedOut >= 1, result.ToString());
        Assert.Equal(CommandExecutionStatus.Expired, (await s.Owner.GetCommandAsync(offline.Id)).Status);
        var timedOut = await s.Owner.GetCommandAsync(running.Id);
        Assert.Equal(CommandExecutionStatus.TimedOut, timedOut.Status);
        Assert.Equal("no_result", timedOut.ErrorCode);

        // When it comes back it can neither start the expired command nor overwrite the timeout.
        await HttpAssert.ProblemAsync(await s.Agent.AckAsync(offline.Id), HttpStatusCode.Conflict, "command_not_executable");
        await HttpAssert.ProblemAsync(await s.Agent.ResultAsync(running.Id), HttpStatusCode.Conflict, "command_result_recorded");
    }

    [Fact]
    public async Task Cancelled_commands_are_never_executed_and_running_ones_get_a_signed_cancel()
    {
        using var s = await fixture.SetUpAsync();
        var queued = await s.Owner.RequestAsync(s.DeviceId, "services");
        Assert.Equal(CommandExecutionStatus.Cancelled, (await HttpAssert.OkAsync<CommandExecutionDto>(await s.Owner.PostAsync($"/api/v1/remote/commands/{queued.Id}/cancel", null))).Status);
        Assert.Empty(await s.Agent.FetchAsync());

        // Cancelled after delivery but before acknowledgement: the acknowledgement fails, so it never runs.
        var delivered = await s.Owner.RequestAsync(s.DeviceId, "uptime");
        Assert.Single(await s.Agent.FetchAsync());
        await HttpAssert.OkAsync<CommandExecutionDto>(await s.Owner.PostAsync($"/api/v1/remote/commands/{delivered.Id}/cancel", null));
        await HttpAssert.ProblemAsync(await s.Agent.AckAsync(delivered.Id), HttpStatusCode.Conflict, "command_not_executable");

        var running = await s.Owner.RequestAsync(s.DeviceId, "processes");
        Assert.Single(await s.Agent.FetchAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await s.Agent.AckAsync(running.Id)).StatusCode);
        var cancelling = await HttpAssert.OkAsync<CommandExecutionDto>(await s.Owner.PostAsync($"/api/v1/remote/commands/{running.Id}/cancel", null));
        Assert.Equal(CommandExecutionStatus.Acknowledged, cancelling.Status);
        Assert.NotNull(cancelling.CancelRequestedAtUtc);

        var cancel = await s.Agent.VerifyAsync(Assert.Single(await s.Agent.FetchAsync()));
        Assert.Equal(CommandTypes.Cancel, cancel.CommandType);
        Assert.Equal(running.Id.ToString("D"), cancel.Parameters["executionId"]);
        Assert.Equal(HttpStatusCode.NoContent, (await s.Agent.ResultAsync(running.Id, CommandOutcome.Cancelled)).StatusCode);
        Assert.Equal(CommandExecutionStatus.Cancelled, (await s.Owner.GetCommandAsync(running.Id)).Status);
        await HttpAssert.ProblemAsync(await s.Owner.PostAsync($"/api/v1/remote/commands/{running.Id}/cancel", null), HttpStatusCode.Conflict, "command_finished");
    }

    [Fact]
    public async Task A_device_refusing_a_command_is_recorded_as_rejected()
    {
        using var s = await fixture.SetUpAsync();
        var requested = await s.Owner.RequestAsync(s.DeviceId, "system-info");
        Assert.Single(await s.Agent.FetchAsync());
        Assert.Equal(HttpStatusCode.NoContent, (await s.Agent.AckAsync(requested.Id, accepted: false, CommandRejectionCodes.InvalidSignature)).StatusCode);
        var rejected = await s.Owner.GetCommandAsync(requested.Id);
        Assert.Equal(CommandExecutionStatus.Rejected, rejected.Status);
        Assert.Equal(CommandRejectionCodes.InvalidSignature, rejected.ErrorCode);
    }

    [Fact]
    public async Task Tenants_cannot_reach_each_others_devices_or_commands()
    {
        using var a = await fixture.SetUpAsync();
        using var b = await fixture.SetUpAsync();
        var commandOfA = await a.Owner.RequestAsync(a.DeviceId, "system-info");

        // Users of B: A's device and command do not exist for them.
        await HttpAssert.ProblemAsync(await b.Owner.OperationAsync(a.DeviceId, "system-info"), HttpStatusCode.NotFound);
        await HttpAssert.ProblemAsync(await b.Owner.GetAsync($"/api/v1/remote/commands/{commandOfA.Id}"), HttpStatusCode.NotFound);
        await HttpAssert.ProblemAsync(await b.Owner.PostAsync($"/api/v1/remote/commands/{commandOfA.Id}/cancel", null), HttpStatusCode.NotFound);
        Assert.DoesNotContain(
            (await HttpAssert.OkAsync<PagedResult<CommandExecutionDto>>(await b.Owner.GetAsync("/api/v1/remote/commands"))).Items,
            c => c.Id == commandOfA.Id);
        Assert.DoesNotContain(
            (await HttpAssert.OkAsync<PagedResult<RemoteAuditEventDto>>(await b.Owner.GetAsync("/api/v1/remote/audit?pageSize=200"))).Items,
            e => e.ExecutionId == commandOfA.Id);

        // B's device: never receives, acknowledges or completes A's command.
        Assert.Empty(await b.Agent.FetchAsync());
        await HttpAssert.ProblemAsync(await b.Agent.AckAsync(commandOfA.Id), HttpStatusCode.NotFound);
        await HttpAssert.ProblemAsync(await b.Agent.ResultAsync(commandOfA.Id), HttpStatusCode.NotFound);

        // Another device of the same tenant cannot take it either.
        var token = await fixture.CreateTokenAsync(a.Tenant);
        var sibling = await fixture.EnrollAsync(token, DeviceTestKit.Identity());
        using var siblingAgent = fixture.CreateDeviceClient(sibling.Certificate);
        Assert.Empty(await siblingAgent.FetchAsync());
        await HttpAssert.ProblemAsync(await siblingAgent.AckAsync(commandOfA.Id), HttpStatusCode.NotFound);

        Assert.Single(await a.Agent.FetchAsync());
    }

    [Fact]
    public async Task User_tokens_cannot_use_the_agent_command_channel()
    {
        using var s = await fixture.SetUpAsync();
        var requested = await s.Owner.RequestAsync(s.DeviceId, "uptime");
        Assert.True((await s.Owner.GetAsync("/" + AgentRoutes.Commands)).StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
        Assert.True((await s.Owner.AckAsync(requested.Id)).StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
        using var anonymous = fixture.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/" + AgentRoutes.CommandKeys)).StatusCode);
    }

    [Fact]
    public async Task Technician_scope_and_allowed_hours_restrict_which_devices_can_be_reached_and_when()
    {
        using var s = await fixture.SetUpAsync();
        var (tech, client) = await fixture.TechnicianAsync(s.Tenant, Permissions.Remote.View);
        using (client)
        {
            var branch = await s.Owner.CreateBranchAsync("BEY" + Guid.NewGuid().ToString("N")[..4]);
            await HttpAssert.OkAsync<RemoteAccessScopeDto>(await s.Owner.PutAsJsonAsync($"/api/v1/remote/scopes/{tech.UserId}", new UpdateRemoteAccessScopeRequest(null, [branch.Id]), ApiFixture.Json));
            await HttpAssert.ProblemAsync(await client.OperationAsync(s.DeviceId, "uptime"), HttpStatusCode.Forbidden, RemoteDenialCodes.ScopeDenied);

            await HttpAssert.OkAsync<Solvix.Modules.Devices.Application.Devices.DeviceDetailsDto>(
                await s.Owner.PutAsJsonAsync($"/api/v1/devices/{s.DeviceId}/assignment", new Solvix.Modules.Devices.Application.Devices.UpdateDeviceAssignmentRequest(null, branch.Id, null), ApiFixture.Json));
            await client.RequestAsync(s.DeviceId, "uptime");

            // A window that excludes "now" blocks everyone, including the owner.
            var now = DateTimeOffset.UtcNow;
            var from = ((now.Hour + 2) % 24) * 60;
            await HttpAssert.OkAsync<RemoteAccessPolicyDto>(await s.Owner.PutAsJsonAsync("/api/v1/remote/policy",
                new UpdateRemoteAccessPolicyRequest([CommandTypes.Restart], 60, 240, "UTC", from, from + 60, 0b111_1111), ApiFixture.Json));
            await HttpAssert.ProblemAsync(await s.Owner.OperationAsync(s.DeviceId, "uptime"), HttpStatusCode.Forbidden, RemoteDenialCodes.OutsideAllowedHours);
            await HttpAssert.ProblemAsync(await client.OperationAsync(s.DeviceId, "uptime"), HttpStatusCode.Forbidden, RemoteDenialCodes.OutsideAllowedHours);
        }

        var denials = await HttpAssert.OkAsync<PagedResult<RemoteAuditEventDto>>(await s.Owner.GetAsync($"/api/v1/remote/audit?eventType={RemoteAuditEvents.AccessDenied}"));
        Assert.Contains(denials.Items, e => e.Detail == RemoteDenialCodes.ScopeDenied && e.ActorUserId == tech.UserId);
        Assert.Contains(denials.Items, e => e.Detail == RemoteDenialCodes.OutsideAllowedHours);
    }

    [Fact]
    public async Task The_audit_trail_is_append_only_and_tampering_is_detected()
    {
        using var s = await fixture.SetUpAsync();
        await s.Owner.RequestAsync(s.DeviceId, "uptime");

        // Nobody edits history, not even the schema owner in platform scope.
        var ex = await Assert.ThrowsAsync<PostgresException>(() => fixture.ExecuteAsOwnerAsync(
            "UPDATE remote_audit_events SET detail = 'edited' WHERE tenant_id = @t", ("t", s.Tenant.TenantId)));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);

        // The application role, in its own tenant, can neither delete nor truncate it.
        await using (var app = new NpgsqlConnection(fixture.Database.AppConnectionString))
        {
            await app.OpenAsync();
            await using (var tenant = new NpgsqlCommand("SELECT set_config('app.tenant_id', @t, false)", app))
            {
                tenant.Parameters.AddWithValue("t", s.Tenant.TenantId.ToString());
                await tenant.ExecuteScalarAsync();
            }

            foreach (var sql in new[] { "DELETE FROM remote_audit_events", "TRUNCATE remote_audit_events" })
            {
                await using var command = new NpgsqlCommand(sql, app);
                await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
            }
        }

        Assert.True((await HttpAssert.OkAsync<AuditChainVerification>(await s.Owner.GetAsync("/api/v1/remote/audit/verify"))).Valid);

        // Someone with enough database power to bypass the trigger is still caught by the hash chain.
        await fixture.ExecutePlatformSqlAsync(
            """
            ALTER TABLE remote_audit_events DISABLE TRIGGER trg_remote_audit_events_append_only;
            UPDATE remote_audit_events SET detail = 'edited' WHERE tenant_id = @t AND sequence = 1;
            ALTER TABLE remote_audit_events ENABLE TRIGGER trg_remote_audit_events_append_only;
            """,
            ("t", s.Tenant.TenantId));
        var verification = await HttpAssert.OkAsync<AuditChainVerification>(await s.Owner.GetAsync("/api/v1/remote/audit/verify"));
        Assert.False(verification.Valid);
        Assert.Equal(1, verification.FirstInvalidSequence);
    }
}
