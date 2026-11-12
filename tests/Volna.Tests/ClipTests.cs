using Volna.Core;
using Xunit;

namespace Volna.Tests;

public class StoryboardTests
{
    // As yt-dlp lists them for a twelve-hour video: one sheet of 100 tiny frames, and sheets of nine larger ones.
    static List<Dictionary<string, object?>> Formats()
    {
        var sheets = new List<object?>();
        for (int n = 0; n < 4; n++) sheets.Add(Json.Map(("url", "https://i.ytimg.com/sb/x/L3/M" + n + ".jpg"), ("duration", n < 3 ? 90.0 : 50.0)));
        return new()
        {
            Json.Map(("format_id", "sb3"), ("width", 48.0), ("height", 27.0), ("rows", 10.0), ("columns", 10.0), ("fps", 100 / 320.0),
                ("fragments", new List<object?> { Json.Map(("url", "https://i.ytimg.com/sb/x/L0/default.jpg"), ("duration", 320.0)) })),
            Json.Map(("format_id", "sb0"), ("width", 320.0), ("height", 180.0), ("rows", 3.0), ("columns", 3.0), ("fps", 0.1), ("fragments", sheets)),
            Json.Map(("format_id", "137"), ("width", 1920.0), ("height", 1080.0)),
        };
    }

    [Fact]
    public void The_sharpest_board_previews_and_the_one_sheet_board_fills_the_strip()
    {
        var (detail, overview) = Storyboard.FromFormats(Formats());
        Assert.Equal(320, detail!.Width);
        Assert.Equal(48, overview!.Width);
        Assert.Single(overview.Sheets);
    }

    [Theory]
    [InlineData(0.0, 0, 0, 0)]
    [InlineData(25.0, 0, 640, 0)]       // the third frame of the first sheet
    [InlineData(95.0, 1, 0, 0)]         // the first frame of the second sheet
    [InlineData(125.0, 1, 0, 180)]      // the fourth: second row
    [InlineData(315.0, 3, 320, 180)]    // the fifth frame of the last sheet, which holds only five
    [InlineData(5000.0, 3, 320, 180)]   // past the end, the last frame there is
    public void A_moment_finds_its_frame(double seconds, int sheet, int x, int y)
    {
        var (detail, _) = Storyboard.FromFormats(Formats());
        var frame = detail!.At(seconds)!;
        Assert.Equal("https://i.ytimg.com/sb/x/L3/M" + sheet + ".jpg", frame.Url);
        Assert.Equal((x, y), (frame.X, frame.Y));
    }

    [Fact]
    public void A_site_without_frames_has_no_board() =>
        Assert.Equal((null, null), Storyboard.FromFormats(new[] { Json.Map(("format_id", "hls-720"), ("width", 1280.0)) }));

    [Fact]
    public void Frames_are_asked_for_without_the_players_script() =>
        Assert.Contains("youtube:player_skip=js", Engine.StoryboardArgs("https://www.youtube.com/watch?v=x", Array.Empty<string>(), ""));
}

public class ClipRangeTests
{
    [Theory]
    [InlineData("https://youtu.be/abc?t=95", 95.0)]
    [InlineData("https://www.youtube.com/watch?v=abc&t=1h2m5s", 3725.0)]
    [InlineData("https://www.youtube.com/watch?v=abc&t=90s", 90.0)]
    [InlineData("https://www.youtube.com/watch?v=abc#t=4m", 240.0)]
    [InlineData("https://www.youtube.com/watch?v=abc", null)]
    [InlineData("https://www.youtube.com/watch?v=abc&t=", null)]
    [InlineData("https://www.youtube.com/watch?v=abc&t=soon", null)]
    public void A_link_can_say_where_to_start(string url, double? expected) => Assert.Equal(expected, Engine.StartOf(url));

    [Theory]
    [InlineData(10.4, 40.2, 100.0, "10-41")]
    [InlineData(0.0, 100.0, 100.0, null)]          // the whole video is no part
    [InlineData(90.0, 400.0, 100.0, "90-100")]     // not past the end
    [InlineData(5.0, 5.0, 100.0, null)]           // nothing to download
    public void A_part_is_whole_seconds(double start, double end, double duration, string? expected) =>
        Assert.Equal(expected, QueueController.SectionFor(start, end, duration));

