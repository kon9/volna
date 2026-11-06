using System.Diagnostics;
using System.Globalization;
using System.Text;
using Avalonia.Threading;
using Volna.App;
using Volna.Core;

namespace Volna.Demo;

/// <summary>
/// Real downloads through the app's own queue and through the agent, every file then checked with ffprobe: what a
/// person actually gets, not what the code means to do. Only openly licensed media. It needs the network and takes a
/// while, so it runs before a release rather than on every change:
///   Demo acceptance &lt;folder&gt; [--agent path/to/volna-agent] [--only name,name]
/// </summary>
static class Acceptance
{
    const string Bunny = "https://www.youtube.com/watch?v=aqz-KE-bpKQ";              // Big Buck Bunny, Blender Foundation, CC BY
    const string Captions = "https://www.youtube.com/watch?v=sO_lhW8av3U";           // Blender Conference recap: automatic captions only
    const string Trailer = "https://commons.wikimedia.org/wiki/File:Big_Buck_Bunny_Trailer_400p.ogv";
    const string Archive = "https://archive.org/details/Popeye_forPresident";        // public domain
    const string Playlist = "https://www.youtube.com/playlist?list=PLa1F2ddGya_-HcK589tF_9EJgvO4ha1CM"; // Blender development updates, about ten
    const string Channel = "https://www.youtube.com/@BlenderOfficial";
    const string Song = "Kevin MacLeod - Sneaky Snitch", OtherSong = "Kevin MacLeod - Monkeys Spinning Monkeys"; // CC BY

    static readonly List<(string Scenario, bool Ok, string Detail)> Results = new();
    static string root = "";
    static StreamWriter? log;
    static string scenario = "";

