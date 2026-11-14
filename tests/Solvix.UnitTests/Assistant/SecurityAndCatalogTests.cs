namespace Solvix.UnitTests.Assistant;

using System.Text.Json;
using Solvix.Modules.Assistant.Domain;
using Solvix.Modules.Assistant.Remediation;
using Solvix.Modules.Assistant.Security;
using Solvix.SharedKernel.Exceptions;
using Solvix.SharedKernel.Fleet;

public sealed class SecretRedactorTests
{
    [Theory]
    [InlineData("my password is Hunter2!x please help", "Hunter2!x")]
    [InlineData("password: s3cr3t-value", "s3cr3t-value")]
    [InlineData("Server=db;User Id=sa;Password=P@ssw0rd123;Encrypt=true", "P@ssw0rd123")]
    [InlineData("api_key = \"abcd1234efgh5678\"", "abcd1234efgh5678")]
    [InlineData("Authorization: Bearer abcdefghijklmnopqrstuvwxyz0123456789", "abcdefghijklmnopqrstuvwxyz0123456789")]
    [InlineData("key AKIAIOSFODNN7EXAMPLE leaked", "AKIAIOSFODNN7EXAMPLE")]
    [InlineData("anthropic sk-ant-api03-abcdefghijklmnop", "sk-ant-api03-abcdefghijklmnop")]
    [InlineData("clone https://user:topsecretpw@git.example.com/repo", "topsecretpw")]
    [InlineData("token=ghp_abcdefghijklmnopqrstuvwxyz0123456789", "ghp_abcdefghijklmnopqrstuvwxyz0123456789")]
    public void Secrets_are_removed(string input, string secret)
    {
        var result = SecretRedactor.Redact(input);
        Assert.DoesNotContain(secret, result.Text, StringComparison.Ordinal);
        Assert.True(result.Count >= 1);
    }

