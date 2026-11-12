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
    public ConflictException(string message)
        : base(message)
    {
    }

    public ConflictException()
    {
    }

    public ConflictException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    public override string Code => "conflict";
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

    public IReadOnlyDictionary<string, string[]> Errors { get; }

    public override string Code => "validation_failed";
}

public class ForbiddenException : DomainException
{
    public ForbiddenException(string message)
        : base(message)
    {
    }

    public ForbiddenException()
        : base("You are not allowed to perform this action.")
    {
    }

    public ForbiddenException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    public override string Code => "forbidden";
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
    public UnauthorizedException(string message)
        : base(message)
    {
    }

    public UnauthorizedException()
        : base("Authentication is required.")
    {
    }

    public UnauthorizedException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    public override string Code => "unauthorized";
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
