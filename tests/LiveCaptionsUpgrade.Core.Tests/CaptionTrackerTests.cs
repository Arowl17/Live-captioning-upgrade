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
        _emitted.Apply(update);
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

        Assert.Equal(new[] { "Goodbye every" }, flushed.NewSentences);
        Assert.Equal(new[] { "Done here." }, _emitted);
    }

    [Fact]
    public void Flush_after_everything_is_final_emits_nothing()
    {
        Feed("All done.");
        Feed("All done.", advanceMs: 1100);

        Assert.Empty(_tracker.Flush().NewSentences);
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
    public void Start_of_a_sentence_longer_than_the_visible_text_is_kept()
    {
        Feed("Done. This is a very long sentence that keeps");
        Feed("long sentence that keeps going and going");
        Feed("keeps going and going on and on");
        Feed("going on and on. Next one");

        Assert.Equal(new[] { "Done.", "This is a very long sentence that keeps going and going on and on." }, _emitted);
    }

    [Fact]
    public void Start_is_kept_when_a_word_is_corrected_as_it_scrolls_away()
    {
        Feed("Done. Please check their services for the");
        Feed("check there service for the delivery date");
        Feed("service for the delivery date. Thanks");

        Assert.Equal(new[] { "Done.", "Please check there service for the delivery date." }, _emitted);
    }

    [Fact]
    public void Start_is_kept_when_only_the_last_word_is_left()
    {
        Feed("Done. Is this the right number for");
        Feed("Done. Is this the right number for you?");
        Feed("you? I can call");

        Assert.Equal(new[] { "Done.", "Is this the right number for you?" }, _emitted);
    }

    [Fact]
    public void Unfinished_sentence_is_not_mistaken_for_an_earlier_short_one()
    {
        Feed("Maybe. Okay");
        Feed("Maybe. Okay then. Maybe");

        Assert.Equal(new[] { "Maybe.", "Okay then." }, _emitted);
    }

    [Fact]
    public void Repeated_short_sentence_needs_its_context_to_count_as_seen()
    {
        Feed("Yes. Is it");
        Feed("Yes. Is it the blue one? Yes. And");

        Assert.Equal(new[] { "Yes.", "Is it the blue one?", "Yes." }, _emitted);
    }

    [Fact]
    public void Short_tail_of_an_old_sentence_at_the_top_is_not_emitted_again()
    {
        Feed("We went to the store. And then");
        Feed("store. And then we drove home again and");
        Feed("store. And then we drove home again and slept. Bye");

        Assert.Equal(new[] { "We went to the store.", "And then we drove home again and slept." }, _emitted);
    }

    [Fact]
    public void A_sentence_rewritten_after_it_was_saved_does_not_make_the_next_one_repeat()
    {
        // Live Captions closes "Riverside, Texas seven." for a moment, then rewrites it once the zip code is complete.
        Feed("Delivery. Uh, 4100 Maple Grove, uh. Riverside, Texas seven.");
        Feed("Delivery. Uh, 4100 Maple Grove, uh. Riverside, Texas seven. Five");
        Feed("Delivery. Uh, 4100 Maple Grove, uh. Riverside TX 75231 in the");
        Feed("Delivery. Uh, 4100 Maple Grove, uh. Riverside TX 75231 in the main. Pick");
        Feed("Delivery. Uh, 4100 Maple Grove, uh. Riverside TX 75231 in the main. Pick up");
        Feed("Delivery. Uh, 4100 Maple Grove, uh. Riverside TX 75231 in the main. Pick up building");
        Feed("Delivery. Uh, 4100 Maple Grove, uh. Riverside TX 75231 in the main. Pick up building, the main building.");
        Feed("Uh, 4100 Maple Grove, uh. Riverside TX 75231 in the main. Pick up building, the main building. Zero");

        Assert.Equal(
            new[]
            {
                "Delivery.",
                "Uh, 4100 Maple Grove, uh.",

                // "Riverside, Texas seven." stayed in the live text: the zip code was still being read out.
                "Riverside TX 75231 in the main.",
                "Pick up building, the main building.",
            },
            _emitted);
    }

    [Fact]
    public void A_number_read_out_digit_by_digit_is_not_repeated()
    {
        // Live Captions puts each digit in a sentence of its own, then joins and rewrites them as the number goes on.
        const string Before = "Yes, from the Northside location. That's the place to mess up the order.";
        Feed(Before + " Three.");
        Feed(Before + " Three. Three");
        Feed(Before + " Three, three, zero.");
        Feed(Before + " Three, three, zero. Six");
        Feed(Before + " 3306.");
        Feed(Before + " 3306. One.");
        Feed(Before + " 3306. One. Two");
        Feed(Before + " 3306. 12.");
        Feed(Before + " 3306. 12. Seven.");
        Feed(Before + " 3306. 12. Seven. Seven");
        Feed(Before + " 3306. 12. Seven, seven.");
        Feed(Before + " 3306. 12. Seven, seven. Five");
        Feed(Before + " 3306. 12. Seven, seven, five, zero.");
        Feed(Before + " 3306. 12. Seven, seven, five, zero. Four");
        Feed(Before + " 3306. 12. 7750.");
        Feed(Before + " 3306. 12. 7750. Four.");
        Feed(Before + " 3306. 12. 7750. Four. One");
        Feed(Before + " 3306. 12. 7750. 41.");
        Feed(Before + " 3306. 12. 7750. 41. Thank you");

        Assert.Single(_emitted, "3306.");
        Assert.Single(_emitted, "12.");
        Assert.Single(_emitted, "7750.");
        Assert.Single(_emitted, "41.");
        Assert.True(_emitted.Count <= 14, string.Join(" | ", _emitted));
    }

    [Fact]
    public void A_card_number_read_in_groups_stays_live_until_it_is_complete()
    {
        Feed("What's the card number? Four");
        Feed("What's the card number? Four. One");
        Feed("What's the card number? Four, one. One. One");
        Feed("What's the card number? 4111. One");
        Feed("What's the card number? 4111. One, one. One");
        Feed("What's the card number? 4111 1111. Two");
        var live = Feed("What's the card number? 4111 1111. Two, two. Two");

        Assert.Equal(new[] { "What's the card number?" }, _emitted);
        Assert.Equal("4111 1111. Two, two. Two", live.Pending);

        Feed("What's the card number? 4111 1111 2222. And the expiry");

        Assert.Equal(new[] { "What's the card number?", "4111 1111 2222." }, _emitted);
    }

    [Fact]
    public void A_number_at_the_end_of_speech_waits_in_the_live_text_for_what_comes_next()
    {
        // The customer may only be pausing between groups of digits: the number stays live, however long the pause.
        Feed("My card is 4111 1111.");
        var live = Feed("My card is 4111 1111.", advanceMs: 5000);
        Assert.Empty(_emitted);
        Assert.Equal("My card is 4111 1111.", live.Pending);

        Feed("My card is 4111 1111 2222 3333. Expiry");
        Assert.Equal(new[] { "My card is 4111 1111 2222 3333." }, _emitted);
        _emitted.Apply(_tracker.Flush());
        Assert.Equal(new[] { "My card is 4111 1111 2222 3333.", "Expiry" }, _emitted);
    }

    [Fact]
    public void A_transfer_number_then_minutes_of_silence_then_the_next_call_repeats_nothing()
    {
        // As in a real transcript: the older version showed the number six times, then the whole earlier call again.
        const string Before = "Yeah, hi. I'd like to order the. Grilled chicken Caesar salad.";
        Feed(Before + " Zero.");
        Feed(Before + " Zero.", advanceMs: 2000);
        Feed(Before + " Zero. Four");
        Feed(Before + " 04. Zero, two");
        Feed(Before + " 040292.");
        Feed(Before + " 040292.", advanceMs: 2000);
        Feed(Before + " 040292.", advanceMs: 380_000);
        Feed(Before + " 040292. Oh yeah");
        Feed(Before + " 040292. Oh yeah, I'd like to order that");
        Feed("I'd like to order the. Grilled chicken Caesar salad. 040292. Oh yeah, I'd like to order that large pizza.");
        Feed("Grilled chicken Caesar salad. 040292. Oh yeah, I'd like to order that large pizza. A pickup.");

        Assert.Equal(
            new[] { "Yeah, hi.", "I'd like to order the.", "Grilled chicken Caesar salad.", "040292.", "Oh yeah, I'd like to order that large pizza." },
            _emitted);
    }

    [Fact]
    public void A_card_number_heard_partly_as_teens_is_shown_once()
    {
        // "Five. Two. Eleven." for "five two one one", then rewritten.
        const string Before = "OK. The card number is";
        Feed(Before + " 4000.");
        Feed(Before + " 4000. Five.");
        Feed(Before + " 4000. Five. Two.");
        Feed(Before + " 4000. Five. Two. Eleven.");
        Feed(Before + " 4000 5211.");
        Feed(Before + " 4000 5211. Seven.");
        Feed(Before + " 4000 5211. Seven, seven, three.");
        Feed(Before + " 4000 5211 7730.");
        Feed(Before + " 4000 5211 7730. Expiration date is");

        Assert.Equal(new[] { "OK.", "The card number is 4000 5211 7730." }, _emitted);
    }

    [Fact]
    public void A_number_read_as_pairs_replaces_its_early_part_when_rewritten()
    {
        // "Fifty two." had to go into the history (another number came straight after), then Live Captions
        // rewrote both as one.
        Feed("Is it this one? Fifty two.");
        Feed("Is it this one? Fifty two.", advanceMs: 1500);
        Feed("Is it this one? Fifty two. Yes");
        Feed("Is it this one? 5211 7730. Yes.");
        Feed("Is it this one? 5211 7730. Yes. OK");

        Assert.Equal(new[] { "Is it this one?", "5211 7730.", "Yes." }, _emitted);
    }

    [Fact]
    public void The_same_short_number_said_again_is_shown_again()
    {
        Feed("The code is 12. 12. Yes");
        Feed("The code is 12. 12. Yes, 12. OK");

        Assert.Equal(new[] { "The code is 12.", "12.", "Yes, 12." }, _emitted);
    }

    [Fact]
    public void A_corrected_number_is_not_mistaken_for_the_earlier_one()
    {
        Feed("It's 3306. Four");
        Feed("It's 3306. Sorry");
        Feed("It's 3306. Sorry, 3307. OK");

        Assert.Equal(new[] { "It's 3306.", "Sorry, 3307." }, _emitted);
    }

    [Fact]
    public void Several_rewritten_sentences_in_a_row_still_do_not_cause_repeats()
    {
        Feed("Card number is. Four. One. One");
        Feed("Card number is 4111 1111 1111 1111. Expiry");
        Feed("Card number is 4111 1111 1111 1111. Expiry is 0327. And");
        Feed("Card number is 4111 1111 1111 1111. Expiry is 0327. And the code");
        Feed("Card number is 4111 1111 1111 1111. Expiry is 0327. And the code is 123. OK");

        Assert.Single(_emitted, sentence => sentence.Contains("4111 1111 1111 1111", StringComparison.Ordinal));
        Assert.DoesNotContain(_emitted, sentence => sentence is "Four." or "One.");
        Assert.Single(_emitted, "Expiry is 0327.");
        Assert.Single(_emitted, "And the code is 123.");
    }

    [Fact]
    public void A_short_rewritten_sentence_does_not_repeat_either()
    {
        Feed("What is the apartment number? Seven.");
        Feed("What is the apartment number? Seven. Five");
        Feed("What is the apartment number? 75.");
        Feed("What is the apartment number? 75. And");
        Feed("What is the apartment number? 75. And the gate");
        Feed("What is the apartment number? 75. And the gate code is 1234. OK");

        Assert.Equal(new[] { "What is the apartment number?", "75.", "And the gate code is 1234." }, _emitted);
    }

    [Fact]
    public void A_new_sentence_starting_like_the_last_one_is_not_swallowed()
    {
        Feed("Is that right? Yes.");
        Feed("Is that right? Yes. Five");
        Feed("Is that right? 5.");
        Feed("Is that right? 5. Yes");
        Feed("Is that right? 5. Yes please do it. OK");

        Assert.Equal(new[] { "Is that right?", "Yes.", "5.", "Yes please do it." }, _emitted);
    }

    [Fact]
    public void A_shorter_repeat_of_part_of_a_sentence_is_still_shown()
    {
        Feed("The address is 4100 Maple Grove. 4100");
        Feed("The address is 4100 Maple Grove. 4100 Maple Grove. Yes");

        Assert.Equal(new[] { "The address is 4100 Maple Grove.", "4100 Maple Grove." }, _emitted);
    }

    [Fact]
    public void A_short_answer_given_again_is_shown_again()
    {
        Feed("Is it 4100? Yes. Maple Grove? Yes. OK");

        Assert.Equal(new[] { "Is it 4100?", "Yes.", "Maple Grove?", "Yes." }, _emitted);
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
