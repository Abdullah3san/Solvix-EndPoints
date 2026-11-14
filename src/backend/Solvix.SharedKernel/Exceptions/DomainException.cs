namespace Solvix.SharedKernel.Exceptions;

/// <summary>Base type for all exceptions that are translated into HTTP problem responses.</summary>
public abstract class DomainException : Exception
{
    protected DomainException(string message)
        : base(message)
    {
    }

    protected DomainException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    protected DomainException()
    {
    }

    /// <summary>Machine-readable error code (snake_case).</summary>
    public abstract string Code { get; }
}

public sealed class NotFoundException : DomainException
{
    public NotFoundException(string resource, object key)
        : base($"{resource} '{key}' was not found.")
    {
        Resource = resource;
        Key = key.ToString() ?? string.Empty;
    }

    public NotFoundException()
    {
        Resource = string.Empty;
        Key = string.Empty;
    }

    public NotFoundException(string message)
        : base(message)
    {
        Resource = string.Empty;
        Key = string.Empty;
    }

    public NotFoundException(string message, Exception? innerException)
        : base(message, innerException)
    {
        Resource = string.Empty;
        Key = string.Empty;
    }

    public string Resource { get; }

    public string Key { get; }

    public override string Code => "not_found";
}

public sealed class ConflictException : DomainException
{
    private readonly string? _code;

    public ConflictException(string message)
        : base(message)
    {
    }

    /// <summary>Conflict with a specific, machine-readable <paramref name="code"/>.</summary>
    public ConflictException(string message, string code)
        : base(message)
    {
        _code = code;
    }

    /// <summary>Conflict with a specific code, preserving the underlying exception.</summary>
    public ConflictException(string message, string code, Exception? innerException)
        : base(message, innerException)
    {
        _code = code;
    }

    public ConflictException()
    {
    }

    public ConflictException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    public override string Code => _code ?? "conflict";
}

public sealed class ValidationException : DomainException
{
    public ValidationException(IReadOnlyDictionary<string, string[]> errors)
        : base("One or more validation errors occurred.")
    {
        Errors = errors;
    }

    public ValidationException(string property, string error)
        : this(new Dictionary<string, string[]> { [property] = [error] })
    {
    }

    /// <summary>A validation failure with a specific, machine-readable <paramref name="code"/>.</summary>
    public ValidationException(string property, string error, string code)
        : this(new Dictionary<string, string[]> { [property] = [error] })
    {
        _code = code;
    }

    public ValidationException()
        : base("One or more validation errors occurred.")
    {
        Errors = new Dictionary<string, string[]>();
    }

    public ValidationException(string message)
        : base(message)
    {
        Errors = new Dictionary<string, string[]>();
    }

    public ValidationException(string message, Exception? innerException)
        : base(message, innerException)
    {
        Errors = new Dictionary<string, string[]>();
    }

    private readonly string? _code;

    public IReadOnlyDictionary<string, string[]> Errors { get; }

    public override string Code => _code ?? "validation_failed";
}

public class ForbiddenException : DomainException
{
    private readonly string? _code;

    public ForbiddenException(string message)
        : base(message)
    {
    }

    /// <param name="code">Machine-readable reason, e.g. <c>insufficient_privilege</c>.</param>
    public ForbiddenException(string message, string code)
        : base(message)
    {
        _code = code;
    }

    public ForbiddenException()
        : base("You are not allowed to perform this action.")
    {
    }

    public ForbiddenException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    public override string Code => _code ?? "forbidden";
}

/// <summary>Raised whenever code attempts to read or write data of a tenant other than the current one.</summary>
public sealed class CrossTenantAccessException : ForbiddenException
{
    public CrossTenantAccessException(string message)
        : base(message)
    {
    }

    public CrossTenantAccessException()
        : base("Cross-tenant access is not permitted.")
    {
    }

    public CrossTenantAccessException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    public override string Code => "cross_tenant_access";
}

public sealed class UnauthorizedException : DomainException
{
    private readonly string? _code;

    public UnauthorizedException(string message)
        : base(message)
    {
    }

    /// <summary>Authentication failure with a specific, machine-readable <paramref name="code"/>.</summary>
    public UnauthorizedException(string message, string code)
        : base(message)
    {
        _code = code;
    }

    public UnauthorizedException()
        : base("Authentication is required.")
    {
    }

    public UnauthorizedException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    public override string Code => _code ?? "unauthorized";
}

public sealed class TenantContextMissingException : DomainException
{
    public TenantContextMissingException()
        : base("No tenant has been resolved for the current operation.")
    {
    }

    public TenantContextMissingException(string message)
        : base(message)
    {
    }

    public TenantContextMissingException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    public override string Code => "tenant_context_missing";
}

public sealed class TenantInactiveException : DomainException
{
    public TenantInactiveException(string status)
        : base($"The tenant is not active (status: {status}).")
    {
    }

    public TenantInactiveException()
        : base("The tenant is not active.")
    {
    }

    public TenantInactiveException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    public override string Code => "tenant_inactive";
}

/// <summary>A required dependency (for example the AI model) is not configured or currently unavailable.</summary>
public sealed class ServiceUnavailableException : DomainException
{
    public ServiceUnavailableException(string message, string code = "service_unavailable")
        : base(message)
    {
        Code = code;
    }

    public ServiceUnavailableException()
        : base("The service is currently unavailable.")
    {
        Code = "service_unavailable";
    }

    public ServiceUnavailableException(string message, Exception? innerException)
        : base(message, innerException)
    {
        Code = "service_unavailable";
    }

    public override string Code { get; }
}

/// <summary>An upload exceeded the permitted size.</summary>
public sealed class PayloadTooLargeException : DomainException
{
    public PayloadTooLargeException(string message)
        : base(message)
    {
    }

    public PayloadTooLargeException()
        : base("The payload is too large.")
    {
    }

    public PayloadTooLargeException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    public override string Code => "payload_too_large";
}

/// <summary>Raised when a caller exceeds an agreed request rate. Mapped to HTTP 429 with a Retry-After header.</summary>
public sealed class TooManyRequestsException : DomainException
{
    public TooManyRequestsException(int retryAfterSeconds)
        : base($"Too many requests. Retry in {retryAfterSeconds} second(s).")
    {
        RetryAfterSeconds = retryAfterSeconds;
    }

    public TooManyRequestsException()
        : this(60)
    {
    }

    public TooManyRequestsException(string message)
        : base(message)
    {
        RetryAfterSeconds = 60;
    }

    public TooManyRequestsException(string message, Exception? innerException)
        : base(message, innerException)
    {
        RetryAfterSeconds = 60;
    }

    public int RetryAfterSeconds { get; }

    public override string Code => "too_many_requests";
}

/// <summary>
/// The request is well-formed but the operation is not possible for the target
/// (for example a capability the platform or ownership model does not offer).
/// Distinct from <see cref="ConflictException"/>: retrying will never succeed.
/// </summary>
public sealed class UnsupportedOperationException : DomainException
{
    public UnsupportedOperationException(string message)
        : base(message)
    {
    }

    public UnsupportedOperationException()
        : base("The operation is not supported for this target.")
    {
    }

    public UnsupportedOperationException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    public override string Code => "unsupported_operation";
}
