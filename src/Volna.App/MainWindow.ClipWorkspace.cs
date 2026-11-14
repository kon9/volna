using System.Collections.ObjectModel;
using Avalonia.Controls;
using Volna.Core;

namespace Volna.App;

public sealed class ClipDraft
{
    public double Start { get; init; }
    public double End { get; init; }
    public string Name { get; set; } = "";
    public string RangeText => Format.Duration(Start) + " – " + Format.Duration(End);
}

public sealed class ClipChapterRow
{
    public required VideoChapter Chapter { get; init; }
    public bool Selected { get; set; }
    public string Title => Chapter.Title;
    public string RangeText => Format.Duration(Chapter.Start) + " – " + Format.Duration(Chapter.End);
}

public sealed record ClipCaptionRow(CaptionTrack Track)
{
    public string Label => Track.Name + (Track.Automatic ? " · " + Loc.T("clip.automatic") : "");
}

public sealed record ClipCueRow(SubtitleCue Cue)
{
    public string Text => Cue.Text;
    public string RangeText => Format.Duration(Cue.Start) + " – " + Format.Duration(Cue.End);
}

public partial class MainWindow
{
    readonly ObservableCollection<ClipDraft> clipDrafts = new();
    readonly Dictionary<string, List<ClipDraft>> clipSavedDrafts = new(StringComparer.Ordinal);
    readonly List<ClipChapterRow> clipChapters = new();
    IReadOnlyList<SubtitleCue> clipCues = Array.Empty<SubtitleCue>();
    ClipMetadataInfo? clipMetadata;
    ProcessRunner? clipMetadataRunner, clipTranscriptRunner;
    bool clipMetadataBusy, clipLanguageLoading;
    int clipTranscriptGeneration;

    void WireClipWorkspace()
    {
        ClipPartsList.ItemsSource = clipDrafts;
        clipDrafts.CollectionChanged += (_, _) => UpdateClipDrafts();
        ClipPartsTab.IsCheckedChanged += (_, _) => { if (ClipPartsTab.IsChecked == true) SelectClipTab(0); };
        ClipChaptersTab.IsCheckedChanged += (_, _) => { if (ClipChaptersTab.IsChecked == true) SelectClipTab(1); };
        ClipTranscriptTab.IsCheckedChanged += (_, _) => { if (ClipTranscriptTab.IsChecked == true) SelectClipTab(2); };
        ClipCaptionLanguage.SelectionChanged += (_, _) =>
        {
            if (!clipLanguageLoading && ClipCaptionLanguage.SelectedItem is ClipCaptionRow row) _ = LoadClipTranscriptAsync(row.Track);
        };
        ClipTranscriptQuery.TextChanged += (_, _) => FilterClipTranscript();
        ClipPlaybackSeek.ValueChanged += (_, e) =>
        {
            if (!clipSeekUpdating && clipPreviewFile != null) clipPlayer?.Seek(e.NewValue);
        };
    }

    string ClipDraftKey => clipTarget?.WebpageUrl ?? clipTarget?.SourceUrl ?? "";

    void OpenClipWorkspace()
    {
        ClipName.Text = clipTarget?.SectionName ?? "";
        clipDrafts.Clear();
        if (clipSavedDrafts.TryGetValue(ClipDraftKey, out var saved))
            foreach (var draft in saved) clipDrafts.Add(draft);
        ClipPartsTab.IsChecked = true;
        SelectClipTab();
        UpdateClipDrafts();
        ClipChaptersStatus.Text = Loc.T("clip.metadata.loading");
        ClipTranscriptStatus.Text = Loc.T("clip.metadata.loading");
        ClipTranscriptQuery.Text = "";
    }

    void CloseClipWorkspace()
    {
        if (clipTarget != null) clipSavedDrafts[ClipDraftKey] = clipDrafts.ToList();
        StopClipPlayback();
        clipMetadataRunner?.Cancel();
        clipTranscriptRunner?.Cancel();
        clipMetadataRunner = clipTranscriptRunner = null;
        clipMetadataBusy = false;
        clipTranscriptGeneration++;
        clipMetadata = null;
        clipCues = Array.Empty<SubtitleCue>();
        clipChapters.Clear();
        ClipChaptersList.ItemsSource = null;
        ClipTranscriptList.ItemsSource = null;
        clipLanguageLoading = true;
        ClipCaptionLanguage.ItemsSource = null;
        clipLanguageLoading = false;
        ClipAddChaptersButton.IsEnabled = false;
    }

