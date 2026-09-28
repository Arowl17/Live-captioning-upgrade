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

    public static IEnumerable<object[]> Seeds() =>
        Enumerable.Range(1, int.TryParse(Environment.GetEnvironmentVariable("SIMULATION_SEEDS"), out int count) ? count : 50).Select(seed => new object[] { seed });

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

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Punctuation_changes_and_merged_sentences_lose_or_repeat_no_words(int seed)
    {
        var random = new Random(seed);
        var script = Enumerable.Range(0, 50)
            .Select(i => i % 6 == 0 ? RandomSentence(random, 20, 40) : RandomSentence(random, 2, 12))
            .ToList();
        var options = new SimulationOptions { MergeChance = 0.15, CommaChance = 0.1, TerminatorChangeChance = 0.1 };

        var (emitted, finalText) = Simulate(script, random, options);

        Assert.Equal(Words(finalText), Words(emitted));
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Text_briefly_going_blank_loses_nothing(int seed)
    {
        var random = new Random(seed);
        var script = Enumerable.Range(0, 40)
            .Select(i => i % 5 == 0 ? RandomSentence(random, 20, 40) : RandomSentence(random, 2, 12))
            .ToList();

        var (emitted, _) = Simulate(script, random, new SimulationOptions { BlankChance = 0.05 });

        Assert.Equal(script, emitted);
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Sentences_rewritten_after_closing_early_are_never_repeated(int seed)
    {
        // Reading out an address or a number, Live Captions often ends the sentence too soon ("Riverside, Texas
        // seven."), then rewrites it once it has heard more ("Riverside TX 75231 in the main."). The early version
        // has already been shown by then, which is fine; but the rewritten one must appear once, not over and over.
        var random = new Random(seed);
        var script = Enumerable.Range(0, 40)
            .Select(i => i % 4 == 0 ? RandomSentence(random, 6, 18) : RandomSentence(random, 1, 8))
            .ToList();
        var options = new SimulationOptions { RewriteChance = 0.35 };

        var (emitted, _) = Simulate(script, random, options);

        // No sentence is shown more often than it was said or shown early. (Also shown: the rest of a sentence whose
        // start was shown early, once.)
        foreach (var shownSentence in emitted.GroupBy(sentence => sentence))
        {
            int saidTimes = script.Concat(options.EarlyVersions).Count(sentence => sentence == shownSentence.Key);
            Assert.True(shownSentence.Count() <= Math.Max(saidTimes, 1),
                $"\"{shownSentence.Key}\" was shown {shownSentence.Count()} times\nSHOWN: " + string.Join(" | ", emitted));
        }

        // Nothing said is lost apart from the word that was misheard in each early version, and nothing extra is
        // shown apart from the early versions themselves.
        string[] said = Words(script);
        string[] shown = Words(emitted);
        int common = CommonSubsequenceLength(said, shown);
        Assert.True(said.Length - common <= options.Rewrites, $"Lost {said.Length - common} words in {options.Rewrites} rewrites");
        Assert.True(shown.Length - common <= options.EarlyWords, $"{shown.Length - common} extra words, early versions had {options.EarlyWords}");
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Numbers_read_out_digit_by_digit_are_never_repeated(int seed)
    {
        // A phone number, card number or code read out a digit at a time: Live Captions shows each digit as a sentence
        // of its own, joins them ("Seven, seven, two."), and rewrites them as digits ("772."), in chunks.
        var random = new Random(seed);
        var script = Enumerable.Range(0, 40)
            .Select(i => random.NextDouble() < 0.4 ? RandomNumber(random) : RandomSentence(random, 2, 12))
            .ToList();
        var options = new SimulationOptions { DigitByDigit = true, RewriteChance = 0.05 };

        var (emitted, _) = Simulate(script, random, options);

        // Spelled-out digits ("zero.", "Seven, seven.") may show up while a number is read out with pauses; anything
        // else (sentences, and numbers in digits) no more often than said or shown early.
        foreach (var shownSentence in emitted.GroupBy(sentence => sentence).Where(g => !IsSpelledDigits(g.Key)))
        {
            int saidTimes = script.Concat(options.EarlyVersions).Count(sentence => sentence == shownSentence.Key);
            Assert.True(shownSentence.Count() <= Math.Max(saidTimes, 1),
                $"\"{shownSentence.Key}\" was shown {shownSentence.Count()} times\nSHOWN: " + string.Join(" | ", emitted));
        }

        string[] said = Words(script);
        string[] shown = Words(emitted);
        int common = CommonSubsequenceLength(said, shown);
        Assert.True(said.Length - common <= options.Rewrites, $"Lost {said.Length - common} words\nSHOWN: " + string.Join(" | ", emitted));
        Assert.True(shown.Length - common <= options.EarlyWords, $"{shown.Length - common} extra words, early versions had {options.EarlyWords}");
    }

    [Fact]
    public void Sentences_that_differ_only_in_numbers_are_all_kept()
    {
        var random = new Random(3);
        var script = new List<string>
        {
            "Can I have your phone number please?", "My number is 555 0134.", "Sorry, my number is 555 0135.",
            "So that is 555 0135?", "Yes.", "And your order number is 48213?", "My order number is 48213.",
            "My order number is 48231.", "Sorry, it's 48231.", "Thank you.", "The total is 23 dollars and 40 cents.",
            "The total is 32 dollars and 40 cents.",
        };

        Assert.Equal(script, Simulate(script, random, new SimulationOptions { LineWidth = 60 }).Emitted);
    }

    [Fact]
    public void Titles_and_initials_do_not_split_sentences()
    {
        var random = new Random(5);
        var script = new List<string>
        {
            "Hello, this is Mr. Smith from billing.", "I spoke to Dr. Jones yesterday.", "My name is John A. Smith.",
            "Mrs. Brown and Ms. Green are on the account.", "Please call back after 3 p.m. today.", "Okay.",
        };

        Assert.Equal(script, Simulate(script, random, new SimulationOptions()).Emitted);
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

    private static List<string> Run(IReadOnlyList<string> script, Random random, int lineWidth, int maxLines, bool harsh = false) =>
        Simulate(script, random, new SimulationOptions { LineWidth = lineWidth, MaxLines = maxLines, Harsh = harsh }).Emitted;

    /// <summary>
    /// Plays <paramref name="script"/> through a simulated Live Captions and returns what the tracker emitted,
    /// plus the text Live Captions finally settled on (it may have changed punctuation along the way).
    /// </summary>
    private static (List<string> Emitted, string FinalText) Simulate(IReadOnlyList<string> script, Random random, SimulationOptions options)
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
            if (random.NextDouble() < options.BlankChance)
            {
                // Live Captions momentarily shows no text.
                emitted.Apply(tracker.Process(string.Empty, now));
                now = now.AddMilliseconds(150);
            }

            emitted.Apply(tracker.Process(Visible(words, options.LineWidth, options.MaxLines), now));
        }

        foreach (string sentence in script)
        {
            int sentenceStart = words.Count;
            if (options.DigitByDigit && sentence.Length > 1 && sentence[..^1].All(char.IsDigit))
            {
                ReadOutDigits(sentence[..^1]);
                continue;
            }

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
                else if (options.Harsh && w > 0 && random.NextDouble() < 0.1)
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

                if (w + 2 < sentenceWords.Length && random.NextDouble() < options.RewriteChance)
                {
                    // Live Captions ends the sentence too soon, the last word misheard as a number...
                    string heard = words[^1];
                    string misheard = NumberWords[random.Next(NumberWords.Length)];
                    words[^1] = (w == 0 ? char.ToUpperInvariant(misheard[0]) + misheard[1..] : misheard) + ".";
                    options.EarlyVersions.Add(string.Join(" ", words.Skip(sentenceStart)));
                    Show(random.NextDouble() < 0.5 ? 1500 : 150);
                    options.Rewrites++;
                    options.EarlyWords += w + 1;

                    // ...the next word looks like the start of a new sentence...
                    string next = sentenceWords[w + 1];
                    words.Add(char.ToUpperInvariant(next[0]) + next[1..]);
                    Show();

                    // ...then it rewrites the whole thing as one sentence.
                    words[^2] = heard;
                    words[^1] = next;
                    Show();
                    w++;
                    continue;
                }

                if (w == 0 && sentenceStart > 0 && random.NextDouble() < options.MergeChance)
                {
                    // The previous sentence looked finished, but Live Captions decides this one continues it:
                    // "I called yesterday. And" becomes "I called yesterday, and".
                    Show();
                    words[sentenceStart - 1] = words[sentenceStart - 1].TrimEnd('.', '?', '!') + ",";
                    words[sentenceStart] = char.ToLowerInvariant(words[sentenceStart][0]) + words[sentenceStart][1..];
                }
                else if (w == 1 && sentenceStart > 0 && random.NextDouble() < options.TerminatorChangeChance)
                {
                    // The previous sentence turns out to be a question, or not.
                    string previous = words[sentenceStart - 1];
                    char end = previous[^1];
                    if (end is '.' or '?')
                    {
                        words[sentenceStart - 1] = previous[..^1] + (end == '.' ? "?" : ".");
                    }
                }

                if (w > 1 && random.NextDouble() < options.CommaChance)
                {
                    // A comma appears after an earlier word of the sentence being spoken.
                    int target = sentenceStart + random.Next(w - 1);
                    if (char.IsLetterOrDigit(words[target][^1]))
                    {
                        words[target] += ",";
                    }
                }

                // Live Captions sometimes adds a few words in one update (at most 3 within one 150 ms poll).
                if (!options.Harsh || isLast || ++unseenWords >= 3 || random.NextDouble() < 0.5)
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
        emitted.Apply(tracker.Flush());
        return (emitted, string.Join(" ", words));

        void ReadOutDigits(string number)
        {
            int start = words.Count;
            int done = 0; // digits already rewritten as a number at the start
            for (int d = 0; d < number.Length; d++)
            {
                // The digit first looks like a sentence of its own...
                string digit = DigitWords[number[d] - '0'];
                bool closed = random.NextDouble() < 0.5;
                words.Add(char.ToUpperInvariant(digit[0]) + digit[1..] + (closed ? "." : string.Empty));
                if (closed)
                {
                    Early(words[^1]);
                }

                Show(random.NextDouble() < 0.3 ? 1500 : 150);

                // ...then it's joined to the number so far, and now and then the lot is rewritten as digits.
                words.RemoveRange(start, words.Count - start);
                if (random.NextDouble() < 0.4)
                {
                    done = d + 1;
                }

                if (done > 0)
                {
                    words.Add(number[..done] + ".");
                    Early(words[^1]);
                }

                var spelled = number[done..(d + 1)].Select(c => DigitWords[c - '0']).ToList();
                for (int w = 0; w < spelled.Count; w++)
                {
                    string word = w == 0 ? char.ToUpperInvariant(spelled[w][0]) + spelled[w][1..] : spelled[w];
                    words.Add(word + (w < spelled.Count - 1 ? "," : "."));
                }

                if (spelled.Count > 0)
                {
                    Early(string.Join(" ", words.Skip(words.Count - spelled.Count)));
                }

                Show();
            }

            words.RemoveRange(start, words.Count - start);
            words.Add(number + ".");
            Show();
        }

        // A version of the number that looks finished and may be emitted before Live Captions settles on the final one.
        void Early(string text)
        {
            options.EarlyVersions.Add(text);
            options.EarlyWords += text.Split(' ').Length;
        }
    }

    /// <summary>The words of some text, ignoring case and punctuation.</summary>
    private static string[] Words(string text) =>
        text.Split(' ').Select(TextSimilarity.Normalize).Where(word => word.Length > 0).ToArray();

    private static string[] Words(IEnumerable<string> sentences) => Words(string.Join(" ", sentences));

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

    private sealed class SimulationOptions
    {
        public int LineWidth { get; init; } = 70;

        public int MaxLines { get; init; } = 3;

        public bool Harsh { get; init; }

        public double MergeChance { get; init; }

        public double CommaChance { get; init; }

        public double TerminatorChangeChance { get; init; }

        public double BlankChance { get; init; }

        public double RewriteChance { get; init; }

        public bool DigitByDigit { get; init; }

        /// <summary>Counted while simulating: sentences ended too soon and rewritten, and the words of their early versions.</summary>
        public int Rewrites { get; set; }

        public int EarlyWords { get; set; }

        public List<string> EarlyVersions { get; } = new();
    }

    private static readonly string[] NumberWords = { "seven", "five", "two", "three", "nine", "zero" };

    private static readonly string[] DigitWords = { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine" };

    private static bool IsSpelledDigits(string sentence) =>
        Words(sentence).All(word => DigitWords.Contains(word));

    private static string RandomNumber(Random random) =>
        string.Concat(Enumerable.Range(0, random.Next(2, 8)).Select(_ => (char)('0' + random.Next(10)))) + ".";

    /// <summary>Length of the longest sequence of words appearing in both, in the same order.</summary>
    private static int CommonSubsequenceLength(string[] a, string[] b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                current[j] = a[i - 1] == b[j - 1] ? previous[j - 1] + 1 : Math.Max(previous[j], current[j - 1]);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    private static string RandomSentence(Random random, int minWords, int maxWords)
    {
        int count = random.Next(minWords, maxWords + 1);
        var words = Enumerable.Range(0, count).Select(_ => Vocabulary[random.Next(Vocabulary.Length)]).ToList();
        words[0] = char.ToUpperInvariant(words[0][0]) + words[0][1..];
        return string.Join(" ", words) + (random.NextDouble() < 0.2 ? "?" : ".");
    }
}