    [Fact]
    public void Private_keys_and_jwts_are_removed()
    {
        var text = "-----BEGIN RSA PRIVATE KEY-----\nMIIEabc\n-----END RSA PRIVATE KEY-----\nand eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dBjftJeZ4CVPmB92K27uhbUJU1p1r";
        var result = SecretRedactor.Redact(text);
        Assert.DoesNotContain("MIIEabc", result.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJhbGci", result.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Ordinary_text_is_untouched()
    {
        const string text = "POS-018 has 97% CPU; restart the Spooler service after the token bucket resets. Free space: 4 GB.";
        Assert.Equal(text, SecretRedactor.Redact(text).Text);
    }

    [Fact]
    public void Pathological_input_does_not_hang()
    {
        var evil = new string('a', 200_000) + "password=" + new string('b', 200_000);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        _ = SecretRedactor.Redact(evil);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
    }
}

public sealed class ActionCatalogTests
{
    private static IReadOnlyDictionary<string, JsonElement> Params(string json) =>
        JsonDocument.Parse(json).RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

    [Theory]
    [InlineData("run_powershell")]
    [InlineData("execute_command")]
    [InlineData("run_script")]
    [InlineData("cmd")]
    [InlineData("")]
    [InlineData("restart_service; whoami")]
    public void Unknown_actions_are_not_in_the_catalog(string name) => Assert.Null(ActionCatalog.Find(name));

    [Fact]
    public void Catalog_has_no_script_execution_capability()
    {
        var forbidden = new[] { "script", "powershell", "shell", "command", "exec", "cmd" };
        Assert.DoesNotContain(ActionCatalog.All, a => forbidden.Any(f => a.Name.Contains(f, StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain(Enum.GetNames<DeviceCommandType>(), n => forbidden.Any(f => n.Contains(f, StringComparison.OrdinalIgnoreCase)));
        Assert.DoesNotContain(ActionCatalog.All.SelectMany(a => a.Parameters), p => forbidden.Any(f => p.Name.Contains(f, StringComparison.OrdinalIgnoreCase)));
    }

    [Theory]
    [InlineData("Spooler; Remove-Item -Recurse C:\\")]
    [InlineData("$(whoami)")]
    [InlineData("a b")]
    [InlineData("`calc`")]
    [InlineData("svc && del *")]
    [InlineData("")]
    public void Injection_in_identifier_parameters_is_rejected(string value)
    {
        var action = ActionCatalog.Find("restart_service")!;
        var json = JsonSerializer.Serialize(new Dictionary<string, string> { ["serviceName"] = value });
        Assert.Throws<ValidationException>(() => ActionCatalog.NormalizeParameters(action, Params(json)));
    }

    [Fact]
    public void Valid_parameters_are_normalised_and_defaults_applied()
    {
        var restartService = ActionCatalog.NormalizeParameters(ActionCatalog.Find("restart_service")!, Params("""{"serviceName":"Spooler"}"""));
        Assert.Equal("Spooler", restartService["serviceName"]);

        var restart = ActionCatalog.NormalizeParameters(ActionCatalog.Find("restart_device")!, Params("{}"));
        Assert.Equal("5", restart["delayMinutes"]);
    }

    [Fact]
    public void Unknown_missing_and_out_of_range_parameters_are_rejected()
    {
        var service = ActionCatalog.Find("restart_service")!;
        Assert.Throws<ValidationException>(() => ActionCatalog.NormalizeParameters(service, Params("""{"serviceName":"Spooler","extra":"x"}""")));
        Assert.Throws<ValidationException>(() => ActionCatalog.NormalizeParameters(service, Params("{}")));
        Assert.Throws<ValidationException>(() => ActionCatalog.NormalizeParameters(service, Params("""{"serviceName":{"a":1}}""")));
        var restart = ActionCatalog.Find("restart_device")!;
        Assert.Throws<ValidationException>(() => ActionCatalog.NormalizeParameters(restart, Params("""{"delayMinutes":99999}""")));
        Assert.Throws<ValidationException>(() => ActionCatalog.NormalizeParameters(restart, Params("""{"delayMinutes":-1}""")));
    }

    [Fact]
    public void Risk_levels_and_required_roles_reflect_sensitivity()
    {
        Assert.Equal(RiskLevel.High, ActionCatalog.Find("deploy_software")!.BaseRisk);
        Assert.Equal(RiskLevel.High, ActionCatalog.Find("uninstall_software")!.BaseRisk);
        Assert.Equal(RiskLevel.Low, ActionCatalog.Find("collect_diagnostics")!.BaseRisk);
        Assert.All(ActionCatalog.All.Where(a => a.ReadOnly), a => Assert.Equal(RiskLevel.Low, a.BaseRisk));
    }
}

public sealed class ProposalStateTests
{
    private static RemediationProposal New(DateTimeOffset expires) =>
        new(Guid.NewGuid(), null, Guid.NewGuid(), "a@b.c", "restart_service", "t", "r", RiskLevel.Medium, "{}", "[]", 1, "fp", expires);

    private static readonly DateTimeOffset Now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Happy_path_pending_executing_succeeded()
    {
        var p = New(Now.AddHours(1));
        Assert.Equal(ProposalStatus.PendingApproval, p.Status);
        p.BeginExecution(Guid.NewGuid(), "x@y.z", null, Now);
        Assert.Equal(ProposalStatus.Executing, p.Status);
        p.Complete(ProposalStatus.Succeeded, "[]", Now);
        Assert.True(p.IsTerminal);
    }

    [Fact]
    public void Cannot_approve_twice_or_after_reject()
    {
        var p = New(Now.AddHours(1));
        p.Reject(Guid.NewGuid(), null, "no", Now);
        Assert.Throws<ConflictException>(() => p.BeginExecution(Guid.NewGuid(), null, null, Now));
        Assert.Throws<ConflictException>(() => p.Reject(Guid.NewGuid(), null, null, Now));
    }

    [Fact]
    public void Expired_proposals_cannot_be_approved()
    {
        var p = New(Now.AddMinutes(-1));
        Assert.True(p.IsExpiredAt(Now));
        Assert.Throws<ConflictException>(() => p.BeginExecution(Guid.NewGuid(), null, null, Now));
    }

    [Fact]
    public void Cannot_complete_without_executing()
    {
        var p = New(Now.AddHours(1));
        Assert.Throws<ConflictException>(() => p.Complete(ProposalStatus.Succeeded, "[]", Now));
    }
}
