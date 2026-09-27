using System.Text;

namespace LiveCaptionsUpgrade.Core;

/// <summary>Fuzzy text comparison, used to recognise a sentence after Live Captions has slightly revised it.</summary>
public static class TextSimilarity
{
    /// <summary>Lower-cases text and drops punctuation so "Hello, there." and "hello there" compare equal.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        bool pendingSpace = false;
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                if (pendingSpace && sb.Length > 0)
                {
                    sb.Append(' ');
                }

                pendingSpace = false;
                sb.Append(char.ToLowerInvariant(c));
            }
            else if (char.IsWhiteSpace(c))
            {
                pendingSpace = true;
            }
        }

        return sb.ToString();
    }

    /// <summary>Returns 1.0 for identical strings, falling towards 0.0 as the edit distance grows.</summary>
    public static double Ratio(string a, string b)
    {
        int max = Math.Max(a.Length, b.Length);
        if (max == 0)
        {
            return 1.0;
        }

        return 1.0 - (double)Levenshtein(a, b) / max;
    }

    public static int Levenshtein(string a, string b)
    {
        if (a.Length == 0)
        {
            return b.Length;
        }

        if (b.Length == 0)
        {
            return a.Length;
        }

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
