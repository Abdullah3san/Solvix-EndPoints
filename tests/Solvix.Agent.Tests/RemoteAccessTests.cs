namespace Solvix.Agent.Tests;

using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Solvix.Agent.Contracts;
using Solvix.Agent.Core.Communication;
using Solvix.Agent.Core.Configuration;
using Solvix.Agent.Core.Health;
using Solvix.Agent.Core.Remote;

/// <summary>Local policy and relay behaviour of remote command sessions.</summary>
public sealed class RemoteAccessTests : IDisposable
{
    private readonly FakeAgentApi _api = new();
    private readonly FakePrompter _prompter = new();
    private readonly FakeShells _shells = new();
    private readonly FakeDesktop _desktop = new();
    private readonly AgentHealthMonitor _health = new(TimeProvider.System, new ConnectionMonitor(TimeProvider.System));

    public void Dispose() => _api.Dispose();

    private RemoteAccessWorker Worker(RemoteAccessMode mode, RemoteFeatures features = RemoteFeatures.Shell)
    {
        var options = Options.Create(new AgentOptions
        {
            DataDirectory = Path.Combine(Path.GetTempPath(), "solvix-remote-test-" + Guid.NewGuid().ToString("N")),
            RemoteAccess = new RemoteAccessOptions { Mode = mode, Features = features },
        });
        return new RemoteAccessWorker(
            _api, options, _prompter, _shells, _desktop,
            new ScreenSessionRunner(_api, _desktop, NullLogger<ScreenSessionRunner>.Instance) { FrameInterval = TimeSpan.FromMilliseconds(10), KeepAlive = TimeSpan.FromMilliseconds(50) },
            new FileSessionRunner(_api, options, NullLogger<FileSessionRunner>.Instance),
            _health, NullLogger<RemoteAccessWorker>.Instance)
        { ErrorDelay = TimeSpan.FromMilliseconds(10) };
    }

