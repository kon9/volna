using Volna.Core;
using Xunit;

namespace Volna.Tests;

public class ClipMetadataTests
{
    static List<object?> Track(string name) => [Json.Map(("name", name), ("ext", "vtt"), ("url", "https://example.com/captions.vtt"))];

    [Fact]
    public void Chapters_are_sorted_bounded_and_missing_ends_use_the_next_chapter()
    {
        var info = ClipMetadata.Parse(Json.Map(("duration", 120.0), ("chapters", new List<object?>
        {
            Json.Map(("start_time", 60.0), ("end_time", 150.0), ("title", "Second topic")),
            Json.Map(("start_time", 0.0), ("title", "Introduction")),
            Json.Map(("start_time", -5.0), ("end_time", 5.0)),
            Json.Map(("start_time", double.NaN), ("end_time", 10.0)),
            Json.Map(("start_time", 120.0), ("end_time", 130.0)),
        })));
        Assert.Equal(new[] { new VideoChapter(0, 60, "Introduction"), new VideoChapter(60, 120, "Second topic") }, info.Chapters);
    }

    [Fact]
    public void Bad_metadata_gives_empty_lists_and_never_infinite_ranges()
    {
        var info = ClipMetadata.Parse(Json.Map(("duration", double.PositiveInfinity), ("chapters", new List<object?>
        {
            Json.Map(("start_time", 0.0), ("end_time", double.PositiveInfinity)),
            Json.Map(("start_time", 3.0), ("end_time", 2.0)),
            Json.Map(("start_time", 8.0)),
            "not a chapter",
        })));
        Assert.Null(info.Duration);
        Assert.Empty(info.Chapters);
        Assert.Empty(info.Captions);
        Assert.Null(info.PreferredCaption());
    }

    [Fact]
    public void Captions_include_authors_tracks_and_original_automatic_captions_without_translation_duplicates()
    {
        var info = ClipMetadata.Parse(Json.Map(("language", "en"),
            ("subtitles", Json.Map(("fr", Track("Français")), ("en", Track("English")), ("live_chat", Track("Chat")))),
            ("automatic_captions", Json.Map(("en-orig", Track("English (Original)")), ("en", Track("English")), ("ru", Track("Russian"))))));
        Assert.Equal(new[] { "fr", "en", "en-orig" }, info.Captions.Select(c => c.Language));
        Assert.Equal("en", info.PreferredCaption("fr")!.Language);
        Assert.False(info.PreferredCaption()!.Automatic);
        Assert.True(info.Captions[^1].Automatic);
    }

    [Fact]
    public void Spoken_automatic_captions_are_available_without_an_orig_alias()
    {
        var info = ClipMetadata.Parse(Json.Map(("language", "ru"),
            ("automatic_captions", Json.Map(("en", Track("English")), ("ru", Track("Русский"))))));
        Assert.Equal("ru", Assert.Single(info.Captions).Language);
        Assert.True(info.PreferredCaption()!.Automatic);
    }

    [Theory]
    [InlineData("en-US", "en")]
    [InlineData("en", "en-US")]
    [InlineData("pt_BR", "pt-BR")]
    [InlineData("EN", "en")]
    [InlineData("zh-Hans", "zh")]
    public void Original_automatic_captions_match_base_and_regional_language_codes(string language, string track)
    {
        var info = ClipMetadata.Parse(Json.Map(("language", language),
            ("automatic_captions", Json.Map(("fr", Track("French translation")), (track, Track("Original captions"))))));
        Assert.Equal(track, Assert.Single(info.Captions).Language);
        Assert.True(info.PreferredCaption()!.Automatic);
    }

    [Fact]
    public void Preferred_manual_captions_match_a_regional_video_language_before_unrelated_languages()
    {
        var info = ClipMetadata.Parse(Json.Map(("language", "en-US"),
            ("subtitles", Json.Map(("fr", Track("French")), ("en", Track("English"))))));
        Assert.Equal("en", info.PreferredCaption()!.Language);
    }

