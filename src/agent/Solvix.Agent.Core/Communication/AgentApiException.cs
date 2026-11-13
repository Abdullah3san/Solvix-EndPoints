namespace Solvix.Agent.Core.Communication;

using System.Net;

public enum AgentApiFailure
{
    /// <summary>Network error, timeout, 5xx, 408 or 429: retry with backoff.</summary>
    Transient = 1,

    /// <summary>401/403: the device credential (or enrollment token) was not accepted.</summary>
    Authentication = 2,

    /// <summary>Other 4xx: the server rejected the request; retrying unchanged will not help.</summary>
    Rejected = 3,
}

public sealed class AgentApiException : Exception
{
    public AgentApiException(AgentApiFailure failure, HttpStatusCode? status, string? code, string message, Exception? inner = null)
        : base(message, inner)
    {
        Failure = failure;
        Status = status;
        Code = code;
    }

    public AgentApiException()
        : this(AgentApiFailure.Transient, null, null, "Agent API call failed.")
    {
    }

    public AgentApiException(string message)
        : this(AgentApiFailure.Transient, null, null, message)
    {
    }

    public AgentApiException(string message, Exception innerException)
        : this(AgentApiFailure.Transient, null, null, message, innerException)
    {
    }

    public AgentApiFailure Failure { get; }

    public HttpStatusCode? Status { get; }

    /// <summary>Machine-readable problem code from the server, if any (e.g. <c>enrollment_token_expired</c>).</summary>
    public string? Code { get; }

    public static AgentApiFailure Classify(HttpStatusCode status) => (int)status switch
    {
        401 or 403 => AgentApiFailure.Authentication,
        408 or 429 => AgentApiFailure.Transient,
        >= 500 => AgentApiFailure.Transient,
        _ => AgentApiFailure.Rejected,
    };
}
