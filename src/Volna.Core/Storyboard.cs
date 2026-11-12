namespace Volna.Core;

/// <summary>
/// The small preview frames YouTube shows over its seek bar. They come as sheets: each one a grid of frames that covers
/// a stretch of the video. They cost a few kilobytes each, so a part of a long video can be found by its picture
/// without downloading any of the video.
/// </summary>
public sealed class Storyboard
{
    public sealed record Sheet(string Url, double Start, double Duration);

    /// <summary>One frame: where it sits on its sheet.</summary>
    public sealed record Frame(string Url, int X, int Y, int Width, int Height);

    public int Width { get; }
    public int Height { get; }
    public int Rows { get; }
    public int Columns { get; }
    public double FramesPerSecond { get; }
    public IReadOnlyList<Sheet> Sheets { get; }

    public Storyboard(int width, int height, int rows, int columns, double framesPerSecond, IReadOnlyList<Sheet> sheets)
    {
        Width = width;
        Height = height;
        Rows = rows;
        Columns = columns;
        FramesPerSecond = framesPerSecond;
        Sheets = sheets;
    }

    /// <summary>The frame shown at this moment of the video.</summary>
    public Frame? At(double seconds)
    {
        if (Sheets.Count == 0) return null;
        int low = 0, high = Sheets.Count - 1;
        while (low < high)
        {
            int middle = (low + high + 1) / 2;
            if (Sheets[middle].Start <= seconds) low = middle;
            else high = middle - 1;
        }
        Sheet sheet = Sheets[low];
        // The last sheet is often only partly filled.
        int frames = Math.Clamp((int)Math.Ceiling(sheet.Duration * FramesPerSecond - 1e-6), 1, Rows * Columns);
        int index = Math.Clamp((int)Math.Floor((seconds - sheet.Start) * FramesPerSecond + 1e-6), 0, frames - 1);
        return new Frame(sheet.Url, index % Columns * Width, index / Columns * Height, Width, Height);
    }

    /// <summary>
    /// From yt-dlp's list of formats: the sharpest storyboard, for a large preview, and the one that covers the whole video
    /// on the fewest sheets, for a strip of frames under it. Either is null when the site has none.
    /// </summary>
    public static (Storyboard? Detail, Storyboard? Overview) FromFormats(IEnumerable<Dictionary<string, object?>> formats)
    {
        var boards = new List<Storyboard>();
        foreach (var format in formats)
        {
            if (format.Str("format_id") is not { } id || !id.StartsWith("sb", StringComparison.Ordinal)) continue;
            int width = (int)(format.Num("width") ?? 0), height = (int)(format.Num("height") ?? 0);
            int rows = (int)(format.Num("rows") ?? 0), columns = (int)(format.Num("columns") ?? 0);
            double fps = format.Num("fps") ?? 0;
            if (width <= 0 || height <= 0 || rows <= 0 || columns <= 0 || fps <= 0) continue;
            var sheets = new List<Sheet>();
            double start = 0;
            foreach (var fragment in (format.Raw("fragments") as List<object?> ?? new()).OfType<Dictionary<string, object?>>())
            {
                if (fragment.Str("url") is not { } url || fragment.Num("duration") is not { } duration || duration <= 0) continue;
                sheets.Add(new Sheet(url, start, duration));
                start += duration;
            }
            if (sheets.Count > 0) boards.Add(new Storyboard(width, height, rows, columns, fps, sheets));
        }
        if (boards.Count == 0) return (null, null);
        var detail = boards.MaxBy(b => b.Width * b.Height)!;
        // A strip of a dozen frames needs at least that many; the fewest sheets keeps it to one or two downloads.
        var overview = boards.Where(b => b.Sheets.Sum(s => Math.Ceiling(s.Duration * b.FramesPerSecond)) >= 12)
            .OrderBy(b => b.Sheets.Count).ThenByDescending(b => b.Width).FirstOrDefault() ?? detail;
        return (detail, overview);
    }
}
