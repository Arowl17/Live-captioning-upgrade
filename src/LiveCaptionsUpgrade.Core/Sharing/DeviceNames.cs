using System.Text;

namespace LiveCaptionsUpgrade.Core.Sharing;

/// <summary>Cleans up names received from the network before they are shown or stored.</summary>
public static class DeviceNames
{
    public const int MaxLength = 40;

    public static string Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "Unnamed computer";
        }

        var builder = new StringBuilder(Math.Min(name.Length, MaxLength));
        foreach (char c in name)
        {
            if (builder.Length >= MaxLength)
            {
                break;
            }

            // Control characters and invisible direction overrides could make a name look like another one.
            if (!char.IsControl(c) && c is not ('​' or '‎' or '‏' or '‪' or '‫' or '‬' or '‭' or '‮'))
            {
                builder.Append(c);
            }
        }

        string cleaned = builder.ToString().Trim();
        return cleaned.Length == 0 ? "Unnamed computer" : cleaned;
    }
}
