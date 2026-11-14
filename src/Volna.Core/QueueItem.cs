using System.ComponentModel;
using System.Globalization;
using System.Text;

namespace Volna.Core;

public enum ItemState { Fetching, Ready, Queued, Downloading, Processing, Done, Failed, Cancelled }

public sealed class Preferences
{
    public string Folder { get; set; } = "";
    public bool Audio { get; set; } = true;
    public int Height { get; set; } = 1080;
    public string AudioFormat { get; set; } = "mp3";
    public string Container { get; set; } = "mp4";
    public bool OpenFolder { get; set; } = true;
    public int Parallel { get; set; } = 2;
    public bool EmbedThumbnail { get; set; } = true;
    public bool Subtitles { get; set; }
    public bool SponsorBlock { get; set; }
    public bool AutoStart { get; set; }
    public bool WatchClipboard { get; set; } = true;
    public bool OriginalTracks { get; set; } = true;
    public bool Sound { get; set; } = true;
    public bool Lyrics { get; set; }
    public bool WritePlaylists { get; set; } = true;
    public bool SkipDownloaded { get; set; }
    public bool CleanNames { get; set; }
    public string Language { get; set; } = "auto";
    public string Theme { get; set; } = "dark";
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public bool Maximized { get; set; }
    public string Proxy { get; set; } = ""; // saved unless it holds a password

