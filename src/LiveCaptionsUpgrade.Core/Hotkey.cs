using System.Diagnostics.CodeAnalysis;

namespace LiveCaptionsUpgrade.Core;

/// <summary>Modifier keys of a <see cref="Hotkey"/>. Values match the Win32 RegisterHotKey MOD_* flags.</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 0x1,
    Ctrl = 0x2,
    Shift = 0x4,
    Win = 0x8,
}

/// <summary>A keyboard shortcut such as Ctrl+Alt+H, stored in settings as text.</summary>
/// <param name="Modifiers">Modifier keys that must be held.</param>
/// <param name="Key">The main key, named as in WPF's <c>System.Windows.Input.Key</c> enum (e.g. "H", "F9", "D1").</param>
public sealed record Hotkey(HotkeyModifiers Modifiers, string Key)
{
    private static readonly (HotkeyModifiers Flag, string Name)[] ModifierNames =
    {
        (HotkeyModifiers.Ctrl, "Ctrl"),
        (HotkeyModifiers.Alt, "Alt"),
        (HotkeyModifiers.Shift, "Shift"),
        (HotkeyModifiers.Win, "Win"),
    };

    /// <summary>
    /// A global shortcut needs a modifier, or it would swallow that key everywhere
    /// (typing "h" would hide the captions). Function keys are allowed alone.
    /// </summary>
    public bool IsValid => Key.Length > 0 && (Modifiers != HotkeyModifiers.None || IsFunctionKey(Key));

    public static bool TryParse(string? text, [NotNullWhen(true)] out Hotkey? hotkey)
    {
        hotkey = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var modifiers = HotkeyModifiers.None;
        string? key = null;
        foreach (string rawPart in text.Split('+'))
        {
            string part = rawPart.Trim();
            var modifier = ParseModifier(part);
            if (modifier != HotkeyModifiers.None)
            {
                modifiers |= modifier;
            }
            else if (key is null && part.Length > 0)
            {
                // Digits are shown as "1" but WPF names the key "D1"; key names are capitalised ("h" -> "H", "f5" -> "F5").
                key = part.Length == 1 && char.IsDigit(part[0]) ? "D" + part : char.ToUpperInvariant(part[0]) + part[1..];
            }
            else
            {
                return false;
            }
        }

        if (key is null)
        {
            return false;
        }

        var candidate = new Hotkey(modifiers, key);
        if (!candidate.IsValid)
        {
            return false;
        }

        hotkey = candidate;
        return true;
    }

    public override string ToString()
    {
        var parts = new List<string>();
        foreach (var (flag, name) in ModifierNames)
        {
            if (Modifiers.HasFlag(flag))
            {
                parts.Add(name);
            }
        }

        parts.Add(Key.Length == 2 && Key[0] == 'D' && char.IsDigit(Key[1]) ? Key[1..] : Key);
        return string.Join("+", parts);
    }

    private static HotkeyModifiers ParseModifier(string part) => part.ToUpperInvariant() switch
    {
        "CTRL" or "CONTROL" => HotkeyModifiers.Ctrl,
        "ALT" => HotkeyModifiers.Alt,
        "SHIFT" => HotkeyModifiers.Shift,
        "WIN" or "WINDOWS" => HotkeyModifiers.Win,
        _ => HotkeyModifiers.None,
    };

    private static bool IsFunctionKey(string key) =>
        key.Length is 2 or 3 && key[0] == 'F' && int.TryParse(key[1..], out int n) && n is >= 1 and <= 24;
}
