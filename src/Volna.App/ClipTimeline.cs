using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Volna.Core;

namespace Volna.App;

/// <summary>A frame of a storyboard: its sheet, and where on the sheet it is.</summary>
public sealed record FrameCrop(Bitmap Sheet, Rect Source);

public enum ClipPart { None, Start, End, Body }

/// <summary>
/// A strip of frames from a stretch of the video, with the chosen part framed on it. The frame is dragged by its middle
/// to move the part, or by an edge to change it; a click elsewhere moves the part there. Arrow keys move it a second
/// at a time, ten with Shift.
/// </summary>
public sealed class ClipTimeline : Control
{
    const double Inset = 8, HandleWidth = 10, Grab = 10, NarrowestFrame = 16;

    ClipRange? range;
    ClipPart dragging, last = ClipPart.Body;
    double grabOffset;
    double? hover;

    public ClipTimeline()
    {
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public ClipRange? Range
    {
        get => range;
        set
        {
            if (range != null) range.Changed -= InvalidateVisual;
            range = value;
            if (range != null) range.Changed += InvalidateVisual;
            InvalidateVisual();
        }
    }

    /// <summary>The stretch of the video the strip shows, in seconds.</summary>
    public double ViewStart { get; private set; }
    public double ViewEnd { get; private set; } = 1;

    public void SetView(double start, double end)
    {
        ViewStart = start;
        ViewEnd = Math.Max(start + 1, end);
        InvalidateVisual();
    }

    /// <summary>The frame at a moment, or null while it is not there yet.</summary>
    public Func<double, FrameCrop?>? Frames { get; set; }

    /// <summary>The moment worth showing in the preview: under the pointer, or at the edge being dragged. Null to show the start.</summary>
    public event Action<double?, ClipPart>? Peek;
    public event Action? DragEnded;

    public bool IsDragging => dragging != ClipPart.None;

    Rect Track => new(Inset, 2, Math.Max(1, Bounds.Width - 2 * Inset), Math.Max(1, Bounds.Height - 4));
    double X(double seconds) => Track.X + (seconds - ViewStart) / (ViewEnd - ViewStart) * Track.Width;
    double Seconds(double x) => Math.Clamp(ViewStart + (x - Track.X) / Track.Width * (ViewEnd - ViewStart), ViewStart, ViewEnd);

    /// <summary>The frame around the part. A part too short to see on this strip still gets a frame that can be held.</summary>
    (double Left, double Right, bool Narrow) Marker()
    {
        double left = X(range!.Start), right = X(range.End);
        bool narrow = right - left < 2 * HandleWidth + 6;
        if (right - left < NarrowestFrame)
        {
            double middle = (left + right) / 2;
            left = middle - NarrowestFrame / 2;
            right = middle + NarrowestFrame / 2;
        }
        return (left, right, narrow);
    }

    IBrush Resource(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out object? value) && value is IBrush brush ? brush : fallback;

    public override void Render(DrawingContext context)
    {
        Rect track = Track;
        var shape = new RoundedRect(track, 8);
        IBrush accent = Resource("AccentGradient", Brushes.DeepPink);
        using (context.PushClip(shape))
        {
            context.FillRectangle(Resource("Well", Brushes.Black), track);
            if (Frames != null)
            {
                // Frames keep the 16:9 of the video, as many as fit side by side.
                int count = Math.Max(1, (int)Math.Round(track.Width / (track.Height * 16 / 9)));
                double width = track.Width / count;
                for (int n = 0; n < count; n++)
                {
                    var dest = new Rect(track.X + n * width, track.Y, width, track.Height);
                    if (Frames(Seconds(dest.Center.X)) is not { } frame) continue;
                    context.DrawImage(frame.Sheet, Fill(frame.Source, dest.Size), dest);
                }
            }
            if (range == null) return;
            var (left, right, _) = Marker();
            IBrush dim = new SolidColorBrush(Color.FromArgb(165, 8, 10, 14));
            context.FillRectangle(dim, new Rect(track.X, track.Y, Math.Max(0, left - track.X), track.Height));
            context.FillRectangle(dim, new Rect(right, track.Y, Math.Max(0, track.Right - right), track.Height));
            if (hover is { } at && !IsDragging)
            {
                double x = Math.Round(X(at)) + 0.5;
                context.DrawLine(new Pen(new SolidColorBrush(Colors.White, 0.75), 1), new Point(x, track.Y), new Point(x, track.Bottom));
            }
        }
        if (range == null) return;
        var (l, r, narrow) = Marker();
        var frameRect = new Rect(l, track.Y + 1, r - l, track.Height - 2);
        context.DrawRectangle(null, new Pen(accent, 2.5), new RoundedRect(frameRect, 6));
        if (narrow) return;
        foreach (double edge in new[] { l, r })
        {
            if (edge < track.X - 1 || edge > track.Right + 1) continue;
            var handle = new Rect(edge - HandleWidth / 2, track.Y, HandleWidth, track.Height);
            context.DrawRectangle(accent, null, new RoundedRect(handle, 4));
            double middle = Math.Round(handle.Center.X);
            context.DrawLine(new Pen(Brushes.White, 1.5), new Point(middle, handle.Center.Y - 7), new Point(middle, handle.Center.Y + 7));
        }
    }

    // The middle of the frame, cut to the shape of the place it goes, as a picture with Stretch=UniformToFill.
    static Rect Fill(Rect source, Size dest)
    {
        double scale = Math.Max(dest.Width / source.Width, dest.Height / source.Height);
        double width = dest.Width / scale, height = dest.Height / scale;
        return new Rect(source.X + (source.Width - width) / 2, source.Y + (source.Height - height) / 2, width, height);
    }

    ClipPart PartAt(double x)
    {
        if (range == null) return ClipPart.None;
        var (left, right, narrow) = Marker();
        if (!narrow && Math.Abs(x - left) <= Grab && x <= (left + right) / 2) return ClipPart.Start;
        if (!narrow && Math.Abs(x - right) <= Grab) return ClipPart.End;
        return x >= left && x <= right ? ClipPart.Body : ClipPart.None;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (range == null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        double x = e.GetPosition(this).X;
        dragging = PartAt(x);
        if (dragging == ClipPart.None)
        {
            // A click beside the part moves it there, and it can be dragged on from that point.
            range.MoveTo(Seconds(x) - range.Length / 2);
            dragging = ClipPart.Body;
        }
        last = dragging;
        grabOffset = Seconds(x) - range.Start;
        e.Pointer.Capture(this);
        Focus();
        e.Handled = true;
        Report();
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (range == null) return;
        double x = e.GetPosition(this).X;
        if (!IsDragging)
        {
            hover = Seconds(x);
            Cursor = new Cursor(PartAt(x) is ClipPart.Start or ClipPart.End ? StandardCursorType.SizeWestEast : StandardCursorType.Hand);
            Peek?.Invoke(hover, ClipPart.None);
            InvalidateVisual();
            return;
        }
        double at = Seconds(x);
        switch (dragging)
        {
            case ClipPart.Start: range.Set(Math.Min(at, range.End - ClipRange.Shortest), range.End); break;
            case ClipPart.End: range.Set(range.Start, Math.Max(at, range.Start + ClipRange.Shortest)); break;
            default: range.MoveTo(at - grabOffset); break;
        }
        Report();
    }

    void Report() => Peek?.Invoke(dragging == ClipPart.End || (dragging == ClipPart.None && last == ClipPart.End) ? range!.End : range!.Start, dragging == ClipPart.None ? last : dragging);

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!IsDragging) return;
        dragging = ClipPart.None;
        e.Pointer.Capture(null);
        DragEnded?.Invoke();
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (!IsDragging) return;
        dragging = ClipPart.None;
        DragEnded?.Invoke();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        hover = null;
        if (!IsDragging) Peek?.Invoke(null, ClipPart.None);
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (range == null || e.Key is not (Key.Left or Key.Right)) return;
        double step = (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1) * (e.Key == Key.Left ? -1 : 1);
        switch (last)
        {
            case ClipPart.Start: range.Set(Math.Min(range.Start + step, range.End - ClipRange.Shortest), range.End); break;
            case ClipPart.End: range.Set(range.Start, Math.Max(range.End + step, range.Start + ClipRange.Shortest)); break;
            default: range.MoveTo(range.Start + step); break;
        }
        Report();
        DragEnded?.Invoke();
        e.Handled = true;
    }
}
