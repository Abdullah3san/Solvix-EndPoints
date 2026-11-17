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

    /// <summary>The request is queued; an administrator has to approve this computer.</summary>
    PendingApproval = 5,
}

/// <summary>
/// Secure enrollment module: generates a fresh non-exportable key, sends a CSR
/// together with the short-lived enrollment token, installs the issued device
/// certificate and destroys the token.
/// </summary>
public sealed partial class EnrollmentCoordinator(
    IAgentApiClient api,
    BootstrapTokenStore bootstrap,
    PendingEnrollmentStore pending,
    DeviceCredentialStore credentials,
    IAgentStateStore stateStore,
    IDeviceIdentityProvider identityProvider,
    IOptions<AgentOptions> options,
    TimeProvider time,
    ILogger<EnrollmentCoordinator> logger)
{
    /// <summary>How long to wait before polling again while an administrator has not decided yet.</summary>
    public TimeSpan PollInterval { get; private set; } = TimeSpan.FromSeconds(30);

    public async Task<EnrollmentResult> EnrollAsync(CancellationToken cancellationToken)
    {
        if (pending.Load() is { } waiting)
        {
            return await PollAsync(waiting, cancellationToken);
        }

        var token = bootstrap.Read();
        if (string.IsNullOrWhiteSpace(token))
        {
            return EnrollmentResult.NoToken;
        }

        var identity = await identityProvider.GetAsync(cancellationToken);
        var (keyName, csr) = credentials.CreateSigningRequest(identity.Hostname);
        EnrollmentSubmission submission;
        try
        {
            submission = await api.SubmitEnrollmentAsync(
                new EnrollmentRequest { EnrollmentToken = token, CertificateSigningRequestPem = csr, Device = identity },
                cancellationToken);
        }
        catch (AgentApiException ex)
        {
            credentials.DeleteKey(keyName);
            if (ex.Failure == AgentApiFailure.Transient || ex.Code == "enrollment_queue_full")
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

        if (submission.Enrolled is { } response)
        {
            Complete(keyName, response);
            bootstrap.Delete();
            return EnrollmentResult.Enrolled;
        }

        var waitingFor = submission.Pending!;
        PollInterval = ClampInterval(waitingFor.PollIntervalSeconds);
        pending.Save(new PendingEnrollmentState(keyName, waitingFor.PollToken, waitingFor.PollIntervalSeconds, waitingFor.ExpiresAtUtc));
        LogPending(logger, waitingFor.PendingEnrollmentId);
        return EnrollmentResult.PendingApproval;
    }

    private async Task<EnrollmentResult> PollAsync(PendingEnrollmentState waiting, CancellationToken cancellationToken)
    {
        EnrollmentPollResponse poll;
        try
        {
            poll = await api.PollEnrollmentAsync(waiting.PollToken, cancellationToken);
        }
        catch (AgentApiException ex) when (ex.Failure == AgentApiFailure.Transient)
        {
            LogTransient(logger, ex.Message);
            return EnrollmentResult.Transient;
        }
        catch (AgentApiException ex)
        {
            // The server does not know this request any more: start over with a new one.
            LogRequestLost(logger, ex.Message);
            Discard(waiting);
            return EnrollmentResult.Transient;
        }

        PollInterval = ClampInterval(poll.PollIntervalSeconds);
        switch (poll.Status)
        {
            case EnrollmentPollStatus.Approved when poll.Enrollment is { } enrollment:
                Complete(waiting.KeyName, enrollment);
                pending.Delete();
                bootstrap.Delete();
                return EnrollmentResult.Enrolled;
            case EnrollmentPollStatus.Rejected:
                Discard(waiting);
                bootstrap.MarkRejected();
                LogDeclined(logger, poll.Message ?? "no reason given");
                return EnrollmentResult.TokenRejected;
            case EnrollmentPollStatus.Pending:
                return EnrollmentResult.PendingApproval;
            default:
                // Expired or already collected: the stored code is still valid, so ask again.
                Discard(waiting);
                return EnrollmentResult.Transient;
        }
    }

    private void Discard(PendingEnrollmentState waiting)
    {
        credentials.DeleteKey(waiting.KeyName);
        pending.Delete();
    }

    private static TimeSpan ClampInterval(int seconds) => TimeSpan.FromSeconds(Math.Clamp(seconds, 15, 600));

    private void Complete(string keyName, EnrollmentResponse response)
    {
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

        LogEnrolled(logger, response.DeviceId, response.TenantId, response.ReEnrolled, response.CertificateNotAfterUtc);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Enrollment request {RequestId} is waiting for an administrator to approve this computer.")]
    private static partial void LogPending(ILogger logger, Guid requestId);

    [LoggerMessage(Level = LogLevel.Error, Message = "An administrator declined this computer: {Reason}")]
    private static partial void LogDeclined(ILogger logger, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The pending enrollment request is no longer known to the server ({Message}); requesting again.")]
    private static partial void LogRequestLost(ILogger logger, string message);

    [LoggerMessage(Level = LogLevel.Information, Message = "Enrolled as device {DeviceId} in tenant {TenantId} (re-enrolled: {ReEnrolled}); certificate valid until {NotAfter:O}")]
    private static partial void LogEnrolled(ILogger logger, Guid deviceId, Guid tenantId, bool reEnrolled, DateTimeOffset notAfter);

    [LoggerMessage(Level = LogLevel.Error, Message = "Enrollment token rejected ({Code}): {Message}. Supply a new token with 'Solvix.Agent enroll --token <token>'.")]
    private static partial void LogRejected(ILogger logger, string code, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Enrollment failed temporarily: {Message}")]
    private static partial void LogTransient(ILogger logger, string message);
}
