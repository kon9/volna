using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Volna.Core;

namespace Volna.App;

// Choosing a part of a video: a large frame, a strip of the whole video and a closer one around the part, and the
// times as text. Only the part is downloaded, so thirty seconds of a twelve-hour stream cost thirty seconds of it.
public partial class MainWindow
{
    const double DefaultPart = 30;

    readonly ClipRange clipRange = new();
    readonly Dictionary<string, Bitmap?> clipSheets = new(StringComparer.Ordinal);
    readonly HashSet<string> clipLoading = new(StringComparer.Ordinal);
    readonly Dictionary<string, (Storyboard? Detail, Storyboard? Overview, DateTime At)> storyboards = new(StringComparer.Ordinal);
    readonly SemaphoreSlim clipGate = new(4);
    readonly DispatcherTimer clipSettle = new() { Interval = TimeSpan.FromMilliseconds(150) };
    Storyboard? clipDetailBoard, clipOverviewBoard;
    ProcessRunner? clipRunner;
    Bitmap? clipThumb;
    double? clipPeek;
    ClipPart clipPeekPart, clipFocus = ClipPart.Start;
    int clipGeneration;
    bool clipWired;

    void WireClip()
    {
        clipWired = true;
        ClipOverview.Range = ClipDetail.Range = clipRange;
        ClipOverview.Frames = ClipDetail.Frames = TrackFrame;
        clipRange.Changed += OnClipChanged;
        ClipOverview.Peek += OnClipPeek;
        ClipDetail.Peek += OnClipPeek;
        ClipDetail.DragEnded += () => FitClipZoom(false);
        foreach (var box in new[] { ClipFrom, ClipTo })
        {
            box.AddHandler(KeyDownEvent, OnClipKey, RoutingStrategies.Tunnel);
            box.AddHandler(PointerWheelChangedEvent, OnClipWheel, RoutingStrategies.Tunnel);
            box.LostFocus += (_, _) => ApplyClipText(box);
        }
        clipSettle.Tick += (_, _) => { clipSettle.Stop(); ShowClipPreview(true); };
        SizeChanged += (_, _) => { if (ClipOverlay.IsVisible) FitClipDialog(); };
    }

    // The large frame gives way first on a short window, and the rest scrolls only when even that is not enough.
    void FitClipDialog()
    {
        ClipContent.Measure(new Size(ClipDialog.Width, double.PositiveInfinity));
        double rest = ClipContent.DesiredSize.Height - ClipPreviewBox.Height;
        ClipPreviewBox.Height = Math.Clamp(Bounds.Height - ClipDialog.Margin.Top - ClipDialog.Margin.Bottom - 2 - rest, 120, 333);
    }