    public static int Run(string folder, string[] args)
    {
        root = Path.GetFullPath(folder);
        Directory.CreateDirectory(root);
        string? agent = Option(args, "--agent");
        var only = (Option(args, "--only") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        using var writer = new StreamWriter(Path.Combine(root, "acceptance.log"), false, new UTF8Encoding(false));
        log = writer;
        Loc.Use("en");
        if (ToolManager.Missing().Count > 0) Fail("tools", "missing: " + string.Join(", ", ToolManager.Missing()) + ". Run Demo install first.");
        else
        {
            var scenarios = new (string Name, Action Run)[]
            {
                ("video-1080-mp4", VideoFullHd),
                ("video-720-mkv-part", VideoPart),
                ("video-max-part", VideoMax),
                ("video-subtitles", VideoSubtitles),
                ("audio-mp3-by-name", () => AudioByName("mp3", "mp3", "mp3")),
                ("audio-m4a-by-name", () => AudioByName("m4a", "m4a", "aac")),
                ("audio-best-by-name", () => AudioByName("best", null, null)),
                ("other-site-video", OtherSiteVideo),
                ("other-site-audio", OtherSiteAudio),
                ("archive-org-video", ArchiveVideo),
                ("import-table", ImportTable),
                ("playlist-two", PlaylistTwo),
                ("channel-listing", ChannelListing),
                ("agent", () => Agent(agent)),
            };
            foreach (var (name, run) in scenarios)
            {
                if (only.Count > 0 && !only.Contains(name)) continue;
                scenario = name;
                Console.WriteLine("=== " + name);
                var clock = Stopwatch.StartNew();
                try { run(); }
                catch (Exception ex) { Fail("crashed", ex.GetType().Name + ": " + ex.Message); }
                Write("took " + clock.Elapsed.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + " s");
            }
        }
        int failed = Results.Count(r => !r.Ok);
        Console.WriteLine();
        Console.WriteLine("RESULT " + (Results.Count - failed) + " passed, " + failed + " failed");
        foreach (var r in Results.Where(r => !r.Ok)) Console.WriteLine("  FAIL " + r.Scenario + ": " + r.Detail);
        return failed == 0 ? 0 : 1;
    }

    static string? Option(string[] args, string name)
    {
        int at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }

    static string Short(string? text, int length) => text == null ? "" : text.Length > length ? text[..length] : text;

    static void Write(string line)
    {
        log?.WriteLine(scenario + "  " + line);
        log?.Flush();
    }

    static void Pass(string what, string detail = "")
    {
        Results.Add((scenario, true, what));
        Console.WriteLine("  ok   " + what + (detail.Length > 0 ? "  (" + detail + ")" : ""));
        Write("ok " + what + " " + detail);
    }

    static void Fail(string what, string detail)
    {
        Results.Add((scenario, false, what + ": " + detail));
        Console.WriteLine("  FAIL " + what + ": " + detail);
        Write("FAIL " + what + " " + detail);
    }

    static bool Expect(bool ok, string what, string detail = "")
    {
        if (ok) Pass(what, detail); else Fail(what, detail.Length > 0 ? detail : "no");
        return ok;
    }

    // ---------- Running the real queue ----------

    static List<QueueItem> Download(Preferences prefs, Action<QueueController> add, Action<QueueController>? beforeStart = null, bool start = true)
    {
        string folder = Path.Combine(root, scenario);
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        prefs.Folder = folder;
        prefs.OpenFolder = false;
        prefs.Sound = false;
        var window = new MainWindow(new YtDlpBackend(), prefs, new List<string>(), false);
        window.Queue.Log += Write;
        window.Show();
        Pump();
        add(window.Queue);
        Until(() => window.Queue.Items.Count > 0 && window.Queue.Items.All(i => i.State != ItemState.Fetching), TimeSpan.FromMinutes(4));
        beforeStart?.Invoke(window.Queue);
        if (start)
        {
            window.Queue.StartAll();
            Until(() => window.Queue.Items.All(i => i.State is ItemState.Done or ItemState.Failed or ItemState.Cancelled or ItemState.Ready), TimeSpan.FromMinutes(15));
        }
        var items = window.Queue.Items.ToList();
        foreach (var item in items.Where(i => i.State == ItemState.Failed)) Write("failed card: " + item.Title + ": " + item.StatusText + " | " + item.LastErrors);
        window.Close();
        Pump();
        return items;
    }

    static void Pump()
    {
        for (int n = 0; n < 3; n++) Dispatcher.UIThread.RunJobs();
    }

    static void Until(Func<bool> done, TimeSpan limit)
    {
        var clock = Stopwatch.StartNew();
        while (!done() && clock.Elapsed < limit) { Pump(); Thread.Sleep(20); }
        Pump();
    }

    static QueueItem? Single(List<QueueItem> items)
    {
        if (!Expect(items.Count == 1, "one card", items.Count + " cards")) return null;
        var item = items[0];
        if (!Expect(item.State == ItemState.Done, "downloaded", item.State + ": " + item.StatusText)) return null;
        return Expect(item.FilePath != null && File.Exists(item.FilePath), "file exists", item.FilePath ?? "no path") ? item : null;
    }

    // ---------- Checking files ----------

    sealed record Media(Dictionary<string, object?> Format, List<Dictionary<string, object?>> Streams)
    {
        public IEnumerable<Dictionary<string, object?>> Of(string type) => Streams.Where(s => s.Str("codec_type") == type);
        public double Duration => Format.Num("duration") ?? double.Parse(Format.Str("duration") ?? "0", CultureInfo.InvariantCulture);
        // Ogg keeps its tags on the stream rather than the container.
        public string? Tag(string name) => new[] { Format }.Concat(Streams).Select(s => (s.Raw("tags") as Dictionary<string, object?>)?.FirstOrDefault(p => p.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value as string).FirstOrDefault(v => v != null);
        public bool HasCover => Streams.Any(s => (s.Raw("disposition") as Dictionary<string, object?>).Num("attached_pic") == 1);
    }

    static Media? Probe(string file)
    {
        var info = new ProcessStartInfo(ToolManager.Ffprobe!) { RedirectStandardOutput = true, UseShellExecute = false, StandardOutputEncoding = Encoding.UTF8 };
        foreach (string arg in new[] { "-v", "quiet", "-print_format", "json", "-show_format", "-show_streams", file }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        var data = Json.ParseObject(output);
        if (data?.Raw("format") is not Dictionary<string, object?> format) { Fail("readable by ffprobe", file); return null; }
        var streams = (data.Raw("streams") as List<object?> ?? new()).OfType<Dictionary<string, object?>>().ToList();
        Pass("readable by ffprobe", Path.GetFileName(file));
        return new Media(format, streams);
    }

    static void ExpectDuration(Media media, double seconds, double tolerance) =>
        Expect(Math.Abs(media.Duration - seconds) <= tolerance, "length about " + seconds + " s", media.Duration.ToString("0.0", CultureInfo.InvariantCulture) + " s");

    static void ExpectVideo(Media media, Func<int, bool> height, string what, bool needsH264 = false)
    {
        var video = media.Of("video").FirstOrDefault(s => (s.Raw("disposition") as Dictionary<string, object?>).Num("attached_pic") != 1);
        if (!Expect(video != null, "has a video stream")) return;
        int h = (int)(video!.Num("height") ?? 0);
        Expect(height(h), what, h + "p " + video.Str("codec_name"));
        if (needsH264) Expect(video.Str("codec_name") == "h264", "H.264, which every player opens", video.Str("codec_name") ?? "");
        Expect(media.Of("audio").Any(), "has sound");
    }

    // ---------- Scenarios: the app ----------

    static void VideoFullHd()
    {
        var item = Single(Download(new Preferences { Audio = false, Height = 1080, Container = "mp4", EmbedThumbnail = true }, q => q.Add(new[] { Bunny })));
        if (item == null) return;
        Expect(!item.StatusText.Contains(Loc.T("status.exists", "", "")[..8], StringComparison.Ordinal), "reported as just downloaded", item.StatusText);
        Expect(item.FilePath!.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase), "mp4 as chosen", Path.GetFileName(item.FilePath));
        if (Probe(item.FilePath) is not { } media) return;
        ExpectVideo(media, h => h == 1080, "1080p as chosen", needsH264: true);
        ExpectDuration(media, 635, 3);
    }

    static void VideoPart()
    {
        var item = Single(Download(new Preferences { Audio = false, Height = 720, Container = "mkv" }, q => q.Add(new[] { Bunny }),
            q => Expect(q.SetSection(q.Items[0], "1:00", "1:30"), "part accepted")));
        if (item == null || Probe(item.FilePath!) is not { } media) return;
        Expect(item.FilePath!.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase), "mkv as chosen", Path.GetFileName(item.FilePath));
        ExpectVideo(media, h => h == 720, "720p as chosen");
        ExpectDuration(media, 30, 2.5);
    }

    static void VideoMax()
    {
        var item = Single(Download(new Preferences { Audio = false, Height = 0, Container = "mp4" }, q => q.Add(new[] { Bunny }),
            q => q.SetSection(q.Items[0], "0:10", "0:16")));
        if (item == null || Probe(item.FilePath!) is not { } media) return;
        ExpectVideo(media, h => h >= 2160, "the highest quality (4K or more)");
        ExpectDuration(media, 6, 2);
    }

    static void VideoSubtitles()
    {
        var item = Single(Download(new Preferences { Audio = false, Height = 360, Container = "mp4", Subtitles = true }, q => q.Add(new[] { Captions })));
        if (item == null || Probe(item.FilePath!) is not { } media) return;
        Expect(media.Of("subtitle").Any(), "subtitles inside the video", media.Of("subtitle").Count() + " subtitle streams");
        var beside = Directory.GetFiles(Path.GetDirectoryName(item.FilePath)!, "*.vtt");
        Expect(beside.Length > 0, "subtitles beside the video", string.Join(", ", beside.Select(Path.GetFileName)));
        Expect(media.Of("subtitle").Count() == 1, "no duplicate subtitle tracks", media.Of("subtitle").Count().ToString(CultureInfo.InvariantCulture));
    }

    static void AudioByName(string format, string? extension, string? codec)
    {
        var item = Single(Download(new Preferences { Audio = true, AudioFormat = format, OriginalTracks = true, EmbedThumbnail = true }, q => q.Add(Engine.ParseEntries(Song))));
        if (item == null) return;
        Expect(item.IsOriginal, "found the studio recording", item.Channel + " - " + item.Title);
        Expect(!item.ShowReview, "not marked for checking", item.ReviewText);
        if (extension != null) Expect(item.FilePath!.EndsWith("." + extension, StringComparison.OrdinalIgnoreCase), extension + " as chosen", Path.GetFileName(item.FilePath));
        else Expect(Engine.AudioExtensions.Contains(Path.GetExtension(item.FilePath!).TrimStart('.').ToLowerInvariant()), "an audio file", Path.GetFileName(item.FilePath!));
        if (Probe(item.FilePath!) is not { } media) return;
        Expect(!media.Of("video").Any(s => (s.Raw("disposition") as Dictionary<string, object?>).Num("attached_pic") != 1), "no video in an audio file");
        if (codec != null) Expect(media.Of("audio").Any(s => s.Str("codec_name") == codec), codec + " sound", string.Join(",", media.Of("audio").Select(s => s.Str("codec_name"))));
        Expect(media.HasCover, "cover inside");
        Expect((media.Tag("title") ?? "").Contains("Sneaky Snitch", StringComparison.OrdinalIgnoreCase), "title tag", media.Tag("title") ?? "none");
        Expect((media.Tag("artist") ?? "").Contains("Kevin MacLeod", StringComparison.OrdinalIgnoreCase), "artist tag", media.Tag("artist") ?? "none");
        ExpectDuration(media, 137, 4);
    }

    static void OtherSiteVideo()
    {
        var item = Single(Download(new Preferences { Audio = false, Height = 1080, Container = "mp4" }, q => q.Add(new[] { Trailer })));
        if (item == null || Probe(item.FilePath!) is not { } media) return;
        Expect(!item.StatusText.Contains(Loc.T("status.exists", "", "")[..8], StringComparison.Ordinal), "reported as just downloaded", item.StatusText);
        ExpectVideo(media, h => h == 400, "the site's only quality, 400p");
        ExpectDuration(media, 33, 2);
    }

    static void OtherSiteAudio()
    {
        var item = Single(Download(new Preferences { Audio = true, AudioFormat = "mp3" }, q => q.Add(new[] { Trailer })));
        if (item == null || Probe(item.FilePath!) is not { } media) return;
        Expect(item.FilePath!.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase), "mp3", Path.GetFileName(item.FilePath));
        Expect(media.Of("audio").Any(), "has sound");
    }

