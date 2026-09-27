using LiveCaptionsUpgrade.Core;

namespace LiveCaptionsUpgrade.Core.Tests;

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+H", HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, "H")]
    [InlineData(" control + shift + F5 ", HotkeyModifiers.Ctrl | HotkeyModifiers.Shift, "F5")]
    [InlineData("Win+Alt+1", HotkeyModifiers.Win | HotkeyModifiers.Alt, "D1")]
    [InlineData("F9", HotkeyModifiers.None, "F9")]
    public void Parses_valid_shortcuts(string text, HotkeyModifiers modifiers, string key)
    {
        Assert.True(Hotkey.TryParse(text, out var hotkey));
        Assert.Equal(new Hotkey(modifiers, key), hotkey);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("H")]
    [InlineData("Ctrl+Alt")]
    [InlineData("Ctrl+H+J")]
    public void Rejects_invalid_shortcuts(string? text)
    {
        Assert.False(Hotkey.TryParse(text, out _));
    }

    [Fact]
    public void Formats_modifiers_in_a_fixed_order_and_digits_plainly()
    {
        var hotkey = new Hotkey(HotkeyModifiers.Shift | HotkeyModifiers.Alt | HotkeyModifiers.Ctrl, "D3");

        Assert.Equal("Ctrl+Alt+Shift+3", hotkey.ToString());
        Assert.True(Hotkey.TryParse(hotkey.ToString(), out var roundTripped));
        Assert.Equal(hotkey, roundTripped);
    }

    [Fact]
    public void Letter_without_modifier_is_not_valid()
    {
        Assert.False(new Hotkey(HotkeyModifiers.None, "H").IsValid);
        Assert.True(new Hotkey(HotkeyModifiers.None, "F12").IsValid);
    }
}
