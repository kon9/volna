using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace Volna.Core;

/// <summary>
/// The queue: what is looked up, what downloads when, and what a card shows. It has no user interface in it.
/// Everything here runs on one thread (the synchronization context it is created on); background work comes back to
/// that thread through Post, in order.
/// </summary>
public sealed class QueueController : IDisposable
{
    // Lookups are deliberately unhurried: downloading takes far longer than searching anyway, and a gentle pace keeps
    // YouTube from throttling. About one search per second still stays well ahead of the downloads.
    public const int SearchResults = 6, FetchWorkers = 2, SearchBatch = 5, MaxThrottleRetries = 3;

    readonly IQueueBackend backend;
    readonly Preferences prefs;
    readonly SynchronizationContext context;
    readonly Queue<QueueItem> fetchQueue = new(), trackQueue = new();
    readonly HashSet<ProcessRunner> fetchRunners = new();
    readonly Dictionary<string, QueueItem> byVideo = new(StringComparer.Ordinal);
    readonly SemaphoreSlim thumbGate = new(6);
    readonly object queueFileGate = new();
    int nextNumber = 1, fetchWorkers, queueSignature, duplicateCount;
    bool trackWorker, loginHintShown, closing, repairing, repairRunning, musicDownShown;
    int musicFailures;
    DateTime lastRepair;
    Dictionary<string, List<string>> folderNames = new(StringComparer.Ordinal);
    string? duplicateTitle;
    DateTime fetchPausedUntil;
    int fetchBackoff;
    Dictionary<string, List<string>>? folderCache;
    string? folderCacheKey;
    DateTime folderCacheTime;
    Timer? duplicateTimer;

    public ObservableCollection<QueueItem> Items { get; } = new();
    public ObservableCollection<FollowedList> Followed { get; } = new();

    public event Action<string, ToastKind>? Toast;
    public event Action<string>? Log;
    public event Action? SummaryChanged;
    /// <summary>A run ended with this many files saved and this many failed.</summary>
    public event Action<int, int>? RunCompleted;

    /// <summary>Delay between Music requests, and how far a run's pacing goes; tests set it to zero.</summary>
    public int MusicDelayMilliseconds { get; set; } = 700;

    /// <summary>The first pause when YouTube asks to slow down; each further one doubles.</summary>
    public int ThrottleBaseSeconds { get; set; } = 30;

    /// <summary>While set, nothing starts yt-dlp: its file is being replaced by an update.</summary>
    public bool Hold
    {
        get => hold;
        set
        {
            hold = value;
            if (!hold) { PumpFetch(); Pump(); }
        }
    }
    bool hold;

    public QueueController(IQueueBackend backend, Preferences prefs)
    {
        this.backend = backend;
        this.prefs = prefs;
        context = SynchronizationContext.Current ?? throw new InvalidOperationException("The queue needs a synchronization context.");
        foreach (var list in FollowedList.Load()) Followed.Add(list);
    }

    void Post(Action action) => context.Post(_ => action(), null);

    void Say(string text, ToastKind kind)
    {
        Toast?.Invoke(text, kind);
        if (kind == ToastKind.Error) Write(null, text);
    }

    void Write(QueueItem? item, string line)
    {
        if (item != null)
        {
            item.Log.Add(line);
            if (item.Log.Count > 500) item.Log.RemoveAt(0);
        }
        Log?.Invoke(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + (item != null ? "  #" + item.Number.ToString(CultureInfo.InvariantCulture) : "") + "  " + line);
    }

    // ---------- Adding ----------

    /// <summary>Adds parsed entries. Returns how many cards were created and how many were already in the list.</summary>
    public (int Added, int Skipped) Add(IEnumerable<string> entries) => Add(entries, null);

    /// <summary>Adds songs from an export. What it knows about each one helps to find the right recording.</summary>
    public (int Added, int Skipped) AddTracks(IEnumerable<LibraryTrack> tracks)
    {
        var hints = new Dictionary<string, List<LibraryTrack>>(StringComparer.Ordinal);
        foreach (var track in tracks)
        {
            if (!hints.TryGetValue(track.Entry, out var same)) hints[track.Entry] = same = new();
            same.Add(track);
        }
        return Add(hints.Keys.ToList(), hints);
    }

    (int Added, int Skipped) Add(IEnumerable<string> entries, Dictionary<string, List<LibraryTrack>>? hints)
    {
        int added = 0, skipped = 0;
        var known = new Dictionary<string, QueueItem>(StringComparer.Ordinal);
        foreach (var existing in Items)
        {
            known[existing.SourceUrl] = existing;
            if (existing.WebpageUrl != null) known[existing.WebpageUrl] = existing;
        }
        void Describe(QueueItem card, string entry)
        {
            if (hints == null || !hints.TryGetValue(entry, out var same)) return;
            card.ExpectedSeconds ??= same[0].Seconds;
            card.Isrc ??= same[0].Isrc;
            foreach (string list in same.Select(t => t.List)) if (list.Length > 0 && !card.Lists.Contains(list)) card.Lists.Add(list);
        }
        foreach (string entry in entries)
        {
            if (known.TryGetValue(entry, out QueueItem? owner)) { Describe(owner, entry); skipped++; continue; }
            bool search = Engine.IsSearch(entry);
            if (!search && !Uri.TryCreate(entry, UriKind.Absolute, out _)) { skipped++; continue; }
            var item = new QueueItem { Number = nextNumber++, SourceUrl = entry, AudioMode = prefs.Audio, State = ItemState.Fetching };
            if (search)
            {
                item.Query = entry[Engine.SearchPrefix.Length..];
                item.Title = item.Query;
                item.Site = Loc.T("card.search");
            }
            else
            {
                Uri uri = new(entry);
                item.Title = uri.Host.Replace("www.", "", StringComparison.Ordinal) + uri.PathAndQuery;
                if (item.Title.Length > 90) item.Title = item.Title[..90] + "…";
            }
            if (prefs.AutoStart) { item.StartWhenReady = true; item.InRun = true; }
            Describe(item, entry);
            known[entry] = item;
            Items.Add(item);
            added++;
            Write(item, (search ? "Search: " : "Added: ") + (item.Query ?? entry));
            BeginLookup(item, false);
        }
        PumpFetch();
        RefreshLabels();
        NotifySummary();
        return (added, skipped);
    }

    // ---------- Lookups ----------

    void BeginLookup(QueueItem item, bool pump)
    {
        if (item.IsSearch && WantsOriginal(item) && !item.TrackChecked) QueueTrackLookup(item);
        else QueueFetch(item, pump);
    }

    void QueueFetch(QueueItem item, bool pump)
    {
        item.State = ItemState.Fetching;
        item.Indeterminate = false;
        item.StatusText = Loc.T(item.IsSearch ? "status.searching" : "status.fetching");
        item.Changed();
        if (!item.FetchQueued) { item.FetchQueued = true; fetchQueue.Enqueue(item); }
        if (pump) PumpFetch();
    }

    void PumpFetch()
    {
        if (closing || hold) return;
        if (repairing) { ContinueRepair(); return; }
        if (DateTime.Now < fetchPausedUntil)
        {
            ScheduleOnce(fetchPausedUntil - DateTime.Now + TimeSpan.FromMilliseconds(300), PumpFetch);
            return;
        }
        while (fetchWorkers < FetchWorkers && fetchQueue.Count > 0)
        {
            var batch = new List<QueueItem>();
            while (fetchQueue.Count > 0 && batch.Count < SearchBatch)
            {
                QueueItem next = fetchQueue.Peek();
                if (next.Removed) { fetchQueue.Dequeue(); next.FetchQueued = false; continue; }
                // A link always goes alone, because it can be a whole playlist; searches share a process, which saves its start-up time.
                if (!next.IsSearch && batch.Count > 0) break;
                fetchQueue.Dequeue();
                next.FetchQueued = false;
                batch.Add(next);
                if (!next.IsSearch) break;
            }
            if (batch.Count == 0) continue;
            fetchWorkers++;
            _ = RunFetchAsync(batch);
        }
    }

    void ScheduleOnce(TimeSpan delay, Action action)
    {
        Timer? timer = null;
        timer = new Timer(_ => { Post(action); timer?.Dispose(); }, null, delay, Timeout.InfiniteTimeSpan);
    }

