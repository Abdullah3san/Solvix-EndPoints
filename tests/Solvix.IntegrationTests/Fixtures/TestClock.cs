namespace Solvix.IntegrationTests.Fixtures;

using Solvix.SharedKernel.Abstractions;

/// <summary>Real time plus an adjustable offset, so monitoring tests can let minutes pass instantly.</summary>
public sealed class TestClock : IClock
{
    private long _offsetTicks;

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow.AddTicks(Interlocked.Read(ref _offsetTicks));

    public TimeSpan Offset => TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));

    public void Advance(TimeSpan by) => Interlocked.Add(ref _offsetTicks, by.Ticks);

    public void Reset() => Interlocked.Exchange(ref _offsetTicks, 0);
}