    [Fact]
    public void Caption_language_is_an_exact_match_and_the_video_is_not_downloaded()
    {
        var args = ClipMetadata.TranscriptArgs("https://example.com/video", new CaptionTrack("en.*", "English", true), "captions", [], "");
        Assert.Equal("^en\\.\\*$", args[args.IndexOf("--sub-langs") + 1]);
        Assert.Contains("--skip-download", args);
        Assert.Contains("--write-auto-subs", args);
        Assert.DoesNotContain("--write-subs", args);
        Assert.Contains("--skip-download", ClipMetadata.MetadataArgs("https://example.com/video", [], ""));
    }

    [Fact]
    public void Timed_cues_keep_fractional_times_clean_markup_and_ignore_vtt_metadata()
    {
        const string vtt = """
            WEBVTT

            NOTE not caption text
            00:00.000 --> 00:01.000
            Ignore this block

            STYLE
            ::cue { color: red; }

            cue-one
            01:02:03.125 --> 01:02:05.750 align:start position:0%
            <v Narrator>Hello &amp; welcome</v>
            to <b>the film</b>.

            02:08.000 --> 02:09.000
            A shorter timestamp.
            """;
        var cues = Subtitles.ParseVtt(vtt);
        Assert.Equal(new SubtitleCue(3723.125, 3725.75, "Hello & welcome to the film."), cues[0]);
        Assert.Equal(new SubtitleCue(128, 129, "A shorter timestamp."), cues[1]);
        Assert.Equal(2, cues.Count);
    }

    [Fact]
    public void Invalid_ranges_and_cue_ids_do_not_leak_into_the_transcript()
    {
        const string vtt = """
            WEBVTT

            1
            00:00:05.000 --> 00:00:04.000
            Backwards

            2
            00:99:00.000 --> 01:00:00.000
            Bad minutes

            3
            malformed --> timestamp
            Bad syntax

            4
            00:00:10.000 --> 00:00:11.000
            Valid
            """;
        Assert.Equal(new SubtitleCue(10, 11, "Valid"), Assert.Single(Subtitles.ParseVtt(vtt)));
    }

    [Fact]
    public void Automatic_rolling_lines_are_deduplicated_but_a_later_repetition_is_preserved()
    {
        const string vtt = """
            WEBVTT

            00:00:01.000 --> 00:00:03.000
            hello <00:00:01.500><c>there</c>

            00:00:03.000 --> 00:00:05.000
            hello there
            and welcome

            00:00:05.000 --> 00:00:06.000
            and welcome

            00:00:10.000 --> 00:00:12.000
            and welcome
            """;
        var cues = Subtitles.ParseVtt(vtt, true);
        Assert.Equal(new[] { new SubtitleCue(1, 3, "hello there"), new SubtitleCue(3, 6, "and welcome"), new SubtitleCue(10, 12, "and welcome") }, cues);
        Assert.Equal(4, Subtitles.ParseVtt(vtt).Count); // Manual captions may deliberately repeat words.
    }

    [Fact]
    public void Word_by_word_automatic_cues_keep_the_complete_phrase_and_its_start()
    {
        const string vtt = """
            WEBVTT

            00:00:01.000 --> 00:00:03.000
            Hello

            00:00:01.500 --> 00:00:03.000
            Hello world
            """;
        Assert.Equal(new SubtitleCue(1, 3, "Hello world"), Assert.Single(Subtitles.ParseVtt(vtt, true)));
    }

    [Fact]
    public void Cancelled_caption_requests_leave_no_temporary_downloads() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        // A cancelled runner never starts this file; it only makes tool discovery independent of the machine.
        File.WriteAllText(Path.Combine(AppPaths.Tools, OperatingSystem.IsWindows() ? "yt-dlp.exe" : "yt-dlp"), "not an executable");
        using var runner = new ProcessRunner();
        runner.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => ClipMetadata.TranscriptAsync("https://example.com/video", new CaptionTrack("en", "English", false), "", runner));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(AppPaths.Cache, "clip-captions")));
        await Assert.ThrowsAsync<OperationCanceledException>(() => ClipMetadata.LoadAsync("https://example.com/video", "", runner));
    });
}
