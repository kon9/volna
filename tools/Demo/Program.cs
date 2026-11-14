using Avalonia.VisualTree;
// Renders the real interface without a screen, with invented songs, to make the README's pictures and animation.
// It also doubles as a smoke test: if the window cannot be built on a platform, this fails.
//
//   dotnet run --project tools/Demo -- smoke
//   dotnet run --project tools/Demo -- pictures <folder>
//   dotnet run --project tools/Demo -- frames <folder>      (then see tools/make-media.sh)
//   dotnet run --project tools/Demo -- small <folder> [language]   the smallest window, to see that every text fits
//   dotnet run --project tools/Demo -- clips <folder>     offline clip-workspace interactions and screenshots
//   dotnet run --project tools/Demo -- live <folder> "Artist - Song" ...   real searches and downloads, for checking by hand
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Volna.App;
using Volna.Core;
using SkiaSharp;

namespace Volna.Demo;

/// <summary>A pretend YouTube: every song exists, and downloads advance only when the script says so.</summary>
sealed class DemoBackend : IQueueBackend
{
    public Task<ToolUpdate> UpdateToolAsync(string proxy, bool nightly, Action<string> log) => Task.FromResult(new ToolUpdate(true, false));

    public Task<string?> LyricsAsync(string artist, string title, double? seconds, string proxy) => Task.FromResult<string?>(null);

    public readonly List<ActiveDownload> Active = new();
    public string Folder = "";
    public bool Offline;
    public Func<Task<ClipMetadataInfo>>? OnClipMetadata;
    public Func<CaptionTrack, Task<IReadOnlyList<SubtitleCue>>>? OnTranscript;

    public const string ClipFixtureTitle = "Morning Journey — demo film";

    public static ClipMetadataInfo ClipFixtureMetadata()
    {
        bool ru = Loc.Language == "ru";
        return new ClipMetadataInfo(ru ? "ru" : "en", 600,
            [new VideoChapter(0, 60, ru ? "Рассвет" : "Dawn"),
             new VideoChapter(60, 150, ru ? "Через долину" : "Across the valley"),
             new VideoChapter(150, 240, ru ? "У тихого озера" : "Beside the quiet lake"),
             new VideoChapter(240, 600, ru ? "Дорога домой" : "The journey home")],
            [new CaptionTrack("en", "English", false), new CaptionTrack("ru", "Русский", false)]);
    }

    public static IReadOnlyList<SubtitleCue> ClipFixtureCues(string language) => language == "ru"
        ? [new SubtitleCue(12, 18, "Утренний свет освещает тихую долину."),
           new SubtitleCue(75.2, 81.8, "Мы идём вдоль реки к зелёным холмам."),
           new SubtitleCue(169, 176, "Тропа выходит к спокойному озеру.")]
        : [new SubtitleCue(12, 18, "Morning light reaches the quiet valley."),
           new SubtitleCue(75.2, 81.8, "We follow the river toward the green hills."),
           new SubtitleCue(169, 176, "The path opens beside a still lake.")];

    public Task<ClipMetadataInfo> ClipMetadataAsync(string url, string proxy, ProcessRunner runner) =>
        OnClipMetadata?.Invoke() ?? Task.FromResult(ClipFixtureMetadata());

    public Task<IReadOnlyList<SubtitleCue>> TranscriptAsync(string url, CaptionTrack track, string proxy, ProcessRunner runner) =>
        OnTranscript?.Invoke(track) ?? Task.FromResult(ClipFixtureCues(track.Language));

    public sealed class ActiveDownload
    {
        public DownloadOptions Options = null!;
        public Action<string> Line = _ => { };
        public TaskCompletionSource<RunResult> Done = new();
        public double Percent;
        public double Rate;
    }

    // Openly licensed music, with the covers its makers gave it: Kevin MacLeod and Scott Buckley (CC BY), Komiku and
    // HoliznaCC0 (CC0). Commercial songs in a demo would advertise downloading them.
    static readonly Dictionary<string, (string Id, string Title, string Artist, int Seconds)[]> Songs = new()
    {
        ["Kevin MacLeod - Pixelland"] = new[] { ("Z-bi29z4Z1k", "Pixelland", "Kevin MacLeod", 242), ("rQqr10MC_uw", "Pixelland", "Kevin MacLeod Archive", 236), ("uDTZLmGb2Ms", "Pixelland (1 hour)", "Kevin MacLeod Archive", 3974) },
        ["Cipher"] = new[] { ("N0KuBFK9r24", "Cipher", "Kevin MacLeod", 232), ("UiHkZTeXrcA", "Cipher", "Kevin MacLeod Archive", 236) },
        ["Scott Buckley - Horizons"] = new[] { ("dkyBVvZXJaM", "Horizons", "Scott Buckley", 294) },
        ["Komiku - We need a plan"] = new[] { ("Io5GL-WcUIU", "We need a plan", "Komiku", 122) },
        ["HoliznaCC0 - Lo-Fi #1"] = new[] { ("71KiBM_tKiE", "Lo-Fi #1", "HoliznaCC0", 157) },
        ["Scott Buckley - Filaments"] = new[] { ("lR2lMI5Wn-A", "Filaments", "Scott Buckley", 340) },
        ["Komiku - The end ?"] = new[] { ("Vr4Hm9JBJNY", "The end ?", "Komiku", 239) },
    };

    public static readonly string[] List = Songs.Keys.ToArray();

