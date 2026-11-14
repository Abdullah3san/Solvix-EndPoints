namespace Solvix.SharedKernel.Entities;

/// <summary>
/// Base class for all persisted aggregate roots and entities.
/// Identifiers are client-generated UUIDv7 values so that inserts are
/// index-friendly and ids can be known before the row is written.
/// </summary>
public abstract class Entity : IAuditable
{
    public Guid Id { get; protected set; } = Guid.CreateVersion7();

    public DateTimeOffset CreatedAtUtc { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; }
}