    public void OpenClip(QueueItem item)
    {
        if (!item.ShowClip || item.Info.Num("duration") is not { } duration) return;
        if (!clipWired) WireClip();
        CloseClip();
        clipTarget = item;
        double start = 0, end = Math.Min(duration, DefaultPart);
        string[] ends = (item.Section ?? "").Split('-');
        if (ends.Length == 2 && double.TryParse(ends[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double from)
            && double.TryParse(ends[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double to))
            (start, end) = (from, to);
        // A link that starts at a moment, as one shared from a stream often does, most likely points at the part.
        else if (Engine.StartOf(item.SourceUrl) is { } at && at < duration)
            (start, end) = (at, Math.Min(duration, at + DefaultPart));
        clipFocus = ClipPart.Start;
        clipPeek = null;
        ClipOverview.SetView(0, Math.Floor(duration));
        clipRange.Reset(duration, start, end);
        FitClipZoom(true);
        ClipVideoTitle.Text = item.Title;
        ClipTotal.Text = item.Duration;
        ClipWholeButton.IsVisible = item.HasSection && item.CanEdit;
        try { clipThumb = item.ThumbBytes != null ? new Bitmap(new MemoryStream(item.ThumbBytes)) : null; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { clipThumb = null; }
        ClipPreview.Source = clipThumb;
        ClipLoading.IsVisible = false;
        ClipOverlay.IsVisible = true;
        FitClipDialog();
        ShowClipPreview(true);
        if (IsYouTubeCard(item)) _ = LoadStoryboardAsync(item);
        ClipOverview.Focus();
    }

    static bool IsYouTubeCard(QueueItem item) => item.Extractor.StartsWith("Youtube", StringComparison.OrdinalIgnoreCase) && item.WebpageUrl != null && item.VideoId != null;

    void CloseClip()
    {
        ClipOverlay.IsVisible = false;
        clipGeneration++;
        clipRunner?.Cancel();
        clipRunner = null;
        clipSettle.Stop();
        clipTarget = null;
        clipDetailBoard = clipOverviewBoard = null;
        ClipPreview.Source = null;
        foreach (var sheet in clipSheets.Values) sheet?.Dispose();
        clipSheets.Clear();
        clipLoading.Clear();
        clipThumb?.Dispose();
        clipThumb = null;
    }

    async Task LoadStoryboardAsync(QueueItem item)
    {
        string key = item.Extractor + "/" + item.VideoId;
        int generation = clipGeneration;
        // The addresses of the sheets are signed, and do not last for ever.
        if (!storyboards.TryGetValue(key, out var known) || DateTime.UtcNow - known.At > TimeSpan.FromHours(1))
        {
            ClipLoading.IsVisible = true;
            var runner = new ProcessRunner();
            clipRunner = runner;
            try
            {
                var (detail, overview) = await backend.StoryboardAsync(item.WebpageUrl!, prefs.Proxy, runner);
                known = (detail, overview, DateTime.UtcNow);
                if (detail != null) storyboards[key] = known;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Log("No preview frames: " + ex.Message); }
            finally
            {
                runner.Dispose();
                if (clipRunner == runner) clipRunner = null;
            }
        }
        if (generation != clipGeneration) return;
        ClipLoading.IsVisible = false;
        clipDetailBoard = known.Detail;
        clipOverviewBoard = known.Overview;
        ClipOverview.InvalidateVisual();
        ClipDetail.InvalidateVisual();
        ShowClipPreview(true);
    }

    // The strips take what is there, the sharp frames first; a frame not there yet is asked for.
    FrameCrop? TrackFrame(double seconds) => Crop(clipDetailBoard, seconds, true) ?? Crop(clipOverviewBoard, seconds, true);

    FrameCrop? Crop(Storyboard? board, double seconds, bool load)
    {
        if (board?.At(seconds) is not { } frame) return null;
        if (!clipSheets.TryGetValue(frame.Url, out Bitmap? sheet))
        {
            if (load) _ = LoadSheetAsync(frame.Url);
            return null;
        }
        // A sheet smaller than its grid, as the last one can be, has no picture there.
        if (sheet == null || frame.X + frame.Width > sheet.PixelSize.Width || frame.Y + frame.Height > sheet.PixelSize.Height) return null;
        return new FrameCrop(sheet, new Rect(frame.X, frame.Y, frame.Width, frame.Height));
    }

    async Task LoadSheetAsync(string url)
    {
        if (!clipLoading.Add(url)) return;
        int generation = clipGeneration;
        Bitmap? sheet = null;
        await clipGate.WaitAsync();
        try
        {
            if (generation != clipGeneration) return;
            byte[]? data = await backend.ThumbnailAsync(url, prefs.Proxy);
            if (data != null) sheet = await Task.Run(() => new Bitmap(new MemoryStream(data)));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { sheet = null; }
        finally { clipGate.Release(); }
        if (generation != clipGeneration)
        {
            sheet?.Dispose();
            return;
        }
        clipSheets[url] = sheet;
        ClipOverview.InvalidateVisual();
        ClipDetail.InvalidateVisual();
        ShowClipPreview(false);
    }

    void OnClipChanged()
    {
        ClipFrom.Text = Format.Duration(clipRange.Start);
        ClipTo.Text = Format.Duration(clipRange.End);
        ClipLength.Text = Format.Duration(clipRange.Length);
        // The closer strip stays put while it is being dragged on, and follows the part otherwise.
        if (!ClipDetail.IsDragging) FitClipZoom(false);
        if (clipPeek == null || ClipOverview.IsDragging || ClipDetail.IsDragging) ShowClipPreview(false);
    }

    void OnClipPeek(double? seconds, ClipPart part)
    {
        clipPeek = seconds;
        clipPeekPart = part;
        if (part != ClipPart.None) clipFocus = part == ClipPart.End ? ClipPart.End : ClipPart.Start;
        ShowClipPreview(false);
    }

    /// <summary>
    /// A strip of a twelve-hour video gives a minute or more to each point of the screen, too coarse to set a part of
    /// thirty seconds. A second strip shows a few minutes around the part, where a point is about a second.
    /// </summary>
    void FitClipZoom(bool force)
    {
        double duration = clipRange.Duration, length = clipRange.Length;
        double span = Math.Min(duration, Math.Max(60, length * 8));
        bool show = duration > span * 1.5;
        if (ClipZoomPanel.IsVisible != show)
        {
            ClipZoomPanel.IsVisible = show;
            if (ClipOverlay.IsVisible) FitClipDialog();
        }
        if (!show) return;
        double start = ClipDetail.ViewStart, shown = ClipDetail.ViewEnd - start;
        bool fits = clipRange.Start >= start && clipRange.End <= start + shown && shown >= span / 2.5 && shown <= span * 2.5;
        if (!force && fits) return;
        start = Math.Clamp((clipRange.Start + clipRange.End - span) / 2, 0, duration - span);
        ClipDetail.SetView(start, start + span);
        ClipZoomText.Text = Loc.T("clip.zoom", Format.Duration(start), Format.Duration(start + span));
    }

    /// <summary>The large frame: the moment under the pointer, or the edge last moved. The sharp frame waits until the pointer rests.</summary>
    void ShowClipPreview(bool settled)
    {
        if (!ClipOverlay.IsVisible) return;
        ClipPart part = clipPeek != null ? clipPeekPart : clipFocus;
        double at = clipPeek ?? (clipFocus == ClipPart.End ? clipRange.End : clipRange.Start);
        ClipPreviewTime.Text = Format.Duration(at);
        ClipPreviewLabel.Text = part switch { ClipPart.End => Loc.T("clip.at.end"), ClipPart.None => "", _ => Loc.T("clip.at.start") };
        ClipPreviewLabel.IsVisible = ClipPreviewLabel.Text.Length > 0;
        if ((Crop(clipDetailBoard, at, settled) ?? Crop(clipOverviewBoard, at, true)) is { } frame)
            ClipPreview.Source = new CroppedBitmap(frame.Sheet, new PixelRect((int)frame.Source.X, (int)frame.Source.Y, (int)frame.Source.Width, (int)frame.Source.Height));
        // Otherwise the last frame stays, rather than a flash of nothing.
        else ClipPreview.Source ??= clipThumb;
        if (!settled)
        {
            clipSettle.Stop();
            clipSettle.Start();
        }
    }

    void ClipCommand(string command)
    {
        string[] parts = command.Split(':', 2);
        if (parts.Length != 2 || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) return;
        switch (parts[0])
        {
            case "clipStart":
                clipFocus = ClipPart.Start;
                clipRange.Set(Math.Min(clipRange.Start + value, clipRange.End - ClipRange.Shortest), clipRange.End);
                break;
            case "clipEnd":
                clipFocus = ClipPart.End;
                clipRange.Set(clipRange.Start, Math.Max(clipRange.End + value, clipRange.Start + ClipRange.Shortest));
                break;
            case "clipLength":
                double start = Math.Min(clipRange.Start, Math.Max(0, clipRange.Duration - value));
                clipRange.Set(start, start + value);
                break;
        }
        clipPeek = null;
        ShowClipPreview(false);
    }

    // A time typed before the end moves only the start, and so on; one past the other end moves the whole part there.
    void ApplyClipText(TextBox box)
    {
        if (clipTarget == null) return;
        if (Engine.ParseTime(box.Text) is not { } value)
        {
            OnClipChanged();
            return;
        }
        clipPeek = null;
        if (box == ClipFrom)
        {
            clipFocus = ClipPart.Start;
            if (value < clipRange.End) clipRange.Set(value, clipRange.End);
            else clipRange.MoveTo(value);
        }
        else
        {
            clipFocus = ClipPart.End;
            if (value > clipRange.Start) clipRange.Set(clipRange.Start, value);
            else clipRange.MoveTo(value - clipRange.Length);
        }
        OnClipChanged();
        ShowClipPreview(false);
    }

    void OnClipKey(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox box) return;
        if (e.Key == Key.Enter)
        {
            ApplyClipText(box);
            box.SelectAll();
            e.Handled = true;
        }
        else if (e.Key is Key.Up or Key.Down)
        {
            ApplyClipText(box);
            Nudge(box, (e.Key == Key.Up ? 1 : -1) * (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1));
            e.Handled = true;
        }
    }

    void OnClipWheel(object? sender, PointerWheelEventArgs e)
    {
        if (sender is not TextBox box || e.Delta.Y == 0) return;
        Nudge(box, Math.Sign(e.Delta.Y) * (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1));
        e.Handled = true;
    }

    void Nudge(TextBox box, double seconds) =>
        ClipCommand((box == ClipFrom ? "clipStart:" : "clipEnd:") + seconds.ToString(CultureInfo.InvariantCulture));

    void ApplyClip()
    {
        if (clipTarget == null) return;
        if (FocusManager?.GetFocusedElement() is TextBox box && (box == ClipFrom || box == ClipTo)) ApplyClipText(box);
        prefs.Folder = (FolderBox.Text ?? "").Trim();
        QueueItem target = clipTarget;
        QueueItem? card = Queue.DownloadPart(target, clipRange.Start, clipRange.End);
        if (card == null)
        {
            ShowToast(Loc.T("toast.clip.bad"), ToastKind.Warning);
            return;
        }
        CloseClip();
        if (card != target)
        {
            ShowToast(Loc.T("clip.added"), ToastKind.Info);
            QueueList.ScrollIntoView(card);
        }
    }

    void ClearClip()
    {
        if (clipTarget != null) Queue.SetSection(clipTarget, null, null);
        CloseClip();
    }
}