    static Dictionary<string, object?> Song((string Id, string Title, string Artist, int Seconds) s) => Json.Map(("id", s.Id), ("title", s.Title), ("uploader", s.Artist), ("duration", (double)s.Seconds),
        ("webpage_url", "https://www.youtube.com/watch?v=" + s.Id), ("ie_key", "Youtube"), ("extractor_key", "Youtube"), ("source", "music"), ("is_original", true));

    // The Blender Foundation's open films (CC BY), for the pictures of video mode.
    static readonly (string Id, string Title, string Channel, int Seconds, int Height)[] Videos =
    {
        ("eRsGyueVLvQ", "Sintel - Open Movie by Blender Foundation", "Blender", 888, 872),
        ("R6MlUcmOul8", "Tears of Steel - Blender VFX Open Movie", "Blender", 734, 800),
        ("WhWc3b3KhnY", "Spring - Blender Open Movie", "Blender Studio", 464, 858),
        ("PVGeM40dABA", "Coffee Run - Blender Open Movie", "Blender Studio", 185, 858),
        ("UXqq0ZvbOnk", "CHARGE - Blender Open Movie", "Blender Studio", 263, 1608),
    };

    public static readonly string[] VideoList = Videos.Select(v => v.Title).ToArray();

    public Task<LookupResult> LookupAsync(IReadOnlyList<string> requests, int limit, string proxy, ProcessRunner runner, Action<string> log, Action<int>? found = null)
    {
        var entries = new List<Dictionary<string, object?>>();
        foreach (string request in requests)
        {
            string query = request[(request.IndexOf(':') + 1)..];
            if (query == ClipFixtureTitle)
            {
                entries.Add(Json.Map(("id", "volna-demo-clip"), ("title", ClipFixtureTitle), ("uploader", "Volna Demo"),
                    ("duration", 600.0), ("height", 720.0), ("webpage_url", "https://example.org/demo-film"),
                    ("thumbnail", "https://example.org/demo-cover.jpg"), ("ie_key", "Generic"), ("extractor_key", "Generic")));
                continue;
            }
            int at = Array.FindIndex(Videos, v => v.Title == query);
            if (at < 0) continue;
            var v = Videos[at];
            entries.Add(Json.Map(("id", v.Id), ("title", v.Title), ("uploader", v.Channel), ("duration", (double)v.Seconds), ("height", (double)v.Height),
                ("webpage_url", "https://www.youtube.com/watch?v=" + v.Id), ("ie_key", "Youtube"), ("extractor_key", "Youtube"), ("playlist_title", query)));
        }
        return Task.FromResult(new LookupResult(entries, "", false));
    }

    public Task<List<Dictionary<string, object?>>> SearchMusicAsync(string query, int limit, string proxy)
    {
        var found = Songs.TryGetValue(query, out var songs) ? songs.Select(Song).ToList() : new List<Dictionary<string, object?>>();
        return Task.FromResult(found);
    }

    public Task<RunResult> DownloadAsync(DownloadOptions options, ProcessRunner runner, Action<string> onLine)
    {
        var active = new ActiveDownload { Options = options, Line = onLine, Rate = 1.6 + Active.Count * 0.7 };
        Active.Add(active);
        return active.Done.Task;
    }

    /// <summary>One frame's worth of download progress.</summary>
    public void Tick()
    {
        foreach (var d in Active.ToList())
        {
            d.Percent = Math.Min(100, d.Percent + d.Rate);
            double total = 9.4e6, done = total * d.Percent / 100;
            d.Line("__PROGRESS__downloading|" + done.ToString(CultureInfo.InvariantCulture) + "|" + total.ToString(CultureInfo.InvariantCulture) + "|NA|" + (4.2e6 + d.Rate * 3e5).ToString(CultureInfo.InvariantCulture) + "|" + ((100 - d.Percent) / 8).ToString(CultureInfo.InvariantCulture) + "|none|opus");
            if (d.Percent < 100) continue;
            string file = Path.Combine(d.Options.Folder, "demo [" + Guid.NewGuid().ToString("N") + "].mp3");
            using (var stream = File.Create(file)) stream.SetLength(9_400_000);
            d.Line("__FILE__" + file);
            Active.Remove(d);
            d.Done.SetResult(new RunResult(0, false));
        }
    }

    public Task<(Storyboard? Detail, Storyboard? Overview)> StoryboardAsync(string url, string proxy, ProcessRunner runner) => Task.FromResult<(Storyboard?, Storyboard?)>((null, null));

    static readonly HttpClient Covers = new() { Timeout = TimeSpan.FromSeconds(10) };
    static readonly Dictionary<string, byte[]> Fetched = new();