    public static Preferences Load()
    {
        var prefs = new Preferences();
        try
        {
            if (!File.Exists(AppPaths.Settings)) return prefs;
            var map = Json.ParseObject(File.ReadAllText(AppPaths.Settings));
            if (map == null) return prefs;
            // A setting missing from an older file takes its default, so an update never quietly turns a feature off.
            prefs.Folder = map.Str("folder") ?? "";
            prefs.Audio = map.Raw("audio") is bool audioValue ? audioValue : true;
            prefs.Height = (int)(map.Num("height") ?? 1080);
            prefs.AudioFormat = map.Str("audioFormat") ?? "mp3";
            prefs.Container = map.Str("container") ?? "mp4";
            prefs.OpenFolder = map.Raw("openFolder") is bool openFolderValue ? openFolderValue : true;
            prefs.Parallel = Math.Clamp((int)(map.Num("parallel") ?? 2), 1, 4);
            prefs.EmbedThumbnail = map.Raw("embedThumbnail") is bool embedThumbnailValue ? embedThumbnailValue : true;
            prefs.Subtitles = map.Raw("subtitles") is bool subtitlesValue ? subtitlesValue : false;
            prefs.SponsorBlock = map.Raw("sponsorBlock") is bool sponsorBlockValue ? sponsorBlockValue : false;
            prefs.AutoStart = map.Raw("autoStart") is bool autoStartValue ? autoStartValue : false;
            prefs.WatchClipboard = map.Raw("watchClipboard") is bool watchClipboardValue ? watchClipboardValue : true;
            prefs.OriginalTracks = map.Raw("originalTracks") is bool originalTracksValue ? originalTracksValue : true;
            prefs.Sound = map.Raw("sound") is bool soundValue ? soundValue : true;
            prefs.Lyrics = map.Flag("lyrics");
            prefs.WritePlaylists = map.Raw("writePlaylists") is bool writePlaylistsValue ? writePlaylistsValue : true;
            prefs.SkipDownloaded = map.Flag("skipDownloaded");
            prefs.CleanNames = map.Flag("cleanNames");
            prefs.Proxy = map.Str("proxy") ?? "";
            prefs.Language = map.Str("language") ?? "auto";
            prefs.Theme = map.Str("theme") ?? "dark";
            prefs.WindowWidth = map.Num("windowWidth") ?? 0;
            prefs.WindowHeight = map.Num("windowHeight") ?? 0;
            prefs.Maximized = map.Flag("maximized");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return prefs;
    }

    public void Save()
    {
        try
        {
            AppPaths.WriteAtomic(AppPaths.Settings, Json.Write(Json.Map(("folder", Folder), ("audio", Audio), ("height", Height), ("audioFormat", AudioFormat), ("container", Container),
                ("openFolder", OpenFolder), ("parallel", Parallel), ("embedThumbnail", EmbedThumbnail), ("subtitles", Subtitles), ("sponsorBlock", SponsorBlock), ("autoStart", AutoStart),
                ("watchClipboard", WatchClipboard), ("originalTracks", OriginalTracks), ("sound", Sound), ("lyrics", Lyrics), ("writePlaylists", WritePlaylists), ("skipDownloaded", SkipDownloaded), ("cleanNames", CleanNames), ("language", Language), ("proxy", Proxy.Contains('@', StringComparison.Ordinal) ? "" : Proxy), ("theme", Theme),
                ("windowWidth", WindowWidth), ("windowHeight", WindowHeight), ("maximized", Maximized)), true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>One card in the queue. Properties are plain; Changed() refreshes every binding of the card at once.</summary>
public sealed class QueueItem : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    public int Number { get; set; }
    public string SourceUrl { get; set; } = "";
    public string? WebpageUrl { get; set; }
    public string? VideoId { get; set; }
    public string? ThumbUrl { get; set; }
    public string? FilePath { get; set; }
    public string? Query { get; set; }
    public string? ExistingPath { get; set; }
    public string Extractor { get; set; } = "";
    public bool Fetched { get; set; }
    public bool StartWhenReady { get; set; }
    public bool InRun { get; set; }
    public bool ModeLocked { get; set; }
    public bool AlreadyHad { get; set; }
    public bool Removed { get; set; }
    public bool NeedsLogin { get; set; }
    public bool FetchQueued { get; set; }
    public bool TrackQueued { get; set; }
    public bool TrackChecked { get; set; }
    public bool RegularTried { get; set; }
    public bool ManualPick { get; set; }
    public double? ExpectedSeconds { get; set; }
    public bool ForceDownload { get; set; }
    public bool Retrying { get; set; }
    public string? PendingOutput { get; set; } // being written by a converter, and worthless until that ends
    public int? HeightOverride { get; set; }
    public string? Isrc { get; set; }
    public List<string> Lists { get; } = new();
    public string? Section { get; set; }
    public string? SectionName { get; set; }
    public string? ReviewReason { get; set; }
    public bool ReviewDismissed { get; set; }
    public bool RepairTried { get; set; }
    public bool InHistory { get; set; }
    public bool FollowRefresh { get; set; }
    public Dictionary<string, object?>? Info { get; set; }
    public Dictionary<string, object?>? ClipInfo { get; set; }
    public Dictionary<string, object?>? TrackInfo { get; set; }
    public string? ClipPlaylist { get; set; }
    public string? LastErrors { get; set; }
    public int ThrottleRetries { get; set; }
    public List<Dictionary<string, object?>>? Alternatives { get; set; }
    public List<Dictionary<string, object?>>? ClipAlternatives { get; set; }
    public List<Dictionary<string, object?>>? TrackAlternatives { get; set; }
    public ProcessRunner? Runner { get; set; }
    public DateTime StartedAt { get; set; }
    public double Speed { get; set; }
    public List<string> Log { get; } = new();
    public StringBuilder Errors { get; } = new();

    public string Title { get; set; } = "";
    public string Channel { get; set; } = "";
    public string Album { get; set; } = "";
    public string Views { get; set; } = "";
    public string Site { get; set; } = "";
    public string Duration { get; set; } = "";
    public string Badge { get; set; } = "";
    public string Playlist { get; set; } = "";
    public byte[]? ThumbBytes { get; set; }
    public bool AudioMode { get; set; }
    public bool IsOriginal { get; set; }
    public bool Chosen { get; set; }
    public string ModeText { get; set; } = "";
    public ItemState State { get; set; }
    public string StatusText { get; set; } = "";
    public string SpeedText { get; set; } = "";
    public double Progress { get; set; }
    public bool Indeterminate { get; set; }

    public bool IsSearch => Query != null;
    public string? FileKey => VideoId == null ? null : Section == null ? VideoId : VideoId + " clip " + Section;
    public bool ShowReview => ReviewReason != null && CanEdit && State != ItemState.Fetching;
    public string ReviewText => ReviewReason ?? "";
    public bool ShowHistory => InHistory && ExistingPath == null && CanEdit && State != ItemState.Fetching;
    public bool HasSection => Section != null;
    /// <summary>A part can be chosen once the length is known; a finished card gives its further parts cards of their own.</summary>
    public bool ShowClip => Fetched && Info.Num("duration") is > 0 && Info.Str("live_status") != "is_live" && !IsActive && State != ItemState.Fetching;
    public string ClipText => HasSection ? SectionName is { Length: > 0 } ? SectionName + " · " + SectionText : SectionText : Loc.T("card.clip");
    public string SectionText
    {
        get
        {
            string[] ends = (Section ?? "").Split('-');
            return ends.Length == 2 ? Loc.T("card.section", Format.Duration(double.Parse(ends[0], CultureInfo.InvariantCulture)), Format.Duration(double.Parse(ends[1], CultureInfo.InvariantCulture))) : "";
        }
    }
    public bool CanFollow => !IsSearch && Lists.Count > 0;
    public bool CanExpandPlaylist => !IsSearch && Engine.PlaylistOf(SourceUrl) != null;
    public bool IsActive => State is ItemState.Downloading or ItemState.Processing;
    public bool IsDone => State == ItemState.Done;
    public bool IsFailed => State == ItemState.Failed;
    public bool IsStopped => State == ItemState.Cancelled;
    public bool IsDownloading => State == ItemState.Downloading;
    public bool IsProcessing => State == ItemState.Processing;
    public bool IsLookingUp => State == ItemState.Fetching;
    public bool CanEdit => !IsActive && State != ItemState.Done;
    public string Meta => string.Join("  ·  ", new[] { Channel, Album, Views, Site }.Where(s => !string.IsNullOrEmpty(s)));
    public bool HasMeta => Meta.Length > 0;
    public bool HasDuration => !string.IsNullOrEmpty(Duration);
    public bool HasBadge => !string.IsNullOrEmpty(Badge);
    public bool HasPlaylist => !string.IsNullOrEmpty(Playlist);
    public bool HasSpeed => !string.IsNullOrEmpty(SpeedText);
    public bool HasThumb => ThumbBytes != null;
    public bool NoThumb => ThumbBytes == null;
    public bool IsBusyIndicator => State == ItemState.Fetching || Indeterminate;
    public bool ShowProgress => State == ItemState.Fetching || IsActive;
    public bool ShowStartOne => State == ItemState.Ready;
    public bool ShowRetry => State is ItemState.Failed or ItemState.Cancelled;
    public bool ShowCancel => IsActive || State == ItemState.Queued;
    public bool ShowRemove => !IsActive && State != ItemState.Queued;
    public string ExistingPathText => ExistingPath ?? "";
    public bool ShowExists => ExistingPath != null && !ForceDownload && State != ItemState.Done && !IsActive;
    public bool CanDownloadAgain => (State == ItemState.Done || ShowExists) && !IsActive;
    public bool ShowAlternatives => Alternatives is { Count: > 1 } && CanEdit && State != ItemState.Fetching;
    public string AlternativesText => Alternatives == null ? "" : Loc.T("card.versions", Alternatives.Count - 1);
    public string ModeGlyph => AudioMode ? "" : "";
    public string PlaceholderGlyph => IsSearch && !Fetched ? "" : AudioMode ? "" : "";
    public string PlaylistGlyph => IsSearch ? "" : "";
    public string StatusGlyph => State switch
    {
        ItemState.Fetching => "",
        ItemState.Ready => "",
        ItemState.Queued => "",
        ItemState.Downloading => "",
        ItemState.Processing => "",
        ItemState.Done => "",
        ItemState.Failed => "",
        _ => ""
    };
}

public enum ToastKind { Info, Success, Warning, Error }

public sealed record QueueSummary(int Total, int Done, int Active, int Queued, int Fetching, int Failed, int Startable, double Speed, double RunFraction, int RunDone, int RunTotal, string? PausedUntil, int Review = 0);
