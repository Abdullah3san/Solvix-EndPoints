namespace Solvix.SharedKernel.Exceptions;

/// <summary>A third-party dependency (payment provider, AI service) failed. Mapped to HTTP 502; details stay in the logs.</summary>
public sealed class ExternalServiceException : DomainException
{
    public ExternalServiceException(string service, string message)
        : base(message)
    {
        Service = service;
    }

    public ExternalServiceException()
        : this("external", "An upstream service is unavailable.")
    {
    }

    public ExternalServiceException(string message)
        : this("external", message)
    {
    }

    public ExternalServiceException(string message, Exception? innerException)
        : base(message, innerException)
    {
        Service = "external";
    }

    public string Service { get; } = "external";

    public override string Code => "upstream_unavailable";
}
