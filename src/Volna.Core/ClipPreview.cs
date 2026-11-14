using System.Globalization;

namespace Volna.Core;

/// <summary>A disposable, low-resolution preview. It never enters the downloads folder or the queue.</summary>
public sealed class ClipPreviewFile(string path, double start, double end) : IDisposable
{
    public string Path { get; } = path;
    public double Start { get; } = start;
    public double End { get; } = end;

    public void Dispose()
    {
        // The path belongs to this service; do not remove anything if a caller supplied another file.
        string? folder = System.IO.Path.GetDirectoryName(Path);
        string root = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppPaths.Cache, "clip-previews")) + System.IO.Path.DirectorySeparatorChar;
        if (folder == null || !System.IO.Path.GetFullPath(folder).StartsWith(root, StringComparison.OrdinalIgnoreCase)) return;
        try { Directory.Delete(folder, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}

public static class ClipPreview
{
    /// <summary>Long selections preview their first two minutes, rather than fetching an entire long chapter.</summary>
    public const double MaximumSeconds = 120;

    public static double EndFor(double start, double end)
    {
        if (!double.IsFinite(start) || !double.IsFinite(end) || start < 0 || end <= start)
            throw new ArgumentOutOfRangeException(nameof(end));
        return Math.Min(end, start + MaximumSeconds);
    }

    public static List<string> Arguments(string url, double start, double end, string folder, string proxy, IEnumerable<string> auth)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("A preview needs an HTTP or HTTPS video link.", nameof(url));
        double boundedEnd = EndFor(start, end);
        string section = "*" + start.ToString("0.###", CultureInfo.InvariantCulture) + "-" + boundedEnd.ToString("0.###", CultureInfo.InvariantCulture);
        var args = Engine.CommonArgs(auth, proxy);
        args.AddRange(["--no-playlist", "--playlist-items", "1", "--no-simulate", "--no-progress", "--no-warnings",
            "--format", "bv*[height<=360]+ba/b[height<=360]/worst", "--format-sort", "res:360,vcodec:h264,acodec:aac",
            "--download-sections", section, "--force-keyframes-at-cuts", "--merge-output-format", "mkv",
            "--paths", folder, "--output", "preview.%(ext)s", "--print", "after_move:__PREVIEW__%(filepath)s", "--", url]);
        return args;
    }

    public static async Task<ClipPreviewFile> PrepareAsync(string url, double start, double end, string proxy, ProcessRunner runner, Action<string>? log = null)
    {
        double boundedEnd = EndFor(start, end);
        string folder = System.IO.Path.Combine(AppPaths.Cache, "clip-previews", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string? file = null;
        var diagnostics = new DownloadDiagnostics();
        var lease = Auth.Lease();
        RunResult? result = null;
        bool keep = false;
        try
        {
            result = await runner.RunAsync(ToolManager.YtDlp, Arguments(url, start, boundedEnd, folder, proxy, lease.Args), line =>
            {
                if (line.StartsWith("__PREVIEW__", StringComparison.Ordinal)) file = line[11..];
                else
                {
                    diagnostics.Add(line);
                    log?.Invoke(line);
                }
            }).ConfigureAwait(false);
            if (result.Cancelled) throw new OperationCanceledException();
            if (result.ExitCode != 0) throw new IOException(diagnostics.FailureMessage(result.ExitCode));
            if (file == null || !File.Exists(file))
                throw new IOException("The preview downloader did not create a video file.");
            // Only accept the file in our fresh directory, even if a tool printed a surprising path.
            if (!System.IO.Path.GetFullPath(file).StartsWith(System.IO.Path.GetFullPath(folder) + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("The preview returned an unexpected file path.");
            keep = true;
            return new ClipPreviewFile(file, start, boundedEnd);
        }
        finally
        {
            lease.Complete(result is { ExitCode: 0, Cancelled: false });
            if (!keep)
            {
                try { Directory.Delete(folder, true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>Keep the cause before yt-dlp's final "ffmpeg exited" line, without retaining an unlimited tool log.</summary>
    internal sealed class DownloadDiagnostics
    {
        readonly Queue<string> lines = new();

        public void Add(string line)
        {
            line = line.Trim();
            if (line.Length == 0) return;
            if (line.Length > 500) line = line[..500] + "…";
            lock (lines)
            {
                if (lines.Count == 6) lines.Dequeue();
                lines.Enqueue(line);
            }
        }

        public string FailureMessage(int exitCode)
        {
            string message = $"The preview could not be downloaded (exit code {exitCode}).";
            lock (lines)
                return lines.Count == 0 ? message : message + Environment.NewLine + string.Join(Environment.NewLine, lines);
        }
    }
}
