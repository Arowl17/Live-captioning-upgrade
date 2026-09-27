using LiveCaptionsUpgrade.Core;

namespace LiveCaptionsUpgrade.Core.Tests;

public class CaptionTrackerTests
{
    private static readonly TimeSpan Idle = TimeSpan.FromMilliseconds(1000);
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly CaptionTracker _tracker = new(Idle);
    private readonly List<string> _emitted = new();
    private DateTimeOffset _now = T0;

    private CaptionUpdate Feed(string text, int advanceMs = 150)
    {
        _now = _now.AddMilliseconds(advanceMs);
        var update = _tracker.Process(text, _now);
        _emitted.AddRange(update.NewSentences);
        return update;
    }

    [Fact]
    public void Words_being_spoken_stay_pending()
    {
        var update = Feed("Hello every");

        Assert.Empty(update.NewSentences);
        Assert.Equal("Hello every", update.Pending);
        Assert.True(update.TextChanged);
    }

    [Fact]
    public void Sentence_is_final_once_the_next_one_starts()
    {
        Feed("Hello everyone");
        Feed("Hello everyone.");
        var update = Feed("Hello everyone. Today we");

        Assert.Equal(new[] { "Hello everyone." }, update.NewSentences);
        Assert.Equal("Today we", update.Pending);
    }

    [Fact]
    public void Punctuated_last_sentence_is_final_after_idle_delay()
    {
        Feed("Thanks for watching.");
        Assert.Empty(_emitted);

        var update = Feed("Thanks for watching.", advanceMs: 1100);

        Assert.Equal(new[] { "Thanks for watching." }, update.NewSentences);
        Assert.Equal(string.Empty, update.Pending);
        Assert.False(update.TextChanged);
    }

    [Fact]
    public void Unpunctuated_last_sentence_is_not_finalised_by_idle()
    {
        Feed("And then we");
        var update = Feed("And then we", advanceMs: 5000);

        Assert.Empty(update.NewSentences);
        Assert.Equal("And then we", update.Pending);
    }

    [Fact]
    public void Idle_finalised_sentence_is_not_emitted_again_when_speech_continues()
    {
        Feed("First point.");
        Feed("First point.", advanceMs: 1100);
        Feed("First point. Second");
        Feed("First point. Second point. Third");

        Assert.Equal(new[] { "First point.", "Second point." }, _emitted);
    }

    [Fact]
    public void Nothing_is_duplicated_when_old_lines_scroll_off_the_top()
    {
        Feed("One is here. Two is here. Three");
        Feed("Two is here. Three is here. Four");
        Feed("Three is here. Four is here. Five");

        Assert.Equal(new[] { "One is here.", "Two is here.", "Three is here.", "Four is here." }, _emitted);
    }

    [Fact]
    public void Front_truncated_fragment_matches_the_committed_sentence()
    {
        Feed("We were going to the grocery store together. Then");
        Feed("going to the grocery store together. Then we left. After");

        Assert.Equal(new[] { "We were going to the grocery store together.", "Then we left." }, _emitted);
    }

    [Fact]
    public void Small_revision_of_a_committed_sentence_is_not_emitted_again()
    {
        Feed("I think their going home. We");
        Feed("I think they're going home. We should go too. Now");

        Assert.Equal(new[] { "I think their going home.", "We should go too." }, _emitted);
    }

    [Fact]
    public void Repeated_short_sentence_is_emitted_each_time()
    {
        Feed("Are you ready? Yes. OK");
        Feed("Are you ready? Yes. Okay then. Yes. Go");

        Assert.Equal(new[] { "Are you ready?", "Yes.", "Okay then.", "Yes." }, _emitted);
    }

    [Fact]
    public void Several_sentences_arriving_at_once_are_all_emitted_in_order()
    {
        var update = Feed("A cat sat. A dog ran. A bird flew. And");

        Assert.Equal(new[] { "A cat sat.", "A dog ran.", "A bird flew." }, update.NewSentences);
    }

    [Fact]
    public void Line_breaks_from_live_captions_are_treated_as_spaces()
    {
        Feed("This is the first\nline of text. And the");

        Assert.Equal(new[] { "This is the first line of text." }, _emitted);
    }

    [Fact]
    public void Flush_emits_the_unfinished_sentence()
    {
        Feed("Done here. Goodbye every");

        var flushed = _tracker.Flush();

        Assert.Equal(new[] { "Goodbye every" }, flushed);
        Assert.Equal(new[] { "Done here." }, _emitted);
    }

    [Fact]
    public void Flush_after_everything_is_final_emits_nothing()
    {
        Feed("All done.");
        Feed("All done.", advanceMs: 1100);

        Assert.Empty(_tracker.Flush());
    }

    [Fact]
    public void Cleared_text_followed_by_new_speech_is_emitted()
    {
        Feed("Old topic. Old");
        Feed(string.Empty);
        Feed("Brand new subject. More");

        Assert.Equal(new[] { "Old topic.", "Brand new subject." }, _emitted);
    }

    [Fact]
    public void Unchanged_text_reports_no_change()
    {
        Feed("Hello there");
        var update = Feed("Hello there");

        Assert.False(update.TextChanged);
        Assert.Empty(update.NewSentences);
        Assert.Equal("Hello there", update.Pending);
    }

    [Fact]
    public void Reset_forgets_committed_sentences()
    {
        Feed("Same words. Next");
        _tracker.Reset();
        Feed("Same words. Next");

        Assert.Equal(new[] { "Same words.", "Same words." }, _emitted);
    }
}
