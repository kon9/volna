using Volna.Core;
using Xunit;

namespace Volna.Tests;

public class TranscriptSearchTests
{
    [Fact]
    public void A_phrase_can_span_cues_and_keeps_the_complete_context()
    {
        SubtitleCue[] cues = [new(1, 3, "hello there"), new(3, 5, "and welcome today"), new(5, 7, "to the film")];
        Assert.Equal(new SubtitleCue(1, 5, "hello there and welcome today"), Assert.Single(TranscriptSearch.Find(cues, "there and welcome")));
    }

    [Fact]
    public void Search_normalizes_case_and_whitespace_in_both_query_and_captions()
    {
        SubtitleCue[] cues = [new(1, 2, "  Привет\nМИР  "), new(2, 4, "и\tдобро пожаловать")];
        Assert.Equal(new SubtitleCue(1, 4, "Привет МИР и добро пожаловать"), Assert.Single(TranscriptSearch.Find(cues, " мир \n И   добро ")));
    }

    [Fact]
    public void Later_occurrences_remain_but_multiple_hits_in_the_same_cue_are_not_duplicated()
    {
        SubtitleCue[] cues = [new(1, 3, "hello hello"), new(3, 5, "world"), new(10, 12, "hello again")];
        Assert.Equal(new[] { cues[0], cues[2] }, TranscriptSearch.Find(cues, "hello"));
    }

    [Fact]
    public void A_phrase_can_span_more_than_two_cues()
    {
        SubtitleCue[] cues = [new(1, 2, "A quick"), new(2, 3, "look at"), new(3, 4, "the river"), new(4, 5, "today")];
        Assert.Equal(new SubtitleCue(1, 4, "A quick look at the river"), Assert.Single(TranscriptSearch.Find(cues, "quick look at the")));
    }

    [Fact]
    public void Long_silences_and_out_of_order_cues_do_not_form_a_phrase()
    {
        Assert.Empty(TranscriptSearch.Find(new SubtitleCue[] { new(1, 2, "hello"), new(8, 9, "world") }, "hello world"));
        Assert.Empty(TranscriptSearch.Find(new SubtitleCue[] { new(10, 11, "hello"), new(1, 2, "world") }, "hello world"));
        Assert.Equal(new SubtitleCue(1, 8, "hello world"), Assert.Single(TranscriptSearch.Find(new SubtitleCue[] { new(1, 2, "hello"), new(7, 8, "world") }, "hello world")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \n\t ")]
    public void An_empty_query_returns_the_original_cues(string? query)
    {
        SubtitleCue[] cues = [new(1, 3, "hello")];
        Assert.Same(cues, TranscriptSearch.Find(cues, query));
    }

    [Fact]
    public void No_matches_and_empty_transcripts_return_no_results()
    {
        Assert.Empty(TranscriptSearch.Find([], "hello"));
        Assert.Empty(TranscriptSearch.Find(new SubtitleCue[] { new(1, 2, "hello") }, "absent"));
    }

    [Fact]
    public void Overlapping_captions_keep_the_latest_end_of_the_matched_span()
    {
        SubtitleCue[] cues = [new(1, 6, "hello"), new(2, 4, "world")];
        Assert.Equal(new SubtitleCue(1, 6, "hello world"), Assert.Single(TranscriptSearch.Find(cues, "hello world")));
    }
}