    // The real cover, at the size a fine screen needs. Without a network the picture is drawn instead.
    public async Task<byte[]?> ThumbnailAsync(string url, string proxy)
    {
        if (Offline) return Drawn(url);
        string? id = url.Contains("/vi/", StringComparison.Ordinal) ? url.Split("/vi/")[1].Split('/')[0] : null;
        if (id != null)
        {
            lock (Fetched) if (Fetched.TryGetValue(id, out var known)) return known;
            // Older videos have no wide cover at 720p, only the larger or the smaller one.
            foreach (string size in new[] { "hq720", "maxresdefault", "hqdefault" })
            {
                try
                {
                    byte[] data = await Covers.GetByteArrayAsync("https://i.ytimg.com/vi/" + id + "/" + size + ".jpg").ConfigureAwait(false);
                    lock (Fetched) Fetched[id] = data;
                    return data;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { }
            }
        }
        return Drawn(url);
    }

    // Invented cover art in the manner of anime singles: a sky, a moon or a sun, and a city, hills or the sea, drawn from
    // the address so each song keeps its own. Twice the size it is shown at, so it stays sharp on fine screens.
    static byte[] Drawn(string url)
    {
        var random = new Random(StableSeed(url));
        const int W = 736, H = 416;
        var skies = new[]
        {
            (new SKColor(0x1B, 0x1F, 0x4B), new SKColor(0xF0, 0x6C, 0x9B)),
            (new SKColor(0x0B, 0x13, 0x2B), new SKColor(0x3A, 0x50, 0x8C)),
            (new SKColor(0xFF, 0x8A, 0x80), new SKColor(0xFF, 0xD8, 0x9B)),
            (new SKColor(0x2E, 0x1A, 0x47), new SKColor(0xFF, 0x6E, 0xC7)),
            (new SKColor(0x6F, 0xB8, 0xF0), new SKColor(0xFF, 0xEE, 0xD0)),
            (new SKColor(0x10, 0x2A, 0x43), new SKColor(0x5E, 0xE7, 0xDF)),
        };
        var (top, bottom) = skies[random.Next(skies.Length)];
        using var bitmap = new SKBitmap(W, H);
        using var canvas = new SKCanvas(bitmap);
        using (var sky = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, H), new[] { top, bottom }, SKShaderTileMode.Clamp) })
            canvas.DrawRect(0, 0, W, H, sky);
        using (var star = new SKPaint { Color = SKColors.White.WithAlpha(190), IsAntialias = true })
            for (int n = 0; n < 70; n++) canvas.DrawCircle(random.Next(W), random.Next(H / 2), (float)(random.NextDouble() * 1.8 + 0.5), star);
        float cx = random.Next(W / 5, W * 4 / 5), cy = random.Next(H / 6, H * 2 / 5), r = random.Next(40, 66);
        using (var glow = new SKPaint { IsAntialias = true, Shader = SKShader.CreateRadialGradient(new SKPoint(cx, cy), r * 3.2f, new[] { SKColors.White.WithAlpha(110), SKColors.White.WithAlpha(0) }, SKShaderTileMode.Clamp) })
            canvas.DrawCircle(cx, cy, r * 3.2f, glow);
        using (var disc = new SKPaint { Color = new SKColor(0xFF, 0xF6, 0xE6), IsAntialias = true })
            canvas.DrawCircle(cx, cy, r, disc);
        var shade = new SKColor((byte)(top.Red / 3), (byte)(top.Green / 3), (byte)(top.Blue / 3));
        switch (random.Next(3))
        {
            case 0: // a city at night, its windows lit
                using (var building = new SKPaint { Color = shade, IsAntialias = true })
                using (var window = new SKPaint { Color = new SKColor(0xFF, 0xD9, 0x7A, 210) })
                {
                    for (float x = -10; x < W; x += random.Next(36, 70))
                    {
                        float width = random.Next(34, 66), height = random.Next(90, 230);
                        canvas.DrawRect(x, H - height, width, height, building);
                        for (float wy = H - height + 12; wy < H - 10; wy += 16)
                            for (float wx = x + 7; wx < x + width - 8; wx += 12)
                                if (random.Next(4) == 0) canvas.DrawRect(wx, wy, 5, 7, window);
                    }
                }
                break;
            case 1: // hills, one behind the other
                for (int layer = 0; layer < 3; layer++)
                {
                    using var path = new SKPath();
                    float baseline = H * (0.55f + layer * 0.14f);
                    path.MoveTo(0, H);
                    for (float x = 0; x <= W; x += 46) path.LineTo(x, baseline - (float)(Math.Sin((x + random.Next(80)) / 70.0 + layer) * 26) - random.Next(18));
                    path.LineTo(W, H);
                    path.Close();
                    using var hill = new SKPaint { Color = shade.WithAlpha((byte)(150 + layer * 50)), IsAntialias = true };
                    canvas.DrawPath(path, hill);
                }
                break;
            default: // the sea, with the light on it
                float horizon = H * 0.62f;
                using (var sea = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, horizon), new SKPoint(0, H), new[] { shade.WithAlpha(230), shade }, SKShaderTileMode.Clamp) })
                    canvas.DrawRect(0, horizon, W, H - horizon, sea);
                using (var light = new SKPaint { Color = new SKColor(0xFF, 0xF6, 0xE6, 120), IsAntialias = true })
                    for (float y = horizon + 8; y < H; y += 12)
                    {
                        float half = (float)(r * (0.4 + random.NextDouble())) * (1 + (y - horizon) / H);
                        canvas.DrawRoundRect(cx - half, y, half * 2, 3, 1.5f, 1.5f, light);
                    }
                break;
        }
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 92);
        return data.ToArray();
    }

    static int StableSeed(string text) { int hash = 17; foreach (char c in text) hash = unchecked(hash * 31 + c); return hash; }
}

