using System.Globalization;
using System.Text.RegularExpressions;

namespace Volna.Core;

public sealed class DownloadOptions
{
    public string Url { get; set; } = "";
    public string Folder { get; set; } = "";
    public bool Audio { get; set; }
    public int Height { get; set; }
    public string AudioFormat { get; set; } = "mp3";
    public string Container { get; set; } = "mp4";
    public bool EmbedThumbnail { get; set; } = true;
    public bool Subtitles { get; set; }
    public bool SponsorBlock { get; set; }
    public bool Original { get; set; }      // an art track: its file is named "Artist - Track"
    public bool CutOfftopic { get; set; }   // audio from a music video: cut the parts that are not music
    public bool KeepSource { get; set; }    // another file of this video exists, and extracting audio must not delete it
    public string Proxy { get; set; } = "";
    public bool Overwrite { get; set; } // replace a file that is already there
    public string? Section { get; set; } // only this part of the video, in seconds: "90-120"
    public bool CleanNames { get; set; } // no id in the file's name
}

public static partial class Engine
{
    public const string SearchPrefix = "ytsearch1:";
    public const int PlaylistLimit = 5000;
    static readonly char[] LineBreaks = { '\r', '\n' };

    [GeneratedRegex(@"https?://[^\s""'<>]+", RegexOptions.IgnoreCase)] private static partial Regex LinkPattern();
    // "1. ", "02) ", "- ", "• ", "#3 " in front of a song name.
    // Not a digit after the dot: "1.5 Degrees" is a song, not the fifth item of something.
    [GeneratedRegex(@"^\s*(?:\d{1,3}\s*\)\s*|\d{1,3}\s*\.(?!\d)\s*|#\d{1,3}\s+|[-–—•*·▪►]\s+)")] private static partial Regex ListMarker();
    // People paste addresses without the https:// in front.
    [GeneratedRegex(@"^(?:www\.|m\.)?(?:youtube\.com|youtu\.be|music\.youtube\.com)/\S+$", RegexOptions.IgnoreCase)] private static partial Regex BareLink();

