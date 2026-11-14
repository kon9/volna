using System.Text;
using System.Text.RegularExpressions;

namespace Volna.Core;

public sealed record VideoChapter(double Start, double End, string Title);
public sealed record CaptionTrack(string Language, string Name, bool Automatic);
public sealed record SubtitleCue(double Start, double End, string Text);

public sealed record ClipMetadataInfo(string? Language, double? Duration, IReadOnlyList<VideoChapter> Chapters, IReadOnlyList<CaptionTrack> Captions)
{
    /// <summary>Prefer an author's captions in the video's language, then the user's language, then original automatic captions.</summary>
    public CaptionTrack? PreferredCaption(string? preferredLanguage = null)
    {
        static CaptionTrack? Matching(IEnumerable<CaptionTrack> tracks, string? language) => language == null ? null
            : tracks.FirstOrDefault(t => string.Equals(t.Language, language, StringComparison.OrdinalIgnoreCase))
                ?? tracks.FirstOrDefault(t => ClipMetadata.SameLanguage(t.Language, language));
        var manual = Captions.Where(t => !t.Automatic).ToList();
        return Matching(manual, Language)
            ?? Matching(manual, preferredLanguage)
            ?? manual.FirstOrDefault()
            ?? Matching(Captions, Language)
            ?? (Captions.Count > 0 ? Captions[0] : null);
    }
}

/// <summary>Chapters and timed captions for finding a part of a video, without downloading the video itself.</summary>
public static class ClipMetadata
{
    // Extractors use both base codes and regional forms (en/en-US, pt_BR/pt-BR) for the same spoken language.
    internal static bool SameLanguage(string code, string language)
    {
        static string Base(string value) => value.Trim().Split(['-', '_'], 2)[0];
        return string.Equals(Base(code), Base(language), StringComparison.OrdinalIgnoreCase);
    }

    public static List<string> MetadataArgs(string url, IEnumerable<string> auth, string proxy)
    {
        var args = Engine.CommonArgs(auth, proxy);
        args.AddRange(["--skip-download", "--no-playlist", "--playlist-items", "1", "--print",
            "%(.{duration,language,chapters,subtitles,automatic_captions})j", "--", url]);
        return args;
    }

    public static List<string> TranscriptArgs(string url, CaptionTrack track, string folder, IEnumerable<string> auth, string proxy)
    {
        var args = Engine.CommonArgs(auth, proxy);
        args.AddRange(["--skip-download", "--no-playlist", "--playlist-items", "1",
            track.Automatic ? "--write-auto-subs" : "--write-subs",
            // A language key comes from the site. Match it literally, rather than treating it as a regex or "all".
            "--sub-langs", "^" + Regex.Escape(track.Language) + "$", "--sub-format", "vtt/best", "--convert-subs", "vtt",
            "--paths", folder, "--output", "subtitle.%(ext)s", "--", url]);
        return args;
    }

    public static async Task<ClipMetadataInfo> LoadAsync(string url, string proxy, ProcessRunner runner)
    {
        var lease = Auth.Lease();
        RunResult? result = null;
        Dictionary<string, object?>? info = null;
        var errors = new StringBuilder();
        try
        {
            result = await runner.RunAsync(ToolManager.YtDlp, MetadataArgs(url, lease.Args, proxy), line =>
            {
                if (line.StartsWith('{') && Json.ParseObject(line) is { } parsed) info = parsed;
                else RememberError(errors, line);
            }).ConfigureAwait(false);
            CheckResult(result, errors);
            if (info == null) throw new IOException("The site did not return video details.");
            return Parse(info);
        }
        finally { lease.Complete(result is { ExitCode: 0, Cancelled: false }); }
    }

