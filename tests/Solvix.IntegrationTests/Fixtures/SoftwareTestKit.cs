namespace Solvix.IntegrationTests.Fixtures;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Solvix.Agent.Contracts;
using Solvix.Agent.Contracts.RemoteManagement;
using Solvix.Agent.Contracts.Software;
using Solvix.Modules.RemoteManagement.CommandBroker;
using Solvix.Modules.Software.Deployments;
using Solvix.Modules.Software.Packages;
using Solvix.Modules.Software.Policies;
using Solvix.SharedKernel.Authorization;
using Solvix.UnitTests.Software;

/// <summary>A tenant with enrolled devices, an approver pair and helpers for the software workflow.</summary>
public sealed record SoftwareSetup(
    TenantSession Tenant,
    HttpClient Owner,
    IReadOnlyList<EnrolledDevice> Devices,
    IReadOnlyList<HttpClient> Agents,
    MemberSession Approver,
    HttpClient ApproverClient,
    MemberSession SecondApprover,
    HttpClient SecondApproverClient) : IDisposable
{
    public Guid DeviceId => Devices[0].DeviceId;

    public HttpClient Agent => Agents[0];

    public void Dispose()
    {
        Owner.Dispose();
        ApproverClient.Dispose();
        SecondApproverClient.Dispose();
        foreach (var agent in Agents)
        {
            agent.Dispose();
        }
    }
}

public static class SoftwareTestKit
{
    public const string ProductCode = "{0A1B2C3D-1111-2222-3333-444455556666}";

    public static async Task<SoftwareSetup> SetUpSoftwareAsync(this ApiFixture fixture, int devices = 1)
    {
        var tenant = await fixture.RegisterTenantAsync();
        var enrolled = new List<EnrolledDevice>();
        for (var i = 0; i < devices; i++)
        {
            enrolled.Add(await fixture.EnrollAsync(await fixture.CreateTokenAsync(tenant), DeviceTestKit.Identity()));
        }

        var approver = await fixture.AddMemberWithPermissionsAsync(tenant, Permissions.Software.Approve);
        var second = await fixture.AddMemberWithPermissionsAsync(tenant, Permissions.Software.Approve);
        return new SoftwareSetup(
            tenant,
            fixture.CreateClient(tenant.Token),
            enrolled,
            enrolled.Select(d => fixture.CreateDeviceClient(d.Certificate)).ToList(),
            approver,
            fixture.CreateClient(approver.Token),
            second,
            fixture.CreateClient(second.Token));
    }

    public static Task<HttpResponseMessage> PostPackageAsync(this HttpClient client, string fileName, byte[] content)
    {
        var body = new ByteArrayContent(content);
        body.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        return client.PostAsync($"/api/v1/software/packages?fileName={Uri.EscapeDataString(fileName)}", body);
    }

    public static async Task<PackageDto> UploadAsync(this HttpClient client, string fileName, byte[] content) =>
        await HttpAssert.OkAsync<PackageDto>(await client.PostPackageAsync(fileName, content), HttpStatusCode.Created);

    /// <summary>A unique, unsigned MSI (compound file with the Windows Installer class id).</summary>
    public static byte[] Msi() => TestPackages.CompoundFile(null, ("\u0005SummaryInformation", Guid.NewGuid().ToByteArray()));

    /// <summary>A unique EXE signed by <paramref name="signer"/>.</summary>
    public static byte[] SignedExe(X509Certificate2 signer) => TestPackages.Sign(TestPackages.Executable(bodyBytes: 4096 + Random.Shared.Next(1, 4096)), signer);

    public static ConfigurePackageRequest MsiConfiguration(string? version = "5.2.0") => new()
    {
        Name = "Contoso Notes",
        Publisher = "Contoso Ltd",
        Version = version,
        SilentPreset = SilentPreset.Msi,
        InstallArguments = ["ALLUSERS=1"],
        Detection = new DetectionRule { Kind = DetectionKind.MsiProductCode, ProductCode = ProductCode, Version = version, VersionOperator = version is null ? VersionOperator.Any : VersionOperator.AtLeast },
    };

    public static ConfigurePackageRequest ExeConfiguration(string name = "Fabrikam Viewer") => new()
    {
        Name = name,
        Publisher = "Fabrikam",
        Version = "9.1",
        SilentPreset = SilentPreset.WixBurn,
        TimeoutMinutes = 30,
        Detection = new DetectionRule { Kind = DetectionKind.UninstallEntry, DisplayName = name, Publisher = "Fabrikam", Version = "9.1", VersionOperator = VersionOperator.AtLeast },
    };

    public static async Task<PackageDto> ConfigureAsync(this HttpClient client, Guid packageId, ConfigurePackageRequest request) =>
        await HttpAssert.OkAsync<PackageDto>(await client.PutAsJsonAsync($"/api/v1/software/packages/{packageId}", request, ApiFixture.Json));

    /// <summary>An uploaded and configured, unsigned MSI.</summary>
    public static async Task<PackageDto> ReadyMsiAsync(this HttpClient client) =>
        await client.ConfigureAsync((await client.UploadAsync("contoso-notes.msi", Msi())).Id, MsiConfiguration());

    public static CreateDeploymentRequest Deployment(Guid packageId, params Guid[] devices) => new()
    {
        Name = "Roll out " + packageId.ToString("N")[..6],
        PackageId = packageId,
        DeviceIds = devices,
    };

