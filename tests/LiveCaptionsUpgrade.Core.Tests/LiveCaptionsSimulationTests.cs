using System.Text;
using LiveCaptionsUpgrade.Core;

namespace LiveCaptionsUpgrade.Core.Tests;

/// <summary>
/// End-to-end checks of <see cref="CaptionTracker"/> against a simulation of how Windows Live Captions
/// updates its text: words arrive one at a time, the newest word is sometimes corrected, punctuation
/// appears a moment after the last word, and only the last few wrapped lines stay visible.
/// </summary>
public class LiveCaptionsSimulationTests
{
    private static readonly string[] Vocabulary =
    {
        "account", "address", "after", "again", "billing", "call", "card", "check", "customer", "delivery",
        "email", "every", "number", "order", "package", "payment", "phone", "please", "problem", "refund",
        "received", "remember", "service", "shipping", "should", "something", "support", "thank", "their",
        "there", "think", "tomorrow", "tracking", "update", "waiting", "week", "where", "would", "yesterday",
        "about", "because", "before", "could", "maybe", "never", "really", "still", "today", "through", "while",
    };

    public static IEnumerable<object[]> Seeds() => Enumerable.Range(1, 50).Select(seed => new object[] { seed });

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Every_sentence_is_emitted_exactly_once(int seed)
    {
        var random = new Random(seed);
        var script = Enumerable.Range(0, 60).Select(_ => RandomSentence(random, 2, 16)).ToList();

        var emitted = Run(script, random, lineWidth: 70, maxLines: 3);

        Assert.Equal(script, emitted);
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Sentences_longer_than_the_visible_text_are_not_cut_short(int seed)
    {
        var random = new Random(seed);
        var script = Enumerable.Range(0, 12)
            .Select(i => i % 3 == 0 ? RandomSentence(random, 40, 70) : RandomSentence(random, 3, 12))
            .ToList();

        var emitted = Run(script, random, lineWidth: 60, maxLines: 3);

        Assert.Equal(script, emitted);
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Tiny_window_with_bursts_of_words_and_late_corrections(int seed)
    {
        var random = new Random(seed);
        var script = Enumerable.Range(0, 40)
            .Select(i => i % 5 == 0 ? RandomSentence(random, 25, 45) : RandomSentence(random, 1, 10))
            .ToList();

        var emitted = Run(script, random, lineWidth: 45, maxLines: 2, harsh: true);

        Assert.Equal(script, emitted);
    }

    [Fact]
    public void Short_replies_and_repeats_are_all_kept()
    {
        var random = new Random(7);
        var script = new List<string>
        {
            "Can you hear me?", "Yes.", "Okay.", "Yes.", "Is the order number correct?", "Yes.", "Yes.",
            "Thank you.", "Thank you.", "Okay.", "Can you hear me?", "Yes.",
        };

        var emitted = Run(script, random, lineWidth: 50, maxLines: 3);

        Assert.Equal(script, emitted);
    }

    private static List<string> Run(IReadOnlyList<string> script, Random random, int lineWidth, int maxLines, bool harsh = false)
    {
        var tracker = new CaptionTracker(TimeSpan.FromMilliseconds(1200));
        var emitted = new List<string>();
        var words = new List<string>();
        var now = new DateTimeOffset(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);
        int unseenWords = 0;

        void Show(int pauseMs = 150)
        {
            unseenWords = 0;
            now = now.AddMilliseconds(pauseMs);
            emitted.AddRange(tracker.Process(Visible(words, lineWidth, maxLines), now).NewSentences);
        }

        foreach (string sentence in script)
        {
            string[] sentenceWords = sentence.Split(' ');
            for (int w = 0; w < sentenceWords.Length; w++)
            {
                string word = sentenceWords[w];
                bool isLast = w == sentenceWords.Length - 1;
                string bare = isLast ? word.TrimEnd('.', '?', '!') : word;

                // Sometimes the newest word is misheard first, then corrected.
                if (random.NextDouble() < 0.15)
                {
                    words.Add(bare + "ing");
                    Show();
                    words[^1] = bare;
                }
                else if (harsh && w > 0 && random.NextDouble() < 0.1)
                {
                    // Or the word before it is corrected once the next word is heard.
                    string previous = words[^1];
                    words[^1] = previous + "s";
                    words.Add(bare);
                    Show();
                    words[^2] = previous;
                }
                else
                {
                    words.Add(bare);
                }

                // Live Captions sometimes adds a few words in one update (at most 3 within one 150 ms poll).
                if (!harsh || isLast || ++unseenWords >= 3 || random.NextDouble() < 0.5)
                {
                    Show();
                }
                if (isLast)
                {
                    // Punctuation arrives a moment after the last word.
                    words[^1] = word;
                    Show();
                }

                // Occasional pauses, long enough to finalise a punctuated sentence.
                if (random.NextDouble() < 0.1)
                {
                    Show(1500);
                    Show();
                }
            }
        }

        Show(2000);
        emitted.AddRange(tracker.Flush());
        return emitted;
    }

    /// <summary>What Live Captions shows: the text word-wrapped, keeping only the last few lines.</summary>
    private static string Visible(List<string> words, int lineWidth, int maxLines)
    {
        var lines = new List<string>();
        var line = new StringBuilder();
        foreach (string word in words)
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > lineWidth)
            {
                lines.Add(line.ToString());
                line.Clear();
            }

            if (line.Length > 0)
            {
                line.Append(' ');
            }

            line.Append(word);
        }

        if (line.Length > 0)
        {
            lines.Add(line.ToString());
        }

        return string.Join("\n", lines.Skip(Math.Max(0, lines.Count - maxLines)));
    }

    private static string RandomSentence(Random random, int minWords, int maxWords)
    {
        int count = random.Next(minWords, maxWords + 1);
        var words = Enumerable.Range(0, count).Select(_ => Vocabulary[random.Next(Vocabulary.Length)]).ToList();
        words[0] = char.ToUpperInvariant(words[0][0]) + words[0][1..];
        return string.Join(" ", words) + (random.NextDouble() < 0.2 ? "?" : ".");
    }
}
