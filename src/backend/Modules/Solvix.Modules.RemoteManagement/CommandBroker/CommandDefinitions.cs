namespace Solvix.Modules.RemoteManagement.CommandBroker;

using System.Globalization;
using Solvix.Agent.Contracts.RemoteManagement;
using Solvix.SharedKernel.Authorization;
using Solvix.SharedKernel.Exceptions;

/// <summary>
/// An allow-listed command: who may request it, how long it may run, whether the requester
/// must confirm the target by name, and which parameters it accepts. Requests can only be
/// made for types listed in <see cref="CommandDefinitions"/>; parameters are validated and
/// normalized here, never taken from the browser as-is.
/// </summary>
public sealed record CommandDefinition(
    string Type,
    string Permission,
    TimeSpan ExecutionTimeout,
    bool RequiresTargetConfirmation,
    bool Disruptive,
    Func<IReadOnlyDictionary<string, string?>, Dictionary<string, string>> Validate);

public static class CommandDefinitions
{
    public const int MaxMessageLength = 200;
    public const int MaxDelaySeconds = 600;

    private static readonly Dictionary<string, CommandDefinition> ByType = new CommandDefinition[]
    {
        new(CommandTypes.SystemInfo, Permissions.Remote.View, TimeSpan.FromMinutes(2), false, false, NoParameters),
        new(CommandTypes.Processes, Permissions.Remote.View, TimeSpan.FromMinutes(2), false, false, NoParameters),
        new(CommandTypes.Services, Permissions.Remote.View, TimeSpan.FromMinutes(2), false, false, NoParameters),
        new(CommandTypes.Uptime, Permissions.Remote.View, TimeSpan.FromMinutes(1), false, false, NoParameters),
        new(CommandTypes.LoggedInUsers, Permissions.Remote.View, TimeSpan.FromMinutes(1), false, false, NoParameters),
        new(CommandTypes.Lock, Permissions.Remote.Lock, TimeSpan.FromMinutes(1), false, true, NoParameters),
        new(CommandTypes.Logoff, Permissions.Remote.Restart, TimeSpan.FromMinutes(2), true, true, PowerParameters),
        new(CommandTypes.Restart, Permissions.Remote.Restart, TimeSpan.FromMinutes(15), true, true, PowerParameters),
        new(CommandTypes.Shutdown, Permissions.Remote.Shutdown, TimeSpan.FromMinutes(15), true, true, PowerParameters),
    }.ToDictionary(d => d.Type, StringComparer.Ordinal);

    /// <summary>The internal cancel command; never requested directly.</summary>
    public static CommandDefinition Cancel { get; } =
        new(CommandTypes.Cancel, Permissions.Remote.View, TimeSpan.FromMinutes(1), false, false, NoParameters);

    public static IReadOnlyCollection<CommandDefinition> All => ByType.Values;

    public static CommandDefinition Get(string type) =>
        ByType.TryGetValue(type, out var definition)
            ? definition
            : throw new ValidationException("commandType", $"'{type}' is not an allowed command.");

    public static bool IsKnown(string type) => ByType.ContainsKey(type);

    private static Dictionary<string, string> NoParameters(IReadOnlyDictionary<string, string?> input)
    {
        var unexpected = input.Keys.FirstOrDefault();
        return unexpected is null
            ? []
            : throw new ValidationException(unexpected, "This operation takes no parameters.");
    }

    /// <summary>Restart / shutdown / sign-off: optional delay (0–600 s) and a message shown to the user.</summary>
    private static Dictionary<string, string> PowerParameters(IReadOnlyDictionary<string, string?> input)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in input)
        {
            switch (key)
            {
                case "delaySeconds":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var delay) || delay is < 0 or > MaxDelaySeconds)
                    {
                        throw new ValidationException(key, $"Delay must be between 0 and {MaxDelaySeconds} seconds.");
                    }

                    result[key] = delay.ToString(CultureInfo.InvariantCulture);
                    break;
                case "message":
                    var message = value?.Trim() ?? string.Empty;
                    if (message.Length > MaxMessageLength || message.Any(char.IsControl))
                    {
                        throw new ValidationException(key, $"The message must be at most {MaxMessageLength} printable characters.");
                    }

                    if (message.Length > 0)
                    {
                        result[key] = message;
                    }

                    break;
                default:
                    throw new ValidationException(key, "Unknown parameter.");
            }
        }

        return result;
    }
}
