namespace Solvix.SharedKernel.Guards;

using Solvix.SharedKernel.Exceptions;

public static class Guard
{
    public static string NotNullOrWhiteSpace(string? value, string name, int maxLength = int.MaxValue)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ValidationException(name, $"{name} is required.");
        }

        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
        {
            throw new ValidationException(name, $"{name} must be at most {maxLength} characters.");
        }

        return trimmed;
    }

    public static string? Optional(string? value, string name, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
        {
            throw new ValidationException(name, $"{name} must be at most {maxLength} characters.");
        }

        return trimmed;
    }
}
