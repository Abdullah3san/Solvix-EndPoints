namespace Solvix.SharedKernel.Exceptions;

/// <summary>The subscription does not allow the operation (plan limit reached, subscription lapsed). Mapped to HTTP 402.</summary>
public sealed class PaymentRequiredException : DomainException
{
    private readonly string _code;

    public PaymentRequiredException(string message, string code)
        : base(message)
    {
        _code = code;
    }

    public PaymentRequiredException()
        : this("The subscription does not allow this operation.", "payment_required")
    {
    }

    public PaymentRequiredException(string message)
        : this(message, "payment_required")
    {
    }

    public PaymentRequiredException(string message, Exception? innerException)
        : base(message, innerException)
    {
        _code = "payment_required";
    }

    public override string Code => _code;
}