    void SelectClipTab(int? selected = null)
    {
        // A radio button raises its event before the old button has been unchecked.
        int tab = selected ?? (ClipTranscriptTab.IsChecked == true ? 2 : ClipChaptersTab.IsChecked == true ? 1 : 0);
        ClipPartsPanel.IsVisible = tab == 0;
        ClipChaptersPanel.IsVisible = tab == 1;
        ClipTranscriptPanel.IsVisible = tab == 2;
        if (clipTarget != null && !ClipPartsPanel.IsVisible && clipMetadata == null && !clipMetadataBusy) _ = LoadClipMetadataAsync();
        else if (ClipTranscriptPanel.IsVisible && clipMetadata != null && clipCues.Count == 0 && clipTranscriptRunner == null
            && ClipCaptionLanguage.SelectedItem is ClipCaptionRow row) _ = LoadClipTranscriptAsync(row.Track);
    }

    bool HandleClipWorkspaceCommand(string command, object? context)
    {
        switch (command)
        {
            case "clipAdd":
                CommitClipTime();
                AddClipDraft(clipRange.Start, clipRange.End, ClipName.Text);
                ClipPartsTab.IsChecked = true;
                return true;
            case "clipDraftRemove" when context is ClipDraft draft:
                clipDrafts.Remove(draft);
                return true;
            case "clipDraftPick" when context is ClipDraft draft:
                SelectClipRange(draft.Start, draft.End, draft.Name);
                return true;
            case "clipChapterPick" when context is ClipChapterRow row:
                SelectClipRange(row.Chapter.Start, row.Chapter.End, row.Title);
                return true;
            case "clipCuePick" when context is ClipCueRow row:
                SelectClipRange(row.Cue.Start, row.Cue.End, "");
                return true;
            case "clipAddChapters":
                foreach (var chapter in clipChapters.Where(c => c.Selected))
                    AddClipDraft(chapter.Chapter.Start, chapter.Chapter.End, chapter.Title);
                if (clipChapters.Any(c => c.Selected)) ClipPartsTab.IsChecked = true;
                else ShowToast(Loc.T("clip.chapters.choose"), ToastKind.Info);
                return true;
            case "clipDownloadAll": DownloadClipDrafts(); return true;
            case "clipPlay": _ = PlayClipAsync(); return true;
            case "clipStop": StopClipPlayback(); return true;
            case "clipMute": ToggleClipMute(); return true;
            default: return false;
        }
    }

    void CommitClipTime()
    {
        if (FocusManager?.GetFocusedElement() is TextBox box && (box == ClipFrom || box == ClipTo)) ApplyClipText(box);
    }

    void SelectClipRange(double start, double end, string name)
    {
        clipPeek = null;
        clipFocus = ClipPart.Start;
        clipRange.Set(Math.Floor(start), Math.Ceiling(end));
        ClipName.Text = name;
        FitClipZoom(true);
        ShowClipPreview(true);
    }

    void AddClipDraft(double start, double end, string? name)
    {
        start = Math.Clamp(Math.Floor(start), 0, clipRange.Duration - ClipRange.Shortest);
        end = Math.Clamp(Math.Ceiling(end), start + ClipRange.Shortest, clipRange.Duration);
        if (clipDrafts.Any(d => d.Start == start && d.End == end))
        {
            ShowToast(Loc.T("clip.duplicate"), ToastKind.Info);
            return;
        }
        clipDrafts.Add(new ClipDraft { Start = start, End = end, Name = name?.Trim() ?? "" });
    }

    void UpdateClipDrafts()
    {
        ClipPartsEmpty.IsVisible = clipDrafts.Count == 0;
        ClipDownloadAllButton.IsEnabled = clipDrafts.Count > 0;
        ClipDownloadAllButton.Content = clipDrafts.Count == 0 ? Loc.T("clip.download.all") : Loc.T("clip.download.count", clipDrafts.Count);
    }

    void DownloadClipDrafts()
    {
        if (clipTarget == null || clipDrafts.Count == 0) return;
        prefs.Folder = (FolderBox.Text ?? "").Trim();
        var cards = Queue.DownloadParts(clipTarget, clipDrafts.Select(d => new DownloadPartRequest(d.Start, d.End, d.Name)).ToArray());
        if (cards.Count == 0) { ShowToast(Loc.T("toast.clip.bad"), ToastKind.Warning); return; }
        clipDrafts.Clear();
        CloseClip();
        ShowToast(Loc.T("clip.queued", cards.Count), ToastKind.Info);
        QueueList.ScrollIntoView(cards[0]);
    }

