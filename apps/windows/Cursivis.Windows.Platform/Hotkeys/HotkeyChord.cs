namespace Cursivis.Windows.Platform.Hotkeys;

[Flags]
public enum HotkeyModifiers : uint
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Windows = 0x0008,
}

public enum HotkeyChordValidationCode
{
    Valid,
    MissingModifier,
    InvalidVirtualKey,
    ModifierOnly,
    ReservedCombination,
}

public readonly record struct HotkeyChord
{
    private const HotkeyModifiers AllowedModifiers =
        HotkeyModifiers.Alt |
        HotkeyModifiers.Control |
        HotkeyModifiers.Shift |
        HotkeyModifiers.Windows;

    public HotkeyChord(HotkeyModifiers modifiers, uint virtualKey)
    {
        var validation = Validate(modifiers, virtualKey);
        if (validation != HotkeyChordValidationCode.Valid)
        {
            throw new ArgumentException(
                $"The hotkey chord is invalid: {validation}.",
                nameof(virtualKey));
        }

        Modifiers = modifiers;
        VirtualKey = virtualKey;
    }

    public HotkeyModifiers Modifiers { get; }

    public uint VirtualKey { get; }

    public static HotkeyChordValidationCode Validate(
        HotkeyModifiers modifiers,
        uint virtualKey)
    {
        if (modifiers == HotkeyModifiers.None || (modifiers & ~AllowedModifiers) != 0)
        {
            return HotkeyChordValidationCode.MissingModifier;
        }

        if (virtualKey is 0 or > 0xFF)
        {
            return HotkeyChordValidationCode.InvalidVirtualKey;
        }

        if (virtualKey is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C)
        {
            return HotkeyChordValidationCode.ModifierOnly;
        }

        const uint virtualKeyDelete = 0x2E;
        const uint virtualKeyF4 = 0x73;
        const uint virtualKeyL = 0x4C;

        if ((modifiers.HasFlag(HotkeyModifiers.Control) &&
             modifiers.HasFlag(HotkeyModifiers.Alt) &&
             virtualKey == virtualKeyDelete) ||
            (modifiers == HotkeyModifiers.Alt && virtualKey == virtualKeyF4) ||
            (modifiers.HasFlag(HotkeyModifiers.Windows) && virtualKey == virtualKeyL))
        {
            return HotkeyChordValidationCode.ReservedCombination;
        }

        return HotkeyChordValidationCode.Valid;
    }

    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            parts.Add("Win");
        }

        parts.Add(FormatVirtualKey(VirtualKey));
        return string.Join('+', parts);
    }

    private static string FormatVirtualKey(uint virtualKey)
    {
        if (virtualKey is >= 0x30 and <= 0x39 || virtualKey is >= 0x41 and <= 0x5A)
        {
            return ((char)virtualKey).ToString();
        }

        if (virtualKey is >= 0x70 and <= 0x87)
        {
            return $"F{virtualKey - 0x6F}";
        }

        return virtualKey switch
        {
            0x1B => "Escape",
            0x20 => "Space",
            0x09 => "Tab",
            _ => $"VK_{virtualKey:X2}",
        };
    }
}

public static class HotkeyChordParser
{
    public static bool TryParse(string? value, out HotkeyChord chord)
    {
        chord = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string[] parts = value.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        HotkeyModifiers modifiers = HotkeyModifiers.None;
        foreach (string part in parts[..^1])
        {
            HotkeyModifiers modifier = part.ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" => HotkeyModifiers.Control,
                "ALT" => HotkeyModifiers.Alt,
                "SHIFT" => HotkeyModifiers.Shift,
                "WIN" or "WINDOWS" => HotkeyModifiers.Windows,
                _ => HotkeyModifiers.None,
            };
            if (modifier == HotkeyModifiers.None || modifiers.HasFlag(modifier))
            {
                return false;
            }

            modifiers |= modifier;
        }

        string key = parts[^1].ToUpperInvariant();
        uint virtualKey = key.Length == 1 && char.IsAsciiLetterOrDigit(key[0])
            ? key[0]
            : key == "ESCAPE"
                ? 0x1B
                : key.StartsWith('F') && uint.TryParse(key[1..], out uint function) && function is >= 1 and <= 24
                    ? 0x6F + function
                    : 0;
        if (HotkeyChord.Validate(modifiers, virtualKey) != HotkeyChordValidationCode.Valid)
        {
            return false;
        }

        chord = new HotkeyChord(modifiers, virtualKey);
        return true;
    }
}
