namespace Solvix.SharedKernel.Entities;

public interface IAuditable
{
    DateTimeOffset CreatedAtUtc { get; set; }

    DateTimeOffset UpdatedAtUtc { get; set; }
}