    public static Task<HttpResponseMessage> PostDeploymentAsync(this HttpClient client, CreateDeploymentRequest request) =>
        client.PostAsJsonAsync("/api/v1/software/deployments", request, ApiFixture.Json);

    public static async Task<DeploymentDetailDto> CreateDeploymentAsync(this HttpClient client, CreateDeploymentRequest request) =>
        await HttpAssert.OkAsync<DeploymentDetailDto>(await client.PostDeploymentAsync(request), HttpStatusCode.Created);

    public static Task<HttpResponseMessage> DecideAsync(this HttpClient client, Guid deploymentId, bool approve, string? comment = null) =>
        client.PostAsJsonAsync($"/api/v1/software/deployments/{deploymentId}/{(approve ? "approve" : "reject")}", new DeploymentDecisionRequest(comment), ApiFixture.Json);

    public static async Task<DeploymentDetailDto> GetDeploymentAsync(this HttpClient client, Guid deploymentId) =>
        await HttpAssert.OkAsync<DeploymentDetailDto>(await client.GetAsync($"/api/v1/software/deployments/{deploymentId}"));

    /// <summary>One pass of the deployment engine (the shared fixture does not run it in the background).</summary>
    public static async Task<DeploymentPassResult> RunDeploymentsAsync(this ApiFixture fixture, DateTimeOffset? now = null)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<DeploymentProcessor>().RunAsync(now ?? fixture.Factory.Clock.UtcNow, CancellationToken.None);
    }

    /// <summary>Fetches and verifies the next software command for the device; returns it with its parsed operation.</summary>
    public static async Task<(CommandEnvelope Envelope, SoftwareOperation Operation)> NextSoftwareCommandAsync(this HttpClient agent)
    {
        var signed = Assert.Single(await agent.FetchAsync());
        var envelope = await agent.VerifyAsync(signed);
        return (envelope, SoftwareOperation.Parse(envelope.CommandType, envelope.Parameters));
    }

    public static Task<HttpResponseMessage> DownloadAsync(this HttpClient agent, Guid executionId, long? from = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/" + AgentRoutes.SoftwarePackage(executionId));
        if (from is { } offset)
        {
            request.Headers.Range = new RangeHeaderValue(offset, null);
        }

        return agent.SendAsync(request);
    }

    public static Task<HttpResponseMessage> SoftwareResultAsync(this HttpClient agent, Guid executionId, CommandOutcome outcome, SoftwareOperationResult result, string? errorCode = null, int? exitCode = 0) =>
        agent.PostAsJsonAsync(
            "/" + AgentRoutes.CommandResult(executionId),
            new CommandResultReport
            {
                Outcome = outcome,
                ExitCode = exitCode,
                Output = JsonSerializer.Serialize(result, AgentJsonContext.Default.SoftwareOperationResult),
                ErrorCode = errorCode,
                ErrorMessage = errorCode is null ? null : "Simulated: " + errorCode,
                StartedAtUtc = DateTimeOffset.UtcNow,
                CompletedAtUtc = DateTimeOffset.UtcNow,
            },
            ApiFixture.Json);

    /// <summary>The device accepts the command and reports a successful installation.</summary>
    public static async Task InstallSucceedsAsync(this HttpClient agent, Guid executionId, string version = "5.2.0")
    {
        Assert.Equal(HttpStatusCode.NoContent, (await agent.AckAsync(executionId)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await agent.SoftwareResultAsync(executionId, CommandOutcome.Succeeded,
            new SoftwareOperationResult { Outcome = SoftwareOutcome.Installed, InstallerExitCode = 0, DetectionSatisfied = true, DetectedVersion = version, PackageVerified = true })).StatusCode);
    }

    public static async Task SetPolicyAsync(this HttpClient client, UpdateDeploymentPolicyRequest request) =>
        await HttpAssert.OkAsync<DeploymentPolicyDto>(await client.PutAsJsonAsync("/api/v1/software/policy", request, ApiFixture.Json));

    public static UpdateDeploymentPolicyRequest DefaultPolicy(params string[] trusted) =>
        new(true, false, trusted, true, 50, true, true, 72);

    public static async Task<CommandSweepResult> SweepRemoteAsync(this ApiFixture fixture, DateTimeOffset now) => await fixture.SweepCommandsAsync(now);
}

public static class TenantSessionKit
{
    /// <summary>Counts rows with <paramref name="id"/> in <paramref name="table"/> as the runtime role inside <paramref name="tenantId"/>'s RLS session.</summary>
    public static async Task<long> CountInTenantAsync(this ApiFixture fixture, Guid tenantId, string table, Guid id)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        if (!table.All(c => char.IsAsciiLetterLower(c) || c == '_'))
        {
            throw new ArgumentException("Invalid table name.", nameof(table));
        }

        await using var connection = new Npgsql.NpgsqlConnection(fixture.Database.AppConnectionString);
        await connection.OpenAsync();
        await using (var set = new Npgsql.NpgsqlCommand("SELECT set_config('app.tenant_id', @t, false)", connection))
        {
            set.Parameters.AddWithValue("t", tenantId.ToString("D"));
            await set.ExecuteScalarAsync();
        }

        await using var count = new Npgsql.NpgsqlCommand($"SELECT count(*) FROM {table} WHERE id = @id", connection);
        count.Parameters.AddWithValue("id", id);
        return (long)(await count.ExecuteScalarAsync())!;
    }
}
