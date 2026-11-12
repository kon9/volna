using System.Security.Cryptography;
using System.Text;

namespace Volna.Core;

public sealed record LookupResult(List<Dictionary<string, object?>> Entries, string Errors, bool Cancelled);

/// <summary>Everything the queue asks of the outside world, so its logic can run in tests and in the demo without a network.</summary>
public interface IQueueBackend
{
    Task<LookupResult> LookupAsync(IReadOnlyList<string> requests, int limit, string proxy, ProcessRunner runner, Action<string> log, Action<int>? found = null);
    Task<List<Dictionary<string, object?>>> SearchMusicAsync(string query, int limit, string proxy);
    Task<RunResult> DownloadAsync(DownloadOptions options, ProcessRunner runner, Action<string> onLine);
    Task<byte[]?> ThumbnailAsync(string url, string proxy);
    /// <summary>The preview frames of a video, for choosing a part of it. Null where the site has none.</summary>
    Task<(Storyboard? Detail, Storyboard? Overview)> StoryboardAsync(string url, string proxy, ProcessRunner runner);
    Task<ToolUpdate> UpdateToolAsync(string proxy, bool nightly, Action<string> log);
    Task<string?> LyricsAsync(string artist, string title, double? seconds, string proxy);
}

public sealed class YtDlpBackend : IQueueBackend
{
    public async Task<LookupResult> LookupAsync(IReadOnlyList<string> requests, int limit, string proxy, ProcessRunner runner, Action<string> log, Action<int>? found = null)
    {
        var entries = new List<Dictionary<string, object?>>();
        var errors = new StringBuilder();
        var gate = new object();
        var lease = Auth.Lease();
        RunResult? result = null;
        try
        {
            result = await runner.RunAsync(ToolManager.YtDlp, Engine.MetadataArgs(requests, lease.Args, proxy, limit), line =>
            {
                if (line.StartsWith('{') && Json.ParseObject(line) is { } info)
                {
                    int count;
                    lock (gate)
                    {
                        entries.Add(info);
                        count = entries.Count;
                    }
                    found?.Invoke(count);
                    return;
                }
                if (line.StartsWith("ERROR", StringComparison.Ordinal)) lock (gate) errors.AppendLine(line);
                log(line);
            });
        }
        finally { lease.Complete(result is { ExitCode: 0, Cancelled: false }); }
        return new LookupResult(entries, errors.ToString(), result.Cancelled);
    }

    public Task<List<Dictionary<string, object?>>> SearchMusicAsync(string query, int limit, string proxy) => MusicSearch.SearchAsync(query, limit, proxy);

    public async Task<RunResult> DownloadAsync(DownloadOptions options, ProcessRunner runner, Action<string> onLine)
    {
        var lease = Auth.Lease();
        RunResult? result = null;
        try
        {
            result = await runner.RunAsync(ToolManager.YtDlp, Engine.DownloadArgs(options, lease.Args), onLine);
            return result;
        }
        finally { lease.Complete(result is { ExitCode: 0, Cancelled: false }); }
    }

    public Task<ToolUpdate> UpdateToolAsync(string proxy, bool nightly, Action<string> log) => ToolManager.UpdateYtDlpAsync(proxy, nightly, log);

    public async Task<(Storyboard? Detail, Storyboard? Overview)> StoryboardAsync(string url, string proxy, ProcessRunner runner)
    {
        var lease = Auth.Lease();
        RunResult? result = null;
        List<object?>? formats = null;
        try
        {
            result = await runner.RunAsync(ToolManager.YtDlp, Engine.StoryboardArgs(url, lease.Args, proxy), line =>
            {
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    try { formats = Json.Parse(line) as List<object?>; }
                    catch (System.Text.Json.JsonException) { }
                }
            });
        }
        finally { lease.Complete(result is { ExitCode: 0, Cancelled: false }); }
        return formats == null ? (null, null) : Storyboard.FromFormats(formats.OfType<Dictionary<string, object?>>());
    }

    public Task<string?> LyricsAsync(string artist, string title, double? seconds, string proxy) => Lyrics.FindAsync(artist, title, seconds, proxy);

    public async Task<byte[]?> ThumbnailAsync(string url, string proxy)
    {
        string cached = ThumbPath(url);
        if (File.Exists(cached))
        {
            try { return await File.ReadAllBytesAsync(cached); }
            catch (IOException) { }
        }
        byte[] data = await Web.Client(proxy).GetByteArrayAsync(url);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cached)!);
            await File.WriteAllBytesAsync(cached, data);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // the cache is only an optimization
        return data;
    }

    static string ThumbPath(string url)
    {
        string name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
        return Path.Combine(AppPaths.Cache, "thumbs", name[..2], name + ".img");
    }

    /// <summary>Previews nobody has looked at for two months are removed so the cache does not grow forever.</summary>
    public static void PruneThumbnails()
    {
        Task.Run(() =>
        {
            try
            {
                var folder = new DirectoryInfo(Path.Combine(AppPaths.Cache, "thumbs"));
                if (!folder.Exists) return;
                DateTime limit = DateTime.Now.AddDays(-60);
                foreach (var file in folder.EnumerateFiles("*.img", SearchOption.AllDirectories))
                    if (file.LastWriteTime < limit) file.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        });
    }
}
