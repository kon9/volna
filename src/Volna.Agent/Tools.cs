using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Volna.Core;

namespace Volna.Agent;

sealed class ToolResult
{
    public object? Data;
    public bool IsError;
    public readonly List<(string Mime, string Base64)> Images = new();
    public readonly List<(string Mime, string Base64)> Audio = new();
    public static ToolResult Error(string message) => new() { IsError = true, Data = Json.Map(("error", message)) };
}

sealed class AgentJob
{
    public string Id = "", Target = "", Kind = "audio", AudioFormat = "mp3", Container = "mp4";
    public string? Url, VideoId, Title, Artist, MatchedBy, File, Error, Details;
    public int MaxHeight = 1080;
    public bool PreferOriginal = true, IsOriginal, AlreadyExisted;
    public volatile bool Cancelled;
    public volatile string Status = "resolving";
    public double Percent = -1, Speed = -1, Eta = -1;
    public ProcessRunner? Runner;
    public readonly TaskCompletionSource Finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public readonly StringBuilder Errors = new();
    public bool IsDone => Status is "done" or "failed" or "cancelled";
}

static class Tools
{
    static readonly ConcurrentDictionary<string, AgentJob> Jobs = new();
    static int nextJob;
    // Downloads and lookups stay unhurried, so an eager model cannot get the connection throttled by YouTube.
    static readonly SemaphoreSlim DownloadGate = new(2), LookupGate = new(2), MusicGate = new(1);
    static readonly Regex YouTubeId = new(@"(?:[?&]v=|youtu\.be/|/shorts/|/live/|/embed/)([A-Za-z0-9_-]{6,})", RegexOptions.IgnoreCase);
    const int TranscriptPage = 20000;
    const string NeedsHuman = "Ask the person to press Sign in in the Volna window; a model cannot sign in by itself.";

    public static bool WaitForever;  // the command line stays until a download ends; there is nobody to poll

    // ---------- Settings ----------

    public static string Folder()
    {
        string? folder = Environment.GetEnvironmentVariable("VOLNA_FOLDER");
        if (string.IsNullOrWhiteSpace(folder))
        {
            try { folder = Json.ParseObject(File.ReadAllText(AppPaths.Settings)).Str("folder"); }
            catch (IOException) { }
        }
        return Path.GetFullPath(string.IsNullOrWhiteSpace(folder) ? AppPaths.DefaultDownloads : folder);
    }

    static string Proxy() => Environment.GetEnvironmentVariable("VOLNA_PROXY") ?? "";

    // ---------- Arguments ----------

