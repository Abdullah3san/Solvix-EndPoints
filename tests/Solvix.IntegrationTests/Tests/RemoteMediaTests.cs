namespace Solvix.IntegrationTests.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Solvix.Agent.Contracts;
using Solvix.IntegrationTests.Fixtures;
using Solvix.Modules.Support.Application;
using Solvix.Modules.Support.Domain;

/// <summary>Screen sharing and file transfer, as sessions with the same consent, audit and isolation rules.</summary>
[Collection(ApiCollection.Name)]
public sealed class RemoteMediaTests(ApiFixture fixture)
{
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 16, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0, 0xFF, 0xD9];

    private async Task<(TenantSession Tenant, EnrolledDevice Device, HttpClient Agent, HttpClient Admin)> SetUpAsync(
        RemoteFeatures features = RemoteFeatures.Shell | RemoteFeatures.Screen | RemoteFeatures.Files | RemoteFeatures.Control)
    {
        var tenant = await fixture.RegisterTenantAsync();
        var device = await fixture.EnrollAsync(await fixture.CreateTokenAsync(tenant));
        var agent = fixture.CreateDeviceClient(device.Certificate);
        await Heartbeat(agent, features);
        return (tenant, device, agent, fixture.CreateClient(tenant.Token));
    }

    private static async Task Heartbeat(HttpClient agent, RemoteFeatures features) =>
        Assert.True((await agent.PostAsJsonAsync(
            "/" + AgentRoutes.Heartbeat,
            new HeartbeatRequest
            {
                AgentVersion = DeviceTestKit.AgentVersion,
                Health = new AgentHealthReport { StartedAtUtc = DateTimeOffset.UtcNow, UptimeSeconds = 5, RemoteAccess = RemoteAccessMode.ConsentRequired, RemoteFeatures = features },
            },
            ApiFixture.Json)).IsSuccessStatusCode);

    private static async Task<RemoteSessionDto> StartAsync(HttpClient admin, Guid deviceId, RemoteSessionKind kind, bool control = false) =>
        await HttpAssert.OkAsync<RemoteSessionDto>(
            await admin.PostAsJsonAsync($"/api/v1/devices/{deviceId}/remote-sessions", new StartRemoteSessionRequest("support", kind, control), ApiFixture.Json), HttpStatusCode.Created);

    private static async Task AcceptAsync(HttpClient agent, Guid id, bool control = false) =>
        Assert.Equal(HttpStatusCode.NoContent, (await agent.PostAsJsonAsync($"/{AgentRoutes.Remote}/{id}/accept", new AcceptRemoteRequest { Consent = RemoteConsent.User, ControlGranted = control }, ApiFixture.Json)).StatusCode);

    private static string Media(Guid session, string tail) => $"/{AgentRoutes.Remote}/{session}/{tail}";

    private static ByteArrayContent Bytes(byte[] data, string type = "application/octet-stream")
    {
        var content = new ByteArrayContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue(type);
        return content;
    }

    // ---------------------------------------------------------------- screen

    [Fact]
    public async Task Only_features_the_computer_reports_can_be_started()
    {
        var (_, device, _, admin) = await SetUpAsync(RemoteFeatures.Shell);
        await HttpAssert.ProblemAsync(
            await admin.PostAsJsonAsync($"/api/v1/devices/{device.DeviceId}/remote-sessions", new StartRemoteSessionRequest(null, RemoteSessionKind.Screen), ApiFixture.Json), HttpStatusCode.Conflict, "remote_feature_disabled");
        await HttpAssert.ProblemAsync(
            await admin.PostAsJsonAsync($"/api/v1/devices/{device.DeviceId}/remote-sessions", new StartRemoteSessionRequest(null, RemoteSessionKind.Files), ApiFixture.Json), HttpStatusCode.Conflict, "remote_feature_disabled");
        Assert.Equal(RemoteSessionKind.Shell, (await StartAsync(admin, device.DeviceId, RemoteSessionKind.Shell)).Kind);
    }

    [Fact]
    public async Task A_shell_and_a_screen_session_can_be_open_together_but_not_two_of_a_kind()
    {
        var (_, device, _, admin) = await SetUpAsync();
        await StartAsync(admin, device.DeviceId, RemoteSessionKind.Shell);
        await StartAsync(admin, device.DeviceId, RemoteSessionKind.Screen);
        await HttpAssert.ProblemAsync(
            await admin.PostAsJsonAsync($"/api/v1/devices/{device.DeviceId}/remote-sessions", new StartRemoteSessionRequest(null, RemoteSessionKind.Screen), ApiFixture.Json), HttpStatusCode.Conflict, "remote_session_open");
    }

    [Fact]
    public async Task A_screen_session_streams_frames_to_the_technician()
    {
        var (_, device, agent, admin) = await SetUpAsync();
        var session = await StartAsync(admin, device.DeviceId, RemoteSessionKind.Screen);
        var pending = await HttpAssert.OkAsync<PendingRemoteResponse>(await agent.GetAsync($"/{AgentRoutes.Remote}/pending?wait=5"));
        Assert.Equal(RemoteSessionKind.Screen, pending.Request!.Kind);
        await AcceptAsync(agent, session.Id);

        // Nothing yet: the viewer waits, then gets 204 with the state.
        var empty = await admin.GetAsync($"/api/v1/remote-sessions/{session.Id}/screen/frame?after=0&wait=1");
        Assert.Equal(HttpStatusCode.NoContent, empty.StatusCode);
        Assert.Equal("Active", empty.Headers.GetValues("X-Session-Status").Single());

        Assert.Equal(HttpStatusCode.NoContent, (await agent.PostAsync(Media(session.Id, "frame?width=1920&height=1080"), Bytes(Jpeg, "image/jpeg"))).StatusCode);
        var frame = await admin.GetAsync($"/api/v1/remote-sessions/{session.Id}/screen/frame?after=0&wait=5");
        Assert.Equal(HttpStatusCode.OK, frame.StatusCode);
        Assert.Equal("image/jpeg", frame.Content.Headers.ContentType!.MediaType);
        Assert.Equal(Jpeg, await frame.Content.ReadAsByteArrayAsync());
        Assert.Equal("1920", frame.Headers.GetValues("X-Frame-Width").Single());
        Assert.Equal("1080", frame.Headers.GetValues("X-Frame-Height").Single());
        var sequence = long.Parse(frame.Headers.GetValues("X-Frame-Sequence").Single());

        // Same frame again: nothing newer.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.GetAsync($"/api/v1/remote-sessions/{session.Id}/screen/frame?after={sequence}&wait=0")).StatusCode);

        // A newer frame wakes a waiting viewer.
        var waiting = admin.GetAsync($"/api/v1/remote-sessions/{session.Id}/screen/frame?after={sequence}&wait=10");
        await Task.Delay(700);
        var second = Jpeg.Concat(new byte[] { 1, 2, 3 }).ToArray();
        await agent.PostAsync(Media(session.Id, "frame?width=1920&height=1080"), Bytes(second, "image/jpeg"));
        var next = await waiting;
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        Assert.Equal(second, await next.Content.ReadAsByteArrayAsync());

        // Ending stops frames.
        await admin.PostAsync($"/api/v1/remote-sessions/{session.Id}/end", null);
        await HttpAssert.ProblemAsync(await agent.PostAsync(Media(session.Id, "frame?width=1&height=1"), Bytes(Jpeg, "image/jpeg")), HttpStatusCode.Conflict, "remote_session_not_active");
        var after = await admin.GetAsync($"/api/v1/remote-sessions/{session.Id}/screen/frame?after=0&wait=0");
        Assert.Equal(HttpStatusCode.NoContent, after.StatusCode);
        Assert.Equal("Ended", after.Headers.GetValues("X-Session-Status").Single());
    }

    [Fact]
    public async Task Frames_must_be_small_jpegs_from_the_right_computer_for_a_screen_session()
    {
        var (_, device, agent, admin) = await SetUpAsync();
        var session = await StartAsync(admin, device.DeviceId, RemoteSessionKind.Screen);
        await AcceptAsync(agent, session.Id);

        await HttpAssert.ProblemAsync(await agent.PostAsync(Media(session.Id, "frame?width=10&height=10"), Bytes([1, 2, 3, 4, 5], "image/jpeg")), HttpStatusCode.BadRequest, "validation_failed");
        await HttpAssert.ProblemAsync(await agent.PostAsync(Media(session.Id, "frame?width=0&height=10"), Bytes(Jpeg, "image/jpeg")), HttpStatusCode.BadRequest, "validation_failed");
        var huge = new byte[ScreenLimits.MaxFrameBytes + 10];
        huge[0] = 0xFF; huge[1] = 0xD8; huge[2] = 0xFF;
        Assert.True((await agent.PostAsync(Media(session.Id, "frame?width=10&height=10"), Bytes(huge, "image/jpeg"))).StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge);

        // A different computer (other tenant) cannot post to it; a shell session is not a screen.
        var other = await SetUpAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other.Agent.PostAsync(Media(session.Id, "frame?width=10&height=10"), Bytes(Jpeg, "image/jpeg"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Admin.GetAsync($"/api/v1/remote-sessions/{session.Id}/screen/frame?after=0&wait=0")).StatusCode);
        var shell = await StartAsync(admin, device.DeviceId, RemoteSessionKind.Shell);
        await HttpAssert.ProblemAsync(await admin.GetAsync($"/api/v1/remote-sessions/{shell.Id}/screen/frame?after=0&wait=0"), HttpStatusCode.Conflict, "remote_wrong_kind");
    }

    [Fact]
    public async Task Control_needs_an_explicit_request_and_a_grant_from_the_computer()
    {
        var (_, device, agent, admin) = await SetUpAsync();

        // View only: the technician did not ask to control.
        var viewOnly = await StartAsync(admin, device.DeviceId, RemoteSessionKind.Screen, control: false);
        Assert.False(viewOnly.ControlRequested);
        await AcceptAsync(agent, viewOnly.Id, control: true); // even a grant cannot exceed the request
        Assert.False((await HttpAssert.OkAsync<RemoteSessionDto>(await admin.GetAsync($"/api/v1/remote-sessions/{viewOnly.Id}"))).ControlGranted);
        await HttpAssert.ProblemAsync(
            await admin.PostAsJsonAsync($"/api/v1/remote-sessions/{viewOnly.Id}/input", new RemoteInputRequest("""{"type":"move","x":0.5,"y":0.5}"""), ApiFixture.Json), HttpStatusCode.Conflict, "remote_control_not_granted");
        await admin.PostAsync($"/api/v1/remote-sessions/{viewOnly.Id}/end", null);

        // Requested but the computer only allows viewing.
        var refused = await StartAsync(admin, device.DeviceId, RemoteSessionKind.Screen, control: true);
        await AcceptAsync(agent, refused.Id, control: false);
        Assert.False((await HttpAssert.OkAsync<RemoteSessionDto>(await admin.GetAsync($"/api/v1/remote-sessions/{refused.Id}"))).ControlGranted);
        await admin.PostAsync($"/api/v1/remote-sessions/{refused.Id}/end", null);

        // Granted: well-formed events reach the computer, nothing else does.
        var granted = await StartAsync(admin, device.DeviceId, RemoteSessionKind.Screen, control: true);
        await AcceptAsync(agent, granted.Id, control: true);
        Assert.True((await HttpAssert.OkAsync<RemoteSessionDto>(await admin.GetAsync($"/api/v1/remote-sessions/{granted.Id}"))).ControlGranted);
        const string click = """{"type":"down","x":0.25,"y":0.75,"button":"left"}""";
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync($"/api/v1/remote-sessions/{granted.Id}/input", new RemoteInputRequest(click), ApiFixture.Json)).StatusCode);
        var received = await HttpAssert.OkAsync<RemoteInputResponse>(await agent.GetAsync($"/{AgentRoutes.Remote}/{granted.Id}/input?after=0&wait=3"));
        Assert.Equal(click, Assert.Single(received.Chunks).Text);
        foreach (var bad in new[] { "not json", """{"type":"format-disk"}""", """{"x":1}""", new string(' ', ScreenLimits.MaxInputBytes + 1) + "{}" })
        {
            await HttpAssert.ProblemAsync(
                await admin.PostAsJsonAsync($"/api/v1/remote-sessions/{granted.Id}/input", new RemoteInputRequest(bad), ApiFixture.Json), HttpStatusCode.BadRequest, "validation_failed");
        }
    }

    // ----------------------------------------------------------------- files

    private async Task<(RemoteSessionDto Session, HttpClient Agent, HttpClient Admin, EnrolledDevice Device)> FileSessionAsync()
    {
        var (_, device, agent, admin) = await SetUpAsync();
        var session = await StartAsync(admin, device.DeviceId, RemoteSessionKind.Files);
        await AcceptAsync(agent, session.Id);
        return (session, agent, admin, device);
    }

    private static async Task<AgentFileOperation> ClaimAsync(HttpClient agent, Guid session)
    {
        var next = await HttpAssert.OkAsync<PendingFileOperationResponse>(await agent.GetAsync(Media(session, "files/next?wait=10")));
        return next.Operation ?? throw new InvalidOperationException("No operation arrived.");
    }

    [Fact]
    public async Task Listing_a_folder_is_relayed_and_audited()
    {
        var (session, agent, admin, _) = await FileSessionAsync();
        var listing = admin.PostAsJsonAsync($"/api/v1/remote-sessions/{session.Id}/files/list", new ListRemoteFilesRequest("C:\\Drop"), ApiFixture.Json);

        var op = await ClaimAsync(agent, session.Id);
        Assert.Equal(FileOperationKind.List, op.Kind);
        Assert.Equal("C:\\Drop", op.Path);
        var result = new FileOperationResult
        {
            Success = true,
            Path = "C:\\Drop",
            Entries = [new RemoteFileEntry { Name = "notes.txt", Path = "C:\\Drop\\notes.txt", SizeBytes = 12 }, new RemoteFileEntry { Name = "logs", Path = "C:\\Drop\\logs", IsDirectory = true }],
        };
        Assert.Equal(HttpStatusCode.NoContent, (await agent.PostAsJsonAsync(Media(session.Id, $"files/{op.Id}/result"), result, ApiFixture.Json)).StatusCode);

        var dto = await HttpAssert.OkAsync<RemoteFileOperationDto>(await listing);
        Assert.Equal(RemoteFileOperationStatus.Completed, dto.Status);
        Assert.Equal(["notes.txt", "logs"], dto.Entries!.Select(e => e.Name));

        var audit = await HttpAssert.OkAsync<List<RemoteFileOperationDto>>(await admin.GetAsync($"/api/v1/remote-sessions/{session.Id}/files/operations"));
        Assert.Equal("C:\\Drop", Assert.Single(audit).Path);
    }

    [Fact]
    public async Task A_file_is_downloaded_from_the_computer_intact()
    {
        var (session, agent, admin, _) = await FileSessionAsync();
        var data = RandomNumberGenerator.GetBytes(FileLimits.ChunkBytes * 2 + 1234); // three chunks
        var download = admin.GetAsync($"/api/v1/remote-sessions/{session.Id}/files/download?path={Uri.EscapeDataString("C:\\Drop\\report.bin")}");

        var op = await ClaimAsync(agent, session.Id);
        Assert.Equal(FileOperationKind.Download, op.Kind);
        Assert.Equal(HttpStatusCode.NoContent, (await agent.PostAsJsonAsync(Media(session.Id, $"files/{op.Id}/begin"), new BeginFileDownloadRequest { SizeBytes = data.Length }, ApiFixture.Json)).StatusCode);
        for (var i = 0; i * FileLimits.ChunkBytes < data.Length; i++)
        {
            var slice = data.Skip(i * FileLimits.ChunkBytes).Take(FileLimits.ChunkBytes).ToArray();
            Assert.Equal(HttpStatusCode.NoContent, (await agent.PostAsync(Media(session.Id, $"files/{op.Id}/chunk?index={i}"), Bytes(slice))).StatusCode);
        }

        await agent.PostAsJsonAsync(Media(session.Id, $"files/{op.Id}/result"), new FileOperationResult { Success = true, Sha256 = Convert.ToHexStringLower(SHA256.HashData(data)), SizeBytes = data.Length }, ApiFixture.Json);

        var response = await download;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(data.Length, response.Content.Headers.ContentLength);
        Assert.Equal("report.bin", response.Content.Headers.ContentDisposition!.FileName);
        Assert.Equal(data, await response.Content.ReadAsByteArrayAsync());

        var audit = await HttpAssert.OkAsync<RemoteFileOperationDto>(await admin.GetAsync($"/api/v1/remote-sessions/{session.Id}/files/operations/{op.Id}"));
        Assert.Equal(RemoteFileOperationStatus.Completed, audit.Status);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(data)), audit.Sha256);
    }

    [Fact]
    public async Task A_refused_download_reports_the_reason_and_incomplete_data_is_not_accepted()
    {
        var (session, agent, admin, _) = await FileSessionAsync();
        var refused = admin.GetAsync($"/api/v1/remote-sessions/{session.Id}/files/download?path=C%3A%5CWindows%5Csecret");
        var op = await ClaimAsync(agent, session.Id);
        await agent.PostAsJsonAsync(Media(session.Id, $"files/{op.Id}/result"), new FileOperationResult { Success = false, Error = "That folder is not available for transfer." }, ApiFixture.Json);
        var problem = await HttpAssert.ProblemAsync(await refused, HttpStatusCode.Conflict, "file_download_failed");
        Assert.Contains("not available for transfer", problem.GetProperty("detail").GetString(), StringComparison.Ordinal);

        // Announced 300 KB but only the first chunk arrives: completion is turned into a failure.
        var partial = admin.GetAsync($"/api/v1/remote-sessions/{session.Id}/files/download?path=C%3A%5CDrop%5Cbig.bin");
        var second = await ClaimAsync(agent, session.Id);
        await agent.PostAsJsonAsync(Media(session.Id, $"files/{second.Id}/begin"), new BeginFileDownloadRequest { SizeBytes = FileLimits.ChunkBytes + 100 }, ApiFixture.Json);
        await agent.PostAsync(Media(session.Id, $"files/{second.Id}/chunk?index=0"), Bytes(new byte[FileLimits.ChunkBytes]));
        // A chunk outside the announced size is rejected outright.
        await HttpAssert.ProblemAsync(await agent.PostAsync(Media(session.Id, $"files/{second.Id}/chunk?index=5"), Bytes(new byte[10])), HttpStatusCode.BadRequest, "validation_failed");
        await agent.PostAsJsonAsync(Media(session.Id, $"files/{second.Id}/result"), new FileOperationResult { Success = true }, ApiFixture.Json);
        var op2 = await HttpAssert.OkAsync<RemoteFileOperationDto>(await admin.GetAsync($"/api/v1/remote-sessions/{session.Id}/files/operations/{second.Id}"));
        Assert.Equal(RemoteFileOperationStatus.Failed, op2.Status);
        Assert.Contains("completely", op2.Error, StringComparison.Ordinal);
        try
        {
            await partial; // the stream is cut once the transfer is known to be incomplete
        }
        catch (HttpRequestException)
        {
        }
    }

    [Fact]
    public async Task A_file_is_uploaded_to_the_computer_with_its_checksum()
    {
        var (session, agent, admin, _) = await FileSessionAsync();
        var data = RandomNumberGenerator.GetBytes(FileLimits.ChunkBytes * 2 + 77);
        var sha = Convert.ToHexStringLower(SHA256.HashData(data));

        var response = await admin.PostAsync($"/api/v1/remote-sessions/{session.Id}/files/upload?path={Uri.EscapeDataString("C:\\Drop\\tool.exe")}&overwrite=true", Bytes(data));
        var staged = await HttpAssert.OkAsync<RemoteFileOperationDto>(response, HttpStatusCode.Accepted);
        Assert.Equal(RemoteFileOperationStatus.Pending, staged.Status);
        Assert.Equal(data.Length, staged.SizeBytes);
        Assert.Equal(sha, staged.Sha256);

        var op = await ClaimAsync(agent, session.Id);
        Assert.Equal(FileOperationKind.Upload, op.Kind);
        Assert.Equal(sha, op.Sha256);
        Assert.Equal(data.Length, op.SizeBytes);
        Assert.True(op.Overwrite);
        var received = new List<byte>();
        for (var i = 0; i < 3; i++)
        {
            received.AddRange(await (await agent.GetAsync(Media(session.Id, $"files/{op.Id}/chunk/{i}"))).Content.ReadAsByteArrayAsync());
        }

        Assert.Equal(data, received.ToArray());
        Assert.Equal(HttpStatusCode.NotFound, (await agent.GetAsync(Media(session.Id, $"files/{op.Id}/chunk/3"))).StatusCode);
        await agent.PostAsJsonAsync(Media(session.Id, $"files/{op.Id}/result"), new FileOperationResult { Success = true, Sha256 = sha, SizeBytes = data.Length }, ApiFixture.Json);

        var done = await HttpAssert.OkAsync<RemoteFileOperationDto>(await admin.GetAsync($"/api/v1/remote-sessions/{session.Id}/files/operations/{op.Id}?wait=5"));
        Assert.Equal(RemoteFileOperationStatus.Completed, done.Status);

        // Staged data is gone once the computer has it.
        Assert.Equal(HttpStatusCode.Conflict, (await agent.GetAsync(Media(session.Id, $"files/{op.Id}/chunk/0"))).StatusCode);
    }

    [Fact]
    public async Task File_requests_are_validated_and_only_work_in_active_file_sessions()
    {
        var (session, agent, admin, device) = await FileSessionAsync();
        await HttpAssert.ProblemAsync(await admin.PostAsync($"/api/v1/remote-sessions/{session.Id}/files/upload?path=C%3A%5Cx", Bytes([])), HttpStatusCode.BadRequest, "validation_failed");
        await HttpAssert.ProblemAsync(await admin.PostAsync($"/api/v1/remote-sessions/{session.Id}/files/upload?path=", Bytes([1])), HttpStatusCode.BadRequest, "validation_failed");
        await HttpAssert.ProblemAsync(await admin.GetAsync($"/api/v1/remote-sessions/{session.Id}/files/download?path="), HttpStatusCode.BadRequest, "validation_failed");
        await HttpAssert.ProblemAsync(
            await admin.PostAsJsonAsync($"/api/v1/remote-sessions/{session.Id}/input", new RemoteInputRequest("dir"), ApiFixture.Json), HttpStatusCode.Conflict, "remote_input_not_supported");

        // A failed (empty) upload is never offered to the computer.
        Assert.Null((await HttpAssert.OkAsync<PendingFileOperationResponse>(await agent.GetAsync(Media(session.Id, "files/next?wait=0")))).Operation);

        // A shell session has no files; an ended session takes no operations.
        var shell = await StartAsync(admin, device.DeviceId, RemoteSessionKind.Shell);
        await HttpAssert.ProblemAsync(
            await admin.PostAsJsonAsync($"/api/v1/remote-sessions/{shell.Id}/files/list", new ListRemoteFilesRequest(null), ApiFixture.Json), HttpStatusCode.Conflict, "remote_wrong_kind");
        await admin.PostAsync($"/api/v1/remote-sessions/{session.Id}/end", null);
        await HttpAssert.ProblemAsync(
            await admin.PostAsJsonAsync($"/api/v1/remote-sessions/{session.Id}/files/list", new ListRemoteFilesRequest(null), ApiFixture.Json), HttpStatusCode.Conflict, "remote_session_not_active");
        Assert.True((await HttpAssert.OkAsync<PendingFileOperationResponse>(await agent.GetAsync(Media(session.Id, "files/next?wait=0")))).Ended);
    }

    [Fact]
    public async Task File_sessions_are_isolated_between_tenants_and_computers()
    {
        var (session, agent, admin, _) = await FileSessionAsync();
        var listing = admin.PostAsJsonAsync($"/api/v1/remote-sessions/{session.Id}/files/list", new ListRemoteFilesRequest("C:\\Drop"), ApiFixture.Json);
        var op = await ClaimAsync(agent, session.Id);
        var other = await SetUpAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await other.Admin.PostAsJsonAsync($"/api/v1/remote-sessions/{session.Id}/files/list", new ListRemoteFilesRequest(null), ApiFixture.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Admin.GetAsync($"/api/v1/remote-sessions/{session.Id}/files/operations")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Admin.GetAsync($"/api/v1/remote-sessions/{session.Id}/files/download?path=x")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Agent.GetAsync(Media(session.Id, "files/next?wait=0"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Agent.PostAsJsonAsync(Media(session.Id, $"files/{op.Id}/result"), new FileOperationResult { Success = true }, ApiFixture.Json)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.Agent.GetAsync(Media(session.Id, $"files/{op.Id}/chunk/0"))).StatusCode);

        await agent.PostAsJsonAsync(Media(session.Id, $"files/{op.Id}/result"), new FileOperationResult { Success = true, Path = "C:\\Drop", Entries = [] }, ApiFixture.Json);
        await listing;
        // Finishing twice is refused.
        await HttpAssert.ProblemAsync(
            await agent.PostAsJsonAsync(Media(session.Id, $"files/{op.Id}/result"), new FileOperationResult { Success = true }, ApiFixture.Json), HttpStatusCode.Conflict, "file_operation_finished");
    }
}
