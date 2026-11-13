namespace Solvix.Agent.Core.Enrollment;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Solvix.Agent.Contracts;
using Solvix.Agent.Core.Communication;
using Solvix.Agent.Core.Configuration;
using Solvix.Agent.Core.Inventory;
using Solvix.Agent.Core.Security;
using Solvix.Agent.Core.State;

public enum EnrollmentResult
{
    Enrolled = 1,

    /// <summary>No enrollment token has been supplied yet.</summary>
    NoToken = 2,

    /// <summary>The server rejected the token (invalid, expired, revoked or used up).</summary>
    TokenRejected = 3,

    /// <summary>Server unreachable or temporarily failing: retry with backoff.</summary>
    Transient = 4,
}

/// <summary>
/// Secure enrollment module: generates a fresh non-exportable key, sends a CSR
/// together with the short-lived enrollment token, installs the issued device
/// certificate and destroys the token.
/// </summary>
public sealed partial class EnrollmentCoordinator(
    IAgentApiClient api,
    BootstrapTokenStore bootstrap,
    DeviceCredentialStore credentials,
    IAgentStateStore stateStore,
    IDeviceIdentityProvider identityProvider,
    IOptions<AgentOptions> options,
    TimeProvider time,
    ILogger<EnrollmentCoordinator> logger)
{
    public async Task<EnrollmentResult> EnrollAsync(CancellationToken cancellationToken)
    {
        var token = bootstrap.Read();
        if (string.IsNullOrWhiteSpace(token))
        {
            return EnrollmentResult.NoToken;
        }

        var identity = await identityProvider.GetAsync(cancellationToken);
        var (keyName, csr) = credentials.CreateSigningRequest(identity.Hostname);
        EnrollmentResponse response;
        try
        {
            response = await api.EnrollAsync(
                new EnrollmentRequest { EnrollmentToken = token, CertificateSigningRequestPem = csr, Device = identity },
                cancellationToken);
        }
        catch (AgentApiException ex)
        {
            credentials.DeleteKey(keyName);
            if (ex.Failure == AgentApiFailure.Transient)
            {
                LogTransient(logger, ex.Message);
                return EnrollmentResult.Transient;
            }

            // Never retry a rejected token in a loop: it is moved aside until a new one is supplied.
            bootstrap.MarkRejected();
            LogRejected(logger, ex.Code ?? ex.Status?.ToString() ?? "unknown", ex.Message);
            return EnrollmentResult.TokenRejected;
        }
        catch
        {
            credentials.DeleteKey(keyName);
            throw;
        }

        var previous = stateStore.Load();
        credentials.Install(keyName, response.CertificatePem, response.CaCertificatesPem);
        var now = time.GetUtcNow();
        stateStore.Save(previous with
        {
            DeviceId = response.DeviceId,
            TenantId = response.TenantId,
            ServerUrl = options.Value.ServerUrl,
            EnrolledAtUtc = now,
            KeyName = keyName,
            CertificateIssuedAtUtc = now,
            CertificateNotAfterUtc = response.CertificateNotAfterUtc,
            RetiredKeyName = null,
            LastInventoryHash = null,
            LastInventoryAtUtc = null,
            LastSuccessfulContactAtUtc = now,
            Configuration = response.Configuration,
        });

        if (previous.KeyName is not null && previous.KeyName != keyName)
        {
            credentials.DeleteKey(previous.KeyName);
        }

        bootstrap.Delete();
        LogEnrolled(logger, response.DeviceId, response.TenantId, response.ReEnrolled, response.CertificateNotAfterUtc);
        return EnrollmentResult.Enrolled;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Enrolled as device {DeviceId} in tenant {TenantId} (re-enrolled: {ReEnrolled}); certificate valid until {NotAfter:O}")]
    private static partial void LogEnrolled(ILogger logger, Guid deviceId, Guid tenantId, bool reEnrolled, DateTimeOffset notAfter);

    [LoggerMessage(Level = LogLevel.Error, Message = "Enrollment token rejected ({Code}): {Message}. Supply a new token with 'Solvix.Agent enroll --token <token>'.")]
    private static partial void LogRejected(ILogger logger, string code, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Enrollment failed temporarily: {Message}")]
    private static partial void LogTransient(ILogger logger, string message);
}
