namespace Solvix.Agent.Windows.Providers;

using System.Globalization;
using Microsoft.Win32;
using Solvix.Endpoint.Contracts.Configuration;

/// <summary>HKLM (64-bit view) value access with canonical text values for comparison and snapshots.</summary>
internal static class RegistryHelper
{
    public sealed record ValueState(bool KeyExists, bool ValueExists, RegistryValueType? Type, string? Value);

    public static RegistryKey Hklm() => RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);

    public static ValueState Read(string path, string? valueName)
    {
        using var root = Hklm();
        using var key = root.OpenSubKey(path, writable: false);
        if (key is null)
        {
            return new ValueState(false, false, null, null);
        }

        var name = valueName ?? string.Empty;
        var raw = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        if (raw is null)
        {
            return new ValueState(true, false, null, null);
        }

        var kind = key.GetValueKind(name);
        return new ValueState(true, true, FromKind(kind), ToText(kind, raw));
    }

    public static void Write(string path, string? valueName, RegistryValueType type, string value)
    {
        using var root = Hklm();
        using var key = root.CreateSubKey(path, writable: true) ?? throw new InvalidOperationException($"Cannot open HKLM\\{path} for writing.");
        key.SetValue(valueName ?? string.Empty, ToRaw(type, value), ToKind(type));
    }

    public static void Delete(string path, string? valueName)
    {
        using var root = Hklm();
        using var key = root.OpenSubKey(path, writable: true);
        key?.DeleteValue(valueName ?? string.Empty, throwOnMissingValue: false);
    }

    /// <summary>Restores a previously captured state, removing a key that did not exist before if it is now empty.</summary>
    public static void Restore(string path, string? valueName, ValueState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.ValueExists)
        {
            Write(path, valueName, state.Type!.Value, state.Value!);
            return;
        }

        Delete(path, valueName);
        if (!state.KeyExists)
        {
            using var root = Hklm();
            using (var key = root.OpenSubKey(path))
            {
                if (key is null || key.ValueCount > 0 || key.SubKeyCount > 0)
                {
                    return;
                }
            }

            root.DeleteSubKey(path, throwOnMissingSubKey: false);
        }
    }

    public static string ToText(RegistryValueKind kind, object raw) => kind switch
    {
        RegistryValueKind.DWord => unchecked((uint)(int)raw).ToString(CultureInfo.InvariantCulture),
        RegistryValueKind.QWord => unchecked((ulong)(long)raw).ToString(CultureInfo.InvariantCulture),
        RegistryValueKind.MultiString => string.Join('\n', (string[])raw),
        RegistryValueKind.Binary => Convert.ToHexString((byte[])raw),
        _ => raw.ToString() ?? string.Empty,
    };

    public static object ToRaw(RegistryValueType type, string value)
    {
        if (!RegistrySetting.TryNormalizeValue(type, value, out var normalized))
        {
            throw new FormatException($"'{value}' is not a valid {type}.");
        }

        return type switch
        {
            RegistryValueType.DWord => unchecked((int)uint.Parse(normalized, CultureInfo.InvariantCulture)),
            RegistryValueType.QWord => unchecked((long)ulong.Parse(normalized, CultureInfo.InvariantCulture)),
            RegistryValueType.MultiString => normalized.Split('\n'),
            RegistryValueType.Binary => Convert.FromHexString(normalized),
            _ => normalized,
        };
    }

    public static RegistryValueKind ToKind(RegistryValueType type) => type switch
    {
        RegistryValueType.String => RegistryValueKind.String,
        RegistryValueType.ExpandString => RegistryValueKind.ExpandString,
        RegistryValueType.DWord => RegistryValueKind.DWord,
        RegistryValueType.QWord => RegistryValueKind.QWord,
        RegistryValueType.MultiString => RegistryValueKind.MultiString,
        RegistryValueType.Binary => RegistryValueKind.Binary,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static RegistryValueType? FromKind(RegistryValueKind kind) => kind switch
    {
        RegistryValueKind.String => RegistryValueType.String,
        RegistryValueKind.ExpandString => RegistryValueType.ExpandString,
        RegistryValueKind.DWord => RegistryValueType.DWord,
        RegistryValueKind.QWord => RegistryValueType.QWord,
        RegistryValueKind.MultiString => RegistryValueType.MultiString,
        RegistryValueKind.Binary => RegistryValueType.Binary,
        _ => null,
    };

    public static string Describe(ValueState state) =>
        !state.ValueExists ? "(not set)" : $"{state.Value} ({state.Type})";
}
