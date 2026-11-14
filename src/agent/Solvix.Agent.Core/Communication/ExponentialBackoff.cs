namespace Solvix.Agent.Core.Communication;

using Solvix.Agent.Core.Configuration;

/// <summary>Capped exponential backoff with symmetric jitter.</summary>
public sealed class ExponentialBackoff(BackoffOptions options, Func<double>? random = null)
{
    private readonly Func<double> _random = random ?? Random.Shared.NextDouble;

    /// <summary>Delay before retry number <paramref name="failures"/> (1 = first retry).</summary>
    public TimeSpan Delay(int failures)
    {
        if (failures <= 0)
        {
            return TimeSpan.Zero;
        }

        var exponent = Math.Min(failures - 1, 30);
        var seconds = Math.Min(options.MaxSeconds, options.InitialSeconds * Math.Pow(options.Multiplier, exponent));
        var jitter = 1 + (((_random() * 2) - 1) * Math.Clamp(options.JitterRatio, 0, 1));
        return TimeSpan.FromSeconds(Math.Clamp(seconds * jitter, 0, options.MaxSeconds));
    }
}