static class Program
{
    public static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "smoke";
        string output = args.Length > 1 ? args[1] : "demo-output";
        if (mode is not ("install" or "live" or "acceptance")) AppPaths.Override = Path.Combine(Path.GetTempPath(), "volna-demo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(output);
        AppBuilder.Configure<App.App>().UseSkia().WithInterFont().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();
        int result = 1;
        Exception? failure = null;
        try
        {
            result = mode switch
            {
                "small" => Small(output, args.Length > 2 ? args[2] : "ru"),
                "clips" => Clips(output),
                "acceptance" => Acceptance.Run(output, args.Skip(2).ToArray()),
                "smoke" => Smoke(),
                "pictures" => Pictures(output),
                "frames" => Frames(output),
                "live" => Live(output, args.Skip(2).ToArray()),
                "install" => Install(),
                "social" => Social(output),
                _ => throw new ArgumentException("smoke, clips, pictures or frames")
            };
        }
        catch (Exception ex) { failure = ex; }
        if (mode is not ("install" or "live" or "acceptance")) { try { Directory.Delete(AppPaths.Data, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        if (failure != null) { Console.Error.WriteLine(failure); return 1; }
        return result;
    }

    static (MainWindow Window, DemoBackend Backend) Open(string theme, string language, int width = 1240, int height = 820, bool audio = true)
    {
        Loc.Use(language);
        string downloads = Path.Combine(AppPaths.Data, "downloads");
        Directory.CreateDirectory(downloads);
        var prefs = new Preferences { Theme = theme, Language = language, Folder = downloads, Audio = audio, Parallel = 3, OriginalTracks = true, WatchClipboard = true };
        var backend = new DemoBackend { Folder = downloads };
        var window = new MainWindow(backend, prefs, new List<string>(), false) { Width = width, Height = height };
        window.Queue.MusicDelayMilliseconds = 0;
        // The pictures are published, and the real path would show the name of whoever made them.
        window.FindControl<TextBox>("FolderBox")!.Text = "~/Music/Volna";
        window.Show();
        Pump(5);
        return (window, backend);
    }

    static void Pump(int ticks = 1)
    {
        for (int n = 0; n < ticks; n++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    static void Settle(MainWindow window, Func<bool> done, int maxTicks = 400)
    {
        for (int n = 0; n < maxTicks && !done(); n++) { Pump(); Thread.Sleep(5); }
        Pump(10);
    }

    // Twice the size of the window, as a fine screen shows it: at one to one, text in the pictures looks soft on GitHub.
    const double Scale = 2;

    /// <summary>Texts that do not fit where they are: cut short with an ellipsis, or wider than their place.</summary>
    static IEnumerable<string> Overflowing(Visual root)
    {
        foreach (var text in root.GetVisualDescendants().OfType<TextBlock>())
        {
            if (!text.IsEffectivelyVisible || string.IsNullOrEmpty(text.Text) || text.Bounds.Width <= 0) continue;
            bool cut = text.TextLayout.TextLines.Any(l => l.HasCollapsed);
            bool wider = text.TextWrapping == Avalonia.Media.TextWrapping.NoWrap && text.DesiredSize.Width - text.Margin.Left - text.Margin.Right > text.Bounds.Width + 1;
            if (cut || wider) yield return (cut ? "cut  " : "wide ") + text.Text.ReplaceLineEndings(" ");
        }
    }

    static void Save(MainWindow window, string path)
    {
        var size = new PixelSize((int)Math.Round(window.Width * Scale), (int)Math.Round(window.Height * Scale));
        using var frame = new Avalonia.Media.Imaging.RenderTargetBitmap(size, new Vector(96 * Scale, 96 * Scale));
        // A shadow is drawn in a layer of its own, and at twice the size that layer scales what is inside it a second
        // time; over the dark backdrop of a dialog the shadow is hardly seen anyway.
        foreach (var border in window.GetVisualDescendants().OfType<Border>())
            if (border.Classes.Contains("dialog")) border.BoxShadow = default;
        frame.Render(window);
        using var file = File.Create(path);
#pragma warning disable CS0618 // the default encoder settings are what is wanted here
        frame.Save(file);
#pragma warning restore CS0618
    }

    // The whole path with the real yt-dlp: search, original track, download, file on disk.
    static int Live(string folder, string[] lines)
    {
        Loc.Use("en");
        Directory.CreateDirectory(folder);
        var prefs = new Preferences { Folder = Path.GetFullPath(folder), Audio = true, Parallel = 2, OriginalTracks = true, EmbedThumbnail = true, Lyrics = true };
        var window = new MainWindow(new YtDlpBackend(), prefs, new List<string>(), false);
        window.Queue.Log += Console.WriteLine;
        window.Show();
        Pump(5);
        // A library file is imported, as the window's Import button would.
        if (lines.Length == 1 && LibraryImport.Handles(lines[0])) window.Queue.AddTracks(LibraryImport.ReadFile(lines[0]));
        else window.Queue.Add(Engine.ParseEntries(string.Join('\n', lines)));
        window.Queue.StartAll();
        var limit = DateTime.UtcNow.AddMinutes(5);
        while (window.Queue.Items.Any(i => i.State is not (ItemState.Done or ItemState.Failed or ItemState.Cancelled)) && DateTime.UtcNow < limit) { Pump(); Thread.Sleep(50); }
        foreach (var item in window.Queue.Items) Console.WriteLine("RESULT " + item.State + " | original=" + item.IsOriginal + " | check=" + item.ReviewText + " | " + item.Channel + " | " + item.Title + " | " + item.StatusText + " | " + item.FilePath);
        window.Close();
        Pump(3);
        return window.Queue.Items.All(i => i.State == ItemState.Done) ? 0 : 1;
    }

    // The 1280x640 picture GitHub and chat apps show for a link to the project.
    static int Social(string folder)
    {
        using var surface = SKSurface.Create(new SKImageInfo(1280, 640));
        var canvas = surface.Canvas;
        using var background = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(1280, 640), new[] { new SKColor(0x1A, 0x10, 0x18), new SKColor(0x0B, 0x0D, 0x12) }, SKShaderTileMode.Clamp) };
        canvas.DrawRect(0, 0, 1280, 640, background);
        using var picture = SKImage.FromEncodedData(Path.Combine(folder, "queue-dark.png"));
        // A piece of the queue at its real size (the picture is twice that), rather than the whole window shrunk until
        // its text blurs. It runs off the right edge on purpose.
        var piece = new SKRect(36, 276, 36 + 1520, 276 + 1180);
        var shown = new SKRect(560, 40, 560 + piece.Width / 2, 40 + piece.Height / 2);
        using (var frame = new SKPaint { Color = new SKColor(0x23, 0x28, 0x36), IsAntialias = true })
            canvas.DrawRoundRect(new SKRect(shown.Left - 1, shown.Top - 1, shown.Right + 1, shown.Bottom + 1), 18, 18, frame);
        canvas.Save();
        canvas.ClipRoundRect(new SKRoundRect(shown, 18, 18), antialias: true);
        using (var behind = new SKPaint { Color = new SKColor(0x0F, 0x11, 0x17) }) canvas.DrawRect(shown, behind);
        canvas.DrawImage(picture, piece, shown, new SKSamplingOptions(SKCubicResampler.Mitchell), null);
        canvas.Restore();
        // The words keep to the left of the picture, and the block is centred in the height.
        using var white = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var muted = new SKPaint { Color = new SKColor(0xB0, 0xB7, 0xC8), IsAntialias = true };
        using var bold = new SKFont(SKTypeface.FromFamilyName(null, SKFontStyle.Bold), 64);
        using var regular = new SKFont(SKTypeface.FromFamilyName(null, SKFontStyle.Normal), 30);
        float left = 64, room = shown.Left - left - 48;
        string[] lines = { "Find music and video", "by name or link, check", "each match, download." };
        foreach (string line in lines)
            if (regular.MeasureText(line) > room) throw new InvalidOperationException("The words run into the picture: " + line);
        const float IconSize = 96, Gap = 40, NameHeight = 64, LineHeight = 42;
        float height = IconSize + Gap + NameHeight + 24 + lines.Length * LineHeight;
        float top = (640 - height) / 2;
        using var icon = SKBitmap.Decode(Path.Combine("assets", "generated", "icon-256.png"));
        canvas.DrawBitmap(icon, new SKRect(left, top, left + IconSize, top + IconSize), new SKPaint { IsAntialias = true });
        float baseline = top + IconSize + Gap + NameHeight * 0.8f;
        canvas.DrawText("Volna", left, baseline, bold, white);
        baseline += 24 + LineHeight;
        foreach (string line in lines)
        {
            canvas.DrawText(line, left, baseline, regular, muted);
            baseline += LineHeight;
        }
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(Path.Combine(folder, "social.png"), data.ToArray());
        return 0;
    }

    // First-run installation of yt-dlp, FFmpeg and Deno, as the app does it.
    static int Install()
    {
        Loc.Use("en");
        // Off this thread: it is the interface's, so waiting on it here would block the install's own continuations forever.
        Task.Run(() => ToolManager.InstallMissingAsync(new Progress<ToolProgress>(_ => { }), CancellationToken.None)).GetAwaiter().GetResult();
        Console.WriteLine("Missing after install: " + ToolManager.Missing().Count.ToString(CultureInfo.InvariantCulture));
        return ToolManager.Missing().Count == 0 ? 0 : 1;
    }

    // Every overlay and both languages must build and render.
    static int Smoke()
    {
        foreach (string language in Loc.Languages)
        {
            foreach (string theme in new[] { "dark", "light" })
            {
                var (window, _) = Open(theme, language);
                window.Queue.Add(DemoBackend.List.Select(s => Engine.SearchPrefix + s));
                Settle(window, () => window.Queue.Items.All(i => i.State == ItemState.Ready));
                if (window.Queue.Items.Count != DemoBackend.List.Length) throw new InvalidOperationException("Cards are missing.");
                if (window.CaptureRenderedFrame() == null) throw new InvalidOperationException("Nothing was rendered.");
                window.Close();
                Pump(3);
            }
        }
        Console.WriteLine("Smoke test passed on " + System.Runtime.InteropServices.RuntimeInformation.OSDescription);
        return 0;
    }

    // The smallest window the app allows, for a small laptop at a high scale.
    static int Small(string output, string language)
    {
        var (window, _) = Open("dark", language, 880, 540);
        output = Path.Combine(output, language);
        Directory.CreateDirectory(output);
        var report = new List<string>();
        void Check(string what, Visual root) => report.AddRange(Overflowing(root).Distinct().Select(t => what + ": " + t));
        window.Queue.Add(DemoBackend.List.Select(s => Engine.SearchPrefix + s));
        Settle(window, () => window.Queue.Items.All(i => i.State == ItemState.Ready && i.ThumbBytes != null));
        Save(window, Path.Combine(output, "small-queue.png"));
        Check("queue", window);
        foreach (string overlay in new[] { "SettingsOverlay", "AuthOverlay", "PickerOverlay", "ClipOverlay" })
        {
            if (overlay == "AuthOverlay") window.OpenAuth();
            else if (overlay == "ClipOverlay") window.OpenClip(window.Queue.Items[0]);
            else if (overlay == "PickerOverlay") { window.OpenPicker(window.Queue.Items[0]); Settle(window, () => false, 30); }
            else window.FindControl<Grid>(overlay)!.IsVisible = true;
            Pump(10);
            Save(window, Path.Combine(output, "small-" + overlay + ".png"));
            Check(overlay, window.FindControl<Grid>(overlay)!);
            window.FindControl<Grid>(overlay)!.IsVisible = false;
        }
        window.OpenImport(LibraryImport.GuessTable("Pixelland;Kevin MacLeod;4:02\nCipher;Kevin MacLeod;3:52\nHorizons;Scott Buckley;4:54\nFilaments;Scott Buckley;5:40\nWe need a plan;Komiku;2:02\nThe end ?;Komiku;3:59\n")!);
        Pump(10);
        Save(window, Path.Combine(output, "small-ImportOverlay.png"));
        Check("ImportOverlay", window.FindControl<Grid>("ImportOverlay")!);
        window.Close();
        Pump(3);
        File.WriteAllLines(Path.Combine(output, "overflow.txt"), report);
        foreach (string line in report) Console.WriteLine(language + " " + line);
        return 0;
    }

    // Drive the same controls as a person, without starting a media player or contacting a video site.
    static readonly string[] ExpectedClipSections = ["70-100", "60-150", "150-240"];

    static int Clips(string output)
    {
        var checks = new List<string>();
        foreach (string language in new[] { "en", "ru" })
            foreach (string theme in new[] { "dark", "light" })
                foreach (var (width, height) in new[] { (1240, 820), (880, 540) })
                {
                    var (window, backend) = Open(theme, language, width, height, audio: false);
                    backend.Offline = true;
                    Required<TextBox>(window, "FolderBox").Text = backend.Folder;
                    window.Queue.Add([Engine.SearchPrefix + DemoBackend.ClipFixtureTitle]);
                    Settle(window, () => window.Queue.Items.Count == 1 && window.Queue.Items[0].State == ItemState.Ready && window.Queue.Items[0].ThumbBytes != null);
                    Ensure(window.Queue.Items.Count == 1 && window.Queue.Items[0].State == ItemState.Ready, "The demo film did not become ready.");
                    QueueItem item = window.Queue.Items[0];
                    window.OpenClip(item);
                    Pump(8);
                    Ensure(Required<Grid>(window, "ClipOverlay").IsVisible, "The clip editor did not open.");

                    // A named manual range joins two chapters in the same batch.
                    TypeTime(window, "ClipFrom", "1:10");
                    TypeTime(window, "ClipTo", "1:40");
                    string firstName = language == "ru" ? "Заметки" : "Field notes";
                    Required<TextBox>(window, "ClipName").Text = firstName;
                    ClickTag(window, "clipAdd");
                    Ensure(Rows<ClipDraft>(window, "ClipPartsList") is [{ Start: 70, End: 100 }], "The typed range did not reach the batch.");

                    Required<RadioButton>(window, "ClipChaptersTab").IsChecked = true;
                    Pump(8);
                    var chapters = Rows<ClipChapterRow>(window, "ClipChaptersList");
                    Ensure(chapters.Length == 4, "The chapter list is missing. Parts=" + Required<RadioButton>(window, "ClipPartsTab").IsChecked
                        + ", chapters=" + Required<RadioButton>(window, "ClipChaptersTab").IsChecked + ", status=" + Required<TextBlock>(window, "ClipChaptersStatus").Text);
                    var chapterButtons = Required<ItemsControl>(window, "ClipChaptersList").GetVisualDescendants().OfType<Button>().Where(b => Equals(b.Tag, "clipChapterPick")).ToArray();
                    Ensure(chapterButtons.Length == 4, "The chapter buttons are missing.");
                    Click(chapterButtons[1]);
                    Ensure(Required<TextBox>(window, "ClipFrom").Text == "1:00" && Required<TextBox>(window, "ClipTo").Text == "2:30", "Selecting a chapter did not set its boundaries.");
                    var boxes = Required<ItemsControl>(window, "ClipChaptersList").GetVisualDescendants().OfType<CheckBox>().ToArray();
                    Ensure(boxes.Length == 4, "The chapter checkboxes are missing.");
                    boxes[1].IsChecked = boxes[2].IsChecked = true;
                    Pump(3);
                    Ensure(chapters[1].Selected && chapters[2].Selected, "Chapter selection was not bound to the model.");
                    ClickTag(window, "clipAddChapters");
                    Ensure(Rows<ClipDraft>(window, "ClipPartsList").Length == 3, "Selected chapters were not added to the batch.");
                    ClickTag(window, "clipClose");
                    window.OpenClip(item);
                    Pump(8);
                    Ensure(Rows<ClipDraft>(window, "ClipPartsList").Length == 3, "Closing the editor lost its drafts.");

                    string suffix = language + "-" + theme + "-" + width.ToString(CultureInfo.InvariantCulture) + "x" + height.ToString(CultureInfo.InvariantCulture);
                    CaptureClipTab(window, "parts", suffix, output);
                    Required<RadioButton>(window, "ClipChaptersTab").IsChecked = true;
                    Pump(8);
                    CaptureClipTab(window, "chapters", suffix, output);
                    Required<RadioButton>(window, "ClipTranscriptTab").IsChecked = true;
                    Pump(8);
                    Ensure(Rows<ClipCueRow>(window, "ClipTranscriptList").Length == 3, "The transcript did not load.");
                    var query = Required<TextBox>(window, "ClipTranscriptQuery");
                    query.Text = language == "ru" ? "реки" : "river";
                    Pump(5);
                    Ensure(Rows<ClipCueRow>(window, "ClipTranscriptList").Length == 1, "Transcript search did not filter to the matching line.");
                    ClickTag(window, "clipCuePick");
                    Ensure(Required<TextBox>(window, "ClipFrom").Text == "1:15" && Required<TextBox>(window, "ClipTo").Text == "1:22", "A transcript result did not select the complete cue.");
                    query.Text = "no-such-phrase-48931";
                    Pump(5);
                    Ensure(Rows<ClipCueRow>(window, "ClipTranscriptList").Length == 0, "A nonmatching search retained results.");
                    Ensure(Required<TextBlock>(window, "ClipTranscriptStatus").Text == Loc.T("clip.search.empty"), "A nonmatching search lacks an empty state.");
                    query.Text = language == "ru" ? "реки" : "river";
                    Pump(5);
                    CaptureClipTab(window, "transcript", suffix, output);

                    Required<RadioButton>(window, "ClipPartsTab").IsChecked = true;
                    Pump(3);
                    ClickTag(window, "clipDownloadAll");
                    Settle(window, () => backend.Active.Count == 3);
                    Ensure(backend.Active.Count == 3, "Download all did not start the three clips.");
                    Ensure(backend.Active.Select(a => a.Options.Section).SequenceEqual(ExpectedClipSections), "The batch did not preserve its intervals.");
                    Ensure(backend.Active[0].Options.SectionName == firstName, "The named manual clip lost its name.");
                    Ensure(backend.Active[1].Options.SectionName == chapters[1].Title && backend.Active[2].Options.SectionName == chapters[2].Title, "Chapter filenames lost their names.");
                    Ensure(!Required<Grid>(window, "ClipOverlay").IsVisible, "The editor did not close after queuing its batch.");
                    window.Queue.StopAll();
                    foreach (var active in backend.Active) active.Done.TrySetResult(new RunResult(-1, true));
                    Pump(8);
                    window.Close();
                    Pump(3);
                    checks.Add(suffix + ": manual range, named batch, chapters, transcript hit/miss, cue boundaries, retained drafts, layout passed");
                }
        CheckStaleClipCallbacks();
        checks.Add("Delayed metadata and transcripts from closed editors were ignored; empty transcript state passed.");
        File.WriteAllLines(Path.Combine(output, "checks.txt"), checks);
        Console.WriteLine("Clip workspace checks passed: " + checks.Count.ToString(CultureInfo.InvariantCulture) + " scenarios, 24 screenshots.");
        return 0;
    }

    static void CheckStaleClipCallbacks()
    {
        var (window, backend) = Open("dark", "en", audio: false);
        backend.Offline = true;
        window.Queue.Add([Engine.SearchPrefix + DemoBackend.ClipFixtureTitle]);
        Settle(window, () => window.Queue.Items.Count == 1 && window.Queue.Items[0].State == ItemState.Ready);
        var item = window.Queue.Items[0];
        var oldMetadata = new TaskCompletionSource<ClipMetadataInfo>();
        backend.OnClipMetadata = () => oldMetadata.Task;
        window.OpenClip(item);
        Required<RadioButton>(window, "ClipChaptersTab").IsChecked = true;
        Pump(3);
        ClickTag(window, "clipClose");
        backend.OnClipMetadata = null;
        window.OpenClip(item);
        Required<RadioButton>(window, "ClipChaptersTab").IsChecked = true;
        Pump(8);
        oldMetadata.SetResult(new ClipMetadataInfo("en", 600, [], []));
        Pump(8);
        Ensure(Rows<ClipChapterRow>(window, "ClipChaptersList").Length == 4, "Old metadata replaced the reopened editor's chapters.");

        ClickTag(window, "clipClose");
        var oldTranscript = new TaskCompletionSource<IReadOnlyList<SubtitleCue>>();
        backend.OnTranscript = _ => oldTranscript.Task;
        window.OpenClip(item);
        Required<RadioButton>(window, "ClipTranscriptTab").IsChecked = true;
        Pump(3);
        ClickTag(window, "clipClose");
        backend.OnTranscript = null;
        window.OpenClip(item);
        Required<RadioButton>(window, "ClipTranscriptTab").IsChecked = true;
        Pump(8);
        oldTranscript.SetResult([new SubtitleCue(0, 10, "Stale result")]);
        Pump(8);
        Ensure(Rows<ClipCueRow>(window, "ClipTranscriptList").Length == 3, "An old transcript replaced the reopened editor's captions.");

        ClickTag(window, "clipClose");
        backend.OnTranscript = _ => Task.FromResult<IReadOnlyList<SubtitleCue>>([]);
        window.OpenClip(item);
        Required<RadioButton>(window, "ClipTranscriptTab").IsChecked = true;
        Pump(8);
        Ensure(Rows<ClipCueRow>(window, "ClipTranscriptList").Length == 0, "An empty transcript kept old results.");
        Ensure(Required<TextBlock>(window, "ClipTranscriptStatus").Text == Loc.T("clip.transcript.empty"), "An empty transcript kept showing a loading state.");
        window.Close();
        Pump(3);
    }

    static void CaptureClipTab(MainWindow window, string tab, string suffix, string output)
    {
        var dialog = Required<Border>(window, "ClipDialog");
        Pump(8);
        foreach (var control in dialog.GetVisualDescendants().OfType<Control>().Where(c => c.IsEffectivelyVisible && c is Button or TextBox or ComboBox or RadioButton))
        {
            if (control.TranslatePoint(default, dialog) is not { } at) continue;
            Ensure(at.X >= -1 && at.X + control.Bounds.Width <= dialog.Bounds.Width + 1,
                "Control runs outside the clip dialog: " + (control.Name ?? control.GetType().Name) + " (" + suffix + ", " + tab + ")");
        }
        var overflow = Overflowing(dialog).Where(s => !s.EndsWith(DemoBackend.ClipFixtureTitle, StringComparison.Ordinal)).Distinct().ToArray();
        Ensure(overflow.Length == 0, "Clip text does not fit (" + suffix + ", " + tab + "): " + string.Join("; ", overflow));
        if (window.Width < 1060)
        {
            var scroll = dialog.GetVisualDescendants().OfType<ScrollViewer>().First();
            scroll.Offset = new Vector(0, scroll.Extent.Height);
            Pump(8);
        }
        Save(window, Path.Combine(output, "clips-" + tab + "-" + suffix + ".png"));
    }

    static T Required<T>(MainWindow window, string name) where T : Control => window.FindControl<T>(name) ?? throw new InvalidOperationException("Missing control: " + name);

    static T[] Rows<T>(MainWindow window, string name) => Required<ItemsControl>(window, name).ItemsSource?.Cast<T>().ToArray() ?? [];

    static void ClickTag(MainWindow window, string tag)
    {
        var button = window.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.IsEffectivelyVisible && Equals(b.Tag, tag));
        if (button == null) throw new InvalidOperationException("Missing button: " + tag);
        Click(button);
    }

    static void Click(Button button)
    {
        Ensure(button.IsEnabled, "A required button is disabled: " + button.Tag);
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, button));
        Pump(4);
    }