    static void ArchiveVideo()
    {
        var item = Single(Download(new Preferences { Audio = false, Height = 1080, Container = "mp4" }, q => q.Add(new[] { Archive })));
        if (item == null || Probe(item.FilePath!) is not { } media) return;
        ExpectVideo(media, h => h > 0, "a video");
        ExpectDuration(media, 370, 5);
    }

    static void ImportTable()
    {
        string csv = Path.Combine(root, "import.csv");
        File.WriteAllText(csv, "Track URI,Track Name,Artist URI(s),Artist Name(s),Album URI,Album Name,Album Artist URI(s),Album Artist Name(s),Album Release Date,Album Image URL,Disc Number,Track Number,Track Duration (ms),Track Preview URL,Explicit,Popularity,ISRC\n"
            + "spotify:track:1,Sneaky Snitch,x,Kevin MacLeod,x,x,x,x,2020,x,1,1,137000,,false,5,\n"
            + "spotify:track:2,Monkeys Spinning Monkeys,x,Kevin MacLeod,x,x,x,x,2020,x,1,2,125000,,false,5,\n");
        var tracks = LibraryImport.ReadFile(csv);
        Expect(tracks.Count == 2, "two songs read from the file");
        var items = Download(new Preferences { Audio = true, AudioFormat = "mp3", WritePlaylists = true }, q => q.AddTracks(tracks));
        Expect(items.Count == 2 && items.All(i => i.State == ItemState.Done), "both downloaded", string.Join("; ", items.Select(i => i.State + " " + i.Title)));
        Expect(items.All(i => !i.ShowReview), "none marked for checking", string.Join("; ", items.Select(i => i.ReviewText)));
        string m3u = Path.Combine(root, scenario, "import.m3u8");
        if (!Expect(File.Exists(m3u), "playlist file written", m3u)) return;
        var listed = File.ReadAllLines(m3u).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        Expect(listed.Count == 2 && listed.All(l => File.Exists(Path.Combine(root, scenario, l))), "it points at both files", string.Join(" | ", listed));
    }