    async Task RunFetchAsync(List<QueueItem> batch)
    {
        var runner = new ProcessRunner();
        fetchRunners.Add(runner);
        try
        {
            foreach (var item in batch) item.Runner = runner;
            // A search asks for several results: the first is used, the rest can be picked instead.
            var requests = batch.Select(i => i.IsSearch ? "ytsearch" + SearchResults.ToString(CultureInfo.InvariantCulture) + ":" + i.Query : i.SourceUrl).ToList();
            QueueItem? logItem = batch.Count == 1 ? batch[0] : null;
            // A channel takes a minute to list; its card counts as it goes, though not on every single video.
            Action<int>? counted = logItem is { IsSearch: false } ? n => { if (n % 20 == 0) Post(() => ShowFound(logItem, n)); } : null;
            LookupResult result = await backend.LookupAsync(requests, Engine.PlaylistLimit, prefs.Proxy, runner, line => Post(() => Write(logItem, line)), counted);
            foreach (var item in batch) if (item.Runner == runner) item.Runner = null;

            if (!result.Cancelled && Engine.CookiesUnreadable(result.Errors) && (SuspendBrowser() || Auth.Suspended))
            {
                foreach (var item in batch) QueueFetch(item, false);
                return;
            }
            bool throttled = !result.Cancelled && Engine.IsThrottled(result.Errors);
            if (throttled) PauseLookups();
            else if (result.Entries.Count > 0) fetchBackoff = 0;
            var entries = result.Cancelled ? null : result.Entries;
            foreach (var item in batch) item.LastErrors = result.Errors;

            if (batch.Count == 1)
            {
                if (throttled && result.Entries.Count == 0) WaitAndRetry(batch[0]);
                else CompleteFetch(batch[0], entries, result.Errors);
                return;
            }
            // yt-dlp names each search result list after its query, which tells whose result each line is.
            var byQuery = new Dictionary<string, List<Dictionary<string, object?>>>(StringComparer.Ordinal);
            foreach (var entry in result.Entries)
            {
                string title = entry.Str("playlist_title") ?? "";
                if (!byQuery.TryGetValue(title, out var list)) byQuery[title] = list = new();
                list.Add(entry);
            }
            foreach (var item in batch)
            {
                List<Dictionary<string, object?>>? found = null;
                if (entries != null && !byQuery.TryGetValue(item.Query!, out found))
                    found = byQuery.Where(p => string.Equals(p.Key.Trim(), item.Query!.Trim(), StringComparison.OrdinalIgnoreCase)).Select(p => p.Value).FirstOrDefault() ?? new();
                if (throttled && found is not { Count: > 0 }) WaitAndRetry(item);
                else CompleteFetch(item, found, result.Errors);
            }
        }
        catch (Exception ex)
        {
            foreach (var item in batch.Where(i => !i.Removed && i.State == ItemState.Fetching))
            {
                item.Runner = null;
                FailLookup(item, ex.Message, "");
            }
        }
        finally
        {
            runner.Dispose();
            fetchRunners.Remove(runner);
            fetchWorkers--;
            PumpFetch();
            Pump();
        }
    }

    bool SuspendBrowser()
    {
        if (!Auth.Suspend()) return false;
        Write(null, "The browser's sign-ins cannot be read; going on without them");
        Say(Loc.T("toast.browser.unreadable", Browsers.DisplayName(Auth.Browser ?? "")), ToastKind.Warning);
        return true;
    }

    static void ShowFound(QueueItem item, int count)
    {
        if (item.State != ItemState.Fetching) return;
        item.StatusText = Loc.T("status.found", count);
        item.Changed();
    }

    /// <summary>A link opened from a playlist gets one video; this adds the rest of that playlist.</summary>
    public void ExpandPlaylist(QueueItem item)
    {
        if (Engine.PlaylistOf(item.SourceUrl) is { } playlist) Add(new[] { playlist });
    }

    // Several lookups can hit the limit together; only the first one starts a pause, and each new limit doubles it.
    void PauseLookups()
    {
        if (DateTime.Now < fetchPausedUntil) return;
        fetchBackoff = fetchBackoff == 0 ? ThrottleBaseSeconds : Math.Min(300, fetchBackoff * 2);
        fetchPausedUntil = DateTime.Now.AddSeconds(fetchBackoff);
        Write(null, "YouTube asked to slow down; lookups paused for " + fetchBackoff.ToString(CultureInfo.InvariantCulture) + " s");
        Say(Loc.T(Auth.SignedIn ? "toast.throttled" : "toast.throttled.signin", fetchBackoff), ToastKind.Warning);
        NotifySummary();
    }

    // A lookup cut short by the limit is not a "nothing found": the card simply waits for its turn again.
    void WaitAndRetry(QueueItem item)
    {
        if (item.Removed) return;
        // Waiting is for a limit that passes. After a few rounds it is not passing, and the person should know why.
        if (++item.ThrottleRetries > MaxThrottleRetries)
        {
            item.ThrottleRetries = 0;
            FailLookup(item, Engine.FriendlyError(item.LastErrors, Auth.SignedIn), item.LastErrors ?? "");
            return;
        }
        QueueFetch(item, false);
        item.StatusText = Loc.T("status.waitlimit");
        item.Changed();
    }

    void FailLookup(QueueItem item, string message, string errors)
    {
        if (!item.IsSearch && Engine.IsClosedMusicService(item.SourceUrl)) message = Loc.T("error.musicservice");
        item.Fetched = false;
        item.StartWhenReady = false;
        item.Indeterminate = false;
        item.State = ItemState.Failed;
        item.NeedsLogin = Engine.NeedsLogin(errors);
        item.StatusText = message;
        if (item.NeedsLogin) HintLogin();
        item.Changed();
    }

    void HintLogin()
    {
        if (Auth.SignedIn || loginHintShown) return;
        loginHintShown = true;
        Say(Loc.T("toast.needlogin"), ToastKind.Warning);
    }

    /// <summary>Applies a finished lookup to its card. entries == null means the lookup was cancelled.</summary>
    void CompleteFetch(QueueItem item, List<Dictionary<string, object?>>? entries, string errors)
    {
        if (item.Removed) return;
        item.RegularTried = true;
        if (entries is { Count: > 1 }) entries = entries.Where(e => e.Str("title") != "[Deleted video]").ToList();
        // For some exact "Artist - Song" queries YouTube answers with a layout yt-dlp cannot read and the search comes
        // back empty; YouTube Music finds those songs, so it gets one try before giving up.
        if (item.IsSearch && !item.TrackChecked && entries is { Count: 0 })
        {
            Write(item, "Ordinary search found nothing; trying YouTube Music");
            QueueTrackLookup(item);
            return;
        }
        if (entries == null)
        {
            item.Fetched = false;
            item.StartWhenReady = false;
            item.InRun = false;
            item.Indeterminate = false;
            item.State = ItemState.Cancelled;
            item.StatusText = Loc.T("status.cancelled");
            item.Changed();
            return;
        }
        FollowedList? followed = item.FollowRefresh ? Followed.FirstOrDefault(l => l.Url == item.SourceUrl) : null;
        if (entries.Count > 0 && followed != null) followed.Checked = DateTime.Now;
        if (entries.Count == 0)
        {
            if (TryRepair(item, errors)) { QueueFetch(item, false); return; }
            FailLookup(item, item.IsSearch && errors.Length == 0 ? Loc.T("error.notfound") : Engine.FriendlyError(errors, Auth.SignedIn), errors);
            return;
        }

        string? playlist = null;
        int count = entries.Count;
        if (item.IsSearch)
        {
            playlist = Loc.T(WantsOriginal(item) ? "card.nooriginal" : "card.foundby", item.Query);
            count = 1;
            item.Alternatives = item.ClipAlternatives = entries;
        }
        else if (entries.Count > 1 || entries[0].Str("playlist_title") != null) // a playlist of one is still a playlist
        {
            playlist = entries[0].Str("playlist_title") ?? Loc.T("card.playlist");
            if (!item.FollowRefresh) Say(Loc.T(entries.Count >= Engine.PlaylistLimit ? "toast.playlist.capped" : "toast.playlist", playlist, entries.Count, Engine.PlaylistLimit), ToastKind.Success);
        }

        int index = Items.IndexOf(item), duplicates = 0;
        var created = new List<QueueItem>();
        var givenLists = item.Lists.ToList(); // the first card gains its own list below, which is not the others'
        for (int n = 0; n < count; n++)
        {
            QueueItem target = item;
            if (n > 0)
            {
                target = new QueueItem
                {
                    Number = nextNumber++,
                    SourceUrl = item.SourceUrl,
                    AudioMode = item.AudioMode,
                    ModeLocked = item.ModeLocked,
                    StartWhenReady = item.StartWhenReady,
                    InRun = item.InRun,
                    ModeText = item.ModeText,
                    FollowRefresh = item.FollowRefresh
                };
                target.Lists.AddRange(givenLists);
            }
            // A channel is several lists at once: its videos, shorts and streams.
            string? list = !item.IsSearch && playlist != null ? entries[n].Str("playlist_title") ?? playlist : playlist;
            ApplyInfo(target, entries[n], list);
            target.ClipInfo = entries[n];
            target.ClipPlaylist = list;
            if (target.IsOriginal) target.TrackInfo = entries[n];
            if (!item.IsSearch && playlist != null && !target.Lists.Contains(list!)) target.Lists.Add(list!);
            if (item.FollowRefresh && (followed?.Seen.Add(entries[n].Str("id") ?? "") == false || AlreadyHave(target)))
            {
                if (n == 0) { item.Removed = true; Items.Remove(item); index--; }
                continue;
            }
            if (IsTaken(target, Key(target)))
            {
                if (!item.FollowRefresh) duplicates++;
                MergeLists(byVideo[Key(target)], target);
                if (n == 0) { item.Removed = true; Items.Remove(item); index--; }
                continue;
            }
            Claim(target);
            if (n > 0) Items.Insert(Math.Min(Items.Count, index + n), target);
            created.Add(target);
        }
        if (followed != null && entries.Count > 0) FollowedList.Save(Followed);
        if (duplicates > 0) NoteDuplicates(duplicates, count == 1 ? item.Title : null);
        if (item.FollowRefresh && created.Count > 0) Say(Loc.T("toast.follow.new", created.Count, playlist ?? item.Title), ToastKind.Success);

        foreach (var entry in created)
        {
            // A pasted link to a clip is replaced by its track too, if one is found (the clip stays under "other versions").
            if (NeedsTrackLookup(entry))
            {
                entry.Fetched = true;
                QueueTrackLookup(entry);
                _ = LoadThumbAsync(entry);
            }
            else FinishReady(entry);
        }
    }

    static string Key(QueueItem item) => item.Extractor + "/" + item.VideoId;

    // The same song reached from two playlists stays one card, and belongs to both of them.
    static void MergeLists(QueueItem owner, QueueItem other)
    {
        foreach (string list in other.Lists) if (!owner.Lists.Contains(list)) owner.Lists.Add(list);
    }

    // A video is taken only if another card still shows it: a card can move on to a different version.
    bool IsTaken(QueueItem item, string key) =>
        item.VideoId != null && byVideo.TryGetValue(key, out QueueItem? owner) && owner != item && !owner.Removed && Key(owner) == key && Items.Contains(owner);

