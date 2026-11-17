namespace Solvix.Agent.Tests;

using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Solvix.Agent.Contracts;
using Solvix.Agent.Core.Communication;
using Solvix.Agent.Core.Configuration;
using Solvix.Agent.Core.Enrollment;
using Solvix.Agent.Core.Health;
using Solvix.Agent.Core.Remote;

/// <summary>File transfer sandbox and runner, screen sharing runner and helper bridge, and per-kind policy.</summary>
public sealed class FileSandboxTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "solvix-sandbox-" + Guid.NewGuid().ToString("N"));
    private readonly string _outside = Path.Combine(Path.GetTempPath(), "solvix-outside-" + Guid.NewGuid().ToString("N"));

    public FileSandboxTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs"));
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "secret");
    }

    public void Dispose()
    {
        Directory.Delete(_root, true);
        Directory.Delete(_outside, true);
    }

    [Fact]
    public void Paths_inside_the_allowed_folder_resolve()
    {
        var sandbox = new FileSandbox([_root]);
        Assert.True(sandbox.TryResolve(_root, out var root, out _));
        Assert.Equal(_root, root);
        Assert.True(sandbox.TryResolve(Path.Combine(_root, "docs", "a.txt"), out var file, out _));
        Assert.Equal(Path.Combine(_root, "docs", "a.txt"), file);
        Assert.True(sandbox.TryResolve(Path.Combine(_root, "docs") + Path.DirectorySeparatorChar, out _, out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("docs/a.txt")]
    [InlineData("..")]
    public void Relative_and_empty_paths_are_refused(string path)
    {
        Assert.False(new FileSandbox([_root]).TryResolve(path, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Traversal_siblings_and_other_folders_are_refused()
    {
        var sandbox = new FileSandbox([_root]);
        Assert.False(sandbox.TryResolve(Path.Combine(_root, "..", Path.GetFileName(_outside), "secret.txt"), out _, out _));
        Assert.False(sandbox.TryResolve(Path.Combine(_root, "docs", "..", "..", "etc", "passwd"), out _, out _));
        Assert.False(sandbox.TryResolve(Path.Combine(_outside, "secret.txt"), out _, out _));
        // A folder that merely starts with the same characters is not inside the root.
        Directory.CreateDirectory(_root + "evil");
        try
        {
            Assert.False(sandbox.TryResolve(Path.Combine(_root + "evil", "x.txt"), out _, out _));
        }
        finally
        {
            Directory.Delete(_root + "evil", true);
        }

        Assert.False(sandbox.TryResolve("/etc/shadow", out _, out _));
        Assert.False(sandbox.TryResolve(Path.Combine(_root, "a\0b"), out _, out _));
    }

    [Theory]
    [InlineData("file.txt:hidden")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("trailing.")]
    [InlineData("LPT1")]
    public void Alternate_streams_device_names_and_odd_names_are_refused(string name)
    {
        Assert.False(new FileSandbox([_root]).TryResolve(Path.Combine(_root, name), out _, out _));
    }

    [Fact]
    public void Links_that_could_lead_out_of_the_folder_are_refused()
    {
        var link = Path.Combine(_root, "escape");
        try
        {
            Directory.CreateSymbolicLink(link, _outside);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return; // links cannot be created here
        }

        var sandbox = new FileSandbox([_root]);
        Assert.False(sandbox.TryResolve(link, out _, out _));
        Assert.False(sandbox.TryResolve(Path.Combine(link, "secret.txt"), out _, out _));
    }

    [Fact]
    public void Only_absolute_existing_style_roots_count()
    {
        Assert.Empty(new FileSandbox(["relative/folder", "", "  "]).Roots);
        Assert.Equal(2, new FileSandbox([_root, _outside, _root]).Roots.Count);
    }
}

public sealed class FileSessionRunnerTests : IDisposable
{
    private readonly FakeAgentApi _api = new();
    private readonly string _data = Path.Combine(Path.GetTempPath(), "solvix-files-" + Guid.NewGuid().ToString("N"));
    private readonly FileSessionRunner _runner;
    private readonly FileSandbox _sandbox;
    private readonly Guid _session = Guid.NewGuid();
    private readonly string _folder;

    public FileSessionRunnerTests()
    {
        _folder = Path.Combine(_data, "transfer");
        var options = Options.Create(new AgentOptions { DataDirectory = _data, RemoteAccess = new RemoteAccessOptions { MaxFileMegabytes = 1 } });
        _runner = new FileSessionRunner(_api, options, NullLogger<FileSessionRunner>.Instance);
        _sandbox = _runner.CreateSandbox(); // creates the transfer folder
    }

    public void Dispose()
    {
        _api.Dispose();
        Directory.Delete(_data, true);
    }

    private static AgentFileOperation Op(FileOperationKind kind, string path, long size = 0, string? sha = null, bool overwrite = false) =>
        new() { Id = Guid.NewGuid(), Kind = kind, Path = path, SizeBytes = size, Sha256 = sha, Overwrite = overwrite };

    private FileOperationResult Result(AgentFileOperation op) => _api.FileResults.Single(r => r.Op == op.Id).Result;

    [Fact]
    public async Task Listing_with_no_path_offers_only_the_allowed_folders()
    {
        var op = Op(FileOperationKind.List, "");
        await _runner.ExecuteAsync(_session, op, _sandbox, CancellationToken.None);
        var entries = Result(op).Entries!;
        Assert.Equal(_folder, Assert.Single(entries).Path);
        Assert.True(entries[0].IsDirectory);
    }

    [Fact]
    public async Task Listing_a_folder_shows_folders_first_and_never_links()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "zz-folder"));
        File.WriteAllText(Path.Combine(_folder, "b.txt"), "hello");
        File.WriteAllText(Path.Combine(_folder, "a.txt"), "hi");
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(_folder, "link"), Path.GetTempPath());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
        }

        var op = Op(FileOperationKind.List, _folder);
        await _runner.ExecuteAsync(_session, op, _sandbox, CancellationToken.None);
        var result = Result(op);
        Assert.True(result.Success);
        Assert.Equal(["zz-folder", "a.txt", "b.txt"], result.Entries!.Select(e => e.Name));
        Assert.Equal(5, result.Entries![2].SizeBytes);
    }

    [Fact]
    public async Task Anything_outside_the_allowed_folders_is_refused_without_touching_it()
    {
        var listing = Op(FileOperationKind.List, "/etc");
        var download = Op(FileOperationKind.Download, "/etc/passwd");
        var upload = Op(FileOperationKind.Upload, Path.Combine(Path.GetTempPath(), "solvix-evil.txt"), 3, new string('0', 64));
        foreach (var op in new[] { listing, download, upload })
        {
            await _runner.ExecuteAsync(_session, op, _sandbox, CancellationToken.None);
            var result = Result(op);
            Assert.False(result.Success);
            Assert.Contains("not available", result.Error, StringComparison.Ordinal);
        }

        Assert.Empty(_api.FileBegun);
        Assert.False(File.Exists(Path.Combine(Path.GetTempPath(), "solvix-evil.txt")));
    }

    [Fact]
    public async Task A_download_sends_every_chunk_in_order_with_a_matching_checksum()
    {
        var data = RandomNumberGenerator.GetBytes(FileLimits.ChunkBytes + 500);
        File.WriteAllBytes(Path.Combine(_folder, "big.bin"), data);
        var op = Op(FileOperationKind.Download, Path.Combine(_folder, "big.bin"));
        await _runner.ExecuteAsync(_session, op, _sandbox, CancellationToken.None);

        Assert.Equal(data.Length, Assert.Single(_api.FileBegun).Size);
        Assert.Equal([0, 1], _api.FileChunks.Select(c => c.Index));
        Assert.Equal(data, _api.FileChunks.SelectMany(c => c.Data).ToArray());
        var result = Result(op);
        Assert.True(result.Success);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(data)), result.Sha256);
    }

    [Fact]
    public async Task Missing_empty_and_oversized_files_are_not_sent()
    {
        File.WriteAllBytes(Path.Combine(_folder, "empty.bin"), []);
        File.WriteAllBytes(Path.Combine(_folder, "huge.bin"), new byte[1024 * 1024 + 1]); // limit is 1 MB in this test
        foreach (var name in new[] { "nope.bin", "empty.bin", "huge.bin" })
        {
            var op = Op(FileOperationKind.Download, Path.Combine(_folder, name));
            await _runner.ExecuteAsync(_session, op, _sandbox, CancellationToken.None);
            Assert.False(Result(op).Success);
        }

        Assert.Empty(_api.FileBegun);
    }

    [Fact]
    public async Task An_upload_is_verified_before_it_replaces_anything()
    {
        var data = RandomNumberGenerator.GetBytes(FileLimits.ChunkBytes + 10);
        var sha = Convert.ToHexStringLower(SHA256.HashData(data));
        var op = Op(FileOperationKind.Upload, Path.Combine(_folder, "tool.bin"), data.Length, sha);
        _api.UploadData[(op.Id, 0)] = data[..FileLimits.ChunkBytes];
        _api.UploadData[(op.Id, 1)] = data[FileLimits.ChunkBytes..];

        await _runner.ExecuteAsync(_session, op, _sandbox, CancellationToken.None);
        Assert.True(Result(op).Success);
        Assert.Equal(data, File.ReadAllBytes(Path.Combine(_folder, "tool.bin")));
        Assert.Empty(Directory.GetFiles(_folder, "*.tmp"));
    }

    [Fact]
    public async Task A_damaged_upload_is_discarded_and_the_existing_file_is_kept()
    {
        var target = Path.Combine(_folder, "keep.txt");
        File.WriteAllText(target, "original");
        var op = Op(FileOperationKind.Upload, target, 4, new string('a', 64), overwrite: true); // wrong checksum
        _api.UploadData[(op.Id, 0)] = [1, 2, 3, 4];

        await _runner.ExecuteAsync(_session, op, _sandbox, CancellationToken.None);
        var result = Result(op);
        Assert.False(result.Success);
        Assert.Contains("checksum", result.Error, StringComparison.Ordinal);
        Assert.Equal("original", File.ReadAllText(target));
        Assert.Empty(Directory.GetFiles(_folder, "*.tmp"));
    }

    [Fact]
    public async Task Uploads_never_overwrite_unless_allowed_and_respect_size_limits()
    {
        var target = Path.Combine(_folder, "exists.txt");
        File.WriteAllText(target, "original");
        var data = new byte[] { 9, 9, 9 };
        var sha = Convert.ToHexStringLower(SHA256.HashData(data));

        var refused = Op(FileOperationKind.Upload, target, 3, sha, overwrite: false);
        _api.UploadData[(refused.Id, 0)] = data;
        await _runner.ExecuteAsync(_session, refused, _sandbox, CancellationToken.None);
        Assert.Contains("already exists", Result(refused).Error, StringComparison.Ordinal);
        Assert.Equal("original", File.ReadAllText(target));

        var allowed = Op(FileOperationKind.Upload, target, 3, sha, overwrite: true);
        _api.UploadData[(allowed.Id, 0)] = data;
        await _runner.ExecuteAsync(_session, allowed, _sandbox, CancellationToken.None);
        Assert.True(Result(allowed).Success);
        Assert.Equal(data, File.ReadAllBytes(target));

        var tooBig = Op(FileOperationKind.Upload, Path.Combine(_folder, "big.bin"), 5L * 1024 * 1024, sha);
        await _runner.ExecuteAsync(_session, tooBig, _sandbox, CancellationToken.None);
        Assert.Contains("larger than", Result(tooBig).Error, StringComparison.Ordinal);

        var missingFolder = Op(FileOperationKind.Upload, Path.Combine(_folder, "nodir", "x.bin"), 3, sha);
        await _runner.ExecuteAsync(_session, missingFolder, _sandbox, CancellationToken.None);
        Assert.Contains("does not exist", Result(missingFolder).Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_session_loop_runs_claimed_operations_until_the_session_ends()
    {
        File.WriteAllText(Path.Combine(_folder, "one.txt"), "x");
        var op = Op(FileOperationKind.List, _folder);
        _api.FileOperations.Enqueue(new PendingFileOperationResponse { Operation = op });
        using var session = new CancellationTokenSource();

        await _runner.RunAsync(_session, session); // the fake ends the session when the queue is empty
        Assert.True(Result(op).Success);
    }
}

public sealed class ScreenSessionRunnerTests
{
    private static readonly byte[] FrameA = [0xFF, 0xD8, 0xFF, 1];
    private static readonly byte[] FrameB = [0xFF, 0xD8, 0xFF, 2];

    private sealed class Desktop : IRemoteDesktop
    {
        public List<ScreenFrame> Frames { get; } = [new(FrameA, 100, 50), new(FrameA, 100, 50), new(FrameB, 100, 50)];

        public List<ScreenInputEvent> Injected { get; } = [];

        public bool IsAvailable { get; set; } = true;

        private int _next;

        public Task<ScreenFrame?> CaptureAsync(CancellationToken cancellationToken)
        {
            var index = Math.Min(Interlocked.Increment(ref _next) - 1, Frames.Count - 1);
            return Task.FromResult<ScreenFrame?>(Frames[index]);
        }

        public Task InjectAsync(ScreenInputEvent input, CancellationToken cancellationToken)
        {
            lock (Injected)
            {
                Injected.Add(input);
            }

            return Task.CompletedTask;
        }
    }

    private static async Task<(FakeAgentApi Api, Desktop Desktop)> RunAsync(bool control, params string[] inputs)
    {
        var api = new FakeAgentApi { RemoteInputEndsWhenEmpty = false };
        var desktop = new Desktop();
        api.RemoteInput.Enqueue(new RemoteInputResponse { Chunks = inputs.Select((t, i) => new RemoteChunk { Sequence = i + 1, Text = t }).ToList() });
        var runner = new ScreenSessionRunner(api, desktop, NullLogger<ScreenSessionRunner>.Instance)
        {
            FrameInterval = TimeSpan.FromMilliseconds(10),
            KeepAlive = TimeSpan.FromMinutes(5),
        };
        using var session = new CancellationTokenSource();
        var running = runner.RunAsync(Guid.NewGuid(), control, session);
        await Task.Delay(400);
        api.RemoteInput.Enqueue(new RemoteInputResponse { Ended = true });
        await running.WaitAsync(TimeSpan.FromSeconds(10));
        return (api, desktop);
    }

    [Fact]
    public async Task Only_changed_frames_are_sent()
    {
        var (api, _) = await RunAsync(control: false);
        Assert.Equal([FrameA, FrameB], api.ScreenFrames.Select(f => f.Jpeg).ToList());
        Assert.All(api.ScreenFrames, f => Assert.Equal((100, 50), (f.Width, f.Height)));
        api.Dispose();
    }

    [Fact]
    public async Task Input_is_applied_only_when_control_was_granted()
    {
        const string click = """{"type":"down","x":0.5,"y":0.5,"button":"left"}""";
        var (apiView, viewOnly) = await RunAsync(control: false, click);
        Assert.Empty(viewOnly.Injected);
        apiView.Dispose();

        var (apiControl, controlled) = await RunAsync(control: true, click, "not json", """{"type":"key","name":"Enter","pressed":true}""");
        Assert.Equal([ScreenInputEvent.Down, ScreenInputEvent.Key], controlled.Injected.Select(i => i.Type));
        apiControl.Dispose();
    }

    [Fact]
    public async Task Sharing_stops_when_the_desktop_goes_away()
    {
        var api = new FakeAgentApi { RemoteInputEndsWhenEmpty = false };
        var desktop = new Desktop();
        desktop.Frames.Clear();
        var gone = new GoneDesktop();
        var runner = new ScreenSessionRunner(api, gone, NullLogger<ScreenSessionRunner>.Instance) { FrameInterval = TimeSpan.FromMilliseconds(10) };
        using var session = new CancellationTokenSource();
        await runner.RunAsync(Guid.NewGuid(), false, session).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(session.IsCancellationRequested);
        api.Dispose();
    }

    private sealed class GoneDesktop : IRemoteDesktop
    {
        public bool IsAvailable => false;

        public Task<ScreenFrame?> CaptureAsync(CancellationToken cancellationToken) => Task.FromResult<ScreenFrame?>(null);

        public Task InjectAsync(ScreenInputEvent input, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

public sealed class ScreenHelperBridgeTests
{
    private sealed class FakeCapture : IScreenCapture
    {
        public ScreenFrame? Next { get; set; } = new([0xFF, 0xD8, 0xFF, 7], 64, 32);

        public ScreenFrame? Capture() => Next;
    }

    private sealed class FakeInjector : IInputInjector
    {
        public List<ScreenInputEvent> Events { get; } = [];

        public void Inject(ScreenInputEvent input) => Events.Add(input);
    }

    private static async Task<(NamedPipeServerStream Server, NamedPipeClientStream Client)> PairAsync()
    {
        var name = "solvix-helper-test-" + Guid.NewGuid().ToString("N");
        var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Task.WhenAll(server.WaitForConnectionAsync(), client.ConnectAsync(3000));
        return (server, client);
    }

    [Fact]
    public async Task The_service_captures_and_injects_through_the_helper()
    {
        var (server, client) = await PairAsync();
        var capture = new FakeCapture();
        var injector = new FakeInjector();
        using var stop = new CancellationTokenSource();
        var helper = new ScreenHelperLoop(capture, injector, "unused").ServeAsync(client, stop.Token);
        var broker = new ScreenHelperBroker(NullLogger<ScreenHelperBroker>.Instance);
        Assert.False(broker.IsAvailable);
        broker.Attach(server);
        Assert.True(broker.IsAvailable);

        var frame = await broker.CaptureAsync(CancellationToken.None);
        Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF, 7 }, frame!.Jpeg);
        Assert.Equal((64, 32), (frame.Width, frame.Height));

        await broker.InjectAsync(new ScreenInputEvent { Type = ScreenInputEvent.Move, X = 0.1, Y = 0.9 }, CancellationToken.None);
        var injected = Assert.Single(injector.Events);
        Assert.Equal((0.1, 0.9), (injected.X, injected.Y));

        capture.Next = null; // the desktop cannot be captured right now (e.g. secure desktop)
        Assert.Null(await broker.CaptureAsync(CancellationToken.None));
        Assert.True(broker.IsAvailable);

        await stop.CancelAsync();
        client.Dispose();
        try
        {
            await helper.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or EndOfStreamException or ObjectDisposedException)
        {
            // The helper loop ends with the connection.
        }

        // The helper went away: the next request notices and the desktop is unavailable.
        Assert.Null(await broker.CaptureAsync(CancellationToken.None));
        Assert.False(broker.IsAvailable);
    }

    [Fact]
    public async Task Without_a_helper_nothing_can_be_captured()
    {
        var broker = new ScreenHelperBroker(NullLogger<ScreenHelperBroker>.Instance);
        Assert.Null(await broker.CaptureAsync(CancellationToken.None));
        await broker.InjectAsync(new ScreenInputEvent { Type = ScreenInputEvent.Move }, CancellationToken.None);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Only_authorised_helpers_are_attached(bool authorised, bool expectedAvailable)
    {
        var pipe = "solvix-helper-srv-" + Guid.NewGuid().ToString("N");
        var broker = new ScreenHelperBroker(NullLogger<ScreenHelperBroker>.Instance);
        var options = Options.Create(new AgentOptions { RemoteAccess = new RemoteAccessOptions { Mode = RemoteAccessMode.Unattended, Features = RemoteFeatures.Shell | RemoteFeatures.Screen, ScreenHelperPipeName = pipe } });
        var server = new ScreenHelperServer(broker, new FixedAuthorizer(authorised), options, NullLogger<ScreenHelperServer>.Instance);
        await server.StartAsync(CancellationToken.None);
        try
        {
            await using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            await client.ConnectAsync(3000);
            for (var i = 0; i < 50 && broker.IsAvailable != expectedAvailable; i++)
            {
                await Task.Delay(50);
            }

            Assert.Equal(expectedAvailable, broker.IsAvailable);
        }
        finally
        {
            await server.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task The_bridge_stays_closed_unless_screen_sharing_is_enabled()
    {
        var pipe = "solvix-helper-off-" + Guid.NewGuid().ToString("N");
        var broker = new ScreenHelperBroker(NullLogger<ScreenHelperBroker>.Instance);
        var options = Options.Create(new AgentOptions { RemoteAccess = new RemoteAccessOptions { Mode = RemoteAccessMode.Unattended, Features = RemoteFeatures.Shell, ScreenHelperPipeName = pipe } });
        var server = new ScreenHelperServer(broker, new AllowAnyHelper(), options, NullLogger<ScreenHelperServer>.Instance);
        await server.StartAsync(CancellationToken.None);
        await server.StopAsync(CancellationToken.None);
        await using var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
        await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync(300));
    }

    private sealed class FixedAuthorizer(bool answer) : IHelperAuthorizer
    {
        public bool IsAuthorized(NamedPipeServerStream pipe) => answer;
    }
}

public sealed class KeyMapAndChoiceTests
{
    [Theory]
    [InlineData("Enter", 0x0D, false)]
    [InlineData("ArrowLeft", 0x25, true)]
    [InlineData("Delete", 0x2E, true)]
    [InlineData("F5", 0x74, false)]
    [InlineData("F12", 0x7B, false)]
    [InlineData("c", 'C', false)]
    [InlineData("7", '7', false)]
    public void Browser_key_names_map_to_virtual_keys(string name, int expected, bool extended)
    {
        Assert.True(KeyMap.TryGetVirtualKey(name, out var vk, out var isExtended));
        Assert.Equal((ushort)expected, vk);
        Assert.Equal(extended, isExtended);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("é")]
    [InlineData("F13")]
    [InlineData("Unidentified")]
    public void Unknown_keys_are_ignored(string? name) => Assert.False(KeyMap.TryGetVirtualKey(name, out _, out _));

    [Theory]
    [InlineData(null, RemoteFeatures.Shell, true)]
    [InlineData("screen", RemoteFeatures.Shell | RemoteFeatures.Screen, true)]
    [InlineData("files, Screen", RemoteFeatures.Shell | RemoteFeatures.Screen | RemoteFeatures.Files, true)]
    [InlineData("control", RemoteFeatures.Shell | RemoteFeatures.Screen | RemoteFeatures.Control, true)]
    [InlineData("keylogger", RemoteFeatures.Shell, false)]
    public void Remote_features_are_parsed_strictly(string? value, RemoteFeatures expected, bool valid)
    {
        Assert.Equal(valid, RemoteFeatureChoice.TryParse(value, out var features, out var error));
        Assert.Equal(valid, error is null);
        if (valid)
        {
            Assert.Equal(expected, features);
        }
    }

    [Theory]
    [InlineData(@"C:\Users\Public\Solvix", false)]
    [InlineData("relative/path", true)]
    [InlineData("", true)]
    public void Transfer_folders_must_be_specific_full_paths(string folder, bool refused)
    {
        // Windows-style paths are only "rooted" on Windows; on other hosts only the refusals are meaningful.
        if (!OperatingSystem.IsWindows() && !refused)
        {
            return;
        }

        Assert.Equal(!refused, RemoteFeatureChoice.TryValidateFolder(folder, out _, out _));
    }

    [Fact]
    public void A_whole_drive_or_the_windows_folder_is_never_offered()
    {
        var root = Path.GetPathRoot(Path.GetTempPath())!;
        Assert.False(RemoteFeatureChoice.TryValidateFolder(root, out _, out var error));
        Assert.Contains("cannot be offered", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Control_only_exists_together_with_a_screen()
    {
        var shellAndControl = new RemoteAccessOptions { Mode = RemoteAccessMode.Unattended, Features = RemoteFeatures.Shell | RemoteFeatures.Control };
        Assert.Equal(RemoteFeatures.Shell, RemoteAccessWorker.EffectiveFeatures(shellAndControl));
        Assert.Equal(RemoteFeatures.None, RemoteAccessWorker.EffectiveFeatures(new RemoteAccessOptions { Mode = RemoteAccessMode.Disabled, Features = RemoteFeatures.Shell | RemoteFeatures.Screen }));
        var full = new RemoteAccessOptions { Mode = RemoteAccessMode.ConsentRequired, Features = RemoteFeatures.Shell | RemoteFeatures.Screen | RemoteFeatures.Control };
        Assert.Equal(RemoteFeatures.Shell | RemoteFeatures.Screen | RemoteFeatures.Control, RemoteAccessWorker.EffectiveFeatures(full));
    }
}