    static void PlaylistTwo()
    {
        var items = Download(new Preferences { Audio = true, AudioFormat = "mp3" }, q => q.Add(new[] { Playlist }), q =>
        {
            Expect(q.Items.Count >= 3, "the playlist expanded into cards", q.Items.Count + " cards");
            Expect(q.Items.All(i => i.Lists.Count == 1), "each card knows its playlist", q.Items.First().Lists.FirstOrDefault() ?? "none");
            // Two of them are enough to show downloads from a list work; the shortest, to be quick.
            foreach (var extra in q.Items.OrderBy(i => i.Info.Num("duration") ?? 1e9).Skip(2).ToList()) q.Remove(extra);
        });
        Expect(items.Count == 2 && items.All(i => i.State == ItemState.Done), "both downloaded", string.Join("; ", items.Select(i => i.State + " " + i.Title)));
        Expect(Directory.GetFiles(Path.Combine(root, scenario), "*.m3u8").Length == 1, "a playlist file");
    }

    static void ChannelListing()
    {
        var items = Download(new Preferences { Audio = false }, q => q.Add(new[] { Channel }), start: false);
        Expect(items.Count > 1000, "a whole channel listed", items.Count + " cards");
        Expect(!items.Any(i => i.State == ItemState.Failed), "no failed cards", items.Count(i => i.State == ItemState.Failed).ToString(CultureInfo.InvariantCulture));
        Expect(items.Select(i => i.Lists.FirstOrDefault()).Distinct().Count() >= 2, "its sections are separate lists", string.Join(", ", items.Select(i => i.Lists.FirstOrDefault()).Distinct()));
    }