    void Claim(QueueItem item) { if (item.VideoId != null) byVideo[Key(item)] = item; }

    void FinishReady(QueueItem entry)
    {
        entry.Fetched = true;
        entry.State = ItemState.Ready;
        entry.Indeterminate = false;
        entry.StatusText = Loc.T("status.ready");
        entry.ReviewReason = CheckMatch(entry);
        RefreshLabels(new[] { entry });
        if (entry.StartWhenReady)
        {
            entry.StartWhenReady = false;
            if (!MarkIfExists(entry))
            {
                if (entry.InHistory) entry.InRun = false;
                else Enqueue(entry);
            }
        }
        else entry.InRun = false;
        entry.Changed();
        _ = LoadThumbAsync(entry);
    }

    // Anything that says this may not be the song asked for. The card still downloads: this only points at what to look at.
    string? CheckMatch(QueueItem item)
    {
        if (!item.IsSearch || item.ManualPick || item.ReviewDismissed || item.Info == null) return null;
        if (item.ExpectedSeconds is { } wanted && item.Info.Num("duration") is { } length && Math.Abs(length - wanted) > Math.Max(15, wanted * 0.1))
            return Loc.T("check.length", Format.Duration(wanted), Format.Duration(length));
        if (!MusicSearch.Matches(item.Query!, item.Info)) return Loc.T("check.title");
        if (MusicSearch.IsAmbiguous(item.Query!, item.Alternatives)) return Loc.T("check.ambiguous");
        if (WantsOriginal(item) && !item.IsOriginal) return Loc.T("check.nooriginal");
        return null;
    }

    public void AcceptMatch(QueueItem item)
    {
        item.ReviewDismissed = true;
        item.ReviewReason = null;
        item.Changed();
        NotifySummary();
    }

    // Many duplicates in a long list would flash a toast each; they are counted and reported once.
    void NoteDuplicates(int count, string? title)
    {
        duplicateCount += count;
        duplicateTitle = duplicateCount == 1 ? title : null;
        duplicateTimer?.Dispose();
        duplicateTimer = new Timer(_ => Post(() =>
        {
            if (duplicateCount == 0) return;
            Say(duplicateTitle != null ? Loc.T("toast.duplicate", duplicateTitle) : Loc.T("toast.duplicates", duplicateCount), ToastKind.Info);
            duplicateCount = 0;
            duplicateTitle = null;
        }), null, 1200, Timeout.Infinite);
    }

    // ---------- Original tracks ----------

    // Audio from a music video can carry its intro, dialogue and sound effects, so audio downloads prefer the studio
    // recording. Video downloads never do: an art track is a still picture.
    bool WantsOriginal(QueueItem item) => item.AudioMode && prefs.OriginalTracks && !item.ManualPick && (item.IsSearch || IsYouTube(item));

    static bool IsYouTube(QueueItem item) => item.Extractor.StartsWith("Youtube", StringComparison.OrdinalIgnoreCase);

    bool NeedsTrackLookup(QueueItem item)
    {
        if (!WantsOriginal(item) || item.TrackChecked || item.IsOriginal) return false;
        // A link to another site is exactly what the person asked for: only YouTube has a clip and a separate track.
        return item.IsSearch || (IsYouTube(item) && MusicSearch.QueryForClip(item.Info) != null);
    }

    void QueueTrackLookup(QueueItem item)
    {
        if (item.State == ItemState.Queued) { item.StartWhenReady = true; item.InRun = true; }
        item.State = ItemState.Fetching;
        item.Indeterminate = false;
        item.StatusText = Loc.T("status.searchingtrack");
        item.Changed();
        if (!item.TrackQueued) { item.TrackQueued = true; trackQueue.Enqueue(item); }
        if (!trackWorker) { trackWorker = true; _ = RunTrackLookupsAsync(); }
    }

    // One request at a time with a pause between them: searching is never what limits a long list.
    async Task RunTrackLookupsAsync()
    {
        try
        {
            while (trackQueue.Count > 0 && !closing)
            {
                if (DateTime.Now < fetchPausedUntil) await Task.Delay(fetchPausedUntil - DateTime.Now);
                QueueItem item = trackQueue.Dequeue();
                item.TrackQueued = false;
                if (item.Removed) continue;
                string query = item.Query ?? MusicSearch.QueryForClip(item.Info) ?? item.Title;
                List<Dictionary<string, object?>>? results = null;
                try
                {
                    // An export's ISRC names the exact recording. It is only trusted if the result is also the song by name.
                    if (item.Isrc != null)
                    {
                        var exact = (await backend.SearchMusicAsync(item.Isrc, 3, prefs.Proxy)).Where(r => MusicSearch.Matches(query, r)).ToList();
                        if (exact.Count > 0) results = exact;
                    }
                    results ??= await backend.SearchMusicAsync(query, SearchResults + 2, prefs.Proxy);
                    musicFailures = 0;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    Write(item, "YouTube Music search failed: " + ex.Message);
                    // Cards fall back to ordinary search, so nothing fails visibly; the person should still know why they got videos.
                    if (++musicFailures >= 3 && !musicDownShown && !Engine.IsThrottled(ex.Message))
                    {
                        musicDownShown = true;
                        Say(Loc.T("toast.music.down"), ToastKind.Warning);
                    }
                    if (Engine.IsThrottled(ex.Message))
                    {
                        PauseLookups();
                        item.TrackQueued = true;
                        trackQueue.Enqueue(item);
                        continue;
                    }
                }
                if (!item.Removed) CompleteTrackLookup(item, results, query);
                NotifySummary();
                Pump();
                if (MusicDelayMilliseconds > 0) await Task.Delay(MusicDelayMilliseconds);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Write(null, "Track lookups stopped: " + ex.Message); }
        finally
        {
            trackWorker = false;
            // A card queued between the last check and here must not wait forever.
            if (trackQueue.Count > 0 && !closing) { trackWorker = true; _ = RunTrackLookupsAsync(); }
        }
    }

    void CompleteTrackLookup(QueueItem item, List<Dictionary<string, object?>>? results, string query)
    {
        item.TrackChecked = true;
        var best = results != null ? MusicSearch.BestMatch(query, results, item.ExpectedSeconds) : null;
        if (best == null)
        {
            Write(item, "No original track found: " + query);
            if (item.ClipInfo != null) { ShowSource(item, false); FinishReady(item); }
            else if (!item.RegularTried) QueueFetch(item, true); // a search card has nothing yet
            else if (!string.IsNullOrEmpty(item.LastErrors)) FailLookup(item, Engine.FriendlyError(item.LastErrors, Auth.SignedIn), item.LastErrors); // not "nothing found" if it could not look
            else FailLookup(item, Loc.T("error.notfound"), "");
            return;
        }
        string key = "Youtube/" + best.Str("id");
        if (byVideo.TryGetValue(key, out QueueItem? owner) && owner != item && !owner.Removed && Key(owner) == key && Items.Contains(owner))
        {
            MergeLists(owner, item);
            item.Removed = true;
            Items.Remove(item);
            NoteDuplicates(1, best.Str("title"));
            return;
        }
        // The best match goes first so "other versions" lists the remaining ones after it.
        item.TrackInfo = best;
        item.TrackAlternatives = results!.OrderByDescending(r => ReferenceEquals(r, best)).ToList();
        if (item.ClipInfo != null && !item.IsSearch) item.TrackAlternatives.Add(item.ClipInfo); // the pasted clip stays one click away
        // Switched to video while this lookup ran: an art track is a still picture, so look for the video instead.
        if (!WantsOriginal(item) && item.ClipInfo == null && !item.RegularTried) { QueueFetch(item, true); return; }
        // A search that found nothing else shows the track even in video mode.
        if (WantsOriginal(item) || item.ClipInfo == null) ShowSource(item, true);
        FinishReady(item);
    }

    // Puts the original track or the ordinary video on the card.
    void ShowSource(QueueItem item, bool track)
    {
        string? label;
        if (track) label = "";
        else if (item.IsSearch) label = Loc.T(WantsOriginal(item) ? "card.nooriginal" : "card.foundby", item.Query);
        else label = item.ClipPlaylist;
        ApplyInfo(item, (track ? item.TrackInfo : item.ClipInfo)!, label);
        item.Alternatives = track ? item.TrackAlternatives : item.ClipAlternatives;
        item.ReviewReason = CheckMatch(item);
        Claim(item);
        item.Changed();
        _ = LoadThumbAsync(item);
    }

    // After a mode change: switches between track and video, looking up whichever is not known yet.
    void ApplySource(QueueItem item)
    {
        if (item.ManualPick || !item.Fetched || !item.CanEdit || item.State == ItemState.Fetching) return;
        bool want = WantsOriginal(item);
        if (want == item.IsOriginal) return;
        if (want)
        {
            if (item.TrackInfo != null) ShowSource(item, true);
            else if (NeedsTrackLookup(item)) QueueTrackLookup(item);
        }
        else if (item.ClipInfo != null) ShowSource(item, false);
        else if (item.IsSearch)
        {
            if (item.State == ItemState.Queued) { item.StartWhenReady = true; item.InRun = true; }
            QueueFetch(item, true);
        }
    }

    public void ApplySources()
    {
        foreach (var item in Items.ToList()) ApplySource(item);
        NotifySummary();
    }

    public static void ApplyInfo(QueueItem item, Dictionary<string, object?> info, string? playlist)
    {
        item.Info = info;
        item.VideoId = info.Str("id");
        item.Extractor = info.Str("extractor_key") ?? info.Str("ie_key") ?? "";
        string? page = info.Str("webpage_url");
        if (page == null && info.Str("url") is { } url && url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) page = url;
        // YouTube Music results point at music.youtube.com; the same video on www.youtube.com downloads the same way.
        const string music = "https://music.youtube.com/watch";
        if (page != null && page.StartsWith(music, StringComparison.OrdinalIgnoreCase)) page = "https://www.youtube.com/watch" + page[music.Length..];
        item.WebpageUrl = page ?? (item.IsSearch ? null : item.SourceUrl);
        item.Title = info.Str("title") ?? item.Title;
        item.Channel = info.Str("uploader") ?? info.Str("channel") ?? "";
        item.Album = info.Str("album") ?? "";
        item.IsOriginal = info.Flag("is_original") || item.Channel.EndsWith(" - Topic", StringComparison.Ordinal);
        if (item.Channel.EndsWith(" - Topic", StringComparison.Ordinal)) item.Channel = item.Channel[..^8];
        double? duration = info.Num("duration");
        item.Duration = duration is > 0 ? Format.Duration(duration.Value) : "";
        double? views = info.Num("view_count");
        item.Views = views.HasValue ? Format.Views(views.Value) : "";
        double? height = info.Num("height");
        item.Badge = height.HasValue ? Format.QualityBadge((int)height.Value) : "";
        if (info.Str("live_status") == "is_live") item.Badge = Loc.T("card.live");
        bool youtube = item.Extractor.StartsWith("Youtube", StringComparison.OrdinalIgnoreCase);
        item.Site = !youtube ? HostName(item.WebpageUrl) : info.Str("source") == "music" ? "YouTube Music" : "YouTube";
        item.Playlist = playlist ?? "";
        item.ThumbUrl = PickThumbnail(info, youtube ? item.VideoId : null);
    }

    static string HostName(string? url)
    {
        if (url == null || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)) return "";
        string host = uri.Host;
        foreach (string prefix in new[] { "www.", "m." }) if (host.StartsWith(prefix, StringComparison.Ordinal)) host = host[prefix.Length..];
        return host;
    }