    public static List<string> ExtractUrls(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text)) return result;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in LinkPattern().Matches(text))
        {
            string url = match.Value.TrimEnd('.', ',', ';', ')', ']', '}', '!', '?', '»');
            if (url.Length > 8192 || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo)) continue;
            if (seen.Add(url)) result.Add(url);
        }
        return result;
    }

    /// <summary>Every line becomes either the links it contains or a YouTube search for its text.</summary>
    public static List<string> ParseEntries(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(text)) return result;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string raw in text.Split(LineBreaks, StringSplitOptions.RemoveEmptyEntries))
        {
            List<string> urls = ExtractUrls(BareLink().IsMatch(raw.Trim()) ? "https://" + raw.Trim() : raw);
            if (urls.Count > 0)
            {
                foreach (string url in urls) if (seen.Add(url)) result.Add(url);
                continue;
            }
            string query = ListMarker().Replace(raw, "").Trim().Trim('"', '«', '»', '“', '”').Trim();
            // Invisible characters come along from web pages. A single 雨, X or 🔥 can be a whole title.
            query = new string(query.Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) != UnicodeCategory.Format).ToArray()).Trim();
            if (query.Length == 0 || !query.Any(c => char.IsLetterOrDigit(c) || char.IsSurrogate(c))) continue;
            string entry = SearchEntry(query);
            if (seen.Add(entry)) result.Add(entry);
        }
        return result;
    }

    /// <summary>Services whose links lead to no file: their music can only come in as a list of names.</summary>
    public static bool IsClosedMusicService(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return false;
        string host = uri.Host.ToLowerInvariant(), path = uri.AbsolutePath.ToLowerInvariant();
        return host is "open.spotify.com" or "spotify.link" or "music.apple.com" or "www.deezer.com" or "tidal.com" or "listen.tidal.com"
            || (host is "vk.com" or "m.vk.com" or "vk.ru" && (path.StartsWith("/music", StringComparison.Ordinal) || path.StartsWith("/audio", StringComparison.Ordinal)));
    }

    public static bool IsSearch(string entry) => entry.StartsWith(SearchPrefix, StringComparison.Ordinal);

    public static string SearchEntry(string query) => SearchPrefix + (query.Length > 200 ? query[..200] : query);

    /// <summary>"90", "1:30" or "1:02:03" in seconds; null when it is none of these.</summary>
    public static double? ParseTime(string? text)
    {
        double total = 0;
        string[] parts = (text ?? "").Trim().Split(':');
        if (parts.Length > 3) return null;
        foreach (string part in parts)
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) || value < 0) return null;
            total = total * 60 + value;
        }
        return total;
    }

    static readonly string[] ReservedNames = { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

    /// <summary>A name that is safe for a file on every system.</summary>
    public static string SafeFileName(string name)
    {
        string clean = new string(name.Select(c => char.IsControl(c) || "\\/:*?\"<>|".Contains(c, StringComparison.Ordinal) ? ' ' : c).ToArray()).Trim().TrimEnd('.');
        if (clean.Length > 100) clean = clean[..100].Trim();
        if (ReservedNames.Contains(clean, StringComparer.OrdinalIgnoreCase)) clean += " playlist";
        return clean.Length == 0 ? "playlist" : clean;
    }

    /// <summary>A name reduced to its words, so "Artist - Song" is recognized however it is punctuated.</summary>
    public static string NameKey(string name) => string.Join(' ', MusicSearch.Words(name));

    /// <summary>The same for a file, without its extension and the id in its name.</summary>
    /// <remarks>An id has no spaces: "[Live 1977]" is part of the name, and a clip's "[id clip 1-2]" is not the whole song.</remarks>
    public static string FileNameKey(string file)
    {
        Match id = IdInName().Match(file);
        return NameKey(Path.GetFileNameWithoutExtension(id.Success && !id.Groups[1].Value.Contains(' ', StringComparison.Ordinal) ? IdInName().Replace(file, ".") : file));
    }

    /// <summary>
    /// For the clipboard watcher: only links that are very likely one video or track. Any link typed or pasted into the
    /// app works on any site yt-dlp supports; this is only about what gets picked up by itself, so copying an ordinary
    /// page, a profile or a whole channel never adds anything.
    /// </summary>
    public static bool LooksLikeMedia(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https")) return false;
        string host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.", StringComparison.Ordinal)) host = host[4..];
        else if (host.StartsWith("m.", StringComparison.Ordinal)) host = host[2..];
        string path = uri.AbsolutePath.Trim('/').ToLowerInvariant();
        if (path.Length == 0) return false;
        string[] parts = path.Split('/');
        bool Is(params string[] names) => names.Any(n => host == n || host.EndsWith("." + n, StringComparison.Ordinal));
        bool Starts(params string[] prefixes) => prefixes.Any(p => path.StartsWith(p, StringComparison.Ordinal));
        bool Has(params string[] words) => words.Any(w => path.Contains(w, StringComparison.Ordinal));
        if (Is("youtube.com", "youtube-nocookie.com")) return Starts("watch", "shorts/", "playlist", "live/", "embed/");
        if (Is("youtu.be")) return true;
        if (Is("tiktok.com")) return Has("/video/") || Is("vm.tiktok.com", "vt.tiktok.com");
        if (Is("instagram.com")) return Has("reel/", "reels/", "p/", "tv/");
        if (Is("x.com", "twitter.com")) return Has("/status/");
        if (Is("reddit.com")) return Has("/comments/");
        if (Is("facebook.com", "fb.watch")) return Has("watch", "/videos/", "reel/", "share/v/") || Is("fb.watch");
        if (Is("twitch.tv")) return Has("videos/", "/clip/") || Is("clips.twitch.tv");
        if (Is("vimeo.com")) return parts[^1].All(char.IsDigit) || parts.Length >= 2 && parts[^2].All(char.IsDigit);
        if (Is("soundcloud.com", "mixcloud.com")) return parts.Length >= 2; // artist/track, not only an artist page
        if (Is("bandcamp.com")) return Starts("track/", "album/");
        if (Is("dailymotion.com")) return Starts("video/");
        if (Is("rutube.ru")) return Starts("video/", "shorts/");
        if (Is("vk.com", "vkvideo.ru", "ok.ru")) return Has("video", "clip");
        if (Is("bilibili.com")) return Starts("video/");
        if (Is("nicovideo.jp")) return Starts("watch/");
        if (Is("kick.com")) return Has("videos/", "clips/", "clip_");
        if (Is("coub.com")) return Starts("view/");
        if (Is("dzen.ru")) return Starts("video/", "shorts/");
        if (Is("ted.com")) return Starts("talks/");
        if (Is("pinterest.com")) return Starts("pin/");
        if (Is("streamable.com")) return parts.Length == 1;
        return false;
    }

    public static List<string> CommonArgs(IEnumerable<string> auth, string proxy)
    {
        var args = new List<string> { "--ignore-config", "--no-plugin-dirs", "--no-remote-components",
            "--cache-dir", Path.Combine(AppPaths.Cache, "yt-dlp"), "--encoding", "utf-8", "--no-colors", "--socket-timeout", "30" };
        if (ToolManager.Deno is { } deno) args.AddRange(new[] { "--no-js-runtimes", "--js-runtimes", "deno:" + deno });
        if (ToolManager.FfmpegFolder is { } ffmpeg) args.AddRange(new[] { "--ffmpeg-location", ffmpeg });
        args.AddRange(auth);
        if (!string.IsNullOrWhiteSpace(proxy)) args.AddRange(new[] { "--proxy", proxy });
        return args;
    }

    public static List<string> MetadataArgs(IEnumerable<string> entries, IEnumerable<string> auth, string proxy, int limit = PlaylistLimit)
    {
        var args = CommonArgs(auth, proxy);
        // A short pause between requests inside one lookup keeps a long list from looking like a flood.
        args.AddRange(["--sleep-requests", "0.75", "--ignore-errors"]);
        // Lazily, so that a whole channel shows how far it has got instead of nothing for a minute.
        args.AddRange(new[] { "--flat-playlist", "--lazy-playlist", "--skip-download", "--no-playlist",
            "--playlist-items", "1:" + limit.ToString(CultureInfo.InvariantCulture),
            "--print", "%(.{id,title,uploader,channel,duration,thumbnail,thumbnails,view_count,webpage_url,url,ie_key,extractor_key,height,playlist_title,live_status,track,artist,creator,album})j",
            "--" });
        args.AddRange(entries);
        return args;
    }

    public const string ProgressTemplate = "download:__PROGRESS__%(progress.status)s|%(progress.downloaded_bytes)s|%(progress.total_bytes)s|%(progress.total_bytes_estimate)s|%(progress.speed)s|%(progress.eta)s|%(info.vcodec)s|%(info.acodec)s";

    // A part of a video has its own name, so it is never mistaken for the whole file.
    static string OutputName(DownloadOptions o)
    {
        string id = o.Section != null ? " [%(id)s clip " + o.Section + "]" : o.CleanNames ? "" : " [%(id)s]";
        return (o.Original ? "%(artist,creator,uploader).60B - %(track,title).90B" : "%(title).110B") + id + ".%(ext)s";
    }

    public static List<string> DownloadArgs(DownloadOptions o, IEnumerable<string> auth)
    {
        var args = CommonArgs(auth, o.Proxy);
        args.AddRange(new[] { "--newline", "--progress", "--progress-delta", "0.4",
            "--no-simulate", "--windows-filenames", "--trim-filenames", "160", "--no-overwrites", "--continue",
            "--retries", "5", "--fragment-retries", "5", "--concurrent-fragments", "4",
            "--abort-on-unavailable-fragments", "--no-playlist", "--playlist-items", "1",
            // --print implies quiet; the ordinary output is what the per-card log and "already downloaded" rely on.
            "--no-quiet",
            "--paths", o.Folder, "--output", OutputName(o),
            "--print", "after_move:__FILE__%(filepath)s",
            "--progress-template", ProgressTemplate,
            "--progress-template", "postprocess:__PP__%(progress.postprocessor)s|%(progress.status)s",
            "--embed-metadata" });
        if (o.Audio)
        {
            args.AddRange(new[] { "--format", o.AudioFormat == "m4a" ? "ba[ext=m4a]/ba/b" : "ba/b",
                "--extract-audio", "--audio-format", o.AudioFormat, "--audio-quality", "0" });
        }
        else
        {
            // <=? also accepts formats of unknown height, which sites with little metadata have.
            string limit = o.Height > 0 ? "[height<=?" + o.Height.ToString(CultureInfo.InvariantCulture) + "]" : "";
            // lang first: among equal videos the original soundtrack wins, however good a dubbed one's codec is.
            args.AddRange(new[] { "--format", "bv*" + limit + "+ba/b" + limit,
                "--format-sort", "lang,res,vcodec:h264,acodec:aac", "--merge-output-format", o.Container, "--remux-video", o.Container });
            // --ignore-errors: subtitles refused by the site must not cost the video itself.
            if (o.Subtitles) args.AddRange(new[] { "--write-subs", "--write-auto-subs", "--sub-langs", SubtitleLanguages(Loc.Language), "--embed-subs", "--ignore-errors" });
        }
        if (o.EmbedThumbnail) args.AddRange(["--embed-thumbnail", "--convert-thumbnails", "jpg"]);
        var cuts = new List<string>();
        if (o.SponsorBlock) cuts.Add("sponsor,selfpromo,interaction");
        if (o.CutOfftopic) cuts.Add("music_offtopic");
        if (cuts.Count > 0) args.AddRange(new[] { "--sponsorblock-remove", string.Join(",", cuts) });
        if (o.KeepSource) args.Add("--keep-video");
        if (o.Overwrite) args.Add("--force-overwrites");
        if (o.Section != null) args.AddRange(["--download-sections", "*" + o.Section, "--force-keyframes-at-cuts"]);
        args.Add("--");
        args.Add(o.Url);
        return args;
    }

    /// <summary>Subtitles in the language of the interface, and in English. "-orig" is a second copy of automatic ones.</summary>
    public static string SubtitleLanguages(string language) => (language == "en" ? "en.*" : language + ".*,en.*") + ",-.*-orig";

    /// <summary>The playlist a link to one of its videos was opened from. YouTube's endless mixes do not count.</summary>
    public static string? PlaylistOf(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || !uri.Host.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase) || !uri.AbsolutePath.StartsWith("/watch", StringComparison.OrdinalIgnoreCase)) return null;
        string? list = uri.Query.TrimStart('?').Split('&').FirstOrDefault(p => p.StartsWith("list=", StringComparison.Ordinal))?[5..];
        return string.IsNullOrEmpty(list) || list.StartsWith("RD", StringComparison.Ordinal) ? null : "https://www.youtube.com/playlist?list=" + list;
    }

    /// <summary>Failures that a newer yt-dlp usually fixes: YouTube changes faster than releases of this app.</summary>
    public static bool LooksOutdated(string? errors)
    {
        string lower = (errors ?? "").ToLowerInvariant();
        if (lower.Contains("country") || lower.Contains("geo") || lower.Contains("region")) return false; // no update changes where someone is
        return lower.Contains("http error 403") || lower.Contains("signature") || lower.Contains("nsig") || lower.Contains("challenge")
            || lower.Contains("unable to extract") || lower.Contains("precondition check failed") || lower.Contains("javascript runtime");
    }

    /// <summary>The chosen browser cannot be read: gone, locked or encrypted. That must not cost links that need no sign-in.</summary>
    public static bool CookiesUnreadable(string? errors)
    {
        string lower = (errors ?? "").ToLowerInvariant();
        return lower.Contains("cookies database") || (lower.Contains("could not copy") && lower.Contains("cookie")) || lower.Contains("failed to decrypt") || lower.Contains("dpapi")
            || (lower.Contains("could not find") && lower.Contains("cookie"));
    }

    /// <summary>
    /// A part of a video comes through ffmpeg, which YouTube now and then turns away with a 403 that only it reports.
    /// The next try usually goes through.
    /// </summary>
    public static bool PartRefused(DownloadOptions o, string errors) => o.Section != null && errors.Contains("ffmpeg exited", StringComparison.OrdinalIgnoreCase);

    /// <summary>Some formats cannot hold a cover. The file is still wanted without one.</summary>
    public static bool CoverDidNotFit(DownloadOptions o, string errors) =>
        o.EmbedThumbnail && errors.Contains("thumbnail", StringComparison.OrdinalIgnoreCase) && errors.Contains("supported", StringComparison.OrdinalIgnoreCase);

    public static bool DiskFull(string? errors)
    {
        string lower = (errors ?? "").ToLowerInvariant();
        return lower.Contains("no space left") || lower.Contains("not enough space") || lower.Contains("errno 28") || lower.Contains("winerror 112") || lower.Contains("недостаточно места");
    }

    /// <summary>The file arrived but does not fit the chosen container: other sites serve formats mp4 cannot hold.</summary>
    public static bool DidNotFit(DownloadOptions o, string errors) =>
        !o.Audio && o.Container != "mkv" && errors.Contains("conversion failed", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Keeps the video as mkv, which holds nearly anything. What was downloaded stays on disk, so nothing is fetched twice;
    /// only the empty file the failed conversion left behind goes, because it would look like a finished download.
    /// </summary>
    public static void FallBackToMkv(DownloadOptions o)
    {
        string extension = "." + o.Container;
        o.Container = "mkv";
        try
        {
            foreach (var file in new DirectoryInfo(o.Folder).EnumerateFiles("*" + extension))
                if (file.Length == 0 && IdInName().IsMatch(file.Name)) file.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>YouTube asking to slow down: too many requests, or the "are you a bot" check rapid lookups trigger.</summary>
    public static bool IsThrottled(string? text)
    {
        string lower = (text ?? "").ToLowerInvariant();
        return lower.Contains("http error 429") || lower.Contains("(429)") || lower.Contains("too many requests") || lower.Contains("not a bot");
    }

    public static bool NeedsLogin(string? text)
    {
        string lower = (text ?? "").ToLowerInvariant();
        return lower.Contains("confirm your age") || lower.Contains("age-restricted") || lower.Contains("inappropriate for some users")
            || lower.Contains("sign in to confirm") || lower.Contains("login required") || lower.Contains("members-only")
            || lower.Contains("private video") || lower.Contains("not a bot") || AsksForAccount(lower);
    }

    /// <summary>How yt-dlp's extractors for other sites say it: each words it differently, but they all point at these options.</summary>
    static bool AsksForAccount(string lower) => lower.Contains("logged-in") || lower.Contains("logged in") || lower.Contains("account credentials")
        || lower.Contains("use --cookies") || lower.Contains("--username");

    public static string FriendlyError(string? text, bool signedIn)
    {
        string lower = (text ?? "").ToLowerInvariant();
        if (lower.Contains("confirm your age") || lower.Contains("age-restricted") || lower.Contains("inappropriate for some users"))
            return Loc.T(signedIn ? "error.age.signedin" : "error.age");
        if (lower.Contains("not a bot") || lower.Contains("sign in to confirm"))
            return Loc.T(signedIn ? "error.bot.signedin" : "error.bot");
        if (lower.Contains("cookies") && (lower.Contains("netscape") || lower.Contains("format"))) return Loc.T("error.cookiefile");
        if (lower.Contains("could not copy") && lower.Contains("cookie")) return Loc.T("error.cookielocked");
        if (lower.Contains("failed to decrypt") || lower.Contains("dpapi")) return Loc.T("error.cookiedecrypt");
        if (lower.Contains("private video") || lower.Contains("login required") || lower.Contains("members-only") || AsksForAccount(lower))
            return Loc.T(signedIn ? "error.private.signedin" : "error.private");
        if (lower.Contains("video is unavailable") || lower.Contains("video unavailable") || lower.Contains("has been removed") || lower.Contains("does not exist"))
            return Loc.T("error.unavailable");
        if (lower.Contains("no video results") || lower.Contains("no entries")) return Loc.T("error.notfound");
        if (lower.Contains("unsupported url")) return Loc.T("error.unsupported");
        if (lower.Contains("requested format is not available")) return Loc.T("error.format");
        if (lower.Contains("conversion failed")) return Loc.T("error.convert");
        if (IsThrottled(lower)) return Loc.T("error.throttled");
        if (lower.Contains("403") || lower.Contains("signature") || lower.Contains("challenge")) return Loc.T("error.update");
        if (DiskFull(lower)) return Loc.T("error.disk");
        if (lower.Contains("file too large") || lower.Contains("errno 27")) return Loc.T("error.filesize");
        if (lower.Contains("timed out") || lower.Contains("unable to download") || lower.Contains("connection") || lower.Contains("resolve")) return Loc.T("error.network");
        return Loc.T("error.other");
    }

    /// <summary>"Title [id].ext" is how every downloaded file is named, which is how existing files are recognized.</summary>
    [GeneratedRegex(@"\[([^\[\]]+)\]\.([A-Za-z0-9]+)$")] public static partial Regex IdInName();

    public static readonly string[] AudioExtensions = { "mp3", "m4a", "opus", "ogg", "oga", "aac", "flac", "wav" };
    public static readonly string[] MediaExtensions = { "mp3", "m4a", "opus", "ogg", "oga", "aac", "flac", "wav", "mp4", "mkv", "webm", "mov" };

    /// <summary>A file in the listing that already satisfies this download, if any.</summary>
    public static string? MatchExisting(IEnumerable<string> filesOfVideo, bool audio, string audioFormat, string container)
    {
        foreach (string file in filesOfVideo)
        {
            string extension = Path.GetExtension(file).TrimStart('.').ToLowerInvariant();
            // mkv is where a video lands when it does not fit the chosen container.
            if (audio ? (audioFormat == "best" ? AudioExtensions.Contains(extension) : extension == audioFormat) : extension == container || extension == "mkv") return file;
        }
        return null;
    }
}

/// <summary>
/// Finds the original studio track. YouTube keeps these as "art tracks" (type ATV, channel "Artist - Topic"), which
/// regular search rarely shows. This is the request the YouTube Music site itself makes for its Songs tab; it returns
/// artist, album, duration and the track type, none of which yt-dlp's Music search gives.
/// </summary>
public static partial class MusicSearch
{
    const string Endpoint = "https://music.youtube.com/youtubei/v1/search?prettyPrint=false";
    const string SongsFilter = "EgWKAQIIAWoMEA4QChADEAQQCRAF";

    // The site's own client names its build by date, and a fixed one would age out sooner or later.
    static string ClientVersion => "1." + DateTime.UtcNow.AddDays(-2).ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".01.00";
    [GeneratedRegex(@"^\d{1,3}:\d{2}(:\d{2})?$")] private static partial Regex TimePattern();
    [GeneratedRegex(@"[\p{L}\p{N}]+")] private static partial Regex WordPattern();
    // Versions that are not "the" track, unless the person asked for one of them.
    static readonly string[] OtherVersions = { "remix", "live", "cover", "instrumental", "karaoke", "sped up", "slowed", "nightcore", "8d", "acoustic", "demo", "radio edit", "ремикс", "кавер" };
    static readonly HashSet<string> NoiseWords = new() { "feat", "ft", "featuring", "official", "the", "a", "an", "and", "of", "prod" };

    public static async Task<List<Dictionary<string, object?>>> SearchAsync(string query, int limit, string proxy, CancellationToken cancel = default)
    {
        string body = Json.Write(Json.Map(
            ("context", Json.Map(("client", Json.Map(("clientName", "WEB_REMIX"), ("clientVersion", ClientVersion), ("hl", Loc.Language)))))
            , ("query", query), ("params", SongsFilter)));
        HttpClient client = Web.Client(proxy);
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36");
        request.Headers.TryAddWithoutValidation("Origin", "https://music.youtube.com");
        using var response = await client.SendAsync(request, cancel).ConfigureAwait(false);
        if ((int)response.StatusCode == 429) throw new HttpRequestException("(429) Too Many Requests");
        response.EnsureSuccessStatusCode();
        string reply = await response.Content.ReadAsStringAsync(cancel).ConfigureAwait(false);
        return ParseResults(reply, limit);
    }

    /// <summary>Public so a captured reply can be tested without the network.</summary>
    public static List<Dictionary<string, object?>> ParseResults(string reply, int limit)
    {
        var results = new List<Dictionary<string, object?>>();
        Collect(Json.Parse(reply), results, limit);
        return results;
    }

    static void Collect(object? node, List<Dictionary<string, object?>> results, int limit)
    {
        if (results.Count >= limit) return;
        if (node is Dictionary<string, object?> map)
        {
            if (map.TryGetValue("musicResponsiveListItemRenderer", out object? renderer))
            {
                var song = ParseSong(renderer as Dictionary<string, object?>);
                if (song != null && results.Count < limit) results.Add(song);
                return;
            }
            foreach (object? value in map.Values) Collect(value, results, limit);
        }
        else if (node is List<object?> list)
        {
            foreach (object? value in list) Collect(value, results, limit);
        }
    }

    static object? Dig(object? node, params object[] path)
    {
        foreach (object step in path)
        {
            if (node == null) return null;
            if (step is int index) node = node is List<object?> list && index < list.Count ? list[index] : null;
            else node = node is Dictionary<string, object?> map && map.TryGetValue((string)step, out object? next) ? next : null;
        }
        return node;
    }

    static Dictionary<string, object?>? ParseSong(Dictionary<string, object?>? item)
    {
        if (item == null) return null;
        object? first = Dig(item, "flexColumns", 0, "musicResponsiveListItemFlexColumnRenderer", "text", "runs", 0);
        string? title = Dig(first, "text") as string;
        string? id = Dig(first, "navigationEndpoint", "watchEndpoint", "videoId") as string ?? Dig(item, "playlistItemData", "videoId") as string;
        if (title == null || id == null) return null;
        string? type = Dig(first, "navigationEndpoint", "watchEndpoint", "watchEndpointMusicSupportedConfigs", "watchEndpointMusicConfig", "musicVideoType") as string;

        var artists = new List<string>();
        string? album = null;
        double duration = 0;
        bool artistSection = true;
        foreach (object? run in Dig(item, "flexColumns", 1, "musicResponsiveListItemFlexColumnRenderer", "text", "runs") as List<object?> ?? new())
        {
            string text = (Dig(run, "text") as string ?? "").Trim();
            if (text == "•") { artistSection = false; continue; }
            if (TimePattern().IsMatch(text))
            {
                duration = 0;
                foreach (string part in text.Split(':')) duration = duration * 60 + int.Parse(part, CultureInfo.InvariantCulture);
                continue;
            }
            string? page = Dig(run, "navigationEndpoint", "browseEndpoint", "browseEndpointContextSupportedConfigs", "browseEndpointContextMusicConfig", "pageType") as string;
            if (page == "MUSIC_PAGE_TYPE_ALBUM") { album = text; continue; }
            // Words between artists come as plain runs, in the language of the reply ("и", "&", ",").
            if (artistSection && text.Length > 0 && Dig(run, "navigationEndpoint") == null && text is "," or "&" or "и" or "and") continue;
            if (artistSection && text.Length > 0) artists.Add(text);
        }
        var song = Json.Map(("id", id), ("title", title), ("webpage_url", "https://www.youtube.com/watch?v=" + id),
            ("ie_key", "Youtube"), ("extractor_key", "Youtube"), ("source", "music"), ("is_original", type == "MUSIC_VIDEO_TYPE_ATV"));
        if (artists.Count > 0) song["uploader"] = string.Join(", ", artists);
        if (album != null) song["album"] = album;
        if (duration > 0) song["duration"] = duration;
        return song;
    }

    public static List<string> Words(string? text) => WordPattern().Matches((text ?? "").ToLowerInvariant()).Select(m => m.Value).Where(w => !NoiseWords.Contains(w)).ToList();

    /// <summary>
    /// First result that really is this song, or null so the caller falls back to ordinary search instead of quietly
    /// downloading the wrong track. YouTube Music always returns something, even for a song it does not have, and the
    /// right artist alone proves nothing: another of their songs must not pass. So "Artist - Title" is compared part by
    /// part. A title written in another script ("Night Train" for 夜の電車) cannot be compared; then the artist decides.
    /// </summary>
    static readonly HashSet<string> ReleaseWords = new() { "remaster", "remastered", "remasterizado", "mono", "stereo", "version", "edition", "deluxe", "bonus", "track", "anniversary", "single", "album", "ремастер" };

    /// <summary>The first result that is this song. When its length is known, one of the same length goes first.</summary>
    public static Dictionary<string, object?>? BestMatch(string query, IEnumerable<Dictionary<string, object?>> results, double? seconds = null)
    {
        var matches = results.Where(song => Matches(query, song)).ToList();
        if (seconds is { } wanted)
            foreach (var song in matches) if (song.Num("duration") is { } length && Math.Abs(length - wanted) <= 4) return song;
        return matches.FirstOrDefault();
    }

    /// <summary>Is this result the song asked for, and not another version of it?</summary>
    public static bool Matches(string query, IDictionary<string, object?> song)
    {
        string lowerQuery = query.ToLowerInvariant();
        string[] parts = Regex.Split(query, @"\s+[-–—]\s+");
        List<string> wantedArtist = parts.Length >= 2 ? Words(parts[0]) : new List<string>();
        List<string> wantedTitle = parts.Length >= 2 ? Words(string.Join(" ", parts.Skip(1))) : Words(query);
        if (wantedTitle.Count == 0) return false;
        string title = song.Str("title") ?? "";
        string lowerTitle = title.ToLowerInvariant();
        if (OtherVersions.Any(v => WordPresent(lowerTitle, v) && !WordPresent(lowerQuery, v))) return false;
        var titleWords = new HashSet<string>(Words(title));
        var allWords = new HashSet<string>(titleWords.Concat(Words(song.Str("uploader") + " " + song.Str("album"))));
        // "Song - 2011 Remaster" from an export is the same song as "Song".
        var plainTitle = wantedTitle.Where(w => !ReleaseWords.Contains(w) && !(w.Length == 4 && w.All(char.IsDigit))).ToList();
        if (plainTitle.Count > 0) wantedTitle = plainTitle;
        if (parts.Length >= 2)
        {
            bool artistFound = wantedArtist.Any(allWords.Contains);
            bool sameScript = HasLatin(string.Join("", wantedTitle)) == HasLatin(title);
            bool titleFound = wantedTitle.Count(titleWords.Contains) * 2 >= wantedTitle.Count;
            return titleFound ? artistFound || wantedArtist.Count == 0 : artistFound && wantedArtist.Count(allWords.Contains) == wantedArtist.Count && !sameScript;
        }
        // No separator: the words may be artist and title together, but some must be in the title.
        return wantedTitle.Count(allWords.Contains) * 2 >= wantedTitle.Count && wantedTitle.Any(titleWords.Contains);
    }

    /// <summary>A name alone, when several artists have a song called exactly that: nothing can tell which was meant.</summary>
    public static bool IsAmbiguous(string query, IEnumerable<Dictionary<string, object?>>? results)
    {
        if (results == null || Regex.IsMatch(query, @"\s+[-–—]\s+")) return false;
        var wanted = new HashSet<string>(Words(query));
        return results.Where(song => wanted.SetEquals(Words(song.Str("title") ?? ""))).Select(song => song.Str("uploader")).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= 2;
    }

    static bool HasLatin(string text) => text.Any(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z');

    // Whole words only: "live" must not reject "Olivetti" or "Alive", nor "demo" "Demons".
    static bool WordPresent(string text, string phrase) => Regex.IsMatch(text, @"(?<![\p{L}\p{N}])" + Regex.Escape(phrase) + @"(?![\p{L}\p{N}])");

    // Only a marker in brackets or closing the title: "Best music video ever" is not a clip of a song called "Best ever".
    const string MarkerWords = @"(?:official\s*(?:music\s*)?(?:video|mv|clip|visualizer|lyric\s*video)|music\s*video|lyric\s*video|official\s*audio|video\s*oficial|клип|\bm/?v\b)";
    [GeneratedRegex(@"[\(\[【][^\)\]】]*" + MarkerWords + @"[^\)\]】]*[\)\]】]|\s" + MarkerWords + @"\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ClipMarker();

    /// <summary>
    /// What to look for when a clip should become its original track: YouTube's own music info when present, otherwise
    /// the title of a clearly marked music video. Null when this does not look like a music clip.
    /// </summary>
    public static string? QueryForClip(IDictionary<string, object?>? info)
    {
        string? track = info.Str("track"), artist = info.Str("artist") ?? info.Str("creator");
        if (track != null && artist != null) return artist + " - " + track;
        string? title = info.Str("title");
        if (title == null || !ClipMarker().IsMatch(title)) return null;
        string cleaned = ClipMarker().Replace(title, " ").Trim();
        return Words(cleaned).Count >= 2 ? cleaned : null;
    }
}

/// <summary>Whether a newer release of this app exists. It never updates anything by itself.</summary>
public static class AppUpdate
{
    public const string Repository = "kon9/volna";

    public static string? Current => typeof(AppUpdate).Assembly.GetName().Version?.ToString(3);

    public static async Task<string?> NewerVersionAsync(string proxy)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/" + Repository + "/releases/latest");
        request.Headers.TryAddWithoutValidation("User-Agent", "Volna");
        using var response = await Web.Client(proxy).SendAsync(request).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        string? tag = Json.ParseObject(await response.Content.ReadAsStringAsync().ConfigureAwait(false)).Str("tag_name");
        return tag != null && Current != null && IsNewer(tag, Current) ? tag.TrimStart('v') : null;
    }

    public static bool IsNewer(string tag, string current) =>
        Version.TryParse(tag.TrimStart('v'), out Version? theirs) && Version.TryParse(current, out Version? ours) && theirs > ours;
}

/// <summary>Song texts from LRCLIB, an open database that needs no account. Timed ones show line by line in many players.</summary>
public static class Lyrics
{
    public static async Task<string?> FindAsync(string artist, string title, double? seconds, string proxy)
    {
        string url = "https://lrclib.net/api/search?artist_name=" + Uri.EscapeDataString(artist) + "&track_name=" + Uri.EscapeDataString(title);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", "Volna (https://github.com/" + AppUpdate.Repository + ")");
        using var response = await Web.Client(proxy).SendAsync(request).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        string reply = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return Pick(reply, seconds);
    }

    /// <summary>Timed text only from a recording of the same length: on another one the lines would drift.</summary>
    public static string? Pick(string reply, double? seconds)
    {
        var found = (Json.Parse(reply) as List<object?>)?.OfType<Dictionary<string, object?>>().ToList();
        if (found == null) return null;
        bool Close(Dictionary<string, object?> song) => seconds == null || (song.Num("duration") is { } length && Math.Abs(length - seconds.Value) <= 5);
        foreach (var song in found.Where(Close)) if (song.Str("syncedLyrics") is { Length: > 0 } timed) return timed;
        foreach (var song in found) if (song.Str("plainLyrics") is { Length: > 0 } plain) return plain;
        return null;
    }
}

/// <summary>HTTP clients that honor the proxy setting: traffic must not quietly go around a proxy someone relies on.</summary>
public static class Web
{
    /// <summary>socks5h is socks5 with remote name resolution, which .NET does anyway. A login in the address goes separately.</summary>
    public static System.Net.WebProxy Proxy(string address)
    {
        string clean = address.Replace("socks5h://", "socks5://", StringComparison.OrdinalIgnoreCase);
        if (!Uri.TryCreate(clean, UriKind.Absolute, out Uri? uri) || uri.UserInfo.Length == 0) return new System.Net.WebProxy(clean);
        string[] login = uri.UserInfo.Split(':', 2);
        var proxy = new System.Net.WebProxy(uri.Scheme + "://" + uri.Authority.Split('@').Last());
        proxy.Credentials = new System.Net.NetworkCredential(Uri.UnescapeDataString(login[0]), login.Length > 1 ? Uri.UnescapeDataString(login[1]) : "");
        return proxy;
    }

    static readonly Dictionary<string, HttpClient> Clients = new();

    public static HttpClient Client(string? proxy)
    {
        string key = (proxy ?? "").Trim();
        lock (Clients)
        {
            if (Clients.TryGetValue(key, out HttpClient? client)) return client;
            var handler = new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All };
            // .NET speaks socks4/5 natively; socks5h is its socks5 with remote name resolution, which it does anyway.
            if (key.Length > 0) { handler.Proxy = Proxy(key); handler.UseProxy = true; }
            client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
            Clients[key] = client;
            return client;
        }
    }
}