    // ---------- Scenarios: the agent, through MCP as a client would use it ----------

    static void Agent(string? path)
    {
        if (path == null || !File.Exists(path)) { Fail("agent", "pass --agent with the path to volna-agent"); return; }
        string folder = Path.Combine(root, scenario);
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        using var client = new McpClient(path, folder);
        Expect(client.Call("initialize", Json.Map(("protocolVersion", "2025-06-18"), ("capabilities", Json.Map()), ("clientInfo", Json.Map(("name", "acceptance"), ("version", "1"))))) != null, "the server starts");
        client.Notify("notifications/initialized");

        var transcript = client.Tool("get_transcript", ("url", Captions));
        Expect((transcript.Data.Str("text") ?? "").Length > 200, "a transcript from automatic captions", (transcript.Data.Str("text") ?? transcript.Text)[..Math.Min(80, (transcript.Data.Str("text") ?? transcript.Text).Length)]);

        var video = client.Tool("download", ("target", Bunny), ("kind", "video"), ("max_height", 360), ("wait_seconds", 55));
        for (int n = 0; n < 20 && video.Data.Str("status") is not ("done" or "failed"); n++) video = client.Tool("download_status", ("job_id", video.Data.Str("job_id")), ("wait_seconds", 55));
        string? file = video.Data.Str("file");
        if (Expect(video.Data.Str("status") == "done" && file != null && File.Exists(file), "a video downloaded", video.Text[..Math.Min(200, video.Text.Length)]) && Probe(file!) is { } media)
        {
            ExpectVideo(media, h => h == 360, "360p as asked");
            var frames = client.Tool("extract_frames", ("path", Path.GetFileName(file!)), ("count", 2), ("width", 320));
            Expect(frames.Images == 2, "two frames as images", frames.Images + " images");
            var clip = client.Tool("extract_audio_clip", ("path", Path.GetFileName(file!)), ("duration_seconds", 5));
            Expect(clip.Data.Str("path") is { } c && File.Exists(c), "an audio clip", clip.Text[..Math.Min(120, clip.Text.Length)]);
        }

        var many = client.Tool("download_list", ("targets", new List<object?> { Song, OtherSong }), ("kind", "audio"));
        Expect(many.Data.Num("total") == 2, "two downloads started", many.Text[..Math.Min(120, many.Text.Length)]);
        var status = many;
        for (int n = 0; n < 20; n++)
        {
            status = client.Tool("download_status", ("wait_seconds", 55));
            var jobs = status.Data.Raw("jobs") as List<object?>;
            if (jobs != null && jobs.OfType<Dictionary<string, object?>>().Where(j => j.Str("kind") == "audio").All(j => j.Str("status") is "done" or "failed")) break;
        }
        var audio = (status.Data.Raw("jobs") as List<object?> ?? new()).OfType<Dictionary<string, object?>>().Where(j => j.Str("kind") == "audio").ToList();
        Expect(audio.Count == 2 && audio.All(j => j.Str("status") == "done"), "both songs downloaded", string.Join("; ", audio.Select(j => j.Str("status") + " " + j.Str("error"))));

        var listing = client.Tool("list_playlist", ("url", Playlist), ("limit", 5));
        Expect(listing.Data.Num("count") >= 3, "a playlist listed", listing.Text[..Math.Min(120, listing.Text.Length)]);
        var refused = client.Tool("media_info", ("path", Path.Combine(root, "import.csv")));
        Expect(refused.IsError, "a file outside its folder is refused");
    }