    static string? PickThumbnail(Dictionary<string, object?> info, string? youtubeId)
    {
        // YouTube's 320x180 frame is always 16:9 and exists for every video, unlike the large custom ones.
        if (!string.IsNullOrEmpty(youtubeId)) return "https://i.ytimg.com/vi/" + youtubeId + "/mqdefault.jpg";
        if (info.Str("thumbnail") is { } single) return single;
        string? best = null;
        double bestScore = double.MaxValue;
        foreach (var entry in (info.Raw("thumbnails") as List<object?> ?? new()).OfType<Dictionary<string, object?>>())
        {
            string? url = entry.Str("url");
            if (url == null) continue;
            double score = entry.Num("width") is { } width ? Math.Abs(width - 480) : 10000;
            if (score <= bestScore) { bestScore = score; best = url; }
        }
        return best;
    }

    void Forget(QueueItem item)
    {
        if (item.VideoId != null && byVideo.TryGetValue(Key(item), out QueueItem? owner) && owner == item) byVideo.Remove(Key(item));
    }

    public async Task LoadThumbAsync(QueueItem item)
    {
        string? url = item.ThumbUrl;
        if (string.IsNullOrEmpty(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;
        await thumbGate.WaitAsync();
        try
        {
            if (item.Removed || item.ThumbUrl != url) return;
            byte[]? data = await backend.ThumbnailAsync(url, prefs.Proxy);
            if (data == null || item.ThumbUrl != url) return;
            item.ThumbBytes = data;
            item.Changed();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Write(item, "Preview did not load: " + ex.Message); }
        finally { thumbGate.Release(); }
    }

    // ---------- Files already in the folder ----------

    // The listing is reused for a few seconds: a long list finishes hundreds of lookups in a row.
    Dictionary<string, List<string>> ScanFolder()
    {
        string folder = prefs.Folder;
        if (folderCache != null && folderCacheKey == folder && (DateTime.Now - folderCacheTime).TotalSeconds < 5) return folderCache;
        var map = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var names = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        folderNames = names;
        folderCache = map;
        folderCacheKey = folder;
        folderCacheTime = DateTime.Now;
        try
        {
            if (!Path.IsPathRooted(folder) || !Directory.Exists(folder)) return map;
            foreach (string file in Directory.EnumerateFiles(folder))
            {
                string name = Path.GetFileName(file);
                if (!Engine.MediaExtensions.Contains(Path.GetExtension(name).TrimStart('.').ToLowerInvariant()) || new FileInfo(file).Length == 0) continue;
                string key = Engine.FileNameKey(name);
                if (!names.TryGetValue(key, out var sameName)) names[key] = sameName = new();
                sameName.Add(file);
                var match = Engine.IdInName().Match(name);
                if (!match.Success) continue;
                if (!map.TryGetValue(match.Groups[1].Value, out var list)) map[match.Groups[1].Value] = list = new();
                list.Add(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return map;
    }

    string? ExistingFor(QueueItem item, Dictionary<string, List<string>> files)
    {
        if (item.FileKey != null && files.TryGetValue(item.FileKey, out var list) && Engine.MatchExisting(list, item.AudioMode, prefs.AudioFormat, prefs.Container) is { } file) return file;
        // A song someone already has under its plain name, in any audio format, need not come again.
        if (item.Section != null || NameOf(item) is not { } name || !folderNames.TryGetValue(name, out var same)) return null;
        return Engine.MatchExisting(same, item.AudioMode, "best", prefs.Container);
    }

    string? NameOf(QueueItem item)
    {
        string? text = item.Query ?? (item.IsOriginal ? item.Channel + " - " + item.Title : prefs.CleanNames ? item.Title : null);
        return text != null && MusicSearch.Words(text).Count >= 2 ? Engine.NameKey(text) : null;
    }

    // Audio and video are different files: having one does not stand for the other.
    static string HistoryKey(QueueItem item) => (item.AudioMode ? "audio/" : "video/") + item.Extractor + "/" + item.FileKey;

    bool AlreadyHave(QueueItem item) => DownloadHistory.Contains(HistoryKey(item)) || ExistingFor(item, ScanFolder()) != null;

    bool MarkIfExists(QueueItem item)
    {
        if (item.ForceDownload || item.ExistingPath == null || !File.Exists(item.ExistingPath)) return false;
        item.State = ItemState.Done;
        item.FilePath = item.ExistingPath;
        if (prefs.Lyrics && item.AudioMode) _ = SaveLyricsAsync(item);
        item.Progress = 100;
        item.Indeterminate = false;
        item.InRun = false;
        item.SpeedText = "";
        item.StatusText = Loc.T("status.exists", Format.Bytes(new FileInfo(item.FilePath).Length), Path.GetExtension(item.FilePath).TrimStart('.').ToUpperInvariant());
        item.Changed();
        Write(item, "Skipped, file already exists: " + item.FilePath);
        return true;
    }

    /// <summary>Updates format labels and the "already in the folder" mark; after a lookup only the new cards are touched.</summary>
    public void RefreshLabels(IEnumerable<QueueItem>? only = null)
    {
        if (Items.Count == 0) return;
        string[] qualities = { "quality.max", "quality.4k", "quality.2k", "quality.1080", "quality.720", "quality.480", "quality.360" };
        int[] heights = { 0, 2160, 1440, 1080, 720, 480, 360 };
        string Video(int height)
        {
            int at = Array.IndexOf(heights, height);
            return Loc.T("mode.video", Loc.T(qualities[at < 0 ? 3 : at]), prefs.Container.ToUpperInvariant());
        }
        string audio = Loc.T("mode.audio", prefs.AudioFormat == "best" ? Loc.T("format.source") : prefs.AudioFormat.ToUpperInvariant());
        if (only == null) folderCache = null; // a full refresh (start, new folder, new format) always looks again
        var files = ScanFolder();
        foreach (var item in only ?? Items)
        {
            if (item.IsActive || item.State == ItemState.Done) continue;
            item.ModeText = item.AudioMode ? audio : Video(item.HeightOverride ?? prefs.Height);
            item.ExistingPath = ExistingFor(item, files);
            item.InHistory = prefs.SkipDownloaded && item.VideoId != null && DownloadHistory.Contains(HistoryKey(item));
            item.Changed();
        }
    }

    public void ModeChanged(bool audio)
    {
        foreach (var item in Items) if (!item.ModeLocked && item.CanEdit) item.AudioMode = audio;
        RefreshLabels();
        ApplySources();
    }

    public void SetItemMode(QueueItem item, bool audio)
    {
        if (!item.CanEdit) return;
        item.AudioMode = audio;
        item.ModeLocked = true;
        RefreshLabels();
        ApplySource(item);
        NotifySummary();
    }

    /// <summary>This one as video in this quality, whatever the settings say for the others.</summary>
    public void SetItemQuality(QueueItem item, int height)
    {
        if (!item.CanEdit) return;
        item.AudioMode = false;
        item.ModeLocked = true;
        item.HeightOverride = height;
        // Choosing a quality on a video that is already there means wanting it in that quality.
        item.ForceDownload = item.ExistingPath != null;
        RefreshLabels();
        ApplySource(item);
        NotifySummary();
    }

    /// <summary>What did not work, one per line, to try elsewhere or keep for later.</summary>
    public string FailedList() => string.Join(Environment.NewLine, Items.Where(i => i.State == ItemState.Failed).Select(i => i.Query ?? i.WebpageUrl ?? i.SourceUrl));

    public void SetAllModes(bool audio)
    {
        foreach (var item in Items.Where(i => i.CanEdit)) { item.AudioMode = audio; item.ModeLocked = true; }
        RefreshLabels();
        ApplySources();
    }

    // ---------- Downloads ----------

    static string ValidateProxy(string proxy)
    {
        if (proxy.Length > 0 && (!Uri.TryCreate(proxy, UriKind.Absolute, out Uri? parsed) || parsed.Scheme is not ("http" or "https" or "socks4" or "socks5" or "socks5h") || string.IsNullOrEmpty(parsed.Host)))
            throw new ArgumentException(Loc.T("error.proxy"));
        return proxy;
    }

    DownloadOptions BuildOptions(QueueItem item)
    {
        string folder = prefs.Folder;
        if (folder.Length == 0 || !Path.IsPathRooted(folder)) throw new ArgumentException(Loc.T("error.nofolder"));
        folder = Path.GetFullPath(folder);
        if (folder.Length > 180) throw new ArgumentException(Loc.T("error.longfolder"));
        Directory.CreateDirectory(folder);
        return new DownloadOptions
        {
            Url = item.WebpageUrl ?? item.SourceUrl,
            Folder = folder,
            Audio = item.AudioMode,
            Height = item.HeightOverride ?? prefs.Height,
            Container = prefs.Container,
            AudioFormat = prefs.AudioFormat,
            EmbedThumbnail = prefs.EmbedThumbnail,
            Subtitles = prefs.Subtitles,
            Language = Engine.LanguageOf(item.Info.Str("language")),
            SponsorBlock = prefs.SponsorBlock && item.Section == null,
            Original = item.IsOriginal,
            CutOfftopic = item.AudioMode && prefs.OriginalTracks && !item.IsOriginal && IsYouTube(item) && item.Section == null,
            Section = item.Section,
            Overwrite = item.ForceDownload,
            CleanNames = prefs.CleanNames,
            Proxy = ValidateProxy(prefs.Proxy),
            KeepSource = item.AudioMode && item.VideoId != null && ScanFolder().ContainsKey(item.VideoId)
        };
    }

    bool ValidateForStart()
    {
        try
        {
            DownloadOptions probe = BuildOptions(new QueueItem { SourceUrl = "https://example.com" });
            string check = Path.Combine(probe.Folder, ".write-check-" + Guid.NewGuid().ToString("N"));
            using (File.Create(check)) { }
            File.Delete(check);
            prefs.Save();
            return true;
        }
        catch (UnauthorizedAccessException) { Say(Loc.T("error.readonlyfolder"), ToastKind.Error); }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException) { Say(ex.Message, ToastKind.Error); }
        return false;
    }

    public void StartAll()
    {
        if (!ValidateForStart()) return;
        RefreshLabels();
        int count = 0, skipped = 0;
        foreach (var item in Items.ToList())
        {
            if (item.State == ItemState.Fetching) { item.StartWhenReady = true; item.InRun = true; count++; }
            else if (item.State is ItemState.Failed or ItemState.Cancelled && !item.Fetched)
            {
                item.StartWhenReady = true;
                item.InRun = true;
                Refetch(item);
                count++;
            }
            else if (item.State is ItemState.Ready or ItemState.Failed or ItemState.Cancelled)
            {
                item.RepairTried = false;
                if (MarkIfExists(item) || item.InHistory) skipped++;
                else { Enqueue(item); count++; }
            }
        }
        if (skipped > 0)
        {
            Say(Loc.T("toast.skipped", skipped), ToastKind.Info);
            if (count == 0) WritePlaylists();
        }
        else if (count == 0) Say(Loc.T(Items.Count == 0 ? "toast.empty" : "toast.alldone"), ToastKind.Info);
        Pump();
    }

    public void StartOne(QueueItem item)
    {
        if (!ValidateForStart()) return;
        RefreshLabels();
        if (MarkIfExists(item)) { Say(Loc.T("toast.exists"), ToastKind.Info); NotifySummary(); return; }
        item.RepairTried = false;
        Enqueue(item);
        Pump();
    }

    public void RetryFailed() => RetryMany(Items.Where(i => i.State == ItemState.Failed).ToList());

    /// <summary>Many at once: checking the folder and refreshing every card once for each of a thousand cards would freeze the window.</summary>
    public void RetryMany(IReadOnlyCollection<QueueItem> items)
    {
        if (items.Count == 0 || !ValidateForStart()) return;
        RefreshLabels();
        foreach (var item in items)
        {
            item.RepairTried = false;
            if (!item.Fetched) Refetch(item);
            else if (!MarkIfExists(item)) Enqueue(item);
        }
        Pump();
    }

    /// <summary>Downloads it once more, replacing the file that is there.</summary>
    public void DownloadAgain(QueueItem item)
    {
        if (item.IsActive || !item.Fetched) return;
        item.ForceDownload = true;
        item.FilePath = null;
        item.State = ItemState.Ready;
        item.StatusText = Loc.T("status.ready");
        StartOne(item);
    }

    static void Enqueue(QueueItem item)
    {
        item.State = ItemState.Queued;
        item.InRun = true;
        item.Progress = 0;
        item.Indeterminate = false;
        item.StatusText = Loc.T("status.queued");
        item.SpeedText = "";
        item.Changed();
    }

    public void Refetch(QueueItem item)
    {
        item.TrackChecked = false;
        item.RegularTried = false;
        BeginLookup(item, true);
    }

    public void Retry(QueueItem item)
    {
        if (!item.Fetched) { Refetch(item); return; }
        StartOne(item);
    }

    public void Cancel(QueueItem item)
    {
        if (item.State == ItemState.Queued)
        {
            item.State = ItemState.Ready;
            item.InRun = false;
            item.StatusText = Loc.T("status.ready");
            item.Changed();
            Pump();
        }
        else if (item.IsActive && item.Runner != null)
        {
            item.StatusText = Loc.T("status.stopping");
            item.Changed();
            item.Runner.Cancel();
        }
    }

    public void Remove(QueueItem item)
    {
        if (item.IsActive || item.State == ItemState.Queued) return;
        item.Removed = true;
        // A search batch is shared by several cards; it is stopped only when its last card goes.
        if (item.State == ItemState.Fetching && item.Runner != null && Items.Count(i => i.Runner == item.Runner) <= 1) item.Runner.Cancel();
        Forget(item);
        Items.Remove(item);
        NotifySummary();
        CheckRunComplete();
    }

    public void ClearDone()
    {
        foreach (var item in Items.Where(i => i.State == ItemState.Done).ToList()) { Forget(item); Items.Remove(item); }
        NotifySummary();
    }

    public void ClearAll()
    {
        foreach (var item in Items.Where(i => !i.IsActive && i.State != ItemState.Queued).ToList()) Remove(item);
        if (Items.Count > 0) Say(Loc.T("toast.stillactive"), ToastKind.Info);
    }

    public void StopAll()
    {
        foreach (var item in Items.ToList())
        {
            if (item.State == ItemState.Queued)
            {
                item.State = ItemState.Ready;
                item.InRun = false;
                item.StatusText = Loc.T("status.ready");
                item.Changed();
            }
            else if (item.State == ItemState.Fetching) { item.StartWhenReady = false; item.InRun = false; }
            else if (item.IsActive) Cancel(item);
            else item.InRun = false;
        }
        NotifySummary();
    }

    public void Pump()
    {
        if (closing || hold) return;
        if (repairing) { ContinueRepair(); NotifySummary(); return; }
        int limit = Math.Clamp(prefs.Parallel, 1, 4);
        int active = Items.Count(i => i.IsActive);
        if (active < limit)
        {
            foreach (var item in Items.Where(i => i.State == ItemState.Queued).ToList())
            {
                if (active >= limit) break;
                if (item.State != ItemState.Queued) continue;
                _ = RunDownloadAsync(item);
                if (item.IsActive) active++;
            }
        }
        NotifySummary();
        CheckRunComplete();
    }

    async Task RunDownloadAsync(QueueItem item)
    {
        DownloadOptions options;
        try { options = BuildOptions(item); }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException)
        {
            item.State = ItemState.Failed;
            item.StatusText = ex.Message;
            item.Changed();
            return;
        }

        item.State = ItemState.Downloading;
        item.Indeterminate = true;
        item.Progress = 0;
        item.Speed = 0;
        item.StatusText = Loc.T("status.connecting");
        item.SpeedText = "";
        item.FilePath = null;
        item.AlreadyHad = false;
        item.Retrying = false;
        item.Errors.Clear();
        item.StartedAt = DateTime.Now;
        item.Changed();
        Write(item, "Downloading " + (item.AudioMode ? "audio" : "video") + ": " + options.Url);

        var runner = new ProcessRunner();
        item.Runner = runner;
        RunResult? result = null;
        string? failure = null;
        try
        {
            result = await backend.DownloadAsync(options, runner, line => Post(() => OnDownloadLine(item, line)));
            await FlushAsync();
            // Cutting is an extra. If the SponsorBlock service is out of reach, the file is still wanted.
            if (result is { Cancelled: false } && item.FilePath == null && (options.SponsorBlock || options.CutOfftopic)
                && item.Errors.ToString().Contains("sponsorblock", StringComparison.OrdinalIgnoreCase))
            {
                Write(item, "SponsorBlock is unavailable; downloading without cutting");
                options.SponsorBlock = options.CutOfftopic = false;
                item.Errors.Clear();
                item.AlreadyHad = false;
                item.Retrying = true;
                result = await backend.DownloadAsync(options, runner, line => Post(() => OnDownloadLine(item, line)));
                await FlushAsync();
            }
            if (result is { Cancelled: false } && item.FilePath == null && Engine.CookiesUnreadable(item.Errors.ToString()) && (SuspendBrowser() || Auth.Suspended))
            {
                item.Errors.Clear();
                item.AlreadyHad = false;
                item.Retrying = true;
                result = await backend.DownloadAsync(options, runner, line => Post(() => OnDownloadLine(item, line)));
                await FlushAsync();
            }
            if (result is { Cancelled: false } && item.FilePath == null && Engine.PartRefused(options, item.Errors.ToString()))
            {
                Write(item, "The site turned the part away; trying once more");
                item.Errors.Clear();
                item.AlreadyHad = false;
                item.Retrying = true;
                result = await backend.DownloadAsync(options, runner, line => Post(() => OnDownloadLine(item, line)));
                await FlushAsync();
            }
            if (result is { Cancelled: false } && item.FilePath == null && Engine.CoverDidNotFit(options, item.Errors.ToString()))
            {
                Write(item, "This format cannot hold a cover; keeping it without one");
                options.EmbedThumbnail = false;
                item.Errors.Clear();
                item.AlreadyHad = false;
                item.Retrying = true;
                result = await backend.DownloadAsync(options, runner, line => Post(() => OnDownloadLine(item, line)));
                await FlushAsync();
            }
            if (result is { Cancelled: false } && item.FilePath == null && Engine.DidNotFit(options, item.Errors.ToString()))
            {
                Write(item, "This format does not fit " + options.Container + "; keeping it as mkv");
                Engine.FallBackToMkv(options);
                item.Errors.Clear();
                item.AlreadyHad = false;
                item.Retrying = true;
                result = await backend.DownloadAsync(options, runner, line => Post(() => OnDownloadLine(item, line)));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { failure = ex.Message; }
        runner.Dispose();
        item.Runner = null;
        // Lines posted before the process ended run first, so the saved file is known by now.
        await FlushAsync();

        item.Speed = 0;
        item.Indeterminate = false;
        item.SpeedText = "";
        if (!string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath))
        {
            item.State = ItemState.Done;
            item.Progress = 100;
            string extension = Path.GetExtension(item.FilePath).TrimStart('.').ToUpperInvariant();
            string size = Format.Bytes(new FileInfo(item.FilePath).Length);
            item.StatusText = Loc.T(item.AlreadyHad ? "status.exists" : "status.done", size, extension);
            if (!item.AlreadyHad) item.SpeedText = Loc.T("status.took", Format.Duration((DateTime.Now - item.StartedAt).TotalSeconds));
            Write(item, "Saved: " + item.FilePath);
            folderCache = null;
            item.ForceDownload = false;
            item.PendingOutput = null;
            item.ExistingPath = null;
            Remember(item);
            if (prefs.Lyrics && item.AudioMode && !item.AlreadyHad) _ = SaveLyricsAsync(item);
        }
        else if (result is { Cancelled: true })
        {
            DropPendingOutput(item);
            item.State = ItemState.Cancelled;
            item.StatusText = Loc.T("status.stopped");
            Write(item, "Stopped");
        }
        else
        {
            string errors = item.Errors.ToString();
            if (failure == null && TryRepair(item, errors))
            {
                Enqueue(item);
                item.StatusText = Loc.T("status.repairing");
                item.Changed();
                Pump();
                return;
            }
            DropPendingOutput(item);
            // Everything still waiting would fail the same way, one card at a time.
            if (Engine.DiskFull(errors))
            {
                foreach (var waiting in Items.Where(i => i.State == ItemState.Queued).ToList())
                {
                    waiting.State = ItemState.Ready;
                    waiting.InRun = false;
                    waiting.StatusText = Loc.T("status.ready");
                    waiting.Changed();
                }
                Say(Loc.T("error.disk"), ToastKind.Error);
            }
            item.State = ItemState.Failed;
            item.NeedsLogin = failure == null && Engine.NeedsLogin(errors);
            item.StatusText = failure ?? Engine.FriendlyError(errors, Auth.SignedIn);
            if (item.NeedsLogin) HintLogin();
            Write(item, "Error: " + item.StatusText);
        }
        item.Changed();
        Pump();
    }

    // A converter writes straight to the final name. Stopped halfway, that would pass for a finished song later on.
    static void DropPendingOutput(QueueItem item)
    {
        if (item.PendingOutput == null) return;
        try { File.Delete(item.PendingOutput); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        item.PendingOutput = null;
    }

    // The clip's id goes in as well: a followed playlist lists clips, even where the track is what was saved.
    static void Remember(QueueItem item)
    {
        var keys = new List<string> { HistoryKey(item) };
        if (item.ClipInfo?.Str("id") is { } clip && item.Section == null) keys.Add((item.AudioMode ? "audio/" : "video/") + item.Extractor + "/" + clip);
        DownloadHistory.Add(keys);
    }

    async Task SaveLyricsAsync(QueueItem item)
    {
        string? file = item.FilePath;
        string? artist = item.Info.Str("artist") ?? (item.IsOriginal ? item.Channel : null), title = item.Info.Str("track") ?? (item.IsOriginal ? item.Title : null);
        if ((artist == null || title == null) && (item.Query ?? MusicSearch.QueryForClip(item.Info)) is { } name
            && System.Text.RegularExpressions.Regex.Split(name, @"\s+[-–—]\s+", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1)) is { Length: >= 2 } parts)
        {
            artist = parts[0].Trim();
            title = string.Join(" - ", parts.Skip(1)).Trim();
        }
        if (file == null || artist == null || title == null) return;
        artist = artist.Split(", ")[0]; // the database knows a duet under its first name
        string path = Path.ChangeExtension(file, ".lrc");
        if (File.Exists(path)) return;
        try
        {
            string? text = await backend.LyricsAsync(artist, title, item.Info.Num("duration"), prefs.Proxy);
            if (text == null) { Write(item, "No lyrics found"); return; }
            await File.WriteAllTextAsync(path, text);
            Write(item, "Lyrics saved: " + path);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Write(item, "Lyrics did not load: " + ex.Message); }
    }

    // ---------- Repairing itself ----------

    // Sites change faster than yt-dlp's copy here. A failure of that kind waits while yt-dlp updates, then runs again once.
    bool TryRepair(QueueItem item, string errors)
    {
        if (item.RepairTried || !Engine.LooksOutdated(errors)) return false;
        item.RepairTried = true;
        Write(item, "This looks like an outdated yt-dlp; updating and trying again");
        if (repairing || DateTime.Now - lastRepair < TimeSpan.FromMinutes(30)) return true;
        repairing = true;
        Say(Loc.T("toast.repair"), ToastKind.Info);
        return true;
    }

    // Its file cannot be replaced while a copy runs, so what is running ends first.
    void ContinueRepair()
    {
        if (repairRunning || hold || fetchWorkers > 0 || Items.Any(i => i.IsActive)) return;
        repairRunning = true;
        _ = RunRepairAsync();
    }

    async Task RunRepairAsync()
    {
        ToolUpdate? update = null;
        try
        {
            update = await backend.UpdateToolAsync(prefs.Proxy, false, line => Post(() => Write(null, line)));
            // A fix often reaches the nightly builds days before a stable release, and the failure is already here.
            if (update is { Ok: true, Changed: false }) update = await backend.UpdateToolAsync(prefs.Proxy, true, line => Post(() => Write(null, line)));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { Write(null, "Update failed: " + ex.Message); }
        Write(null, update is { Ok: true } ? (update.Changed ? "yt-dlp updated" : "yt-dlp was already the newest") : "yt-dlp could not be updated");
        lastRepair = DateTime.Now;
        repairing = repairRunning = false;
        PumpFetch();
        Pump();
    }

    Task FlushAsync()
    {
        var done = new TaskCompletionSource();
        Post(() => done.SetResult());
        return done.Task;
    }

    void OnDownloadLine(QueueItem item, string line)
    {
        if (line.StartsWith("__PROGRESS__", StringComparison.Ordinal)) { OnProgress(item, line[12..].Split('|')); return; }
        if (line.StartsWith("__PP__", StringComparison.Ordinal)) { OnPostprocess(item, line[6..].Split('|')); return; }
        if (line.StartsWith("__FILE__", StringComparison.Ordinal)) { item.FilePath = line[8..].Trim(); return; }
        if (line.StartsWith("ERROR", StringComparison.Ordinal)) item.Errors.AppendLine(line);
        // Only the first try says what was there before: a retry finds what the first one downloaded.
        if (line.Contains("has already been downloaded", StringComparison.Ordinal) && !item.Retrying) item.AlreadyHad = true;
        const string converting = "[ExtractAudio] Destination: ";
        if (line.StartsWith(converting, StringComparison.Ordinal)) item.PendingOutput = line[converting.Length..].Trim();
        else if (line.Contains("original file", StringComparison.Ordinal)) item.PendingOutput = null; // yt-dlp moves on only once it is complete
        Write(item, line);
    }

    static double Field(string[] fields, int index) =>
        index < fields.Length && double.TryParse(fields[index], NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? value : -1;

    void OnProgress(QueueItem item, string[] f)
    {
        if (item.Runner == null) return; // a late line from a download already concluded
        string status = f.Length > 0 ? f[0] : "";
        double done = Field(f, 1), total = Field(f, 2), speed = Field(f, 4), eta = Field(f, 5);
        if (total <= 0) total = Field(f, 3);
        string vcodec = f.Length > 6 ? f[6] : "", acodec = f.Length > 7 ? f[7] : "";
        string what = Loc.T(item.AudioMode ? "what.audio" : vcodec == "none" ? "what.sound" : acodec == "none" ? "what.video" : "what.file");
        if (status == "finished")
        {
            item.State = ItemState.Processing;
            item.Indeterminate = true;
            item.Speed = 0;
            item.SpeedText = "";
            item.StatusText = Loc.T("status.processing");
        }
        else
        {
            item.State = ItemState.Downloading;
            var parts = new List<string>();
            if (total > 0)
            {
                item.Indeterminate = false;
                item.Progress = Math.Clamp(done * 100 / total, 0, 100);
                item.StatusText = Loc.T("status.downloading", what, item.Progress.ToString("0", CultureInfo.InvariantCulture));
                parts.Add(Loc.T("status.of", Format.Bytes(Math.Max(0, done)), Format.Bytes(total)));
            }
            else
            {
                item.Indeterminate = true;
                item.StatusText = done > 0 ? Loc.T("status.downloadingsize", what, Format.Bytes(done)) : Loc.T("status.downloadingplain", what);
            }
            item.Speed = Math.Max(0, speed);
            if (speed > 0) parts.Add(Format.Speed(speed));
            if (eta > 0) parts.Add(Loc.T("status.eta", Format.Duration(eta)));
            item.SpeedText = string.Join("  ·  ", parts);
        }
        item.Changed();
        NotifySummary();
    }

    void OnPostprocess(QueueItem item, string[] f)
    {
        string name = f.Length > 0 ? f[0] : "";
        if (f.Length > 1 && f[1] == "finished") return;
        string key = "status.processing";
        if (name.Contains("Merger", StringComparison.Ordinal)) key = "pp.merge";
        else if (name.Contains("ExtractAudio", StringComparison.Ordinal)) key = "pp.audio";
        else if (name.Contains("EmbedThumbnail", StringComparison.Ordinal) || name.Contains("ThumbnailsConvertor", StringComparison.Ordinal)) key = "pp.cover";
        else if (name.Contains("Metadata", StringComparison.Ordinal)) key = "pp.tags";
        else if (name.Contains("SponsorBlock", StringComparison.Ordinal) || name.Contains("ModifyChapters", StringComparison.Ordinal)) key = "pp.cut";
        else if (name.Contains("Subtitle", StringComparison.Ordinal)) key = "pp.subs";
        else if (name.Contains("Remux", StringComparison.Ordinal) || name.Contains("Fixup", StringComparison.Ordinal)) key = "pp.pack";
        else if (name.Contains("MoveFiles", StringComparison.Ordinal)) key = "pp.move";
        item.State = ItemState.Processing;
        item.Indeterminate = true;
        item.Speed = 0;
        item.SpeedText = "";
        item.StatusText = Loc.T(key);
        item.Changed();
        NotifySummary();
    }

    void CheckRunComplete()
    {
        var run = Items.Where(i => i.InRun).ToList();
        if (run.Count == 0) return;
        if (run.Any(i => i.State == ItemState.Queued || i.IsActive || (i.State == ItemState.Fetching && (i.StartWhenReady || i.FetchQueued || i.TrackQueued)))) return;
        foreach (var item in run) item.InRun = false;
        WritePlaylists();
        int done = run.Count(i => i.State == ItemState.Done), failed = run.Count(i => i.State == ItemState.Failed), stopped = run.Count(i => i.State == ItemState.Cancelled);
        if (done == 0 && failed == 0)
        {
            if (stopped > 0) Say(Loc.T("toast.stopped"), ToastKind.Warning);
            return;
        }
        if (failed == 0) Say(done == 1 ? Loc.T("toast.done.one") : Loc.T("toast.done.many", done), ToastKind.Success);
        else Say(Loc.T("toast.done.errors", done, failed), ToastKind.Warning);
        RunCompleted?.Invoke(done, failed);
    }

    // Only named lists get a file: an import, or a playlist link.
    void WritePlaylists()
    {
        if (!prefs.WritePlaylists) return;
        foreach (string name in Items.SelectMany(i => i.Lists).Distinct(StringComparer.Ordinal).ToList())
        {
            var songs = Items.Where(i => i.Lists.Contains(name) && i.State == ItemState.Done && i.FilePath != null && File.Exists(i.FilePath))
                .Select(i => new PlaylistFile.Song(i.FilePath!, i.Title, i.Info.Num("duration"))).ToList();
            if (songs.Count == 0) continue;
            try { Write(null, "Playlist saved: " + PlaylistFile.Write(prefs.Folder, name, songs)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { Write(null, "Playlist not saved: " + ex.Message); }
        }
    }

    public QueueSummary Summary()
    {
        int total = Items.Count, done = 0, active = 0, queued = 0, fetching = 0, failed = 0, startable = 0, review = 0;
        double speed = 0, sum = 0;
        int runTotal = 0, runDone = 0;
        foreach (var item in Items)
        {
            switch (item.State)
            {
                case ItemState.Done: done++; break;
                case ItemState.Queued: queued++; break;
                case ItemState.Fetching: fetching++; startable++; break;
                case ItemState.Failed: failed++; startable++; break;
                case ItemState.Ready: case ItemState.Cancelled: startable++; break;
                default: active++; speed += item.Speed; break;
            }
            if (item.ShowReview) review++;
            if (!item.InRun) continue;
            runTotal++;
            if (item.State == ItemState.Done) runDone++;
            sum += item.State is ItemState.Done or ItemState.Failed or ItemState.Cancelled ? 100 : item.IsActive ? item.Progress : 0;
        }
        string? paused = DateTime.Now < fetchPausedUntil ? fetchPausedUntil.ToString("HH:mm:ss", CultureInfo.InvariantCulture) : null;
        return new QueueSummary(total, done, active, queued, fetching, failed, startable, speed, runTotal > 0 ? sum / (runTotal * 100.0) : 0, runDone, runTotal, paused, review);
    }

    void NotifySummary() => SummaryChanged?.Invoke();

    // ---------- Version picker ----------

    public bool PrefersTracks(QueueItem item) => item.IsOriginal || WantsOriginal(item);

    public async Task<List<Dictionary<string, object?>>> SearchVersionsAsync(QueueItem target, string query, bool music, ProcessRunner runner)
    {
        List<Dictionary<string, object?>> entries;
        if (music) entries = await backend.SearchMusicAsync(query, 12, prefs.Proxy);
        else
        {
            LookupResult result = await backend.LookupAsync(new[] { "ytsearch10:" + query }, 10, prefs.Proxy, runner, line => Post(() => Write(null, line)));
            if (result.Cancelled) throw new OperationCanceledException();
            if (result.Entries.Count == 0 && result.Errors.Length > 0) throw new InvalidOperationException(Engine.FriendlyError(result.Errors, Auth.SignedIn));
            entries = result.Entries;
        }
        if (music) target.TrackAlternatives = entries; else target.ClipAlternatives = entries;
        target.Alternatives = entries;
        if (target.IsSearch) target.Query = query;
        target.Changed();
        return entries;
    }

    /// <summary>Puts a chosen version on the card. A person's choice is not switched back automatically.</summary>
    public bool Pick(QueueItem target, QueueItem candidate)
    {
        if (!target.CanEdit || target.State == ItemState.Fetching || candidate.Info == null) return false;
        if (candidate.VideoId != null && candidate.VideoId == target.VideoId) return true;
        if (IsTaken(candidate, Key(candidate))) { Say(Loc.T("toast.versiontaken"), ToastKind.Info); return false; }
        Forget(target);
        ApplyInfo(target, candidate.Info, candidate.IsOriginal ? "" : Loc.T("card.chosen", target.Query ?? target.Title));
        target.ThumbBytes = candidate.ThumbBytes;
        if (target.ThumbBytes == null) _ = LoadThumbAsync(target); // the candidate's preview had not arrived yet
        target.Fetched = true;
        target.NeedsLogin = false;
        target.FilePath = null;
        target.ManualPick = true;
        target.ReviewReason = null;
        if (target.State != ItemState.Queued)
        {
            target.InRun = false;
            target.State = ItemState.Ready;
            target.StatusText = Loc.T("status.ready");
            target.Indeterminate = false;
            target.Progress = 0;
        }
        Claim(target);
        target.Changed();
        Write(target, "Another version chosen: " + target.WebpageUrl);
        RefreshLabels();
        NotifySummary();
        Say(Loc.T("toast.versionset", target.Title), ToastKind.Success);
        return true;
    }

    public static QueueItem MakeCandidate(Dictionary<string, object?> info, QueueItem target)
    {
        var candidate = new QueueItem { State = ItemState.Ready };
        ApplyInfo(candidate, info, null);
        candidate.Chosen = candidate.VideoId != null && candidate.VideoId == target.VideoId;
        return candidate;
    }

    // ---------- Sign-in ----------

    /// <summary>After signing in: cards that were waiting for it get another chance.</summary>
    public void SignedIn()
    {
        loginHintShown = false;
        int retried = 0;
        foreach (var item in Items.Where(i => i.NeedsLogin && i.State is ItemState.Failed or ItemState.Cancelled).ToList())
        {
            item.NeedsLogin = false;
            retried++;
            if (!item.Fetched) Refetch(item);
            else
            {
                item.InRun = false;
                item.State = ItemState.Ready;
                item.StatusText = Loc.T("status.ready");
                item.Changed();
            }
        }
        Say(Loc.T(retried > 0 ? "toast.signedin.retry" : "toast.signedin", retried), ToastKind.Success);
        NotifySummary();
    }

    // ---------- Queue that survives a restart ----------

    static readonly string[] SavedKeys = { "id", "title", "uploader", "channel", "duration", "thumbnail", "view_count", "webpage_url", "url",
        "ie_key", "extractor_key", "height", "live_status", "playlist_title", "album", "is_original", "source", "track", "artist", "creator", "language" };

    // Keeps only the fields the card needs; full video info can be tens of kilobytes per item.
    static Dictionary<string, object?> Slim(Dictionary<string, object?> info)
    {
        var slim = new Dictionary<string, object?>();
        foreach (string key in SavedKeys) if (info.TryGetValue(key, out object? value) && value != null) slim[key] = value;
        if (!slim.ContainsKey("thumbnail") && info.TryGetValue("thumbnails", out object? thumbnails)) slim["thumbnails"] = thumbnails;
        return slim;
    }

    /// <summary>Writes unfinished cards to disk if the queue changed. Called every few seconds and on close.</summary>
    public void SaveQueue(bool now)
    {
        int signature = Items.Count;
        unchecked
        {
            foreach (var item in Items)
                signature = signature * 31 + (int)item.State * 7 + (item.AudioMode ? 1 : 0) + (item.ManualPick ? 2 : 0) + (item.Section == null ? 0 : 4) + (item.VideoId == null ? 0 : item.VideoId.GetHashCode(StringComparison.Ordinal));
        }
        if (!now && signature == queueSignature) return;
        queueSignature = signature;
        var list = Items.Where(i => i.State != ItemState.Done && !i.Removed).Select(i => Json.Map(
            ("source", i.SourceUrl), ("query", i.Query), ("audio", i.AudioMode), ("locked", i.ModeLocked), ("manual", i.ManualPick), ("trackChecked", i.TrackChecked),
            ("expected", i.ExpectedSeconds), ("pending", i.PendingOutput), ("review", i.ReviewReason), ("follow", i.FollowRefresh), ("height", i.HeightOverride), ("isrc", i.Isrc), ("lists", i.Lists.Count > 0 ? i.Lists.ToList() : null), ("section", i.Section), ("reviewOk", i.ReviewDismissed),
            ("playlist", i.Playlist), ("clipPlaylist", i.ClipPlaylist), ("info", i.Fetched && i.Info != null ? Slim(i.Info) : null),
            // Both versions a card can show, so switching between them still works after a restart.
            ("clip", i.ClipInfo != null ? Slim(i.ClipInfo) : null), ("track", i.TrackInfo != null ? Slim(i.TrackInfo) : null))).ToList();
        void Write()
        {
            lock (queueFileGate)
            {
                try { AppPaths.WriteAtomic(AppPaths.Queue, Json.Write(list)); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Post(() => queueSignature = 0); } // try again on the next check
            }
        }
        if (now) Write(); else Task.Run(Write);
    }

    public int RestoreQueue()
    {
        List<object?>? saved;
        try
        {
            if (!File.Exists(AppPaths.Queue)) return 0;
            saved = Json.Parse(File.ReadAllText(AppPaths.Queue)) as List<object?>;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            Write(null, "Could not restore the queue: " + ex.Message);
            return 0;
        }
        int restored = 0;
        foreach (var entry in (saved ?? new()).OfType<Dictionary<string, object?>>())
        {
            string? source = entry.Str("source");
            if (source == null) continue;
            bool search = Engine.IsSearch(source);
            if (!search && !Uri.TryCreate(source, UriKind.Absolute, out _)) continue;
            var item = new QueueItem { Number = nextNumber++, SourceUrl = source, AudioMode = entry.Flag("audio"), ModeLocked = entry.Flag("locked"), ManualPick = entry.Flag("manual"), TrackChecked = entry.Flag("trackChecked") };
            item.Query = search ? entry.Str("query") ?? source[Engine.SearchPrefix.Length..] : null;
            item.Title = item.Query ?? source;
            item.ExpectedSeconds = entry.Num("expected");
            item.FollowRefresh = entry.Flag("follow");
            item.StartWhenReady = item.InRun = item.FollowRefresh;
            item.PendingOutput = entry.Str("pending");
            DropPendingOutput(item); // closed while converting
            item.HeightOverride = entry.Num("height") is { } height ? (int)height : null;
            item.Isrc = entry.Str("isrc");
            item.Section = entry.Str("section");
            item.ReviewDismissed = entry.Flag("reviewOk");
            if (entry.Raw("lists") is List<object?> lists) item.Lists.AddRange(lists.OfType<string>());
            if (entry.Raw("info") is Dictionary<string, object?> info)
            {
                ApplyInfo(item, info, entry.Str("playlist"));
                item.ClipInfo = entry.Raw("clip") as Dictionary<string, object?> ?? (item.IsOriginal ? null : info);
                item.TrackInfo = entry.Raw("track") as Dictionary<string, object?> ?? (item.IsOriginal ? info : null);
                item.ClipPlaylist = entry.Str("clipPlaylist") ?? entry.Str("playlist");
                // Parts of one video are cards of their own, beside the card of the whole video.
                if (item.Section == null)
                {
                    if (IsTaken(item, Key(item))) continue;
                    Claim(item);
                }
                item.Fetched = true;
                item.State = ItemState.Ready;
                item.StatusText = Loc.T("status.ready");
                item.ReviewReason = entry.Str("review") ?? CheckMatch(item); // its other versions, which some marks rest on, are not saved
                Items.Add(item);
                _ = LoadThumbAsync(item);
            }
            else
            {
                Items.Add(item);
                BeginLookup(item, false);
            }
            restored++;
        }
        PumpFetch();
        RefreshLabels();
        ApplySources();
        return restored;
    }

    // ---------- Parts of a video ----------

    /// <summary>Downloads only this part, given as "1:30" or seconds. Null or empty ends the wish. False when it makes no sense.</summary>
    public bool SetSection(QueueItem item, string? from, string? to)
    {
        if (!item.CanEdit) return false;
        if (string.IsNullOrWhiteSpace(from) && string.IsNullOrWhiteSpace(to)) item.Section = null;
        else
        {
            double start = string.IsNullOrWhiteSpace(from) ? 0 : Engine.ParseTime(from) ?? -1;
            double end = string.IsNullOrWhiteSpace(to) ? item.Info.Num("duration") ?? -1 : Engine.ParseTime(to) ?? -1;
            if (start < 0 || end <= start) return false;
            item.Section = ((long)start).ToString(CultureInfo.InvariantCulture) + "-" + ((long)Math.Ceiling(end)).ToString(CultureInfo.InvariantCulture);
        }
        RefreshLabels();
        item.Changed();
        return true;
    }

    /// <summary>The section for a part, in whole seconds; null when the part is the whole video.</summary>
    public static string? SectionFor(double start, double end, double? duration)
    {
        long from = (long)Math.Floor(Math.Max(0, start)), to = (long)Math.Ceiling(end);
        if (duration is > 0) to = Math.Min(to, (long)Math.Ceiling(duration.Value));
        if (to - from < 1) return null;
        if (from == 0 && duration is > 0 && to >= Math.Floor(duration.Value)) return null;
        return from.ToString(CultureInfo.InvariantCulture) + "-" + to.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Downloads only this part of the video, in seconds, and starts at once. A card that is finished or busy keeps its
    /// file, and the part gets a card of its own under it; so does any further part of the same video.
    /// </summary>
    public QueueItem? DownloadPart(QueueItem item, double start, double end)
    {
        if (!item.Fetched || item.Info == null || end - start < 1) return null;
        string? section = SectionFor(start, end, item.Info.Num("duration"));
        QueueItem target = item;
        if (!item.CanEdit)
        {
            target = new QueueItem
            {
                Number = nextNumber++,
                SourceUrl = item.WebpageUrl ?? item.SourceUrl,
                AudioMode = item.AudioMode,
                ModeLocked = true,
                HeightOverride = item.HeightOverride,
                ThumbBytes = item.ThumbBytes,
                Fetched = true,
                TrackChecked = true,
                State = ItemState.Ready,
                StatusText = Loc.T("status.ready"),
            };
            ApplyInfo(target, item.Info, item.Playlist.Length > 0 ? item.Playlist : null);
            target.ClipInfo = item.ClipInfo;
            target.TrackInfo = item.TrackInfo;
            int index = Items.IndexOf(item);
            Items.Insert(index < 0 ? Items.Count : index + 1, target);
        }
        target.Section = section;
        target.ForceDownload = false;
        RefreshLabels(new[] { target });
        target.Changed();
        StartOne(target);
        return target;
    }

    // ---------- Followed playlists ----------

    public void Follow(QueueItem item)
    {
        if (!item.CanFollow) return;
        if (Followed.Any(l => l.Url == item.SourceUrl)) { Say(Loc.T("toast.follow.already"), ToastKind.Info); return; }
        var list = new FollowedList { Url = item.SourceUrl, Name = item.Lists[0], Checked = DateTime.Now, Audio = item.AudioMode };
        // What is there now is the starting point, and following does not mean downloading all of it.
        foreach (var card in Items.Where(i => i.SourceUrl == item.SourceUrl))
            if (card.ClipInfo.Str("id") is { } id) list.Seen.Add(id);
        Followed.Add(list);
        FollowedList.Save(Followed);
        Say(Loc.T("toast.follow", item.Lists[0]), ToastKind.Success);
    }

    public void Unfollow(FollowedList list)
    {
        Followed.Remove(list);
        FollowedList.Save(Followed);
    }

    /// <summary>Looks for new videos in followed playlists: once a day by itself, or whenever asked.</summary>
    public void CheckFollowed(bool always)
    {
        bool any = false;
        foreach (var list in Followed.Where(l => always || DateTime.Now - l.Checked > TimeSpan.FromHours(20)).ToList())
        {
            var earlier = Items.Where(i => i.FollowRefresh && i.SourceUrl == list.Url && i.VideoId == null).ToList();
            if (earlier.Any(i => i.State == ItemState.Fetching)) continue;
            foreach (var failed in earlier) Remove(failed);
            any = true;
            var item = new QueueItem { Number = nextNumber++, SourceUrl = list.Url, Title = list.Name, AudioMode = list.Audio, ModeLocked = true, State = ItemState.Fetching, FollowRefresh = true, StartWhenReady = true, InRun = true };
            item.Lists.Add(list.Name);
            Items.Add(item);
            Write(item, "Checking for new videos: " + list.Url);
            BeginLookup(item, false);
        }
        if (!any) return;
        PumpFetch();
        NotifySummary();
    }

    /// <summary>
    /// Stops everything for closing the app; partial files stay and can be continued.
    /// thumbGate stays open, because thumbnail loads still under way release it when they end.
    /// </summary>
    public void Dispose()
    {
        closing = true;
        WritePlaylists();
        SaveQueue(true);
        foreach (var runner in fetchRunners.ToList()) runner.Cancel();
        foreach (var item in Items) item.Runner?.Cancel();
        duplicateTimer?.Dispose();
    }

    public bool AnyActive => Items.Any(i => i.IsActive);
}
