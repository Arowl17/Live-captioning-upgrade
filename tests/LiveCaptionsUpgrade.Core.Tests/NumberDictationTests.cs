using System.Text;

namespace LiveCaptionsUpgrade.Core.Tests;

/// <summary>
/// Customers reading out card numbers, phone numbers, zip codes and codes, the way Windows Live Captions shows
/// them: each digit first as a sentence of its own, then joined with commas, rewritten as digits a group at a time,
/// groups merged, with pauses between groups. What matters is that the history ends up with exactly the digits
/// said, in order: nothing missing, nothing extra.
/// </summary>
public class NumberDictationTests
{
    private static readonly string[] DigitWords = { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine" };

    private static readonly string[] Vocabulary =
    {
        "account", "address", "after", "again", "billing", "call", "card", "check", "customer", "delivery", "email",
        "every", "number", "order", "package", "payment", "phone", "please", "problem", "refund", "received",
        "service", "shipping", "something", "support", "thank", "their", "tomorrow", "tracking", "update", "waiting",
        "where", "would", "yesterday", "about", "because", "before", "could", "maybe", "really", "still", "today",
    };

    private static readonly string[][] Prefixes =
    {
        new[] { "my", "card", "number", "is" }, new[] { "it's" }, new[] { "the", "phone", "number", "is" },
        new[] { "zip", "code" }, new[] { "apartment" }, new[] { "the", "code", "is" },
    };

    private static readonly string[][] Suffixes = { new[] { "please" }, new[] { "and", "the", "expiry" }, new[] { "right" } };

    private static readonly string[][] Streets =
    {
        new[] { "Maple", "Grove", "Boulevard," }, new[] { "Main", "Street," }, new[] { "Oak", "Lawn", "Avenue," },
        new[] { "Elm", "Street", "apartment", "b," },
    };

    private static readonly string[][] Cities = { new[] { "Riverside,", "Texas" }, new[] { "Irving,", "TX" }, new[] { "Plano" } };

    public static IEnumerable<object[]> Seeds() => (Environment.GetEnvironmentVariable("DICTATION_SEED") is { } only
        ? new[] { int.Parse(only) }
        : Enumerable.Range(1, int.TryParse(Environment.GetEnvironmentVariable("DICTATION_SEEDS"), out int count) ? count : 100)).Select(seed => new object[] { seed });

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Every_digit_said_ends_up_in_the_history_once_and_in_order(int seed)
    {
        var call = new Call(seed, maxPauseMs: 2500);
        call.Play(utterances: 30);
        call.AssertHistoryMatches();
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Long_pauses_in_the_middle_of_a_number_lose_and_repeat_nothing(int seed)
    {
        var call = new Call(seed, maxPauseMs: 6000);
        call.Play(utterances: 30);
        call.AssertHistoryMatches();
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Card_numbers_spelled_out_in_a_tiny_window_lose_nothing(int seed)
    {
        // Live Captions doesn't get round to rewriting the digits, and its window shows less than a card number
        // spelled out. That's more than can always be sorted out, but no digit may go missing.
        var call = new Call(seed, maxPauseMs: 2500, lineWidth: 45, maxLines: 2, convertChance: 0.05, longNumbers: true);
        call.Play(utterances: 20);
        call.AssertNoDigitLost();
    }

    [Fact]
    public void Card_numbers_spelled_out_in_a_tiny_window_are_nearly_always_shown_just_once()
    {
        int repeated = 0;
        for (int seed = 1; seed <= 100; seed++)
        {
            var call = new Call(seed, maxPauseMs: 2500, lineWidth: 45, maxLines: 2, convertChance: 0.05, longNumbers: true);
            call.Play(utterances: 20);
            repeated += call.HistoryMatches ? 0 : 1;
        }

        Assert.True(repeated <= 8, $"{repeated} calls in 100 had digits shown twice");
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void A_number_before_anything_else_is_said_loses_nothing(int seed)
    {
        // Straight after starting (or Live Captions restarting) there's nothing emitted before the number to tell a
        // rewrite of its first digits from a new number, so they may show twice; but none may go missing.
        var call = new Call(seed, maxPauseMs: 2500, lineWidth: 45, maxLines: 2, convertChance: 0.05, longNumbers: true, greeting: false);
        call.Play(utterances: 3);
        call.AssertNoDigitLost();
    }

    /// <summary>One simulated call.</summary>
    private sealed class Call
    {
        private readonly Random _random;
        private readonly CaptionTracker _tracker = new(TimeSpan.FromMilliseconds(1200));
        private readonly List<string> _words = new();
        private readonly List<string> _emitted = new();
        private readonly List<string> _said = new();
        private readonly StringBuilder _log = new();
        private readonly int _maxPauseMs;
        private readonly int _lineWidth;
        private readonly int _maxLines;
        private readonly double _convertChance;
        private readonly bool _longNumbers;
        private readonly bool _greeting;
        private DateTimeOffset _now = new(2026, 1, 1, 9, 0, 0, TimeSpan.Zero);

        public Call(int seed, int maxPauseMs, int lineWidth = 0, int maxLines = 0, double convertChance = 0.5, bool longNumbers = false, bool greeting = true)
        {
            _greeting = greeting;
            _random = new Random(seed);
            _maxPauseMs = maxPauseMs;
            (_lineWidth, _maxLines) = lineWidth > 0 ? (lineWidth, maxLines) : new[] { (45, 2), (60, 3), (80, 3), (100, 2), (150, 6) }[_random.Next(5)];
            _convertChance = convertChance;
            _longNumbers = longNumbers;
        }

        public void Play(int utterances)
        {
            if (_greeting)
            {
                SaySentence();
            }

            for (int u = 0; u < utterances; u++)
            {
                if (_random.NextDouble() < 0.5)
                {
                    SaySentence();
                }
                else
                {
                    SayNumber();
                }
            }

            Show(3000);
            Show(8000);
            Apply(_tracker.Flush());
        }

        public void AssertHistoryMatches()
        {
            string? dump = Environment.GetEnvironmentVariable("DICTATION_LOG");
            if (dump != null)
            {
                File.WriteAllText(dump, _log.ToString());
            }

            string saidDigits = Digits(_said);
            string shownDigits = Digits(_emitted);
            string saidWords = string.Join(" ", OtherWords(_said));
            string shownWords = string.Join(" ", OtherWords(_emitted));
            Assert.True(saidDigits == shownDigits && saidWords == shownWords,
                $"Digits said:  {saidDigits}\nDigits shown: {shownDigits}\nWords said:  {saidWords}\nWords shown: {shownWords}\n"
                + $"Window {_lineWidth}x{_maxLines}\nSAID: {string.Join(" | ", _said)}\nSHOWN: {string.Join(" | ", _emitted)}\n\nLOG (last):\n{Tail(_log.ToString(), 6000)}");
        }

        public bool HistoryMatches =>
            Digits(_said) == Digits(_emitted) && OtherWords(_said).SequenceEqual(OtherWords(_emitted));

        public void AssertNoDigitLost()
        {
            string saidDigits = Digits(_said);
            string shownDigits = Digits(_emitted);
            int shown = 0;
            foreach (char digit in saidDigits)
            {
                shown = shownDigits.IndexOf(digit, shown) + 1;
                Assert.True(shown > 0, $"Digits said:  {saidDigits}\nDigits shown: {shownDigits}\nSHOWN: {string.Join(" | ", _emitted)}\n\nLOG (last):\n{Tail(_log.ToString(), 6000)}");
            }
        }

        private static string Tail(string text, int length) => text.Length <= length ? text : text[^length..];

        private void SaySentence()
        {
            int count = _random.Next(2, 9);
            var sentenceWords = Enumerable.Range(0, count).Select(_ => Vocabulary[_random.Next(Vocabulary.Length)]).ToList();
            sentenceWords[0] = char.ToUpperInvariant(sentenceWords[0][0]) + sentenceWords[0][1..];
            string end = _random.NextDouble() < 0.2 ? "?" : ".";
            _said.Add(string.Join(" ", sentenceWords) + end);

            for (int w = 0; w < count; w++)
            {
                if (_random.NextDouble() < 0.1)
                {
                    _words.Add(sentenceWords[w] + "ing"); // misheard, then corrected
                    Show();
                    _words[^1] = sentenceWords[w];
                }
                else
                {
                    _words.Add(sentenceWords[w]);
                }

                Show();
            }

            _words[^1] += end;
            Show();
        }

        /// <summary>
        /// A number read out in groups ("4111 1111 1111 1111", "330 612 7750", "75231"), digit by digit, on its own,
        /// in a sentence ("My card number is ...") or in an address.
        /// </summary>
        private void SayNumber()
        {
            int[] groupSizes = _longNumbers
                ? new[] { 4, 4, 4, 4 }
                : _random.Next(5) switch
                {
                    0 => new[] { 4, 4, 4, 4 },
                    1 => new[] { 3, 3, 4 },
                    2 => new[] { 5 },
                    3 => new[] { 4 },
                    _ => new[] { 2, 2 },
                };
            var number = new Part(Groups: RandomGroups(groupSizes));
            switch (_longNumbers ? 0 : _random.Next(4))
            {
                case 1:
                    var suffix = _random.NextDouble() < 0.5 ? new[] { new Part(Words: Pick(Suffixes)) } : Array.Empty<Part>();
                    SayUtterance(new[] { new Part(Words: Pick(Prefixes)), number }.Concat(suffix).ToArray());
                    break;
                case 2:
                    SayUtterance(
                        new Part(Groups: RandomGroups(new[] { _random.Next(3, 5) })),
                        new Part(Words: Pick(Streets)),
                        new Part(Words: Pick(Cities)),
                        new Part(Groups: RandomGroups(new[] { 5 })));
                    break;
                default:
                    SayUtterance(number);
                    break;
            }
        }

        private List<string> RandomGroups(int[] sizes) =>
            sizes.Select(size => string.Concat(Enumerable.Range(0, size).Select(_ => (char)('0' + _random.Next(10))))).ToList();

        private string[] Pick(string[][] choices) => choices[_random.Next(choices.Length)];

        /// <summary>
        /// Words and numbers said as one sentence, and what Live Captions makes of it along the way: each number as
        /// chunks, each spelled out ("seven, seven") or in digits ("77"), that it closes early ("Riverside, Texas
        /// seven."), rewrites as digits and joins, with the customer pausing between groups of digits.
        /// </summary>
        private void SayUtterance(params Part[] parts)
        {
            int start = _words.Count;
            var items = new List<object>();
            foreach (var part in parts)
            {
                if (part.Words is not null)
                {
                    foreach (string word in part.Words)
                    {
                        items.Add(word);
                        Render(start, items);
                        Show();
                    }

                    continue;
                }

                var chunks = new List<Chunk>();
                foreach (string group in part.Groups!)
                {
                    for (int d = 0; d < group.Length; d++)
                    {
                        // A new digit either starts a chunk that looks like a sentence of its own, or joins the last one.
                        if (chunks.Count == 0 || chunks[^1].Closed || d == 0 && _random.NextDouble() < 0.5 || _random.NextDouble() < 0.25)
                        {
                            // (A chunk followed by a new one has ended: "Five, zero. Nine".)
                            if (chunks.Count > 0)
                            {
                                chunks[^1].Closed = true;
                            }

                            chunks.Add(new Chunk(useOh: _random.NextDouble() < 0.3));
                            items.Add(chunks[^1]);
                        }

                        chunks[^1].Spelled.Add(group[d]);
                        Render(start, items);
                        Show(_random.Next(150, 450));

                        // Along the way, it closes chunks, rewrites them as digits and joins them.
                        if (_random.NextDouble() < 0.4)
                        {
                            chunks[^1].Closed = true;
                        }

                        if (_random.NextDouble() < _convertChance * (d == group.Length - 1 ? 1 : 0.3))
                        {
                            chunks[^1].Convert();
                        }

                        if (chunks.Count > 1 && _random.NextDouble() < 0.3)
                        {
                            var last = chunks[^1];
                            chunks.RemoveAt(chunks.Count - 1);
                            items.Remove(last);
                            chunks[^1].Join(last, space: d == 0);
                        }

                        Render(start, items);
                        Show();
                    }

                    // The customer pauses between groups.
                    Show(_random.NextDouble() < 0.7 ? _random.Next(200, 900) : _random.Next(900, _maxPauseMs));
                }
            }

            // In the end it settles on digits: a phone number maybe as "(330) 612-7750", a number on its own maybe
            // as a sentence per group.
            var words = new List<string>();
            foreach (var part in parts)
            {
                if (part.Words is not null)
                {
                    words.AddRange(part.Words);
                }
                else if (part.Groups!.Count == 3 && _random.NextDouble() < 0.5)
                {
                    words.Add($"({part.Groups[0]})");
                    words.Add($"{part.Groups[1]}-{part.Groups[2]}");
                }
                else
                {
                    words.AddRange(part.Groups);
                }
            }

            words[0] = char.ToUpperInvariant(words[0][0]) + words[0][1..];
            _said.Add(string.Join(" ", words) + ".");
            _words.RemoveRange(start, _words.Count - start);
            if (parts.Length == 1 && _random.NextDouble() < 0.5)
            {
                _words.AddRange(words.Select(word => word + "."));
            }
            else
            {
                words[^1] += ".";
                _words.AddRange(words);
            }

            Show();
        }

        /// <summary>Puts the words and number chunks said so far at the end of the text.</summary>
        private void Render(int start, List<object> items)
        {
            _words.RemoveRange(start, _words.Count - start);
            bool sentenceStart = true;
            foreach (var item in items)
            {
                if (item is string word)
                {
                    _words.Add(sentenceStart ? char.ToUpperInvariant(word[0]) + word[1..] : word);
                    sentenceStart = false;
                }
                else if (item is Chunk chunk && chunk.Words(sentenceStart) is { Count: > 0 } chunkWords)
                {
                    _words.AddRange(chunkWords);
                    sentenceStart = chunk.Closed;
                }
            }
        }

        private void Show(int pauseMs = 150)
        {
            _now = _now.AddMilliseconds(pauseMs);
            string text = Visible();
            var update = _tracker.Process(text, _now);
            _log.Append("TEXT: ").Append(text.Replace("\n", " / ")).Append('\n');
            Apply(update);
        }

        /// <summary>Updates the history the way the overlay does.</summary>
        private void Apply(CaptionUpdate update)
        {
            _emitted.Apply(update);
            if (update.NewSentences.Count > 0)
            {
                _log.Append(update.Replaced > 0 ? $"  EMIT (replacing {update.Replaced}): " : "  EMIT: ")
                    .Append(string.Join(" | ", update.NewSentences)).Append('\n');
            }
        }

        private string Visible()
        {
            var lines = new List<string>();
            var line = new StringBuilder();
            foreach (string word in _words)
            {
                if (line.Length > 0 && line.Length + 1 + word.Length > _lineWidth)
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

            return string.Join("\n", lines.Skip(Math.Max(0, lines.Count - _maxLines)));
        }
    }

    /// <summary>Words said, or a number read out in groups of digits.</summary>
    private sealed record Part(string[]? Words = null, List<string>? Groups = null);

    /// <summary>A piece of a number as Live Captions shows it for now.</summary>
    private sealed class Chunk
    {
        private readonly bool _useOh;

        public Chunk(bool useOh)
        {
            _useOh = useOh;
        }

        public List<char> Spelled { get; } = new();

        public string DigitsDone { get; private set; } = string.Empty;

        public bool Closed { get; set; }

        public void Convert()
        {
            DigitsDone += string.Concat(Spelled);
            Spelled.Clear();
        }

        public void Join(Chunk next, bool space)
        {
            if (next.DigitsDone.Length > 0 || Spelled.Count == 0 && DigitsDone.Length > 0 && next.Spelled.Count == 0)
            {
                Convert();
                next.Convert();
                DigitsDone = DigitsDone + (space ? " " : string.Empty) + next.DigitsDone;
            }
            else
            {
                Spelled.AddRange(next.Spelled);
            }

            Closed = next.Closed;
        }

        /// <summary>The chunk's words; the first one capitalised if it starts a sentence.</summary>
        public List<string> Words(bool sentenceStart)
        {
            var words = new List<string>();
            if (DigitsDone.Length > 0)
            {
                words.AddRange(DigitsDone.Split(' '));
            }

            for (int i = 0; i < Spelled.Count; i++)
            {
                string word = Spelled[i] == '0' && _useOh ? "oh" : DigitWords[Spelled[i] - '0'];
                if (i == 0 && DigitsDone.Length == 0 && sentenceStart)
                {
                    word = char.ToUpperInvariant(word[0]) + word[1..];
                }

                words.Add(i < Spelled.Count - 1 ? word + "," : word);
            }

            if (Closed && words.Count > 0)
            {
                words[^1] = words[^1].TrimEnd(',') + ".";
            }

            return words;
        }
    }

    /// <summary>All digits in the text, in order, with spelled-out digits as digits.</summary>
    private static string Digits(IEnumerable<string> sentences)
    {
        var digits = new StringBuilder();
        foreach (string word in TextSimilarity.Normalize(string.Join(" ", sentences)).Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int spelled = word == "oh" ? 0 : Array.IndexOf(DigitWords, word);
            if (spelled >= 0)
            {
                digits.Append((char)('0' + spelled));
            }
            else if (word.All(char.IsDigit))
            {
                digits.Append(word);
            }
        }

        return digits.ToString();
    }

    private static IEnumerable<string> OtherWords(IEnumerable<string> sentences) =>
        TextSimilarity.Normalize(string.Join(" ", sentences)).Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(word => word != "oh" && Array.IndexOf(DigitWords, word) < 0 && !word.All(char.IsDigit));
}