    sealed class McpClient : IDisposable
    {
        readonly Process process;
        int next;

        public sealed record Answer(Dictionary<string, object?> Data, string Text, int Images, bool IsError);

        public McpClient(string path, string folder)
        {
            var info = new ProcessStartInfo(path) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, StandardOutputEncoding = Encoding.UTF8 };
            info.Environment["VOLNA_FOLDER"] = folder;
            process = Process.Start(info)!;
        }

        public void Notify(string method) => Send(Json.Map(("jsonrpc", "2.0"), ("method", method)));

        void Send(object message)
        {
            process.StandardInput.WriteLine(Json.Write(message));
            process.StandardInput.Flush();
        }

        public Dictionary<string, object?>? Call(string method, Dictionary<string, object?> parameters)
        {
            int id = ++next;
            Send(Json.Map(("jsonrpc", "2.0"), ("id", id), ("method", method), ("params", parameters)));
            while (process.StandardOutput.ReadLine() is { } line)
            {
                var reply = Json.ParseObject(line);
                if (reply.Num("id") == id) return reply.Raw("result") as Dictionary<string, object?>;
            }
            return null;
        }

        public Answer Tool(string name, params (string Key, object? Value)[] arguments)
        {
            var result = Call("tools/call", Json.Map(("name", name), ("arguments", Json.Map(arguments)))) ?? new();
            var content = (result.Raw("content") as List<object?> ?? new()).OfType<Dictionary<string, object?>>().ToList();
            string text = string.Join(" ", content.Where(c => c.Str("type") == "text").Select(c => c.Str("text")));
            Write("agent " + name + ": " + (text.Length > 400 ? text[..400] : text));
            return new Answer(Json.ParseObject(content.FirstOrDefault(c => c.Str("type") == "text")?.Str("text") ?? "{}") ?? new(), text, content.Count(c => c.Str("type") == "image"), result.Flag("isError"));
        }

        public void Dispose()
        {
            try { process.StandardInput.Close(); if (!process.WaitForExit(8000)) process.Kill(true); }
            catch (InvalidOperationException) { }
            process.Dispose();
        }
    }
}