    [Fact]
    public void A_part_stays_inside_the_video_and_keeps_its_length_when_moved()
    {
        var range = new ClipRange();
        range.Reset(3600, 100, 130);
        range.MoveTo(3590);
        Assert.Equal((3570.0, 3600.0), (range.Start, range.End));
        range.Set(50, 20); // an end before the start becomes the shortest part
        Assert.Equal((50.0, 51.0), (range.Start, range.End));
        range.Set(-5, 10.6);
        Assert.Equal((0.0, 11.0), (range.Start, range.End));
    }

    [Fact]
    public void Changes_are_told_once()
    {
        var range = new ClipRange();
        range.Reset(600, 0, 30);
        int told = 0;
        range.Changed += () => told++;
        range.Set(0, 30);
        range.MoveTo(60);
        Assert.Equal(1, told);
    }
}

[Collection("queue")]
public class PartTests
{
    static Dictionary<string, object?> Long(string id) => Json.Map(("id", id), ("title", "A long stream"), ("uploader", "Channel"), ("duration", 43200.0),
        ("webpage_url", "https://www.youtube.com/watch?v=" + id), ("ie_key", "Youtube"), ("extractor_key", "Youtube"));

    [Fact]
    public void A_part_downloads_at_once() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        room.Prefs.Audio = false;
        room.Backend.Results["a x"] = new() { Long("a") };
        room.Queue.Add(TestRoom.Entries("a x"));
        var item = room.Queue.Items[0];
        await room.Until(() => item.State == ItemState.Ready);
        Assert.True(item.ShowClip);
        Assert.Same(item, room.Queue.DownloadPart(item, 21600, 21630));
        await room.Until(() => item.State == ItemState.Done);
        Assert.Equal("21600-21630", room.Backend.Downloads.Single().Section);
        Assert.Contains("6:00:00", item.ClipText, StringComparison.Ordinal);
    });

    [Fact]
    public void Another_part_of_a_finished_video_gets_a_card_of_its_own() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        room.Prefs.Audio = false;
        room.Backend.Results["a x"] = new() { Long("a") };
        room.Queue.Add(TestRoom.Entries("a x"));
        var first = room.Queue.Items[0];
        await room.Until(() => first.State == ItemState.Ready);
        room.Queue.DownloadPart(first, 60, 90);
        await room.Until(() => first.State == ItemState.Done);
        Assert.True(first.ShowClip);

        var second = room.Queue.DownloadPart(first, 3600, 3610)!;
        Assert.NotSame(first, second);
        Assert.Equal(new[] { first, second }, room.Queue.Items);
        await room.Until(() => second.State == ItemState.Done);
        Assert.Equal(new[] { "60-90", "3600-3610" }, room.Backend.Downloads.Select(d => d.Section));
        Assert.Equal("60-90", first.Section);
        Assert.False(second.IsSearch);
    });

    [Fact]
    public void A_live_stream_has_no_parts_to_choose() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        var live = Long("l");
        live["live_status"] = "is_live";
        room.Backend.Results["l x"] = new() { live };
        room.Queue.Add(TestRoom.Entries("l x"));
        var item = room.Queue.Items[0];
        await room.Until(() => item.State == ItemState.Ready);
        Assert.False(item.ShowClip);
    });

    [Fact]
    public void Parts_come_back_after_a_restart_beside_the_whole_video() => SingleThread.Run(async () =>
    {
        using (var room = new TestRoom())
        {
            room.Prefs.Audio = false;
            room.Backend.Results["a x"] = new() { Long("a") };
            room.Backend.OnDownload = (_, _) => new TaskCompletionSource<RunResult>().Task; // never ends: both stay unfinished
            room.Queue.Add(TestRoom.Entries("a x"));
            var whole = room.Queue.Items[0];
            await room.Until(() => whole.State == ItemState.Ready);
            room.Queue.StartOne(whole);
            await room.Until(() => whole.State == ItemState.Downloading);
            room.Queue.DownloadPart(whole, 60, 90);
            Assert.Equal(2, room.Queue.Items.Count);
            room.Queue.SaveQueue(true);
            string saved = File.ReadAllText(AppPaths.Queue);

            using var again = new TestRoom();
            File.WriteAllText(AppPaths.Queue, saved);
            Assert.Equal(2, again.Queue.RestoreQueue());
            Assert.Equal(new string?[] { null, "60-90" }, again.Queue.Items.Select(i => i.Section));
        }
    });
}
