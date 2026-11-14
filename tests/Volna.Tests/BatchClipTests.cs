using Volna.Core;
using Xunit;

namespace Volna.Tests;

[Collection("queue")]
public class BatchClipTests
{
    static async Task<QueueItem> AddVideo(TestRoom room)
    {
        room.Prefs.Audio = false;
        var info = FakeBackend.Video("sample", "A generated sample");
        info["duration"] = 600.0;
        room.Backend.Results["sample video"] = new() { info };
        room.Queue.Add(TestRoom.Entries("sample video"));
        var source = room.Queue.Items[0];
        await room.Until(() => source.State == ItemState.Ready);
        return source;
    }

    [Fact]
    public void A_batch_preserves_names_order_and_the_parallel_limit() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        var source = await AddVideo(room);
        room.Prefs.Parallel = 1;
        var finishes = new Queue<TaskCompletionSource<RunResult>>();
        room.Backend.OnDownload = (options, onLine) =>
        {
            var finished = new TaskCompletionSource<RunResult>();
            finishes.Enqueue(finished);
            string file = Path.Combine(options.Folder, "Sample [sample clip " + options.Section + "].mp4");
            File.WriteAllText(file, "video");
            onLine("__FILE__" + file);
            return finished.Task;
        };

        var cards = room.Queue.DownloadParts(source, new[]
        {
            new DownloadPartRequest(10, 25, "Introduction"),
            new DownloadPartRequest(100, 130, "Second topic"),
            new DownloadPartRequest(400, 450),
        });
        Assert.Equal(3, cards.Count);
        Assert.Same(source, cards[0]);
        Assert.Equal(cards, room.Queue.Items);
        Assert.Equal(new[] { "10-25", "100-130", "400-450" }, cards.Select(c => c.Section));
        Assert.Equal(new[] { "Introduction", "Second topic", null }, cards.Select(c => c.SectionName));
        Assert.Equal(new[] { ItemState.Downloading, ItemState.Queued, ItemState.Queued }, cards.Select(c => c.State));
        Assert.Single(room.Backend.Downloads);
        Assert.Contains("Introduction", cards[0].ClipText, StringComparison.Ordinal);

