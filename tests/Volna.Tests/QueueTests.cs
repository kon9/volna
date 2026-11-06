using Volna.Core;
using Xunit;

namespace Volna.Tests;

[Collection("queue")] // these share the data folder override
public class QueueTests
{
    [Fact]
    public void Search_prefers_the_original_track_in_audio_mode() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        room.Backend.Music["Nova Harbor - Glass Tide"] = new() { FakeBackend.Track("t1", "Glass Tide", "Nova Harbor") };
        room.Queue.Add(TestRoom.Entries("Nova Harbor - Glass Tide"));
        await room.Until(() => room.Queue.Items[0].State == ItemState.Ready);
        Assert.True(room.Queue.Items[0].IsOriginal);
        Assert.Empty(room.Backend.Lookups);
    });

    [Fact]
    public void Video_mode_uses_ordinary_search_not_the_track() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        room.Prefs.Audio = false;
        room.Backend.Results["Nova Harbor - Glass Tide"] = new() { FakeBackend.Video("v1", "Glass Tide (Official Video)") };
        room.Queue.Add(TestRoom.Entries("Nova Harbor - Glass Tide"));
        await room.Until(() => room.Queue.Items[0].State == ItemState.Ready);
        Assert.False(room.Queue.Items[0].IsOriginal);
        Assert.Equal("v1", room.Queue.Items[0].VideoId);
    });

    [Fact] // a list pasted in audio mode, switched to video before its lookups finished
    public void Switching_to_video_during_a_track_lookup_ends_on_the_video() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        room.Backend.HoldMusic = new TaskCompletionSource();
        room.Backend.Music["A - B"] = new() { FakeBackend.Track("t1", "B", "A") };
        room.Backend.Results["A - B"] = new() { FakeBackend.Video("v1", "B (clip)", "A") };
        room.Queue.Add(TestRoom.Entries("A - B"));
        room.Prefs.Audio = false;
        room.Queue.ModeChanged(false);
        room.Backend.HoldMusic.SetResult();
        await room.Until(() => room.Queue.Items[0].State == ItemState.Ready);
        Assert.Equal("v1", room.Queue.Items[0].VideoId);
        Assert.False(room.Queue.Items[0].IsOriginal);
    });

    [Fact]
    public void Switching_modes_moves_between_track_and_video() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        room.Backend.Music["A - B"] = new() { FakeBackend.Track("t1", "B", "A") };
        room.Backend.Results["A - B"] = new() { FakeBackend.Video("v1", "B (clip)", "A") };
        room.Queue.Add(TestRoom.Entries("A - B"));
        var item = room.Queue.Items[0];
        await room.Until(() => item.State == ItemState.Ready);
        Assert.Equal("t1", item.VideoId);
        room.Queue.SetItemMode(item, false);
        await room.Until(() => item.State == ItemState.Ready && item.VideoId == "v1", "the video");
        room.Queue.SetItemMode(item, true);
        await room.Until(() => item.VideoId == "t1", "back to the track");
    });

    [Fact]
    public void A_pasted_clip_becomes_its_track_and_stays_one_click_away() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        const string link = "https://www.youtube.com/watch?v=clip1";
        room.Backend.Results[link] = new() { FakeBackend.Video("clip1", "Cobalt Orchard - Honey Radio (Official Video)", "Cobalt Orchard Official") };
        room.Backend.Music["Cobalt Orchard - Honey Radio"] = new() { FakeBackend.Track("t9", "Honey Radio", "Cobalt Orchard") };
        room.Queue.Add(TestRoom.Entries(link));
        var item = room.Queue.Items[0];
        await room.Until(() => item.State == ItemState.Ready);
        Assert.Equal("t9", item.VideoId);
        Assert.Contains(item.Alternatives!, a => a.Str("id") == "clip1");
    });

    [Fact] // A link to another site is what the person asked for, even with track information in it
    public void A_link_to_another_site_is_never_swapped_for_a_track() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        const string link = "https://artist.bandcamp.com/track/song";
        room.Backend.Results[link] = new() { Json.Map(("id", "b1"), ("title", "Song"), ("track", "Song"), ("artist", "Artist"), ("webpage_url", link), ("extractor_key", "Bandcamp")) };
        room.Backend.Music["Artist - Song"] = new() { FakeBackend.Track("t1", "Song", "Artist") };
        room.Queue.Add(TestRoom.Entries(link));
        var item = room.Queue.Items[0];
        await room.Until(() => item.State == ItemState.Ready);
        Assert.Equal("b1", item.VideoId);
        Assert.Equal("artist.bandcamp.com", item.Site);
        room.Queue.StartAll();
        await room.Until(() => room.Backend.Downloads.Count == 1);
        Assert.False(room.Backend.Downloads[0].CutOfftopic);
    });

    [Fact] // a card that moved to another version no longer blocks the old one
    public void A_video_a_card_moved_away_from_can_be_added_again() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        room.Prefs.Audio = false;
        room.Backend.Results["first"] = new() { FakeBackend.Video("x", "First") };
        room.Backend.Results["second"] = new() { FakeBackend.Video("x", "First") };
        room.Queue.Add(TestRoom.Entries("first"));
        var item = room.Queue.Items[0];
        await room.Until(() => item.State == ItemState.Ready);
        var other = QueueController.MakeCandidate(FakeBackend.Video("z", "Other"), item);
        Assert.True(room.Queue.Pick(item, other));
        room.Queue.Add(TestRoom.Entries("second"));
        await room.Until(() => room.Queue.Items.Count == 2 && room.Queue.Items[1].State == ItemState.Ready, "the second card");
        Assert.Equal("x", room.Queue.Items[1].VideoId);
    });

    [Fact]
    public void The_same_video_found_twice_is_one_card() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        room.Prefs.Audio = false;
        room.Backend.Results["one"] = new() { FakeBackend.Video("x", "Song") };
        room.Backend.Results["two"] = new() { FakeBackend.Video("x", "Song") };
        room.Queue.Add(TestRoom.Entries("one", "two"));
        await room.Until(() => room.Queue.Items.Count == 1 && room.Queue.Items[0].State == ItemState.Ready, "one card");
    });

    [Fact]
    public void A_playlist_expands_into_cards() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        room.Prefs.Audio = false;
        const string link = "https://www.youtube.com/playlist?list=abc";
        room.Backend.Results[link] = new() { FakeBackend.Video("a", "One", playlist: "Mix"), FakeBackend.Video("b", "Two", playlist: "Mix"), FakeBackend.Video("c", "Three", playlist: "Mix") };
        room.Queue.Add(TestRoom.Entries(link));
        await room.Until(() => room.Queue.Items.Count == 3 && room.Queue.Items.All(i => i.State == ItemState.Ready), "three cards");
        Assert.Equal(new[] { "a", "b", "c" }, room.Queue.Items.Select(i => i.VideoId));
    });

    [Fact]
    public void Start_all_downloads_everything_and_reports_once() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        room.Prefs.Audio = false;
        foreach (string name in new[] { "a", "b", "c", "d", "e" }) room.Backend.Results[name + " x"] = new() { FakeBackend.Video(name, name) };
        room.Queue.Add(TestRoom.Entries("a x", "b x", "c x", "d x", "e x"));
        room.Queue.StartAll(); // while they are still being looked up
        await room.Until(() => room.Queue.Items.All(i => i.State == ItemState.Done), "all done");
        await room.Until(() => room.Runs.Count > 0, "the end of the run");
        Assert.Equal((5, 0), room.Runs.Single());
    });

    [Fact]
    public void Never_more_downloads_than_allowed() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        room.Prefs.Audio = false;
        room.Prefs.Parallel = 2;
        var release = new TaskCompletionSource<RunResult>();
        room.Backend.OnDownload = (_, _) => release.Task;
        foreach (string name in new[] { "a", "b", "c", "d" }) room.Backend.Results[name + " x"] = new() { FakeBackend.Video(name, name) };
        room.Queue.Add(TestRoom.Entries("a x", "b x", "c x", "d x"));
        await room.Until(() => room.Queue.Items.All(i => i.State == ItemState.Ready), "all ready");
        room.Queue.StartAll();
        await room.Until(() => room.Backend.Downloads.Count == 2, "two downloads");
        await Task.Delay(50);
        Assert.Equal(2, room.Backend.Downloads.Count);
        room.Queue.StopAll();
        release.SetResult(new RunResult(-1, true));
        await room.Until(() => room.Queue.Items.All(i => !i.IsActive), "stopped");
    });

    [Fact] // a limit is waited out, but not forever; the card then says what is wrong
    public void A_limit_that_never_lifts_ends_in_an_error() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        room.Prefs.Audio = false;
        Assert.False(Engine.IsThrottled("ERROR: rate-limit reached or login required"));
        Assert.False(Engine.IsThrottled("ERROR: Video 429816 is unavailable"));
        Assert.True(Engine.IsThrottled("ERROR: HTTP Error 429: Too Many Requests"));
        // A permanent error goes straight to the card instead of a wait.
        room.Backend.LookupErrors.Enqueue("ERROR: Private video");
        room.Queue.Add(TestRoom.Entries("https://www.youtube.com/watch?v=abc"));
        await room.Until(() => room.Queue.Items[0].State == ItemState.Failed, "a failure");
        Assert.True(room.Queue.Items[0].NeedsLogin);
    });

    [Fact] // the wait has an end
    public void A_rate_limit_is_retried_a_few_times_then_reported() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        room.Prefs.Audio = false;
        room.Queue.ThrottleBaseSeconds = 0;
        for (int n = 0; n < 20; n++) room.Backend.LookupErrors.Enqueue("ERROR: HTTP Error 429: Too Many Requests");
        room.Queue.Add(TestRoom.Entries("https://www.youtube.com/watch?v=abc"));
        await room.Until(() => room.Queue.Items[0].State == ItemState.Failed, "the failure");
        Assert.Equal(QueueController.MaxThrottleRetries + 1, room.Backend.Lookups.Count);
        Assert.Equal(Loc.T("error.throttled"), room.Queue.Items[0].StatusText);
    });

    [Fact] // A pause must not lose the cards: they come through once it lifts
    public void A_rate_limit_that_lifts_lets_the_card_through() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        room.Prefs.Audio = false;
        room.Queue.ThrottleBaseSeconds = 0;
        const string link = "https://www.youtube.com/watch?v=abc";
        room.Backend.Results[link] = new() { FakeBackend.Video("abc", "Song") };
        room.Backend.LookupErrors.Enqueue("ERROR: HTTP Error 429: Too Many Requests");
        room.Queue.Add(TestRoom.Entries(link));
        await room.Until(() => room.Queue.Items[0].State == ItemState.Ready, "the card");
        Assert.Equal(2, room.Backend.Lookups.Count);
    });

    [Fact] // a run with no result must not celebrate
    public void Stopping_everything_is_not_reported_as_success() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        room.Prefs.Audio = false;
        var release = new TaskCompletionSource<RunResult>();
        room.Backend.OnDownload = (_, _) => release.Task;
        room.Backend.Results["a x"] = new() { FakeBackend.Video("a", "a") };
        room.Queue.Add(TestRoom.Entries("a x"));
        await room.Until(() => room.Queue.Items[0].State == ItemState.Ready);
        room.Queue.StartAll();
        await room.Until(() => room.Queue.Items[0].IsActive);
        room.Queue.Cancel(room.Queue.Items[0]);
        release.SetResult(new RunResult(-1, true));
        await room.Until(() => room.Queue.Items[0].State == ItemState.Cancelled);
        Assert.Empty(room.Runs);
    });

    [Fact]
    public void A_file_already_in_the_folder_is_skipped_for_its_format_only() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        File.WriteAllText(Path.Combine(room.Prefs.Folder, "Song [t1].mp3"), "x");
        room.Backend.Music["A - Song"] = new() { FakeBackend.Track("t1", "Song", "A") };
        room.Queue.Add(TestRoom.Entries("A - Song"));
        var item = room.Queue.Items[0];
        await room.Until(() => item.State == ItemState.Ready);
        Assert.True(item.ShowExists);
        room.Queue.StartAll();
        Assert.Equal(ItemState.Done, item.State);
        Assert.Empty(room.Backend.Downloads);
    });

    [Fact] // extracting audio must not delete a video of the same id that is already there
    public void Audio_download_keeps_an_existing_video_of_the_same_id() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        File.WriteAllText(Path.Combine(room.Prefs.Folder, "Song [t1].mp4"), "x");
        room.Backend.Music["A - Song"] = new() { FakeBackend.Track("t1", "Song", "A") };
        room.Queue.Add(TestRoom.Entries("A - Song"));
        var item = room.Queue.Items[0];
        await room.Until(() => item.State == ItemState.Ready);
        room.Queue.StartAll();
        await room.Until(() => item.State == ItemState.Done);
        Assert.True(room.Backend.Downloads.Single().KeepSource);
        Assert.Contains("--keep-video", Engine.DownloadArgs(room.Backend.Downloads.Single(), Array.Empty<string>()));
    });

    [Fact] // Other sites serve formats mp4 cannot hold, and a failed conversion leaves an empty file behind
    public void A_video_that_does_not_fit_the_container_lands_in_mkv() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        room.Prefs.Audio = false;
        room.Backend.Results["a x"] = new() { FakeBackend.Video("a", "a") };
        string empty = "";
        room.Backend.OnDownload = (options, line) =>
        {
            empty = Path.Combine(options.Folder, "a [a].mp4");
            if (options.Container == "mp4")
            {
                File.WriteAllText(empty, "");
                line("ERROR: Postprocessing: Conversion failed!");
                return Task.FromResult(new RunResult(1, false));
            }
            string file = Path.Combine(options.Folder, "a [a].mkv");
            File.WriteAllText(file, "x");
            line("__FILE__" + file);
            return Task.FromResult(new RunResult(0, false));
        };
        room.Queue.Add(TestRoom.Entries("a x"));
        await room.Until(() => room.Queue.Items[0].State == ItemState.Ready);
        room.Queue.StartAll();
        await room.Until(() => room.Queue.Items[0].State == ItemState.Done, "the retry");
        Assert.Equal(2, room.Backend.Downloads.Count);
        Assert.False(File.Exists(empty));
        Assert.EndsWith(".mkv", room.Queue.Items[0].FilePath);
    });

    // ---------- Moving a library ----------

    static Dictionary<string, object?> TrackOf(string id, string title, string artist, double seconds)
    {
        var track = FakeBackend.Track(id, title, artist);
        track["duration"] = seconds;
        return track;
    }

    [Fact]
    public void An_imported_song_of_another_length_is_flagged_but_still_downloads() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        room.Backend.Music["Nova Harbor - Glass Tide"] = new() { TrackOf("t1", "Glass Tide", "Nova Harbor", 120) };
        room.Queue.AddTracks(new[] { new LibraryTrack("Nova Harbor", "Glass Tide", "", 247) });
        var item = room.Queue.Items[0];
        await room.Until(() => item.State == ItemState.Ready);
        Assert.True(item.ShowReview);
        Assert.Equal(1, room.Queue.Summary().Review);
        room.Queue.AcceptMatch(item);
        Assert.False(item.ShowReview);
        room.Queue.StartAll();
        await room.Until(() => item.State == ItemState.Done);
    });

    [Fact]
    public void An_ISRC_finds_the_exact_recording() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        room.Backend.Music["XX1234567890"] = new() { TrackOf("exact", "Glass Tide", "Nova Harbor", 247) };
        room.Backend.Music["Nova Harbor - Glass Tide"] = new() { TrackOf("other", "Glass Tide", "Nova Harbor", 247) };
        room.Queue.AddTracks(new[] { new LibraryTrack("Nova Harbor", "Glass Tide", "", 247, "", null, "XX1234567890") });
        var item = room.Queue.Items[0];
        await room.Until(() => item.State == ItemState.Ready);
        Assert.Equal("exact", item.VideoId);
    });

    [Fact]
    public void An_ISRC_that_leads_to_another_song_is_not_trusted() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        room.Backend.Music["XX1234567890"] = new() { TrackOf("wrong", "Something Else", "Somebody", 100) };
        room.Backend.Music["Nova Harbor - Glass Tide"] = new() { TrackOf("right", "Glass Tide", "Nova Harbor", 247) };
        room.Queue.AddTracks(new[] { new LibraryTrack("Nova Harbor", "Glass Tide", "", 247, "", null, "XX1234567890") });
        var item = room.Queue.Items[0];
        await room.Until(() => item.State == ItemState.Ready);
        Assert.Equal("right", item.VideoId);
    });

    [Fact]
    public void A_music_search_that_keeps_failing_is_mentioned_once() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        room.Backend.MusicFails = true;
        foreach (string name in new[] { "a b", "c d", "e f", "g h" }) room.Backend.Results[name] = new() { FakeBackend.Video(name[..1], name) };
        room.Queue.Add(TestRoom.Entries("a b", "c d", "e f", "g h"));
        await room.Until(() => room.Queue.Items.All(i => i.State == ItemState.Ready));
        Assert.Single(room.Toasts, t => t.Text == Loc.T("toast.music.down"));
    });

    [Fact]
    public void One_card_can_have_its_own_quality() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        room.Prefs.Audio = false;
        room.Prefs.Height = 1080;
        room.Backend.Results["a x"] = new() { FakeBackend.Video("a", "a") };
        room.Queue.Add(TestRoom.Entries("a x"));
        var item = room.Queue.Items[0];
        await room.Until(() => item.State == ItemState.Ready);
        room.Queue.SetItemQuality(item, 720);
        room.Queue.StartOne(item);
        await room.Until(() => room.Backend.Downloads.Count == 1);
        Assert.Equal(720, room.Backend.Downloads[0].Height);
    });

    [Fact]
    public void A_link_to_a_closed_service_says_what_to_do() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        room.Backend.LookupErrors.Enqueue("ERROR: Unsupported URL: https://open.spotify.com/playlist/abc");
        room.Queue.Add(new[] { "https://open.spotify.com/playlist/abc" });
        var item = room.Queue.Items[0];
        await room.Until(() => item.State == ItemState.Failed);
        Assert.Equal(Loc.T("error.musicservice"), item.StatusText);
        Assert.Equal("https://open.spotify.com/playlist/abc", room.Queue.FailedList());
    });

    [Fact]
    public void A_channel_counts_as_it_is_listed_and_each_tab_is_its_own_list() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        room.Prefs.Audio = false;
        const string link = "https://www.youtube.com/@someone";
        room.Backend.Results[link] = new() { FakeBackend.Video("a", "First", null, "Someone - Videos"), FakeBackend.Video("b", "Second", null, "Someone - Shorts") };
        room.Backend.HoldLookups = new TaskCompletionSource();
        room.Queue.Add(new[] { link });
        await room.Until(() => room.Queue.Items[0].StatusText == Loc.T("status.found", 40), "the count");
        room.Backend.HoldLookups.SetResult();
        await room.Until(() => room.Queue.Items.Count == 2 && room.Queue.Items.All(i => i.State == ItemState.Ready));
        Assert.Equal("Someone - Videos", room.Queue.Items[0].Lists.Single());
        Assert.Equal("Someone - Shorts", room.Queue.Items[1].Lists.Single());
    });

    [Fact]
    public void The_rest_of_a_playlist_can_be_added_from_one_of_its_videos() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        room.Prefs.Audio = false;
        const string one = "https://www.youtube.com/watch?v=a&list=PLxyz", all = "https://www.youtube.com/playlist?list=PLxyz";
        room.Backend.Results[one] = new() { FakeBackend.Video("a", "First") };
        room.Backend.Results[all] = new() { FakeBackend.Video("a", "First", null, "Road"), FakeBackend.Video("b", "Second", null, "Road") };
        room.Queue.Add(new[] { one });
        await room.Until(() => room.Queue.Items[0].State == ItemState.Ready);
        Assert.True(room.Queue.Items[0].CanExpandPlaylist);
        room.Queue.ExpandPlaylist(room.Queue.Items[0]);
        await room.Until(() => room.Queue.Items.Count == 2 && room.Queue.Items.All(i => i.State == ItemState.Ready), "the rest");
        Assert.Equal(new[] { "a", "b" }, room.Queue.Items.Select(i => i.VideoId).OrderBy(i => i));
    });

    [Fact]
    public void A_browser_that_cannot_be_read_does_not_cost_a_public_link() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        room.Prefs.Audio = false;
        Auth.UseBrowser("whale");
        room.Backend.Results["a x"] = new() { FakeBackend.Video("a", "a") };
        room.Backend.LookupErrors.Enqueue("ERROR: could not find whale cookies database");
        room.Queue.Add(TestRoom.Entries("a x"));
        var item = room.Queue.Items[0];
        await room.Until(() => item.State == ItemState.Ready, "the lookup without the browser");
        Assert.True(Auth.Suspended);
        Assert.Empty(Auth.Lease().Args);
        Assert.Single(room.Toasts, t => t.Kind == ToastKind.Warning);
        Auth.SignOut();
    });

    [Fact]
    public void A_stopped_conversion_leaves_no_file_that_looks_finished() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        room.Prefs.Audio = false;
        string half = Path.Combine(room.Prefs.Folder, "a [a].mp3");
        room.Backend.Results["a x"] = new() { FakeBackend.Video("a", "a") };
        room.Backend.OnDownload = (_, line) =>
        {
            File.WriteAllText(half, "half");
            line("[ExtractAudio] Destination: " + half);
            return Task.FromResult(new RunResult(-1, true));
        };
        room.Queue.Add(TestRoom.Entries("a x"));
        var item = room.Queue.Items[0];
        await room.Until(() => item.State == ItemState.Ready);
        room.Queue.StartOne(item);
        await room.Until(() => item.State == ItemState.Cancelled);
        Assert.False(File.Exists(half));
    });

    [Fact]
    public void A_full_disk_stops_what_is_waiting() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        room.Prefs.Audio = false;
        room.Prefs.Parallel = 1;
        foreach (string name in new[] { "a x", "b x", "c x" }) room.Backend.Results[name] = new() { FakeBackend.Video(name[..1], name) };
        room.Backend.OnDownload = (_, line) =>
        {
            line("ERROR: [Errno 28] No space left on device");
            return Task.FromResult(new RunResult(1, false));
        };
        room.Queue.Add(TestRoom.Entries("a x", "b x", "c x"));
        await room.Until(() => room.Queue.Items.All(i => i.State == ItemState.Ready));
        room.Queue.StartAll();
        await room.Until(() => room.Queue.Items.Any(i => i.State == ItemState.Failed));
        await room.Until(() => room.Queue.Items.Count(i => i.State == ItemState.Ready) == 2);
        Assert.Single(room.Backend.Downloads);
    });

    [Fact]
    public void A_file_can_be_downloaded_again_in_another_quality() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        room.Prefs.Audio = false;
        File.WriteAllText(Path.Combine(room.Prefs.Folder, "a [a].mp4"), "x");
        room.Backend.Results["a x"] = new() { FakeBackend.Video("a", "a") };
        room.Queue.Add(TestRoom.Entries("a x"));
        var item = room.Queue.Items[0];
        await room.Until(() => item.State == ItemState.Ready);
        Assert.True(item.ShowExists);
        room.Queue.SetItemQuality(item, 1080);
        Assert.False(item.ShowExists);
        room.Queue.StartOne(item);
        await room.Until(() => room.Backend.Downloads.Count == 1);
        Assert.True(room.Backend.Downloads[0].Overwrite);
    });

    [Fact]
    public void Deleted_videos_and_a_playlist_of_one_are_handled() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        room.Prefs.Audio = false;
        const string link = "https://www.youtube.com/playlist?list=abc";
        room.Backend.Results[link] = new() { FakeBackend.Video("a", "First", null, "Road"), FakeBackend.Video("d", "[Deleted video]", null, "Road") };
        room.Queue.Add(new[] { link });
        await room.Until(() => room.Queue.Items.Count == 1 && room.Queue.Items[0].State == ItemState.Ready);
        Assert.Equal("Road", room.Queue.Items[0].Lists.Single());
        Assert.True(room.Queue.Items[0].CanFollow);
    });

    [Fact]
    public void Retrying_a_thousand_failures_stays_quick() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        room.Prefs.Audio = false;
        var names = Enumerable.Range(0, 1000).Select(n => "song " + n).ToArray();
        foreach (string name in names) room.Backend.Results[name] = new() { FakeBackend.Video(name.Replace(' ', '_'), name) };
        room.Queue.Add(TestRoom.Entries(names));
        await room.Until(() => room.Queue.Items.All(i => i.State == ItemState.Ready), "the lookups");
        foreach (var item in room.Queue.Items) item.State = ItemState.Failed;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        room.Backend.OnDownload = (_, _) => new TaskCompletionSource<RunResult>().Task; // nothing finishes: only starting is measured
        room.Queue.RetryFailed();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), clock.Elapsed.ToString());
        foreach (var item in room.Queue.Items) item.Runner?.Cancel();
    });

    [Fact]
    public void A_song_reached_from_two_playlists_lands_in_both() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        room.Prefs.Audio = false;
        const string one = "https://www.youtube.com/playlist?list=one", two = "https://www.youtube.com/playlist?list=two";
        room.Backend.Results[one] = new() { FakeBackend.Video("a", "First", null, "Road"), FakeBackend.Video("b", "Second", null, "Road") };
        room.Backend.Results[two] = new() { FakeBackend.Video("a", "First", null, "Home"), FakeBackend.Video("c", "Third", null, "Home") };
        room.Queue.Add(new[] { one });
        await room.Until(() => room.Queue.Items.Count == 2 && room.Queue.Items.All(i => i.State == ItemState.Ready));
        room.Queue.Add(new[] { two });
        await room.Until(() => room.Queue.Items.Count == 3 && room.Queue.Items.All(i => i.State == ItemState.Ready));
        Assert.Contains("Home", room.Queue.Items.Single(i => i.VideoId == "a").Lists);
    });

    [Fact] // the second try finds the first one's download, and the card must still say it was just downloaded
    public void A_retry_is_not_taken_for_a_file_that_was_there() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        room.Prefs.Audio = false;
        room.Backend.Results["a x"] = new() { FakeBackend.Video("a", "a") };
        int calls = 0;
        room.Backend.OnDownload = (options, line) =>
        {
            calls++;
            if (calls == 1)
            {
                line("ERROR: Postprocessing: Conversion failed!");
                return Task.FromResult(new RunResult(1, false));
            }
            string file = Path.Combine(options.Folder, "a [a].mkv");
            File.WriteAllText(file, "x");
            line("[download] a [a].ogv has already been downloaded");
            line("__FILE__" + file);
            return Task.FromResult(new RunResult(0, false));
        };
        room.Queue.Add(TestRoom.Entries("a x"));
        var item = room.Queue.Items[0];
        await room.Until(() => item.State == ItemState.Ready);
        room.Queue.StartOne(item);
        await room.Until(() => item.State == ItemState.Done);
        Assert.StartsWith(Loc.T("status.done", "", "")[..4], item.StatusText);
    });

    [Fact]
    public void A_part_the_site_turned_away_is_tried_again() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        room.Prefs.Audio = false;
        room.Backend.Results["a x"] = new() { FakeBackend.Video("a", "a") };
        int calls = 0;
        room.Backend.OnDownload = (options, line) =>
        {
            if (++calls == 1)
            {
                line("ERROR: ffmpeg exited with code 1");
                return Task.FromResult(new RunResult(1, false));
            }
            string file = Path.Combine(options.Folder, "a [a clip 60-90].mp4");
            File.WriteAllText(file, "x");
            line("__FILE__" + file);
            return Task.FromResult(new RunResult(0, false));
        };
        room.Queue.Add(TestRoom.Entries("a x"));
        var item = room.Queue.Items[0];
        await room.Until(() => item.State == ItemState.Ready);
        room.Queue.SetSection(item, "1:00", "1:30");
        room.Queue.StartOne(item);
        await room.Until(() => item.State == ItemState.Done, "the second try");
        Assert.Equal(2, calls);
    });

    [Fact]
    public void An_exact_match_needs_no_check() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        room.Backend.Music["Nova Harbor - Glass Tide"] = new() { TrackOf("t1", "Glass Tide", "Nova Harbor", 247) };
        room.Queue.AddTracks(new[] { new LibraryTrack("Nova Harbor", "Glass Tide", "", 248) });
        var item = room.Queue.Items[0];
        await room.Until(() => item.State == ItemState.Ready);
        Assert.False(item.ShowReview);
    });

    [Fact]
    public void A_name_several_artists_share_is_flagged() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        room.Backend.Music["Halogen"] = new() { TrackOf("t1", "Halogen", "Ivy Lane", 200), TrackOf("t2", "Halogen", "Nova Harbor", 180) };
        room.Queue.Add(TestRoom.Entries("Halogen"));
        var item = room.Queue.Items[0];
        await room.Until(() => item.State == ItemState.Ready);
        Assert.True(item.ShowReview);
    });

    [Fact]
    public void A_video_that_is_not_the_song_is_flagged_and_a_choice_clears_it() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        room.Prefs.OriginalTracks = false;
        room.Backend.Results["Nova Harbor - Glass Tide"] = new() { FakeBackend.Video("v1", "Something else entirely"), FakeBackend.Video("v2", "Nova Harbor - Glass Tide") };
        room.Queue.Add(TestRoom.Entries("Nova Harbor - Glass Tide"));
        var item = room.Queue.Items[0];
        await room.Until(() => item.State == ItemState.Ready);
        Assert.True(item.ShowReview);
        Assert.True(room.Queue.Pick(item, QueueController.MakeCandidate(FakeBackend.Video("v2", "Nova Harbor - Glass Tide"), item)));
        Assert.False(item.ShowReview);
    });

    [Fact]
    public void A_song_in_two_playlists_comes_once_and_lands_in_both_files() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        room.Backend.Music["Nova Harbor - Glass Tide"] = new() { TrackOf("t1", "Glass Tide", "Nova Harbor", 247) };
        var (added, _) = room.Queue.AddTracks(new[] { new LibraryTrack("Nova Harbor", "Glass Tide", List: "Road"), new LibraryTrack("Nova Harbor", "Glass Tide", List: "Home") });
        Assert.Equal(1, added);
        var item = room.Queue.Items[0];
        await room.Until(() => item.State == ItemState.Ready);
        room.Queue.StartAll();
        await room.Until(() => item.State == ItemState.Done);
        foreach (string list in new[] { "Road", "Home" })
        {
            string[] lines = File.ReadAllLines(Path.Combine(room.Prefs.Folder, list + ".m3u8"));
            Assert.Equal("#EXTM3U", lines[0]);
            Assert.Contains(Path.GetFileName(item.FilePath!), lines);
        }
    });

    [Fact]
    public void A_later_run_adds_to_the_playlist_file() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        string first = Path.Combine(room.Prefs.Folder, "old [o].mp3");
        File.WriteAllText(first, "x");
        File.WriteAllText(Path.Combine(room.Prefs.Folder, "Road.m3u8"), "#EXTM3U\n#EXTINF:10,Old\nold [o].mp3\n#EXTINF:10,Gone\ngone [g].mp3\n");
        room.Backend.Music["Nova Harbor - Glass Tide"] = new() { TrackOf("t1", "Glass Tide", "Nova Harbor", 247) };
        room.Queue.AddTracks(new[] { new LibraryTrack("Nova Harbor", "Glass Tide", List: "Road") });
        var item = room.Queue.Items[0];
        await room.Until(() => item.State == ItemState.Ready);
        room.Queue.StartAll();
        await room.Until(() => item.State == ItemState.Done);
        string[] lines = File.ReadAllLines(Path.Combine(room.Prefs.Folder, "Road.m3u8"));
        Assert.Contains("old [o].mp3", lines);
        Assert.DoesNotContain("gone [g].mp3", lines);
        Assert.Contains(Path.GetFileName(item.FilePath!), lines);
    });

    [Fact]
    public void A_song_already_there_under_its_plain_name_is_skipped() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        File.WriteAllText(Path.Combine(room.Prefs.Folder, "Nova Harbor - Glass Tide.flac"), "x");
        room.Backend.Music["Nova Harbor - Glass Tide"] = new() { TrackOf("t1", "Glass Tide", "Nova Harbor", 247) };
        room.Queue.Add(TestRoom.Entries("Nova Harbor - Glass Tide"));
        var item = room.Queue.Items[0];
        await room.Until(() => item.State == ItemState.Ready);
        Assert.NotNull(item.ExistingPath);
        room.Queue.StartAll();
        await room.Until(() => item.State == ItemState.Done);
        Assert.Empty(room.Backend.Downloads);
    });

    [Fact]
    public void What_was_deleted_stays_skipped_when_asked() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        room.Prefs.SkipDownloaded = true;
        room.Backend.Music["Nova Harbor - Glass Tide"] = new() { TrackOf("t1", "Glass Tide", "Nova Harbor", 247) };
        room.Queue.Add(TestRoom.Entries("Nova Harbor - Glass Tide"));
        var item = room.Queue.Items[0];
        await room.Until(() => item.State == ItemState.Ready);
        room.Queue.StartAll();
        await room.Until(() => item.State == ItemState.Done);
        File.Delete(item.FilePath!);
        room.Queue.ClearDone();

        room.Queue.Add(TestRoom.Entries("Nova Harbor - Glass Tide"));
        var again = room.Queue.Items[0];
        await room.Until(() => again.State == ItemState.Ready);
        Assert.True(again.ShowHistory);
        room.Queue.SetItemMode(again, false); // audio having been saved says nothing about the video
        Assert.False(again.ShowHistory);
        room.Queue.SetItemMode(again, true);
        Assert.True(again.ShowHistory);
        room.Queue.StartAll();
        Assert.Single(room.Backend.Downloads);
        room.Queue.StartOne(again); // one on purpose still works
        await room.Until(() => again.State == ItemState.Done);
        Assert.Equal(2, room.Backend.Downloads.Count);
    });

    // ---------- Repairing itself ----------

    [Fact]
    public void An_outdated_tool_updates_itself_and_the_download_runs_again() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        room.Prefs.Audio = false;
        room.Backend.Results["a x"] = new() { FakeBackend.Video("a", "a") };
        int calls = 0;
        room.Backend.OnDownload = (options, line) =>
        {
            calls++;
            if (calls == 1)
            {
                line("ERROR: unable to download video data: HTTP Error 403: Forbidden");
                return Task.FromResult(new RunResult(1, false));
            }
            string file = Path.Combine(options.Folder, "a [a].mp4");
            File.WriteAllText(file, "x");
            line("__FILE__" + file);
            return Task.FromResult(new RunResult(0, false));
        };
        room.Queue.Add(TestRoom.Entries("a x"));
        var item = room.Queue.Items[0];
        await room.Until(() => item.State == ItemState.Ready);
        room.Queue.StartAll();
        await room.Until(() => item.State == ItemState.Done, "the repair");
        Assert.Equal(1, room.Backend.Updates);
        Assert.Equal(2, calls);
    });

    [Fact]
    public void When_stable_is_already_newest_nightly_is_tried() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        room.Prefs.Audio = false;
        room.Backend.UpdateChanges = false;
        room.Backend.Results["a x"] = new() { FakeBackend.Video("a", "a") };
        room.Backend.OnDownload = (_, line) =>
        {
            line("ERROR: HTTP Error 403: Forbidden");
            return Task.FromResult(new RunResult(1, false));
        };
        room.Queue.Add(TestRoom.Entries("a x"));
        var item = room.Queue.Items[0];
        await room.Until(() => item.State == ItemState.Ready);
        room.Queue.StartAll();
        await room.Until(() => item.State == ItemState.Failed);
        Assert.Equal(1, room.Backend.Updates);
        Assert.Equal(1, room.Backend.NightlyUpdates);
    });

    [Fact]
    public void The_repair_is_tried_once_per_download() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        room.Prefs.Audio = false;
        room.Backend.Results["a x"] = new() { FakeBackend.Video("a", "a") };
        int calls = 0;
        room.Backend.OnDownload = (_, line) =>
        {
            calls++;
            line("ERROR: HTTP Error 403: Forbidden");
            return Task.FromResult(new RunResult(1, false));
        };
        room.Queue.Add(TestRoom.Entries("a x"));
        var item = room.Queue.Items[0];
        await room.Until(() => item.State == ItemState.Ready);
        room.Queue.StartAll();
        await room.Until(() => item.State == ItemState.Failed);
        Assert.Equal(2, calls);
        Assert.Equal(1, room.Backend.Updates);
    });

    // ---------- Extras ----------

    [Fact]
    public void Lyrics_land_next_to_the_song() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        room.Prefs.Lyrics = true;
        room.Backend.LyricsText = "[00:01] words";
        room.Backend.Music["Nova Harbor - Glass Tide"] = new() { TrackOf("t1", "Glass Tide", "Nova Harbor", 247) };
        room.Queue.Add(TestRoom.Entries("Nova Harbor - Glass Tide"));
        var item = room.Queue.Items[0];
        await room.Until(() => item.State == ItemState.Ready);
        room.Queue.StartAll();
        await room.Until(() => item.State == ItemState.Done);
        string lyrics = Path.ChangeExtension(item.FilePath!, ".lrc");
        await room.Until(() => File.Exists(lyrics), "the lyrics");
        Assert.Equal("[00:01] words", File.ReadAllText(lyrics));
    });

    [Fact]
    public void A_part_of_a_video_is_not_mistaken_for_the_whole() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        room.Prefs.Audio = false;
        File.WriteAllText(Path.Combine(room.Prefs.Folder, "a [a].mp4"), "x");
        room.Backend.Results["a x"] = new() { FakeBackend.Video("a", "a") };
        room.Queue.Add(TestRoom.Entries("a x"));
        var item = room.Queue.Items[0];
        await room.Until(() => item.State == ItemState.Ready);
        Assert.NotNull(item.ExistingPath);
        Assert.True(room.Queue.SetSection(item, "1:30", "2:00"));
        Assert.Equal("90-120", item.Section);
        Assert.Null(item.ExistingPath);
        Assert.False(room.Queue.SetSection(item, "2:00", "1:00"));
        room.Queue.StartOne(item);
        await room.Until(() => room.Backend.Downloads.Count == 1);
        Assert.Equal("90-120", room.Backend.Downloads[0].Section);
    });

    [Fact]
    public void A_followed_playlist_brings_only_what_is_new() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        room.Prefs.Audio = false;
        const string link = "https://www.youtube.com/playlist?list=abc";
        room.Backend.Results[link] = new() { FakeBackend.Video("a", "First", null, "Road"), FakeBackend.Video("b", "Second", null, "Road") };
        room.Queue.Add(new[] { link });
        await room.Until(() => room.Queue.Items.Count == 2 && room.Queue.Items.All(i => i.State == ItemState.Ready));
        var first = room.Queue.Items[0];
        room.Queue.Follow(first);
        Assert.Single(room.Queue.Followed);
        room.Queue.StartAll();
        await room.Until(() => room.Queue.Items.All(i => i.State == ItemState.Done));
        room.Queue.ClearDone();

        room.Backend.Results[link].Add(FakeBackend.Video("c", "Third", null, "Road"));
        room.Queue.CheckFollowed(true);
        await room.Until(() => room.Queue.Items.Count == 1 && room.Queue.Items[0].State == ItemState.Done, "the new video");
        Assert.Equal("c", room.Queue.Items[0].VideoId);
        Assert.Equal(3, room.Backend.Downloads.Count);
    });

    [Fact]
    public void A_followed_playlist_keeps_its_mode_and_a_failed_check_comes_again() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        room.Prefs.Audio = false;
        const string link = "https://www.youtube.com/playlist?list=abc";
        room.Backend.Results[link] = new() { FakeBackend.Video("a", "First", null, "Road"), FakeBackend.Video("b", "Second", null, "Road") };
        room.Queue.Add(new[] { link });
        await room.Until(() => room.Queue.Items.Count == 2 && room.Queue.Items.All(i => i.State == ItemState.Ready));
        room.Queue.Follow(room.Queue.Items[0]);
        room.Queue.ClearAll();
        room.Queue.Followed[0].Checked = DateTime.MinValue;
        room.Prefs.Audio = true;
        room.Backend.Results[link].Add(FakeBackend.Video("c", "Third", null, "Road"));

        room.Backend.LookupErrors.Enqueue("ERROR: Video unavailable");
        room.Queue.CheckFollowed(false);
        await room.Until(() => room.Queue.Items.Count == 1 && room.Queue.Items[0].State == ItemState.Failed);
        Assert.Equal(DateTime.MinValue, room.Queue.Followed[0].Checked);

        room.Queue.CheckFollowed(false);
        await room.Until(() => room.Queue.Items.Count == 1 && room.Queue.Items[0].State == ItemState.Done);
        Assert.Equal("c", room.Queue.Items[0].VideoId); // what was there when followed stays out
        Assert.All(room.Queue.Items, i => Assert.False(i.AudioMode));
        Assert.NotEqual(DateTime.MinValue, room.Queue.Followed[0].Checked);
    });

    [Fact]
    public void Followed_playlists_are_remembered() => SingleThread.Run(() =>
    {
        using var room = new TestRoom();
        FollowedList.Save(new[] { new FollowedList { Url = "https://example.com/p", Name = "Road", Checked = DateTime.Now } });
        using var next = new QueueController(room.Backend, room.Prefs);
        Assert.Equal("Road", Assert.Single(next.Followed).Name);
        next.CheckFollowed(false);
        Assert.Empty(next.Items); // checked a moment ago
        return Task.CompletedTask;
    });

    [Fact] // an outage of the cutting service must not cost the file
    public void A_sponsorblock_outage_retries_without_cutting() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        room.Prefs.Audio = false;
        room.Prefs.SponsorBlock = true;
        room.Backend.Results["a x"] = new() { FakeBackend.Video("a", "a") };
        int calls = 0;
        room.Backend.OnDownload = (options, line) =>
        {
            calls++;
            if (calls == 1)
            {
                line("ERROR: Unable to communicate with SponsorBlock API");
                return Task.FromResult(new RunResult(1, false));
            }
            string file = Path.Combine(options.Folder, "a [a].mp4");
            File.WriteAllText(file, "x");
            line("__FILE__" + file);
            return Task.FromResult(new RunResult(0, false));
        };
        room.Queue.Add(TestRoom.Entries("a x"));
        await room.Until(() => room.Queue.Items[0].State == ItemState.Ready);
        room.Queue.StartAll();
        await room.Until(() => room.Queue.Items[0].State == ItemState.Done, "the retry");
        Assert.Equal(2, calls);
    });

    [Fact]
    public void A_failed_download_explains_itself_and_can_be_retried() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        room.Prefs.Audio = false;
        room.Backend.Results["a x"] = new() { FakeBackend.Video("a", "a") };
        room.Backend.OnDownload = (_, line) => { line("ERROR: Sign in to confirm your age"); return Task.FromResult(new RunResult(1, false)); };
        room.Queue.Add(TestRoom.Entries("a x"));
        var item = room.Queue.Items[0];
        await room.Until(() => item.State == ItemState.Ready);
        room.Queue.StartAll();
        await room.Until(() => item.State == ItemState.Failed);
        Assert.True(item.NeedsLogin);
        Assert.Contains(room.Toasts, t => t.Kind == ToastKind.Warning);
        room.Backend.OnDownload = null;
        room.Queue.Retry(item);
        await room.Until(() => item.State == ItemState.Done, "the retry");
    });

    [Fact] // both versions survive a restart, so switching still works
    public void A_restored_card_still_knows_both_of_its_versions() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        const string link = "https://www.youtube.com/watch?v=clip1";
        room.Backend.Results[link] = new() { FakeBackend.Video("clip1", "Cobalt Orchard - Honey Radio (Official Video)", "Cobalt Orchard Official") };
        room.Backend.Music["Cobalt Orchard - Honey Radio"] = new() { FakeBackend.Track("t9", "Honey Radio", "Cobalt Orchard") };
        room.Queue.Add(TestRoom.Entries(link));
        await room.Until(() => room.Queue.Items[0].State == ItemState.Ready);
        room.Queue.SaveQueue(true);

        var again = new QueueController(room.Backend, room.Prefs) { MusicDelayMilliseconds = 0 };
        Assert.Equal(1, again.RestoreQueue());
        var item = again.Items[0];
        Assert.Equal("t9", item.VideoId);
        again.SetItemMode(item, false);
        Assert.Equal("clip1", item.VideoId);
        Assert.Equal(link, item.SourceUrl);
    });

    [Fact]
    public void Removing_a_search_card_leaves_its_batch_running() => SingleThread.Run(async () =>
    {
        using var room = new TestRoom();
        room.Prefs.Audio = false;
        room.Backend.HoldLookups = new TaskCompletionSource();
        room.Backend.Results["a x"] = new() { FakeBackend.Video("a", "a") };
        room.Backend.Results["b x"] = new() { FakeBackend.Video("b", "b") };
        room.Queue.Add(TestRoom.Entries("a x", "b x"));
        room.Queue.Remove(room.Queue.Items[0]);
        room.Backend.HoldLookups.SetResult();
        await room.Until(() => room.Queue.Items.Count == 1 && room.Queue.Items[0].State == ItemState.Ready, "the other card");
        Assert.Equal("b", room.Queue.Items[0].VideoId);
    });
}