    private static AgentRemoteRequest Request() => new()
    {
        SessionId = Guid.NewGuid(),
        RequestedBy = "Alex (IT)",
        Reason = "Fix printer",
        RequestedAtUtc = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Disabled_by_default_never_polls_and_is_reported_as_disabled()
    {
        var worker = Worker(RemoteAccessMode.Disabled);
        await worker.StartAsync(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);
        Assert.DoesNotContain("remote-pending", _api.Calls);
        Assert.Equal(RemoteAccessMode.Disabled, _health.RemoteAccess);
        Assert.Equal(0, _shells.Started);
    }

    [Fact]
    public async Task Enabled_mode_is_reported_to_the_console_through_health()
    {
        var worker = Worker(RemoteAccessMode.ConsentRequired);
        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(100);
        await worker.StopAsync(CancellationToken.None);
        Assert.Equal(RemoteAccessMode.ConsentRequired, _health.Build(null, null, null, 0).RemoteAccess);
        Assert.Contains("remote-pending", _api.Calls);
    }

    [Fact]
    public async Task A_declined_request_never_starts_a_shell()
    {
        _prompter.Answer = false;
        var request = Request();
        await Worker(RemoteAccessMode.ConsentRequired).HandleAsync(request, RemoteAccessMode.ConsentRequired, CancellationToken.None);

        Assert.Equal(request.RequestedBy, _prompter.LastRequestedBy);
        Assert.Equal(0, _shells.Started);
        Assert.Equal(request.SessionId, Assert.Single(_api.RemoteDenied).Id);
        Assert.Empty(_api.RemoteAccepted);
    }

    [Fact]
    public async Task An_approved_session_relays_input_and_output_then_ends_when_the_technician_ends_it()
    {
        _prompter.Answer = true;
        _api.RemoteInputEndsWhenEmpty = false;
        _api.RemoteInput.Enqueue(new RemoteInputResponse { Chunks = [new RemoteChunk { Sequence = 1, Text = "echo hi\n" }] });
        var request = Request();

        var handling = Worker(RemoteAccessMode.ConsentRequired).HandleAsync(request, RemoteAccessMode.ConsentRequired, CancellationToken.None);
        await _shells.Current.WaitForInputAsync("echo hi\n");
        _shells.Current.Emit("hi\n");
        for (var i = 0; i < 200 && !string.Concat(_api.RemoteOutput).Contains("hi\n", StringComparison.Ordinal); i++)
        {
            await Task.Delay(20);
        }

        _api.RemoteInput.Enqueue(new RemoteInputResponse { Ended = true }); // the technician ends the session
        await handling.WaitAsync(TimeSpan.FromSeconds(10));

        var accepted = Assert.Single(_api.RemoteAccepted);
        Assert.Equal((request.SessionId, RemoteConsent.User), accepted);
        Assert.Contains("hi\n", string.Concat(_api.RemoteOutput));
        Assert.True(_shells.Current.Killed);
        Assert.Equal(request.SessionId, Assert.Single(_api.RemoteEnded));
    }

    [Fact]
    public async Task Unattended_mode_accepts_without_asking_and_records_that_nobody_consented()
    {
        var request = Request();
        await Worker(RemoteAccessMode.Unattended).HandleAsync(request, RemoteAccessMode.Unattended, CancellationToken.None);
        Assert.Equal(0, _prompter.Asked);
        Assert.Equal((request.SessionId, RemoteConsent.Unattended), Assert.Single(_api.RemoteAccepted));
    }

    [Fact]
    public async Task When_the_shell_exits_the_session_is_ended()
    {
        _prompter.Answer = true;
        _api.RemoteInputEndsWhenEmpty = false; // keep polling; the shell exits first
        for (var i = 0; i < 200; i++)
        {
            _api.RemoteInput.Enqueue(new RemoteInputResponse());
        }

        var request = Request();
        var handling = Worker(RemoteAccessMode.ConsentRequired).HandleAsync(request, RemoteAccessMode.ConsentRequired, CancellationToken.None);
        await Task.Delay(100);
        _shells.Current.Exit();
        await handling.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(request.SessionId, Assert.Single(_api.RemoteEnded));
    }

    [Fact]
    public async Task A_shell_that_cannot_start_is_reported_as_a_refusal()
    {
        _prompter.Answer = true;
        _shells.FailToStart = true;
        var request = Request();
        await Worker(RemoteAccessMode.ConsentRequired).HandleAsync(request, RemoteAccessMode.ConsentRequired, CancellationToken.None);
        Assert.Equal(request.SessionId, Assert.Single(_api.RemoteDenied).Id);
        Assert.Empty(_api.RemoteAccepted);
    }

    private async Task<AgentRemoteRequest> HandleKindAsync(RemoteSessionKind kind, bool control, RemoteFeatures features, bool desktopAvailable = true, RemoteAccessMode mode = RemoteAccessMode.ConsentRequired)
    {
        _desktop.IsAvailable = desktopAvailable;
        var request = Request() with { Kind = kind, ControlRequested = control };
        _api.RemoteInputEndsWhenEmpty = true;
        await Worker(mode, features).HandleAsync(request, mode, CancellationToken.None);
        return request;
    }

    [Theory]
    [InlineData(RemoteSessionKind.Screen)]
    [InlineData(RemoteSessionKind.Files)]
    public async Task A_kind_the_computer_does_not_allow_is_refused_without_asking_the_person(RemoteSessionKind kind)
    {
        _prompter.Answer = true;
        var request = await HandleKindAsync(kind, false, RemoteFeatures.Shell);
        Assert.Equal(0, _prompter.Asked);
        Assert.Equal(request.SessionId, Assert.Single(_api.RemoteDenied).Id);
        Assert.Contains("not enabled", _api.RemoteDenied[0].Reason, StringComparison.Ordinal);
        Assert.Empty(_api.RemoteAccepted);
    }

    [Fact]
    public async Task Screen_sharing_is_refused_when_nobodys_desktop_is_available()
    {
        _prompter.Answer = true;
        await HandleKindAsync(RemoteSessionKind.Screen, false, RemoteFeatures.Shell | RemoteFeatures.Screen, desktopAvailable: false);
        Assert.Equal(0, _prompter.Asked); // never ask for something that cannot be done
        Assert.Contains("desktop", _api.RemoteDenied.Single().Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(RemoteFeatures.Shell | RemoteFeatures.Screen | RemoteFeatures.Control, true, true)]
    [InlineData(RemoteFeatures.Shell | RemoteFeatures.Screen, true, false)]
    [InlineData(RemoteFeatures.Shell | RemoteFeatures.Screen | RemoteFeatures.Control, false, false)]
    public async Task Control_is_granted_only_when_asked_for_allowed_locally_and_approved(RemoteFeatures features, bool asked, bool expectedGranted)
    {
        _prompter.Answer = true;
        var request = await HandleKindAsync(RemoteSessionKind.Screen, asked, features);
        Assert.Equal(RemoteSessionKind.Screen, _prompter.LastRequest!.Kind);
        Assert.Equal(expectedGranted, _prompter.LastRequest.ControlRequested); // the person is told exactly what they approve
        var accepted = Assert.Single(_api.RemoteAccepted);
        Assert.Equal(request.SessionId, accepted.Id);
        Assert.Equal(RemoteConsent.User, accepted.Consent);
        Assert.Equal(expectedGranted, _api.RemoteControlGranted.Single());
    }

    [Fact]
    public async Task A_declined_screen_request_shows_nothing()
    {
        _prompter.Answer = false;
        await HandleKindAsync(RemoteSessionKind.Screen, true, RemoteFeatures.Shell | RemoteFeatures.Screen | RemoteFeatures.Control);
        Assert.Empty(_api.ScreenFrames);
        Assert.Single(_api.RemoteDenied);
    }

    [Fact]
    public async Task An_approved_screen_session_streams_frames_and_ends()
    {
        _prompter.Answer = true;
        _api.RemoteInputEndsWhenEmpty = false;
        var request = Request() with { Kind = RemoteSessionKind.Screen };
        var handling = Worker(RemoteAccessMode.ConsentRequired, RemoteFeatures.Shell | RemoteFeatures.Screen).HandleAsync(request, RemoteAccessMode.ConsentRequired, CancellationToken.None);
        await Task.Delay(300);
        _api.RemoteInput.Enqueue(new RemoteInputResponse { Ended = true });
        await handling.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEmpty(_api.ScreenFrames);
        Assert.Equal(request.SessionId, Assert.Single(_api.RemoteEnded));
    }

    [Fact]
    public async Task An_approved_file_session_serves_operations_then_ends_with_the_session()
    {
        _prompter.Answer = true;
        var request = await HandleKindAsync(RemoteSessionKind.Files, false, RemoteFeatures.Shell | RemoteFeatures.Files);
        Assert.Equal(request.SessionId, Assert.Single(_api.RemoteEnded));
        Assert.Equal(0, _shells.Started); // no shell for a file session
    }

    [Fact]
    public async Task The_default_prompter_says_no()
    {
        Assert.False(await new DenyingConsentPrompter().AskAsync(new RemoteConsentRequest("x", null, RemoteSessionKind.Shell, false), TimeSpan.FromSeconds(1), CancellationToken.None));
    }

    private sealed class FakeDesktop : IRemoteDesktop
    {
        public bool IsAvailable { get; set; } = true;

        public Task<ScreenFrame?> CaptureAsync(CancellationToken cancellationToken) => Task.FromResult<ScreenFrame?>(new ScreenFrame([0xFF, 0xD8, 0xFF, 1], 10, 10));

        public Task InjectAsync(ScreenInputEvent input, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakePrompter : IConsentPrompter
    {
        public bool Answer { get; set; }

        public int Asked { get; private set; }

        public string? LastRequestedBy { get; private set; }

        public RemoteConsentRequest? LastRequest { get; private set; }

        public Task<bool> AskAsync(RemoteConsentRequest request, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Asked++;
            LastRequestedBy = request.RequestedBy;
            LastRequest = request;
            return Task.FromResult(Answer);
        }
    }

    private sealed class FakeShells : IRemoteShellFactory
    {
        public int Started { get; private set; }

        public bool FailToStart { get; set; }

        public FakeShell Current { get; private set; } = null!;

        public IRemoteShell Start()
        {
            if (FailToStart)
            {
                throw new InvalidOperationException("no shell");
            }

            Started++;
            return Current = new FakeShell();
        }
    }

    private sealed class FakeShell : IRemoteShell
    {
        private readonly Channel<string> _output = Channel.CreateUnbounded<string>();
        private readonly List<string> _input = [];

        public ChannelReader<string> Output => _output.Reader;

        public bool Killed { get; private set; }

        public Task WriteAsync(string text, CancellationToken cancellationToken)
        {
            lock (_input)
            {
                _input.Add(text);
            }

            return Task.CompletedTask;
        }

        public async Task WaitForInputAsync(string expected)
        {
            for (var i = 0; i < 200; i++)
            {
                lock (_input)
                {
                    if (_input.Contains(expected))
                    {
                        return;
                    }
                }

                await Task.Delay(20);
            }

            throw new TimeoutException("The shell never received the input.");
        }

        public void Emit(string text) => _output.Writer.TryWrite(text);

        public void Exit() => _output.Writer.TryComplete();

        public void Kill() => Killed = true;

        public ValueTask DisposeAsync()
        {
            Killed = true;
            return ValueTask.CompletedTask;
        }
    }
}