    async Task LoadClipMetadataAsync()
    {
        if (clipTarget == null) return;
        int generation = clipGeneration;
        string url = clipTarget.WebpageUrl ?? clipTarget.SourceUrl;
        clipMetadataBusy = true;
        ClipChaptersStatus.Text = ClipTranscriptStatus.Text = Loc.T("clip.metadata.loading");
        using var runner = new ProcessRunner();
        clipMetadataRunner = runner;
        try
        {
            var metadata = await backend.ClipMetadataAsync(url, prefs.Proxy, runner);
            if (generation != clipGeneration) return;
            clipMetadata = metadata;
            clipChapters.Clear();
            clipChapters.AddRange(metadata.Chapters.Select(c => new ClipChapterRow { Chapter = c }));
            ClipChaptersList.ItemsSource = clipChapters.ToArray();
            ClipChaptersStatus.Text = Loc.T(clipChapters.Count == 0 ? "clip.chapters.empty" : "clip.chapters.hint");
            ClipAddChaptersButton.IsEnabled = clipChapters.Count > 0;
            clipLanguageLoading = true;
            var tracks = metadata.Captions.Select(t => new ClipCaptionRow(t)).ToArray();
            ClipCaptionLanguage.ItemsSource = tracks;
            var preferred = metadata.PreferredCaption(Loc.Language);
            ClipCaptionLanguage.SelectedItem = tracks.FirstOrDefault(t => t.Track == preferred) ?? tracks.FirstOrDefault();
            clipLanguageLoading = false;
            ClipTranscriptStatus.Text = Loc.T(tracks.Length == 0 ? "clip.transcript.empty" : "clip.transcript.loading");
            if (ClipTranscriptPanel.IsVisible && ClipCaptionLanguage.SelectedItem is ClipCaptionRow row) await LoadClipTranscriptAsync(row.Track);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (generation != clipGeneration) return;
            Log("Clip metadata: " + ex.Message);
            ClipChaptersStatus.Text = ClipTranscriptStatus.Text = Loc.T("clip.metadata.failed");
        }
        finally
        {
            if (generation == clipGeneration) { clipMetadataBusy = false; clipMetadataRunner = null; }
        }
    }

    async Task LoadClipTranscriptAsync(CaptionTrack track)
    {
        if (clipTarget == null) return;
        clipTranscriptRunner?.Cancel();
        int generation = clipGeneration, transcriptGeneration = ++clipTranscriptGeneration;
        string url = clipTarget.WebpageUrl ?? clipTarget.SourceUrl;
        clipCues = Array.Empty<SubtitleCue>();
        ClipTranscriptList.ItemsSource = null;
        ClipTranscriptStatus.Text = Loc.T("clip.transcript.loading");
        using var runner = new ProcessRunner();
        clipTranscriptRunner = runner;
        try
        {
            var cues = await backend.TranscriptAsync(url, track, prefs.Proxy, runner);
            if (generation != clipGeneration || transcriptGeneration != clipTranscriptGeneration) return;
            clipCues = cues;
            if (cues.Count == 0) ClipTranscriptStatus.Text = Loc.T("clip.transcript.empty");
            else FilterClipTranscript();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (generation != clipGeneration || transcriptGeneration != clipTranscriptGeneration) return;
            Log("Clip transcript: " + ex.Message);
            ClipTranscriptStatus.Text = Loc.T("clip.transcript.failed");
        }
        finally
        {
            if (transcriptGeneration == clipTranscriptGeneration) clipTranscriptRunner = null;
        }
    }

    void FilterClipTranscript()
    {
        if (clipCues.Count == 0) return;
        string query = ClipTranscriptQuery.Text?.Trim() ?? "";
        var matches = TranscriptSearch.Find(clipCues, query);
        ClipTranscriptList.ItemsSource = matches.Take(200).Select(c => new ClipCueRow(c)).ToArray();
        ClipTranscriptStatus.Text = matches.Count == 0 ? Loc.T("clip.search.empty") : matches.Count > 200
            ? Loc.T("clip.search.more", matches.Count) : Loc.T("clip.search.count", matches.Count);
    }
}