    static string? Text(Dictionary<string, object?> args, string name, string? fallback = null)
    {
        if (!args.TryGetValue(name, out object? value) || value == null) return fallback;
        string text = (value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture) ?? "").Trim();
        return text.Length == 0 ? fallback : text;
    }
    static double Number(Dictionary<string, object?> args, string name, double fallback)
    {
        string? text = Text(args, name);
        return text != null && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : fallback;
    }
    static bool Flag(Dictionary<string, object?> args, string name, bool fallback)
    {
        if (!args.TryGetValue(name, out object? value) || value == null) return fallback;
        if (value is bool flag) return flag;
        string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        return text.Equals("true", StringComparison.OrdinalIgnoreCase) || text == "1";
    }
    static bool IsLink(string? text) => Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) && (uri.Scheme == "https" || uri.Scheme == "http") && !string.IsNullOrEmpty(uri.Host) && string.IsNullOrEmpty(uri.UserInfo);

    // ---------- Running yt-dlp ----------

    static async Task<List<string>> RunYtDlpAsync(Func<IReadOnlyList<string>, List<string>> buildArgs, StringBuilder errors)
    {
        var lease = Auth.Lease();
        var lines = new List<string>();
        RunResult? result = null;
        using var runner = new ProcessRunner();
        try
        {
            result = await runner.RunAsync(ToolManager.YtDlp, buildArgs(lease.Args), line =>
            {
                lock (lines)
                {
                    if (line.StartsWith("ERROR", StringComparison.Ordinal)) errors.AppendLine(line);
                    else lines.Add(line);
                }
            }).ConfigureAwait(false);
        }
        finally { lease.Complete(result is { ExitCode: 0, Cancelled: false }); }
        // A browser that cannot be read must not cost links that need no sign-in.
        if (lease.Args.Count > 0 && Engine.CookiesUnreadable(errors.ToString()) && (Auth.Suspend() || Auth.Suspended))
        {
            errors.Clear();
            lock (lines) lines.Clear();
            return await RunYtDlpAsync(buildArgs, errors).ConfigureAwait(false);
        }
        return lines;
    }

    static Task? toolInstall;

    /// <summary>Someone may set up an assistant before ever opening the app, so the tools come by themselves here too.</summary>
    public static void BeginToolInstall()
    {
        if (ToolManager.Missing().Count > 0) toolInstall = Task.Run(() => ToolManager.InstallMissingAsync(new Progress<ToolProgress>(_ => { }), CancellationToken.None, Proxy()));
    }

    /// <summary>Null when the tools are there; otherwise what to tell the model. A setup takes minutes, and a call has about one.</summary>
    public static async Task<ToolResult?> WaitForToolsAsync()
    {
        if (toolInstall == null) return null;
        if (!WaitForever) await Task.WhenAny(toolInstall, Task.Delay(TimeSpan.FromSeconds(50))).ConfigureAwait(false);
        else await Task.WhenAny(toolInstall).ConfigureAwait(false);
        if (!toolInstall.IsCompleted) return ToolResult.Error("The tools are still being installed on first use, which takes a few minutes. Try again in a minute.");
        if (toolInstall.IsFaulted) return ToolResult.Error("The tools could not be installed: " + toolInstall.Exception?.GetBaseException().Message);
        return null;
    }

    static async Task<List<Dictionary<string, object?>>> SearchYouTubeAsync(string query, int limit, StringBuilder errors)
    {
        await LookupGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var lines = await RunYtDlpAsync(auth => Engine.MetadataArgs(new[] { "ytsearch" + limit.ToString(CultureInfo.InvariantCulture) + ":" + query }, auth, Proxy(), limit), errors).ConfigureAwait(false);
            return lines.Where(l => l.StartsWith('{')).Select(Json.ParseObject).OfType<Dictionary<string, object?>>().ToList();
        }
        finally { LookupGate.Release(); }
    }

    static async Task<List<Dictionary<string, object?>>> SearchMusicAsync(string query, int limit)
    {
        await MusicGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var results = await MusicSearch.SearchAsync(query, limit, Proxy()).ConfigureAwait(false);
            await Task.Delay(700).ConfigureAwait(false);
            return results;
        }
        finally { MusicGate.Release(); }
    }

    static Dictionary<string, object?> Describe(Dictionary<string, object?> info)
    {
        string? channel = info.Str("uploader") ?? info.Str("channel");
        bool original = info.Flag("is_original") || (channel != null && channel.EndsWith(" - Topic", StringComparison.Ordinal));
        if (channel != null && channel.EndsWith(" - Topic", StringComparison.Ordinal)) channel = channel[..^8];
        var result = Json.Map(("id", info.Str("id")), ("title", info.Str("title")), ("url", info.Str("webpage_url") ?? info.Str("url")), ("is_original_track", original));
        if (channel != null) result["channel"] = channel;
        if (info.Str("album") != null) result["album"] = info.Str("album");
        if (info.Num("duration") is { } duration) result["duration_seconds"] = duration;
        if (info.Num("view_count") is { } views) result["views"] = views;
        return result;
    }

    static ToolResult Failure(string errors)
    {
        var data = Json.Map(("error", Engine.FriendlyError(errors, Auth.SignedIn)), ("details", errors.Trim()));
        if (Engine.NeedsLogin(errors) && !Auth.SignedIn) data["needs_human"] = NeedsHuman;
        return new ToolResult { IsError = true, Data = data };
    }

    // ---------- Tools: search and information ----------

    public static async Task<ToolResult> SearchAsync(Dictionary<string, object?> args)
    {
        string? query = Text(args, "query");
        if (query == null) return ToolResult.Error("query is required");
        string source = Text(args, "source", "youtube")!.ToLowerInvariant();
        int limit = (int)Math.Clamp(Number(args, "limit", 8), 1, 20);
        List<Dictionary<string, object?>> results;
        if (source == "music") results = await SearchMusicAsync(query, limit).ConfigureAwait(false);
        else if (source == "youtube")
        {
            var errors = new StringBuilder();
            results = await SearchYouTubeAsync(query, limit, errors).ConfigureAwait(false);
            if (results.Count == 0 && errors.Length > 0) return Failure(errors.ToString());
            // For some exact "Artist - Song" queries YouTube's layout comes back unreadable and empty.
            if (results.Count == 0) { results = await SearchMusicAsync(query, limit).ConfigureAwait(false); source = "music"; }
        }
        else return ToolResult.Error("source must be \"youtube\" or \"music\"");
        return new ToolResult { Data = Json.Map(("source", source), ("results", results.Select(Describe).ToList())) };
    }

    static List<string> Keys(Dictionary<string, object?> info, string name) =>
        info.Raw(name) is Dictionary<string, object?> map ? map.Keys.Where(k => k != "live_chat").ToList() : new List<string>();

    public static async Task<ToolResult> VideoInfoAsync(Dictionary<string, object?> args)
    {
        string? url = Text(args, "url");
        if (!IsLink(url)) return ToolResult.Error("url must be a full http(s) link");
        var errors = new StringBuilder();
        List<string> lines;
        await LookupGate.WaitAsync().ConfigureAwait(false);
        try
        {
            lines = await RunYtDlpAsync(auth =>
            {
                var list = Engine.CommonArgs(auth, Proxy());
                list.AddRange(new[] { "--dump-single-json", "--skip-download", "--no-playlist", "--flat-playlist", "--playlist-items", "1:50", "--", url! });
                return list;
            }, errors).ConfigureAwait(false);
        }
        finally { LookupGate.Release(); }
        var info = lines.Where(l => l.StartsWith('{')).Select(Json.ParseObject).FirstOrDefault(i => i != null);
        if (info == null) return Failure(errors.ToString());

        var result = Describe(info);
        foreach (string name in new[] { "upload_date", "like_count", "comment_count", "categories", "chapters", "track", "artist", "album", "age_limit", "live_status", "language" })
            if (info.Raw(name) != null) result[name] = info.Raw(name);
        string? description = info.Str("description");
        if (description != null) result["description"] = description.Length > 5000 ? description[..5000] + "…" : description;
        if (info.Raw("tags") is List<object?> tags) result["tags"] = tags.Take(30).ToList();
        result["subtitle_languages"] = Keys(info, "subtitles");
        result["auto_caption_languages"] = Keys(info, "automatic_captions").Where(k => k.EndsWith("-orig", StringComparison.Ordinal)).ToList();
        var heights = (info.Raw("formats") as List<object?> ?? new()).OfType<Dictionary<string, object?>>().Select(f => f.Num("height")).OfType<double>().ToList();
        if (heights.Count > 0) result["max_height"] = heights.Max();
        if (info.Raw("entries") is List<object?> entries)
        {
            result["is_playlist"] = true;
            result["entries"] = entries.OfType<Dictionary<string, object?>>().Select(Describe).ToList();
        }
        return new ToolResult { Data = result };
    }

    // ---------- Tools: transcript ----------

    public static async Task<ToolResult> TranscriptAsync(Dictionary<string, object?> args)
    {
        string? url = Text(args, "url");
        if (!IsLink(url)) return ToolResult.Error("url must be a full http(s) link");
        string[] wanted = Text(args, "languages", "en")!.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToArray();
        var errors = new StringBuilder();
        string folder = Path.Combine(AppPaths.Cache, "agent-subs", Guid.NewGuid().ToString("N"));
        await LookupGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var lines = await RunYtDlpAsync(auth =>
            {
                var list = Engine.CommonArgs(auth, Proxy());
                list.AddRange(new[] { "--skip-download", "--no-playlist", "--playlist-items", "1", "--print", "%(.{title,language,subtitles,automatic_captions})j", "--", url! });
                return list;
            }, errors).ConfigureAwait(false);
            var info = lines.Where(l => l.StartsWith('{')).Select(Json.ParseObject).FirstOrDefault(i => i != null);
            if (info == null) return Failure(errors.ToString());

            var manual = Keys(info, "subtitles");
            var automatic = Keys(info, "automatic_captions");
            string? language = null;
            bool auto = false;
            foreach (string lang in wanted)
            {
                language = manual.FirstOrDefault(k => k == lang || k.StartsWith(lang + "-", StringComparison.Ordinal));
                if (language != null) break;
            }
            language ??= manual.FirstOrDefault();
            if (language == null)
            {
                // Automatic captions come in the spoken language ("-orig") and as machine translations of it.
                auto = true;
                language = automatic.FirstOrDefault(k => k.EndsWith("-orig", StringComparison.Ordinal));
                if (language != null)
                {
                    string spoken = language[..^5];
                    if (wanted.Contains(spoken) && automatic.Contains(spoken)) language = spoken;
                }
                else foreach (string lang in wanted)
                {
                    language = automatic.FirstOrDefault(k => k == lang);
                    if (language != null) break;
                }
            }
            if (language == null) return new ToolResult { IsError = true, Data = Json.Map(("error", "This video has no subtitles or automatic captions.")) };

            Directory.CreateDirectory(folder);
            errors.Clear();
            await RunYtDlpAsync(auth =>
            {
                var list = Engine.CommonArgs(auth, Proxy());
                list.AddRange(new[] { "--skip-download", "--no-playlist", "--playlist-items", "1", auto ? "--write-auto-subs" : "--write-subs", "--sub-langs", language, "--sub-format", "vtt/best", "--convert-subs", "vtt",
                    "--paths", folder, "--output", "subtitle.%(ext)s", "--", url! });
                return list;
            }, errors).ConfigureAwait(false);
            string? file = Directory.GetFiles(folder, "*.vtt").FirstOrDefault();
            if (file == null) return Failure(errors.Length > 0 ? errors.ToString() : "Subtitles could not be downloaded.");

            string text = Subtitles.VttToText(File.ReadAllText(file), Flag(args, "include_timestamps", false));
            int offset = (int)Math.Max(0, Number(args, "offset", 0));
            int size = (int)Math.Clamp(Number(args, "max_chars", TranscriptPage), 1000, 100000);
            string page = offset >= text.Length ? "" : text.Substring(offset, Math.Min(size, text.Length - offset));
            var data = Json.Map(("title", info.Str("title")), ("language", language), ("automatic", auto), ("total_chars", text.Length), ("offset", offset), ("text", page));
            if (offset + page.Length < text.Length) data["next_offset"] = offset + page.Length;
            return new ToolResult { Data = data };
        }
        finally
        {
            LookupGate.Release();
            try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // ---------- Tools: downloads ----------

    public static async Task<ToolResult> DownloadAsync(Dictionary<string, object?> args)
    {
        string? target = Text(args, "target");
        if (target == null) return ToolResult.Error("target is required: a link, or the name of a song or video");
        string kind = Text(args, "kind", "audio")!.ToLowerInvariant();
        if (kind is not ("audio" or "video")) return ToolResult.Error("kind must be \"audio\" or \"video\"");
        string audioFormat = Text(args, "audio_format", "mp3")!.ToLowerInvariant();
        if (audioFormat is not ("mp3" or "m4a" or "best")) return ToolResult.Error("audio_format must be mp3, m4a or best");
        string container = Text(args, "container", "mp4")!.ToLowerInvariant();
        if (container is not ("mp4" or "mkv")) return ToolResult.Error("container must be mp4 or mkv");
        // Song names may hold a colon ("Re:Start - Some Song"), so only real address forms count.
        if (Regex.IsMatch(target, @"^([a-zA-Z][a-zA-Z0-9+.\-]*://|(file|javascript|data|ftp|about|mailto):)", RegexOptions.IgnoreCase) && !IsLink(target))
            return ToolResult.Error("target must be a song or video name, or a full http(s) link");

        AgentJob job = StartJob(target, kind, audioFormat, container, (int)Number(args, "max_height", 1080), Flag(args, "prefer_original", true));
        CleanOldOutput();
        await WaitAsync(new[] { job }, Number(args, "wait_seconds", 45)).ConfigureAwait(false);
        return new ToolResult { Data = JobView(job), IsError = job.Status == "failed" };
    }

    static AgentJob StartJob(string target, string kind, string audioFormat, string container, int maxHeight, bool preferOriginal)
    {
        var job = new AgentJob
        {
            Target = target,
            Kind = kind,
            AudioFormat = audioFormat,
            Container = container,
            MaxHeight = maxHeight,
            PreferOriginal = preferOriginal
        };
        // The same request while one is running or finished joins it, unless its file has since gone away.
        // Calls run side by side, so finding and adding must be one step.
        lock (Jobs)
        {
            AgentJob? existing = Jobs.Values.FirstOrDefault(j => j.Target == target && j.Kind == kind && j.AudioFormat == audioFormat && j.Container == container
                && j.MaxHeight == job.MaxHeight && j.PreferOriginal == job.PreferOriginal && j.Status is not ("failed" or "cancelled") && (j.File == null || File.Exists(j.File)));
            if (existing != null) job = existing;
            else
            {
                job.Id = "job" + Interlocked.Increment(ref nextJob).ToString(CultureInfo.InvariantCulture);
                Jobs[job.Id] = job;
                PruneJobs();
                AgentJob started = job;
                _ = Task.Run(() => RunJobAsync(started));
            }
        }
        return job;
    }

    /// <summary>Many downloads at once: a list of songs, or what list_playlist returned. They run at the usual gentle pace.</summary>
    public static Task<ToolResult> DownloadListAsync(Dictionary<string, object?> args)
    {
        string kind = Text(args, "kind", "audio")!.ToLowerInvariant();
        if (kind is not ("audio" or "video")) return Task.FromResult(ToolResult.Error("kind must be \"audio\" or \"video\""));
        string audioFormat = Text(args, "audio_format", "mp3")!.ToLowerInvariant();
        if (audioFormat is not ("mp3" or "m4a" or "best")) return Task.FromResult(ToolResult.Error("audio_format must be mp3, m4a or best"));
        string container = Text(args, "container", "mp4")!.ToLowerInvariant();
        if (container is not ("mp4" or "mkv")) return Task.FromResult(ToolResult.Error("container must be mp4 or mkv"));
        string text = args.TryGetValue("targets", out object? given) && given is List<object?> items ? string.Join('\n', items.OfType<string>()) : Text(args, "targets") ?? "";
        // The same reading as the app's input box: numbering goes, and every line is a link or a name.
        List<string> entries = Engine.ParseEntries(text);
        if (entries.Count == 0) return Task.FromResult(ToolResult.Error("targets is required: a list of links or names such as \"Artist - Song\""));
        if (entries.Count > 5000) return Task.FromResult(ToolResult.Error("At most 5000 targets at once"));
        var jobs = entries.Select(e => StartJob(Engine.IsSearch(e) ? e[Engine.SearchPrefix.Length..] : e, kind, audioFormat, container, (int)Number(args, "max_height", 1080), Flag(args, "prefer_original", true))).ToList();
        var data = Summary(jobs);
        data["note"] = "This takes a while. Poll download_status with wait_seconds rather than waiting here.";
        return Task.FromResult(new ToolResult { Data = data });
    }

    // Thousands of jobs would not fit in an answer, and only what is unusual needs attention.
    static Dictionary<string, object?> Summary(List<AgentJob> jobs)
    {
        var failed = jobs.Where(j => j.Status == "failed").ToList();
        return Json.Map(("total", jobs.Count), ("done", jobs.Count(j => j.Status == "done")), ("failed", failed.Count), ("cancelled", jobs.Count(j => j.Status == "cancelled")),
            ("unfinished", jobs.Count(j => !j.IsDone)), ("failures", failed.Take(100).Select(j => Json.Map(("target", j.Target), ("error", j.Error))).ToList()));
    }

    static readonly SemaphoreSlim RepairGate = new(1);
    static DateTime lastRepair;

    // As in the app: a failure a newer yt-dlp may fix updates it, stable first, then nightly. Several downloads can fail
    // together and one update serves them all.
    static async Task<bool> RepairAsync()
    {
        await RepairGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (DateTime.Now - lastRepair < TimeSpan.FromMinutes(30)) return true;
            ToolUpdate update = await ToolManager.UpdateYtDlpAsync(Proxy(), false, _ => { }).ConfigureAwait(false);
            if (update is { Ok: true, Changed: false }) update = await ToolManager.UpdateYtDlpAsync(Proxy(), true, _ => { }).ConfigureAwait(false);
            lastRepair = DateTime.Now;
            return update.Ok;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
        finally { RepairGate.Release(); }
    }

    public static async Task<ToolResult> ListPlaylistAsync(Dictionary<string, object?> args)
    {
        string? url = Text(args, "url");
        if (!IsLink(url)) return ToolResult.Error("url must be a full http(s) link to a playlist or channel");
        int limit = (int)Math.Clamp(Number(args, "limit", 200), 1, Engine.PlaylistLimit);
        var errors = new StringBuilder();
        await LookupGate.WaitAsync().ConfigureAwait(false);
        List<string> lines;
        try { lines = await RunYtDlpAsync(auth => Engine.MetadataArgs(new[] { url! }, auth, Proxy(), limit), errors).ConfigureAwait(false); }
        finally { LookupGate.Release(); }
        var entries = lines.Where(l => l.StartsWith('{')).Select(Json.ParseObject).OfType<Dictionary<string, object?>>().ToList();
        if (entries.Count == 0) return errors.Length > 0 ? Failure(errors.ToString()) : ToolResult.Error("Nothing found at this link.");
        return new ToolResult { Data = Json.Map(("title", entries[0].Str("playlist_title")), ("count", entries.Count), ("limit_reached", entries.Count >= limit), ("entries", entries.Select(Describe).ToList())) };
    }

    // A server can run for weeks; only the most recent finished jobs are worth remembering.
    static void PruneJobs()
    {
        var finished = Jobs.Values.Where(j => j.IsDone).OrderByDescending(j => j.Id.Length).ThenByDescending(j => j.Id, StringComparer.Ordinal).Skip(6000).ToList();
        foreach (var old in finished) Jobs.TryRemove(old.Id, out _);
    }

    static async Task WaitAsync(IEnumerable<AgentJob> list, double seconds)
    {
        // Most MCP clients give up on a call after about a minute, so a longer wait must be split into polls.
        double limit = WaitForever ? 7 * 24 * 3600 : Math.Clamp(seconds, 0, 55);
        if (limit <= 0) return;
        await Task.WhenAny(Task.WhenAll(list.Select(j => j.Finished.Task)), Task.Delay(TimeSpan.FromSeconds(limit))).ConfigureAwait(false);
    }

    static async Task RunJobAsync(AgentJob job)
    {
        try
        {
            await ResolveAsync(job).ConfigureAwait(false);
            if (job.Status == "failed") return;
            if (job.Cancelled) { job.Status = "cancelled"; return; }
            string? existing = FindExisting(job);
            if (existing != null) { job.File = existing; job.AlreadyExisted = true; job.Percent = 100; job.Status = "done"; return; }
            job.Status = "queued";
            await DownloadGate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (job.Cancelled) { job.Status = "cancelled"; return; }
                job.Status = "downloading";
                string folder = Folder();
                Directory.CreateDirectory(folder);
                var options = new DownloadOptions
                {
                    Url = job.Url!,
                    Folder = folder,
                    Audio = job.Kind == "audio",
                    Height = job.MaxHeight,
                    AudioFormat = job.AudioFormat,
                    Container = job.Container,
                    EmbedThumbnail = true,
                    Original = job.IsOriginal,
                    CutOfftopic = job.Kind == "audio" && job.PreferOriginal && !job.IsOriginal,
                    Proxy = Proxy(),
                    KeepSource = job.Kind == "audio" && job.VideoId != null && FilesOf(job).Any()
                };
                var lease = Auth.Lease();
                var runner = new ProcessRunner();
                job.Runner = runner;
                RunResult? result = null;
                try
                {
                    if (job.Cancelled) runner.Cancel();
                    result = await runner.RunAsync(ToolManager.YtDlp, Engine.DownloadArgs(options, lease.Args), line => OnDownloadLine(job, line)).ConfigureAwait(false);
                    // Cutting is an extra. If the SponsorBlock service is out of reach, the file is still wanted.
                    if (!result.Cancelled && job.File == null && options.CutOfftopic && job.Errors.ToString().Contains("sponsorblock", StringComparison.OrdinalIgnoreCase))
                    {
                        options.CutOfftopic = false;
                        job.Errors.Clear();
                        result = await runner.RunAsync(ToolManager.YtDlp, Engine.DownloadArgs(options, lease.Args), line => OnDownloadLine(job, line)).ConfigureAwait(false);
                    }
                    if (!result.Cancelled && job.File == null && Engine.DidNotFit(options, job.Errors.ToString()))
                    {
                        Engine.FallBackToMkv(options);
                        job.Errors.Clear();
                        result = await runner.RunAsync(ToolManager.YtDlp, Engine.DownloadArgs(options, lease.Args), line => OnDownloadLine(job, line)).ConfigureAwait(false);
                    }
                    if (!result.Cancelled && !job.Cancelled && job.File == null && lease.Args.Count > 0 && Engine.CookiesUnreadable(job.Errors.ToString()) && (Auth.Suspend() || Auth.Suspended))
                    {
                        job.Errors.Clear();
                        result = await runner.RunAsync(ToolManager.YtDlp, Engine.DownloadArgs(options, Array.Empty<string>()), line => OnDownloadLine(job, line)).ConfigureAwait(false);
                    }
                    if (!result.Cancelled && !job.Cancelled && job.File == null && Engine.CoverDidNotFit(options, job.Errors.ToString()))
                    {
                        options.EmbedThumbnail = false;
                        job.Errors.Clear();
                        result = await runner.RunAsync(ToolManager.YtDlp, Engine.DownloadArgs(options, lease.Args), line => OnDownloadLine(job, line)).ConfigureAwait(false);
                    }
                    if (!result.Cancelled && !job.Cancelled && job.File == null && Engine.LooksOutdated(job.Errors.ToString()) && await RepairAsync().ConfigureAwait(false))
                    {
                        job.Errors.Clear();
                        result = await runner.RunAsync(ToolManager.YtDlp, Engine.DownloadArgs(options, lease.Args), line => OnDownloadLine(job, line)).ConfigureAwait(false);
                    }
                }
                finally
                {
                    runner.Dispose();
                    job.Runner = null;
                    lease.Complete(result is { ExitCode: 0, Cancelled: false });
                }
                if (job.File != null && File.Exists(job.File)) { job.Percent = 100; job.Status = "done"; }
                else if (result.Cancelled || job.Cancelled) job.Status = "cancelled";
                else Fail(job, job.Errors.ToString());
            }
            finally { DownloadGate.Release(); }
        }
        catch (Exception ex)
        {
            job.Error = ex.Message;
            job.Status = "failed";
        }
        finally { job.Finished.TrySetResult(); }
    }

    static void Fail(AgentJob job, string errors)
    {
        job.Error = Engine.FriendlyError(errors, Auth.SignedIn);
        job.Details = errors.Trim();
        job.Status = "failed";
    }

    // Turns the target into one video: a link stays as given, a name is looked up (original track first for audio).
    static async Task ResolveAsync(AgentJob job)
    {
        if (IsLink(job.Target))
        {
            job.Url = job.Target;
            job.MatchedBy = "link";
            // Only YouTube's links carry an id this simple; on other sites the same pattern could mean something else.
            Match id = YouTubeId.Match(job.Target);
            bool youtube = Uri.TryCreate(job.Target, UriKind.Absolute, out Uri? link) && (link.Host.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase) || link.Host.EndsWith("youtu.be", StringComparison.OrdinalIgnoreCase));
            if (id.Success && youtube) job.VideoId = id.Groups[1].Value;
            return;
        }
        Dictionary<string, object?>? found = null;
        if (job.Kind == "audio" && job.PreferOriginal)
        {
            found = await TryMusicAsync(job.Target).ConfigureAwait(false);
            if (found != null) job.MatchedBy = "youtube_music";
        }
        var errors = new StringBuilder();
        if (found == null)
        {
            found = (await SearchYouTubeAsync(job.Target, 3, errors).ConfigureAwait(false)).FirstOrDefault();
            job.MatchedBy = "youtube";
        }
        if (found == null)
        {
            // Some exact queries come back empty from ordinary search only.
            found = await TryMusicAsync(job.Target).ConfigureAwait(false);
            job.MatchedBy = "youtube_music";
        }
        if (found == null)
        {
            if (errors.Length > 0) Fail(job, errors.ToString());
            else { job.Error = "Nothing found for this name."; job.Status = "failed"; }
            return;
        }
        var view = Describe(found);
        job.VideoId = view.Str("id");
        job.Url = view.Str("url");
        job.Title = view.Str("title");
        job.IsOriginal = view.Flag("is_original_track");
        job.Artist = view.Str("channel");
    }

    static async Task<Dictionary<string, object?>?> TryMusicAsync(string query)
    {
        try { return MusicSearch.BestMatch(query, await SearchMusicAsync(query, 8).ConfigureAwait(false)); }
        catch (HttpRequestException) { return null; }
        catch (TaskCanceledException) { return null; }
    }

    static IEnumerable<string> FilesOf(AgentJob job)
    {
        string folder = Folder();
        if (job.VideoId == null || !Directory.Exists(folder)) return Array.Empty<string>();
        return Directory.EnumerateFiles(folder).Where(f => Engine.IdInName().Match(Path.GetFileName(f)) is { Success: true } m && m.Groups[1].Value == job.VideoId && new FileInfo(f).Length > 0);
    }

    static string? FindExisting(AgentJob job) => Engine.MatchExisting(FilesOf(job), job.Kind == "audio", job.AudioFormat, job.Container);

    static void OnDownloadLine(AgentJob job, string line)
    {
        if (line.StartsWith("__PROGRESS__", StringComparison.Ordinal))
        {
            string[] f = line[12..].Split('|');
            double done = Parse(f, 1), total = Parse(f, 2);
            if (total <= 0) total = Parse(f, 3);
            if (total > 0) job.Percent = Math.Round(Math.Min(100, done * 100 / total), 1);
            job.Speed = Parse(f, 4);
            job.Eta = Parse(f, 5);
            job.Status = f.Length > 0 && f[0] == "finished" ? "processing" : "downloading";
        }
        else if (line.StartsWith("__PP__", StringComparison.Ordinal)) job.Status = "processing";
        else if (line.StartsWith("__FILE__", StringComparison.Ordinal)) job.File = line[8..].Trim();
        else if (line.StartsWith("ERROR", StringComparison.Ordinal)) lock (job.Errors) job.Errors.AppendLine(line);
    }

    static double Parse(string[] fields, int index) =>
        index < fields.Length && double.TryParse(fields[index], NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : -1;

    static Dictionary<string, object?> JobView(AgentJob job)
    {
        var view = Json.Map(("job_id", job.Id), ("status", job.Status), ("target", job.Target), ("kind", job.Kind));
        if (job.Title != null) view["title"] = job.Title;
        if (job.Artist != null) view["channel"] = job.Artist;
        if (job.Url != null) { view["url"] = job.Url; view["is_original_track"] = job.IsOriginal; }
        if (job.MatchedBy != null) view["matched_by"] = job.MatchedBy;
        if (job.Percent >= 0) view["percent"] = job.Percent;
        if (job.Speed > 0 && !job.IsDone) view["speed_bytes_per_second"] = Math.Round(job.Speed);
        if (job.Eta > 0 && !job.IsDone) view["eta_seconds"] = job.Eta;
        if (job.File != null)
        {
            view["file"] = job.File;
            if (File.Exists(job.File)) view["size_bytes"] = new FileInfo(job.File).Length;
        }
        if (job.AlreadyExisted) view["already_existed"] = true;
        if (job.Error != null) view["error"] = job.Error;
        if (job.Details != null) view["details"] = job.Details;
        if (job.Status == "failed" && Engine.NeedsLogin(job.Details) && !Auth.SignedIn) view["needs_human"] = NeedsHuman;
        return view;
    }

    public static async Task<ToolResult> StatusAsync(Dictionary<string, object?> args)
    {
        string? id = Text(args, "job_id");
        AgentJob? job = null;
        if (id != null && !Jobs.TryGetValue(id, out job)) return ToolResult.Error("No such job. Jobs live only as long as this server runs; use list_downloads for finished files.");
        var selected = job != null ? new List<AgentJob> { job } : Jobs.Values.OrderBy(j => j.Id.Length).ThenBy(j => j.Id, StringComparer.Ordinal).ToList();
        await WaitAsync(selected, Number(args, "wait_seconds", 0)).ConfigureAwait(false);
        if (job != null) return new ToolResult { Data = JobView(job) };
        if (selected.Count > 30) return new ToolResult { Data = Summary(selected) };
        return new ToolResult { Data = Json.Map(("jobs", selected.Select(JobView).ToList())) };
    }

    public static Task<ToolResult> CancelAsync(Dictionary<string, object?> args)
    {
        string? id = Text(args, "job_id");
        if (id == null || !Jobs.TryGetValue(id, out AgentJob? job)) return Task.FromResult(ToolResult.Error("No such job"));
        job.Cancelled = true;
        job.Runner?.Cancel();
        return Task.FromResult(new ToolResult { Data = Json.Map(("job_id", id), ("cancel_requested", true)) });
    }

    public static void CancelEverything()
    {
        foreach (var job in Jobs.Values) { job.Cancelled = true; job.Runner?.Cancel(); }
    }

    public static Task<ToolResult> ListDownloadsAsync(Dictionary<string, object?> args)
    {
        string? filter = Text(args, "filter");
        int limit = (int)Math.Clamp(Number(args, "limit", 50), 1, 500);
        string folder = Folder();
        var files = !Directory.Exists(folder) ? new List<FileInfo>() : new DirectoryInfo(folder).EnumerateFiles()
            .Where(IsDownloadedMedia).Where(f => filter == null || f.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => f.LastWriteTime).Take(limit).ToList();
        return Task.FromResult(new ToolResult
        {
            Data = Json.Map(("folder", folder), ("files", files.Select(f => Json.Map(("name", f.Name), ("path", f.FullName), ("size_bytes", f.Length),
                ("modified", f.LastWriteTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)))).ToList()))
        });
    }

    // Only files this app saved ("Title [id].ext" with a media extension); the folder may be the whole Desktop.
    static bool IsDownloadedMedia(FileInfo file)
    {
        Match match = Engine.IdInName().Match(file.Name);
        return match.Success && file.Length > 0 && Engine.MediaExtensions.Contains(match.Groups[2].Value.ToLowerInvariant());
    }

    // ---------- Tools: analysis ----------

    // A model can name only a downloaded file in the downloads folder, by name or by full path.
    public static FileInfo? ResolveMedia(string? path, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(path)) { problem = "path is required: a file from list_downloads"; return null; }
        string folder = Folder();
        FileInfo file;
        try { file = new FileInfo(Path.IsPathRooted(path) ? path : Path.Combine(folder, path)); }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException) { problem = "Invalid path"; return null; }
        StringComparison comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        // Trimming a separator keeps the root's own one, so a folder like D:\ works too.
        if (!string.Equals(Path.TrimEndingDirectorySeparator(file.DirectoryName ?? ""), Path.TrimEndingDirectorySeparator(folder), comparison) || !IsDownloadedMedia(file) || file.LinkTarget != null)
        {
            problem = "Only files listed by list_downloads can be opened.";
            return null;
        }
        if (!file.Exists) { problem = "File not found: " + file.Name; return null; }
        return file;
    }

    static async Task<List<string>> RunToolAsync(string? exe, IEnumerable<string> args)
    {
        if (exe == null) throw new FileNotFoundException("FFmpeg is not installed yet. Open Volna once so it can set up its tools.");
        var lines = new List<string>();
        using var runner = new ProcessRunner();
        await runner.RunAsync(exe, args, line => { lock (lines) lines.Add(line); }).ConfigureAwait(false);
        return lines;
    }

    static async Task<Dictionary<string, object?>?> ProbeAsync(string path)
    {
        // Quiet, because any message on stderr would end up inside the JSON read from the same stream.
        var lines = await RunToolAsync(ToolManager.Ffprobe, new[] { "-v", "quiet", "-print_format", "json=compact=1", "-show_format", "-show_streams", path }).ConfigureAwait(false);
        return Json.ParseObject(string.Concat(lines));
    }

    static double ProbeDuration(Dictionary<string, object?> probe) =>
        double.TryParse((probe.Raw("format") as Dictionary<string, object?>).Str("duration"), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : 0;

    static IEnumerable<Dictionary<string, object?>> Streams(Dictionary<string, object?> probe) => (probe.Raw("streams") as List<object?> ?? new()).OfType<Dictionary<string, object?>>();

    static bool IsCover(Dictionary<string, object?> stream) => (stream.Raw("disposition") as Dictionary<string, object?>).Num("attached_pic") == 1;

    static readonly string[] StreamDetails = { "width", "height", "sample_rate", "channels", "bit_rate" };

    public static async Task<ToolResult> MediaInfoAsync(Dictionary<string, object?> args)
    {
        FileInfo? file = ResolveMedia(Text(args, "path"), out string? problem);
        if (file == null) return ToolResult.Error(problem!);
        var probe = await ProbeAsync(file.FullName).ConfigureAwait(false);
        if (probe == null) return ToolResult.Error("Could not read this file.");
        var format = probe.Raw("format") as Dictionary<string, object?>;
        var streams = Streams(probe).Select(s =>
        {
            var view = Json.Map(("type", s.Str("codec_type")), ("codec", s.Str("codec_name")));
            foreach (string name in StreamDetails) if (s.Raw(name) != null) view[name] = s.Raw(name);
            if (IsCover(s)) view["is_cover_art"] = true;
            return view;
        }).ToList();
        return new ToolResult { Data = Json.Map(("name", file.Name), ("size_bytes", file.Length), ("duration_seconds", ProbeDuration(probe)), ("bit_rate", format.Str("bit_rate")), ("tags", format.Raw("tags")), ("streams", streams)) };
    }

    public static async Task<ToolResult> ExtractFramesAsync(Dictionary<string, object?> args)
    {
        FileInfo? file = ResolveMedia(Text(args, "path"), out string? problem);
        if (file == null) return ToolResult.Error(problem!);
        var probe = await ProbeAsync(file.FullName).ConfigureAwait(false);
        if (probe == null) return ToolResult.Error("Could not read this file.");
        if (!Streams(probe).Any(s => s.Str("codec_type") == "video" && !IsCover(s))) return ToolResult.Error("This file has no video track (audio with at most a cover picture).");
        double duration = ProbeDuration(probe);
        int width = (int)Math.Clamp(Number(args, "width", 768), 160, 1920);
        var times = new List<double>();
        IEnumerable<object?> given = args.Raw("timestamps_seconds") switch
        {
            string text => text.Split(','),
            List<object?> list => list,
            _ => Array.Empty<object?>()
        };
        foreach (object? value in given)
            if (double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out double time) && time >= 0) times.Add(time);
        if (times.Count == 0)
        {
            int count = (int)Math.Clamp(Number(args, "count", 6), 1, 12);
            for (int n = 0; n < count; n++) times.Add(Math.Round(duration * (n + 0.5) / count, 2));
        }
        times = times.Take(12).ToList();

        string folder = Path.Combine(AppPaths.Cache, "agent-output", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var result = new ToolResult();
        var frames = new List<object?>();
        for (int n = 0; n < times.Count; n++)
        {
            string output = Path.Combine(folder, "frame" + (n + 1).ToString("00", CultureInfo.InvariantCulture) + ".jpg");
            await RunToolAsync(ToolManager.Ffmpeg, new[] { "-nostdin", "-v", "error", "-ss", times[n].ToString("0.###", CultureInfo.InvariantCulture), "-i", file.FullName,
                "-frames:v", "1", "-vf", "scale=" + width.ToString(CultureInfo.InvariantCulture) + ":-2", "-q:v", "5", "-y", output }).ConfigureAwait(false);
            if (!File.Exists(output)) continue;
            frames.Add(Json.Map(("timestamp_seconds", times[n]), ("path", output)));
            result.Images.Add(("image/jpeg", Convert.ToBase64String(File.ReadAllBytes(output))));
        }
        if (frames.Count == 0) return ToolResult.Error("No frames could be extracted at these timestamps.");
        result.Data = Json.Map(("duration_seconds", duration), ("frames", frames));
        return result;
    }

    public static async Task<ToolResult> ExtractAudioClipAsync(Dictionary<string, object?> args)
    {
        FileInfo? file = ResolveMedia(Text(args, "path"), out string? problem);
        if (file == null) return ToolResult.Error(problem!);
        double start = Math.Max(0, Number(args, "start_seconds", 0)), length = Math.Clamp(Number(args, "duration_seconds", 60), 1, 1800);
        string folder = Path.Combine(AppPaths.Cache, "agent-output", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string output = Path.Combine(folder, "clip.mp3");
        // Mono at 16 kHz: what speech models expect, and small enough to hand over.
        await RunToolAsync(ToolManager.Ffmpeg, new[] { "-nostdin", "-v", "error", "-ss", start.ToString("0.###", CultureInfo.InvariantCulture), "-t", length.ToString("0.###", CultureInfo.InvariantCulture),
            "-i", file.FullName, "-vn", "-ac", "1", "-ar", "16000", "-b:a", "48k", "-y", output }).ConfigureAwait(false);
        if (!File.Exists(output) || new FileInfo(output).Length == 0) return ToolResult.Error("The clip could not be made (is the start past the end of the file?).");
        long size = new FileInfo(output).Length;
        var result = new ToolResult { Data = Json.Map(("path", output), ("size_bytes", size), ("start_seconds", start), ("duration_seconds", length)) };
        if (Flag(args, "inline", false) && size < 10 * 1024 * 1024) result.Audio.Add(("audio/mpeg", Convert.ToBase64String(File.ReadAllBytes(output))));
        return result;
    }

    public static Task<ToolResult> AccountStatusAsync(Dictionary<string, object?> args)
    {
        var data = Json.Map(("signed_in", Auth.SignedIn), ("downloads_folder", Folder()), ("tools_missing", ToolManager.Unavailable().ToList()));
        if (!Auth.SignedIn) data["note"] = "Age-restricted and private videos may fail. " + NeedsHuman;
        return Task.FromResult(new ToolResult { Data = data });
    }

    // Output of analysis calls is kept for a day so its files can be opened, then removed.
    public static void CleanOldOutput()
    {
        foreach (string name in new[] { "agent-output", "agent-subs" })
        {
            try
            {
                var folder = new DirectoryInfo(Path.Combine(AppPaths.Cache, name));
                if (!folder.Exists) continue;
                foreach (var old in folder.GetDirectories().Where(d => d.CreationTime < DateTime.Now.AddDays(-1))) old.Delete(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