        for (int index = 0; index < cards.Count; index++)
        {
            finishes.Dequeue().SetResult(new RunResult(0, false));
            await room.Until(() => cards[index].State == ItemState.Done);
        }
        Assert.Equal(new[] { "Introduction", "Second topic", null }, room.Backend.Downloads.Select(o => o.SectionName));
        Assert.Single(room.Runs);
        Assert.Equal((3, 0), room.Runs[0]);
    });

    [Theory]
    [InlineData(-1, 20)]
    [InlineData(10, 10)]
    [InlineData(10, 9)]
    [InlineData(600, 610)]
    [InlineData(double.NaN, 20)]
    [InlineData(10, double.PositiveInfinity)]
    public void An_invalid_later_range_does_not_start_or_change_any_card(double start, double end) => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        var source = await AddVideo(room);
        var cards = room.Queue.DownloadParts(source, new[] { new DownloadPartRequest(0, 10, "Good"), new DownloadPartRequest(start, end, "Bad") });
        Assert.Empty(cards);
        Assert.Single(room.Queue.Items);
        Assert.Null(source.Section);
        Assert.Null(source.SectionName);
        Assert.Equal(ItemState.Ready, source.State);
        Assert.Empty(room.Backend.Downloads);
    });

    [Fact]
    public void Repeated_normalized_ranges_are_downloaded_once_and_the_end_is_clamped() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        var source = await AddVideo(room);
        room.Queue.Hold = true;
        var cards = room.Queue.DownloadParts(source, new[]
        {
            new DownloadPartRequest(10.1, 12.2, "First name"),
            new DownloadPartRequest(10.3, 12.7, "Duplicate"),
            new DownloadPartRequest(580, 650, "Last topic"),
        });
        Assert.Equal(2, cards.Count);
        Assert.Equal(new[] { "10-13", "580-600" }, cards.Select(c => c.Section));
        Assert.Equal(new[] { "First name", "Last topic" }, cards.Select(c => c.SectionName));
    });

    [Fact]
    public void A_batch_beside_a_finished_video_does_not_replace_its_card() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        var source = await AddVideo(room);
        room.Queue.StartOne(source);
        await room.Until(() => source.State == ItemState.Done);
        string? originalFile = source.FilePath;
        room.Queue.Hold = true;
        var cards = room.Queue.DownloadParts(source, new[] { new DownloadPartRequest(10, 20), new DownloadPartRequest(30, 40) });
        Assert.Equal(3, room.Queue.Items.Count);
        Assert.Equal(new[] { source, cards[0], cards[1] }, room.Queue.Items);
        Assert.Equal(ItemState.Done, source.State);
        Assert.Equal(originalFile, source.FilePath);
        Assert.Null(source.Section);
        Assert.All(cards, card => Assert.False(card.IsSearch));
    });

    [Fact]
    public void Named_ranges_survive_a_restart() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        var source = await AddVideo(room);
        room.Queue.Hold = true;
        room.Queue.DownloadParts(source, new[] { new DownloadPartRequest(15, 30, "First"), new DownloadPartRequest(60, 90, "Second") });
        room.Queue.SaveQueue(true);
        string saved = File.ReadAllText(AppPaths.Queue);

        using var restored = new TestRoom();
        File.WriteAllText(AppPaths.Queue, saved);
        Assert.Equal(2, restored.Queue.RestoreQueue());
        Assert.Equal(new[] { "15-30", "60-90" }, restored.Queue.Items.Select(c => c.Section));
        Assert.Equal(new[] { "First", "Second" }, restored.Queue.Items.Select(c => c.SectionName));
        Assert.All(restored.Queue.Items, card => Assert.Equal(ItemState.Ready, card.State));
    });

    [Fact]
    public void A_bad_destination_does_not_leave_half_a_batch_in_the_queue() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        var source = await AddVideo(room);
        room.Prefs.Folder = "";
        Assert.Empty(room.Queue.DownloadParts(source, new[] { new DownloadPartRequest(10, 20), new DownloadPartRequest(30, 40) }));
        Assert.Null(source.Section);
        Assert.Single(room.Queue.Items);
        Assert.Empty(room.Backend.Downloads);
    });

    [Fact]
    public void A_custom_name_cannot_be_a_path_or_a_yt_dlp_template()
    {
        var options = new DownloadOptions { Url = "https://example.org/sample", Folder = "downloads", Section = "10-20", SectionName = "../../folder\\name: %(title)s\nnext" };
        var args = Engine.DownloadArgs(options, Array.Empty<string>());
        string output = args[args.IndexOf("--output") + 1];
        Assert.DoesNotContain('/', output);
        Assert.DoesNotContain('\\', output);
        Assert.DoesNotContain(':', output);
        Assert.DoesNotContain('\n', output);
        Assert.Contains("%%(title)s", output, StringComparison.Ordinal);
        Assert.EndsWith(" [%(id)s clip 10-20].%(ext)s", output, StringComparison.Ordinal);
        Assert.Equal("*10-20", args[args.IndexOf("--download-sections") + 1]);
    }

    [Fact]
    public void A_long_unicode_name_leaves_room_for_the_range_and_id()
    {
        string cleaned = Engine.CleanSectionName(string.Concat(Enumerable.Repeat("Кадр 🎬 ", 100)))!;
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(cleaned), 1, 100);
        Assert.False(char.IsHighSurrogate(cleaned[^1]));
        Assert.Null(Engine.CleanSectionName(" ... "));
        Assert.Equal("A useful name", Engine.CleanSectionName(" A useful name "));
    }
}