    static void TypeTime(MainWindow window, string name, string value)
    {
        var box = Required<TextBox>(window, name);
        box.Focus();
        box.Text = value;
        box.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Source = box, Key = Key.Enter });
        Pump(3);
    }

    static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    static int Pictures(string output)
    {
        foreach (var (theme, language, suffix) in new[] { ("dark", "en", "dark"), ("light", "en", "light") })
        {
            var (window, backend) = Open(theme, language);
            Save(window, Path.Combine(output, "empty-" + suffix + ".png"));
            window.Queue.Add(DemoBackend.List.Select(s => Engine.SearchPrefix + s));
            Settle(window, () => window.Queue.Items.All(i => i.State == ItemState.Ready && i.ThumbBytes != null));
            Save(window, Path.Combine(output, "queue-" + suffix + ".png"));
            window.OpenPicker(window.Queue.Items[0]);
            Settle(window, () => false, 40);
            Save(window, Path.Combine(output, "versions-" + suffix + ".png"));
            window.FindControl<Grid>("PickerOverlay")!.IsVisible = false;
            window.OpenAuth();
            Pump(10);
            Save(window, Path.Combine(output, "signin-" + suffix + ".png"));
            window.FindControl<Grid>("AuthOverlay")!.IsVisible = false;
            window.FindControl<Grid>("SettingsOverlay")!.IsVisible = true;
            Pump(10);
            Save(window, Path.Combine(output, "settings-" + suffix + ".png"));
            window.FindControl<Grid>("SettingsOverlay")!.IsVisible = false;
            window.OpenImport(LibraryImport.GuessTable("Pixelland;Kevin MacLeod;4:02\nCipher;Kevin MacLeod;3:52\nHorizons;Scott Buckley;4:54\nFilaments;Scott Buckley;5:40\nWe need a plan;Komiku;2:02\nThe end ?;Komiku;3:59\n", "My list")!);
            Pump(10);
            Save(window, Path.Combine(output, "import-" + suffix + ".png"));
            window.FindControl<Grid>("ImportOverlay")!.IsVisible = false;
            window.Queue.StartAll();
            for (int n = 0; n < 30; n++) { backend.Tick(); Pump(2); Thread.Sleep(5); }
            Save(window, Path.Combine(output, "downloading-" + suffix + ".png"));
            foreach (var active in backend.Active.ToList()) active.Done.TrySetResult(new RunResult(-1, true));
            window.Close();
            Pump(3);

            // Video mode has its own options and its own kind of card.
            var (videos, _) = Open(theme, language, audio: false);
            videos.Queue.Add(DemoBackend.VideoList.Select(s => Engine.SearchPrefix + s));
            Settle(videos, () => videos.Queue.Items.All(i => i.State == ItemState.Ready && i.ThumbBytes != null));
            Save(videos, Path.Combine(output, "video-" + suffix + ".png"));
            videos.Close();
            Pump(3);
        }
        return 0;
    }

    // The README animation, frame by frame, driven by the script instead of the clock so it is the same on every run.
    static int Frames(string output)
    {
        var (window, backend) = Open("dark", "en", 1240, 780);
        int number = 0;
        void Frame(int repeat = 1)
        {
            for (int n = 0; n < repeat; n++)
            {
                Pump(2);
                Save(window, Path.Combine(output, "frame" + (number++).ToString("00000", CultureInfo.InvariantCulture) + ".png"));
                if (number % 40 == 0) { GC.Collect(); GC.WaitForPendingFinalizers(); }
            }
        }

        var input = window.FindControl<TextBox>("InputBox")!;
        Frame(14); // the empty window

        // Typing a list of song names.
        string list = string.Join("\n", DemoBackend.List);
        for (int n = 4; n <= list.Length; n += 4) { input.Text = list[..n]; Frame(); }
        input.Text = list;
        Frame(8);

        // Adding them: cards appear and find their tracks.
        input.Text = "";
        window.Queue.Add(DemoBackend.List.Select(s => Engine.SearchPrefix + s));
        Frame(6);
        for (int n = 0; n < 60 && window.Queue.Items.Any(i => i.State != ItemState.Ready || i.ThumbBytes == null); n++) { Thread.Sleep(30); Frame(); }
        Frame(12);

        // Filtering and downloading.
        window.Queue.StartAll();
        for (int n = 0; n < 200 && window.Queue.Items.Any(i => i.State != ItemState.Done); n++) { backend.Tick(); Thread.Sleep(10); Frame(); }
        Frame(24); // the finished queue
        window.Close();
        Pump(3);
        Console.WriteLine("Frames: " + number.ToString(CultureInfo.InvariantCulture));
        return 0;
    }
}
