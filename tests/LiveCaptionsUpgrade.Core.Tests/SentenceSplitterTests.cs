using LiveCaptionsUpgrade.Core;

namespace LiveCaptionsUpgrade.Core.Tests;

public class SentenceSplitterTests
{
    [Fact]
    public void Splits_on_sentence_punctuation_and_keeps_trailing_fragment()
    {
        var segments = SentenceSplitter.Split("Hi there! How are you? I am fine. And");

        Assert.Equal(
            new[]
            {
                new CaptionSegment("Hi there!", true),
                new CaptionSegment("How are you?", true),
                new CaptionSegment("I am fine.", true),
                new CaptionSegment("And", false),
            },
            segments);
    }

    [Theory]
    [InlineData("It costs 3.5 dollars")]
    [InlineData("Visit example.com today")]
    public void Does_not_split_inside_numbers_or_domains(string text)
    {
        var segments = SentenceSplitter.Split(text);

        Assert.Single(segments);
        Assert.False(segments[0].IsTerminated);
    }

    [Fact]
    public void Keeps_closing_quotes_with_the_sentence()
    {
        var segments = SentenceSplitter.Split("He said \"stop!\" Then left.");

        Assert.Equal("He said \"stop!\"", segments[0].Text);
        Assert.Equal("Then left.", segments[1].Text);
    }

    [Fact]
    public void Splits_cjk_text_without_spaces()
    {
        var segments = SentenceSplitter.Split("你好。今天天气很好！我们");

        Assert.Equal(new[] { "你好。", "今天天气很好！", "我们" }, segments.Select(s => s.Text));
    }

    [Theory]
    [InlineData("Hello, this is Mr. Smith from billing. Hi", "Hello, this is Mr. Smith from billing.")]
    [InlineData("I spoke to Dr. Jones. Ok", "I spoke to Dr. Jones.")]
    [InlineData("My name is John A. Smith. Ok", "My name is John A. Smith.")]
    [InlineData("Call after 3 p.m. today please. Ok", "Call after 3 p.m. today please.")]
    [InlineData("Call after 3 p.m. Thanks", "Call after 3 p.m.")]
    [InlineData("So did I. Then", "So did I.")]
    public void Titles_initials_and_abbreviations_do_not_end_sentences(string text, string firstSentence)
    {
        Assert.Equal(firstSentence, SentenceSplitter.Split(text)[0].Text);
    }

    [Fact]
    public void Empty_text_has_no_segments()
    {
        Assert.Empty(SentenceSplitter.Split("  \n "));
        Assert.Empty(SentenceSplitter.Split(null));
    }

    [Fact]
    public void Normalizes_whitespace()
    {
        Assert.Equal("a b c", SentenceSplitter.NormalizeWhitespace("  a\r\n b\t\tc  "));
    }
}