    public static async Task<IReadOnlyList<SubtitleCue>> TranscriptAsync(string url, CaptionTrack track, string proxy, ProcessRunner runner)
    {
        string folder = Path.Combine(AppPaths.Cache, "clip-captions", Guid.NewGuid().ToString("N"));
        var lease = Auth.Lease();
        RunResult? result = null;
        var errors = new StringBuilder();
        try
        {
            Directory.CreateDirectory(folder);
            result = await runner.RunAsync(ToolManager.YtDlp, TranscriptArgs(url, track, folder, lease.Args, proxy), line => RememberError(errors, line)).ConfigureAwait(false);
            CheckResult(result, errors);
            string? file = Directory.EnumerateFiles(folder, "*.vtt").FirstOrDefault();
            if (file == null) throw new IOException("The site did not return captions.");
            if (new FileInfo(file).Length > 16_000_000) throw new IOException("The caption file is too large.");
            return Subtitles.ParseVtt(await File.ReadAllTextAsync(file).ConfigureAwait(false), track.Automatic);
        }
        finally
        {
            lease.Complete(result is { ExitCode: 0, Cancelled: false });
            try { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    static void RememberError(StringBuilder errors, string line)
    {
        if (!line.StartsWith("ERROR", StringComparison.Ordinal)) return;
        lock (errors) { if (errors.Length < 4000) errors.AppendLine(line[..Math.Min(line.Length, 4000 - errors.Length)]); }
    }

    static void CheckResult(RunResult result, StringBuilder errors)
    {
        if (result.Cancelled) throw new OperationCanceledException();
        if (result.ExitCode != 0) throw new IOException(errors.Length > 0 ? errors.ToString().Trim() : "Could not load video details.");
    }

    public static ClipMetadataInfo Parse(Dictionary<string, object?> info)
    {
        double? duration = info.Num("duration");
        if (duration is not { } length || !double.IsFinite(length) || length <= 0) duration = null;
        string? language = info.Str("language");
        var chapters = new List<VideoChapter>();
        var raw = (info.Raw("chapters") as IEnumerable<object?> ?? []).OfType<Dictionary<string, object?>>()
            .Where(c => c.Num("start_time") is { } start && double.IsFinite(start) && start >= 0 && (duration == null || start < duration))
            .OrderBy(c => c.Num("start_time")).ToList();
        for (int i = 0; i < raw.Count; i++)
        {
            double start = raw[i].Num("start_time")!.Value;
            double? end = raw[i].Num("end_time") ?? (i + 1 < raw.Count ? raw[i + 1].Num("start_time") : duration);
            if (end is not { } stop || !double.IsFinite(stop) || stop <= start) continue;
            if (duration != null) stop = Math.Min(stop, duration.Value);
            chapters.Add(new VideoChapter(start, stop, raw[i].Str("title") ?? Format.Duration(start)));
        }

        var tracks = new List<CaptionTrack>();
        AddTracks(info.Raw("subtitles"), false, null);
        var automatic = info.Raw("automatic_captions") as Dictionary<string, object?>;
        // YouTube exposes translated automatic captions as hundreds of languages. Only its original track belongs here.
        var originals = automatic?.Keys.Where(k => k.EndsWith("-orig", StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.Ordinal);
        var spoken = language != null ? automatic?.Keys.Where(k => SameLanguage(k, language)).ToHashSet(StringComparer.Ordinal) : null;
        AddTracks(automatic, true, originals is { Count: > 0 } ? originals : spoken);
        return new ClipMetadataInfo(language, duration, chapters, tracks);

        void AddTracks(object? value, bool auto, HashSet<string>? allowed)
        {
            if (value is not Dictionary<string, object?> map) return;
            foreach (var (code, formats) in map)
            {
                if (code == "live_chat" || string.IsNullOrWhiteSpace(code) || (allowed != null && !allowed.Contains(code))) continue;
                if (formats is not IEnumerable<object?> entries) continue;
                var list = entries.OfType<Dictionary<string, object?>>().ToList();
                if (list.Count == 0) continue;
                string name = list.Select(f => f.Str("name")).FirstOrDefault(n => n != null) ?? code;
                tracks.Add(new CaptionTrack(code, name, auto));
            }
        }
    }
}
