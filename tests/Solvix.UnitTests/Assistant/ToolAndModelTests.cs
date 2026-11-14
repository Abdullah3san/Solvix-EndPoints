namespace Solvix.UnitTests.Assistant;

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Solvix.Modules.Assistant.Analysis;
using Solvix.Modules.Assistant.Audit;
using Solvix.Modules.Assistant.Configuration;
using Solvix.Modules.Assistant.Domain;
using Solvix.Modules.Assistant.Llm;
using Solvix.Modules.Assistant.Security;
using Solvix.Modules.Assistant.Tools;
using Solvix.SharedKernel.Fleet;
using Solvix.SharedKernel.Identity;

public sealed class ToolExecutorTests
{
    private sealed class FakeTool(TenantRole min, Func<ToolArgs, object> run) : IAssistantTool
    {
        public int Calls { get; private set; }

        public string Name => "fake_tool";

        public string Description => "fake";

        public TenantRole MinimumRole => min;

        public JsonNode InputSchema => new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["q"] = new JsonObject { ["type"] = "string" } } };

        public Task<object> ExecuteAsync(ToolContext context, ToolArgs args, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(run(args));
        }
    }

    private static (ToolExecutor Executor, IAssistantAuditLog Audit) Build(IAssistantTool tool, int maxChars = 16000)
    {
        var audit = Substitute.For<IAssistantAuditLog>();
        var options = Options.Create(new AssistantOptions { MaxToolResultChars = maxChars });
        return (new ToolExecutor([tool], audit, options, NullLogger<ToolExecutor>.Instance), audit);
    }

    private static ToolContext Ctx(TenantRole role) => new(new AssistantPrincipal(Guid.NewGuid(), Guid.NewGuid(), "u@x.y", role), null, new TurnState());

    private static ToolUseBlock Call(string name, string json = "{}") => new("id1", name, JsonDocument.Parse(json).RootElement.Clone());

    [Fact]
    public async Task Role_is_enforced_at_execution_even_if_the_model_calls_a_tool_it_was_not_offered()
    {
        var tool = new FakeTool(TenantRole.Admin, _ => new { ok = true });
        var (executor, audit) = Build(tool);
        var result = await executor.ExecuteAsync(Ctx(TenantRole.Member), Call("fake_tool"), default);
        Assert.True(result.IsError);
        Assert.Equal("denied", result.Status);
        Assert.Equal(0, tool.Calls);
        await audit.Received(1).RecordAsync(Arg.Is<AuditRecord>(r => r.Kind == AuditKinds.ToolCall && r.Status == AuditStatus.Denied), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Tools_above_the_users_role_are_not_even_offered()
    {
        var (executor, _) = Build(new FakeTool(TenantRole.Admin, _ => new { }));
        Assert.Empty(executor.DefinitionsFor(new AssistantPrincipal(Guid.NewGuid(), Guid.NewGuid(), null, TenantRole.Member)));
        Assert.Single(executor.DefinitionsFor(new AssistantPrincipal(Guid.NewGuid(), Guid.NewGuid(), null, TenantRole.Owner)));
    }

    [Fact]
    public async Task Unknown_tools_are_denied_and_audited()
    {
        var (executor, audit) = Build(new FakeTool(TenantRole.Member, _ => new { }));
        var result = await executor.ExecuteAsync(Ctx(TenantRole.Owner), Call("run_powershell", """{"script":"rm -rf /"}"""), default);
        Assert.True(result.IsError);
        await audit.Received(1).RecordAsync(Arg.Is<AuditRecord>(r => r.Status == AuditStatus.Denied), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unknown_arguments_are_rejected_before_the_tool_runs()
    {
        var tool = new FakeTool(TenantRole.Member, _ => new { });
        var (executor, _) = Build(tool);
        var result = await executor.ExecuteAsync(Ctx(TenantRole.Member), Call("fake_tool", """{"q":"x","tenantId":"00000000-0000-0000-0000-000000000001"}"""), default);
        Assert.True(result.IsError);
        Assert.Contains("Unknown argument", result.Content, StringComparison.Ordinal);
        Assert.Equal(0, tool.Calls);
    }

    [Fact]
    public async Task Results_are_redacted_before_reaching_the_model()
    {
        var (executor, _) = Build(new FakeTool(TenantRole.Member, _ => new { note = "service account password=Sup3rS3cret! rotated", device = "POS-1" }));
        var result = await executor.ExecuteAsync(Ctx(TenantRole.Member), Call("fake_tool"), default);
        Assert.False(result.IsError);
        Assert.DoesNotContain("Sup3rS3cret", result.Content, StringComparison.Ordinal);
        Assert.Contains("POS-1", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Oversized_results_are_refused_rather_than_truncated_into_invalid_json()
    {
        var (executor, _) = Build(new FakeTool(TenantRole.Member, _ => new { big = new string('x', 5000) }), maxChars: 1000);
        var result = await executor.ExecuteAsync(Ctx(TenantRole.Member), Call("fake_tool"), default);
        Assert.True(result.IsError);
        Assert.Contains("too large", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unexpected_tool_exceptions_do_not_leak_details()
    {
        var (executor, _) = Build(new FakeTool(TenantRole.Member, _ => throw new InvalidOperationException("connection string Host=secret-db")));
        var result = await executor.ExecuteAsync(Ctx(TenantRole.Member), Call("fake_tool"), default);
        Assert.True(result.IsError);
        Assert.DoesNotContain("secret-db", result.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Audit_inputs_are_passed_for_redaction_and_failures_in_audit_do_not_break_the_call()
    {
        var tool = new FakeTool(TenantRole.Member, _ => new { ok = true });
        var (executor, audit) = Build(tool);
        audit.RecordAsync(Arg.Any<AuditRecord>(), Arg.Any<CancellationToken>()).Returns<Task>(_ => throw new InvalidOperationException("db down"));
        var result = await executor.ExecuteAsync(Ctx(TenantRole.Member), Call("fake_tool"), default);
        Assert.False(result.IsError);
    }
}

public sealed class AnthropicChatModelTests
{
    private sealed class Handler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Seen.Add((request, body));
            return respond(request, body);
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static AnthropicChatModel Create(Handler handler, string key = "sk-test-KEY-123456789012345") =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.example.test/") },
            Options.Create(new AssistantOptions { Llm = new LlmOptions { ApiKey = key, Model = "test-model" } }),
            NullLogger<AnthropicChatModel>.Instance);

    private static ChatRequest SampleRequest() => new(
        "system prompt",
        [ChatMessage.User("hi"), new ChatMessage("assistant", [new ToolUseBlock("t1", "search_devices", JsonDocument.Parse("""{"status":"offline"}""").RootElement.Clone())]), new ChatMessage("user", [new ToolResultBlock("t1", "{\"total\":2}", false)])],
        [new ToolDefinition("search_devices", "desc", new JsonObject { ["type"] = "object" })],
        512);

    [Fact]
    public async Task Request_has_the_documented_shape_and_headers()
    {
        var handler = new Handler((_, _) => Json(HttpStatusCode.OK, """{"model":"test-model","stop_reason":"end_turn","content":[{"type":"text","text":"done"}],"usage":{"input_tokens":10,"output_tokens":5}}"""));
        var response = await Create(handler).CompleteAsync(SampleRequest(), default);

        Assert.Equal("done", response.Text);
        Assert.Equal(new ChatUsage(10, 5), response.Usage);
        var (request, body) = Assert.Single(handler.Seen);
        Assert.Equal("v1/messages", request.RequestUri!.AbsolutePath.TrimStart('/'));
        Assert.Equal("sk-test-KEY-123456789012345", request.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", request.Headers.GetValues("anthropic-version").Single());

        var json = JsonDocument.Parse(body).RootElement;
        Assert.Equal("test-model", json.GetProperty("model").GetString());
        Assert.Equal(512, json.GetProperty("max_tokens").GetInt32());
        Assert.Equal("system prompt", json.GetProperty("system").GetString());
        var messages = json.GetProperty("messages");
        Assert.Equal("tool_use", messages[1].GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal("offline", messages[1].GetProperty("content")[0].GetProperty("input").GetProperty("status").GetString());
        Assert.Equal("tool_result", messages[2].GetProperty("content")[0].GetProperty("type").GetString());
        Assert.Equal("t1", messages[2].GetProperty("content")[0].GetProperty("tool_use_id").GetString());
        Assert.Equal("search_devices", json.GetProperty("tools")[0].GetProperty("name").GetString());
        Assert.DoesNotContain("sk-test-KEY", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tool_use_blocks_are_parsed()
    {
        var handler = new Handler((_, _) => Json(HttpStatusCode.OK, """{"stop_reason":"tool_use","content":[{"type":"text","text":"checking"},{"type":"tool_use","id":"toolu_1","name":"get_fleet_summary","input":{"branch":"Beirut"}}],"usage":{"input_tokens":1,"output_tokens":1}}"""));
        var response = await Create(handler).CompleteAsync(SampleRequest(), default);
        var use = Assert.Single(response.ToolUses);
        Assert.Equal("get_fleet_summary", use.Name);
        Assert.Equal("Beirut", use.Input.GetProperty("branch").GetString());
    }

    [Fact]
    public async Task Transient_errors_are_retried_and_permanent_errors_do_not_leak_the_key()
    {
        var attempts = 0;
        var retrying = new Handler((_, _) => ++attempts < 3
            ? Json((HttpStatusCode)529, "{}")
            : Json(HttpStatusCode.OK, """{"content":[{"type":"text","text":"ok"}],"usage":{}}"""));
        Assert.Equal("ok", (await Create(retrying).CompleteAsync(SampleRequest(), default)).Text);
        Assert.Equal(3, attempts);

        var denied = new Handler((_, _) => Json(HttpStatusCode.Unauthorized, """{"error":{"message":"invalid x-api-key sk-test-KEY-123456789012345"}}"""));
        var ex = await Assert.ThrowsAsync<ChatModelException>(() => Create(denied).CompleteAsync(SampleRequest(), default));
        Assert.DoesNotContain("sk-test-KEY", ex.Message, StringComparison.Ordinal);
        Assert.Single(denied.Seen);
    }

    [Fact]
    public async Task Missing_key_is_reported_without_a_network_call()
    {
        var handler = new Handler((_, _) => Json(HttpStatusCode.OK, "{}"));
        var model = Create(handler, key: "");
        Assert.False(model.IsConfigured);
        await Assert.ThrowsAsync<ChatModelException>(() => model.CompleteAsync(SampleRequest(), default));
        Assert.Empty(handler.Seen);
    }
}

public sealed class AnalyzerTests
{
    [Fact]
    public void Sustained_high_cpu_names_the_busy_process()
    {
        var report = new PerformanceReport(Guid.NewGuid(), "POS-018", TimeSpan.FromHours(24), 288,
            new MetricStats(96, 99, 100), new MetricStats(70, 80, 85), null,
            [new ProcessUsage("SearchIndexer.exe", 62, 1), new ProcessUsage("posapp.exe", 9, 1)], []);
        var finding = Assert.Single(PerformanceAnalyzer.Analyze(report), f => f.Code == "cpu_sustained_high");
        Assert.Equal(FindingSeverity.High, finding.Severity);
        Assert.Contains("SearchIndexer.exe", finding.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public void Healthy_device_has_no_findings()
    {
        var report = new PerformanceReport(Guid.NewGuid(), "OK", TimeSpan.FromHours(24), 288, new MetricStats(20, 40, 55), new MetricStats(40, 50, 60), null, [], []);
        Assert.Empty(PerformanceAnalyzer.Analyze(report));
    }

    [Fact]
    public void Failing_disk_is_critical_and_low_space_is_flagged()
    {
        var hw = new HardwareDiagnostics(Guid.NewGuid(), "X", [new DiskHealth("Disk 0", "Warning", 91, 120, 40)], new BatteryHealth(35, 900), true, 95, 40, []);
        var detail = new DeviceDetail(
            new DeviceSummary(hw.DeviceId, "X", DeviceConnectivity.Online, null, null, null, null, null, null, null, null, null, null, null, null, null),
            null, null, null, [new DiskInfo("C:", "NTFS", 100L << 30, 3L << 30, true)], null, null, null, null, null, false);
        var codes = HardwareAnalyzer.Analyze(hw, detail).Select(f => f.Code).ToList();
        Assert.Contains("disk_smart_failing", codes);
        Assert.Contains("disk_wear", codes);
        Assert.Contains("battery_wear", codes);
        Assert.Contains("memory_errors", codes);
        Assert.Contains("cpu_hot", codes);
        Assert.Contains("long_uptime", codes);
        Assert.Contains("low_disk_space", codes);
        Assert.Equal(FindingSeverity.Critical, HardwareAnalyzer.Analyze(hw, detail).Max(f => f.Severity));
    }

    [Fact]
    public void Security_advice_ranks_by_severity()
    {
        var posture = new SecurityPosture(10,
        [
            new SecurityFinding(SecurityFindingKind.PendingReboot, FindingSeverity.Low, Guid.NewGuid(), "A", "x"),
            new SecurityFinding(SecurityFindingKind.FirewallDisabled, FindingSeverity.High, Guid.NewGuid(), "B", "x"),
            new SecurityFinding(SecurityFindingKind.FirewallDisabled, FindingSeverity.High, Guid.NewGuid(), "C", "x"),
        ]);
        var advice = SecurityAdvisor.Advise(posture);
        Assert.StartsWith("Re-enable the host firewall", advice.Recommendations[0], StringComparison.Ordinal);
        Assert.Equal(2, advice.CountsByKind["FirewallDisabled"]);
    }
}
