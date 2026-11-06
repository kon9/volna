using System.Collections.Concurrent;
using Volna.Core;

namespace Volna.Tests;

/// <summary>Runs test code on one thread, the way the queue runs on the UI thread.</summary>
public sealed class SingleThread : SynchronizationContext
{
    readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> work = new();

    public override void Post(SendOrPostCallback d, object? state) => work.Add((d, state));

    public static void Run(Func<Task> test)
    {
        var previous = Current;
        var context = new SingleThread();
        SetSynchronizationContext(context);
        Exception? failure = null;
        bool finished = false;
        try
        {
            async Task Body()
            {
                try { await test(); }
                catch (Exception ex) { failure = ex; }
                finished = true;
            }
            _ = Body();
            var limit = DateTime.UtcNow.AddSeconds(30);
            while (!finished)
            {
                if (DateTime.UtcNow > limit) throw new TimeoutException("The test did not finish.");
                if (context.work.TryTake(out var item, 50)) item.Callback(item.State);
            }
        }
        finally { SetSynchronizationContext(previous); }
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

/// <summary>A pretend yt-dlp and YouTube Music: answers instantly from tables, and records what was asked.</summary>
public sealed class FakeBackend : IQueueBackend
{
    public readonly List<string> Lookups = new();
    public readonly List<DownloadOptions> Downloads = new();
    public readonly Dictionary<string, List<Dictionary<string, object?>>> Results = new();
    public readonly Dictionary<string, List<Dictionary<string, object?>>> Music = new();
    public readonly Queue<string> LookupErrors = new();
    public Func<DownloadOptions, Action<string>, Task<RunResult>>? OnDownload;
    public TaskCompletionSource? HoldLookups;
    public TaskCompletionSource? HoldMusic;
    public string Folder = "";
    public int Updates, NightlyUpdates;
    public bool UpdateChanges = true, MusicFails;
    public string? LyricsText;

    public Task<ToolUpdate> UpdateToolAsync(string proxy, bool nightly, Action<string> log)
    {
        if (nightly) NightlyUpdates++;
        else Updates++;
        return Task.FromResult(new ToolUpdate(true, nightly || UpdateChanges));
    }

    public Task<string?> LyricsAsync(string artist, string title, double? seconds, string proxy) => Task.FromResult(LyricsText);

    public static Dictionary<string, object?> Video(string id, string title, string? channel = null, string? playlist = null) =>
        Json.Map(("id", id), ("title", title), ("uploader", channel ?? "Channel"), ("webpage_url", "https://www.youtube.com/watch?v=" + id), ("ie_key", "Youtube"), ("extractor_key", "Youtube"), ("playlist_title", playlist));

    public static Dictionary<string, object?> Track(string id, string title, string artist) =>
        Json.Map(("id", id), ("title", title), ("uploader", artist), ("webpage_url", "https://www.youtube.com/watch?v=" + id), ("ie_key", "Youtube"), ("extractor_key", "Youtube"), ("source", "music"), ("is_original", true));

    public async Task<LookupResult> LookupAsync(IReadOnlyList<string> requests, int limit, string proxy, ProcessRunner runner, Action<string> log, Action<int>? found = null)
    {
        Lookups.AddRange(requests);
        found?.Invoke(40);
        if (HoldLookups != null) await HoldLookups.Task;
        string errors = LookupErrors.Count > 0 ? LookupErrors.Dequeue() : "";
        var entries = new List<Dictionary<string, object?>>();
        if (errors.Length == 0)
        {
            foreach (string request in requests)
            {
                string key = request.StartsWith("ytsearch", StringComparison.Ordinal) ? request[(request.IndexOf(':') + 1)..] : request;
                if (Results.TryGetValue(key, out var list))
                    entries.AddRange(list.Select(r => { var copy = new Dictionary<string, object?>(r); if (request.StartsWith("ytsearch", StringComparison.Ordinal)) copy["playlist_title"] = key; return copy; }));
            }
        }
        return new LookupResult(entries, errors, false);
    }

    public async Task<List<Dictionary<string, object?>>> SearchMusicAsync(string query, int limit, string proxy)
    {
        if (HoldMusic != null) await HoldMusic.Task;
        if (MusicFails) throw new HttpRequestException("(500) Server Error");
        return Music.TryGetValue(query, out var list) ? list : new();
    }

    public Task<RunResult> DownloadAsync(DownloadOptions options, ProcessRunner runner, Action<string> onLine)
    {
        Downloads.Add(options);
        if (OnDownload != null) return OnDownload(options, onLine);
        // By default a download succeeds and leaves a file.
        string id = options.Url[(options.Url.IndexOf("v=", StringComparison.Ordinal) + 2)..];
        string file = Path.Combine(options.Folder, "file [" + id + "]." + (options.Audio ? options.AudioFormat : options.Container));
        File.WriteAllText(file, "data");
        onLine("__FILE__" + file);
        return Task.FromResult(new RunResult(0, false));
    }

    public Task<byte[]?> ThumbnailAsync(string url, string proxy) => Task.FromResult<byte[]?>(null);
}

public sealed class TestRoom : IDisposable
{
    public readonly string Folder = Path.Combine(Path.GetTempPath(), "volna-test-" + Guid.NewGuid().ToString("N"));
    public readonly FakeBackend Backend = new();
    public readonly Preferences Prefs = new();
    public readonly QueueController Queue;
    public readonly List<(string Text, ToastKind Kind)> Toasts = new();
    public readonly List<(int Done, int Failed)> Runs = new();

    public TestRoom()
    {
        AppPaths.Override = Path.Combine(Folder, "data");
        Auth.Load();
        Loc.Use("en");
        string downloads = Path.Combine(Folder, "downloads");
        Directory.CreateDirectory(downloads);
        Prefs.Folder = downloads;
        Prefs.Audio = true;
        Prefs.OriginalTracks = true;
        Prefs.Parallel = 2;
        Backend.Folder = downloads;
        Queue = new QueueController(Backend, Prefs) { MusicDelayMilliseconds = 0 };
        Queue.Toast += (text, kind) => Toasts.Add((text, kind));
        Queue.RunCompleted += (done, failed) => Runs.Add((done, failed));
    }

    public static IEnumerable<string> Entries(params string[] lines) => Engine.ParseEntries(string.Join("\n", lines));

    /// <summary>Lets queued work run until the condition holds.</summary>
    public async Task Until(Func<bool> condition, string what = "condition")
    {
        for (int n = 0; n < 2000; n++)
        {
            if (condition()) return;
            await Task.Delay(5);
        }
        throw new TimeoutException("Never happened: " + what + ". States: " + string.Join(", ", Queue.Items.Select(i => i.State)));
    }

    public void Dispose()
    {
        Queue.Dispose();
        AppPaths.Override = TestSetup.Data;
        try { Directory.Delete(Folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
