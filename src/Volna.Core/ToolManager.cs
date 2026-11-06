using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Volna.Core;

public sealed record ToolProgress(string Tool, double Fraction, string Message);

public sealed record ToolUpdate(bool Ok, bool Changed);

/// <summary>
/// Finds yt-dlp, FFmpeg and Deno, and installs missing ones into the data folder from their official releases.
/// They are not bundled: FFmpeg's GPL builds and yt-dlp's frequent updates are both better served this way,
/// and every download is checked against the SHA-256 its release publishes.
/// </summary>
public static class ToolManager
{
    static string Exe(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    public static string? Find(string name)
    {
        string own = Path.Combine(AppPaths.Tools, Exe(name));
        if (File.Exists(own)) return own;
        // Next to the program: for distributions that do bundle them.
        string beside = Path.Combine(AppContext.BaseDirectory, "tools", Exe(name));
        if (File.Exists(beside)) return beside;
        foreach (string folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(folder.Trim('"'), Exe(name));
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }

    public static string YtDlp => Find("yt-dlp") ?? throw new FileNotFoundException(Loc.T("tools.missing", "yt-dlp"));
    public static string? Ffmpeg => Find("ffmpeg");
    public static string? Ffprobe => Find("ffprobe");
    public static string? Deno => Find("deno");

    /// <summary>Updates yt-dlp through its own updater. Changed is false when it was already the newest.</summary>
    public static async Task<ToolUpdate> UpdateYtDlpAsync(string proxy, bool nightly, Action<string> log)
    {
        var args = new List<string> { "--ignore-config", "--no-plugin-dirs", "--socket-timeout", "30" };
        args.AddRange(nightly ? ["--update-to", "nightly"] : ["--update"]);
        if (proxy.Length > 0) { args.Add("--proxy"); args.Add(proxy); }
        var output = new System.Text.StringBuilder();
        using var runner = new ProcessRunner();
        RunResult result = await runner.RunAsync(YtDlp, args, line =>
        {
            lock (output) output.AppendLine(line);
            log(line);
        }).ConfigureAwait(false);
        if (result.ExitCode != 0) return new ToolUpdate(false, false);
        try { File.WriteAllText(UpdateStamp, DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        lock (output) return new ToolUpdate(true, !output.ToString().Contains("up to date", StringComparison.OrdinalIgnoreCase));
    }

    static string UpdateStamp => Path.Combine(AppPaths.Tools, "yt-dlp.checked");

    /// <summary>Sites change often, so a copy this app installed is kept fresh. One installed elsewhere is its owner's to update.</summary>
    public static bool UpdateDue()
    {
        if (Find("yt-dlp") is not { } exe || !exe.StartsWith(AppPaths.Tools, StringComparison.OrdinalIgnoreCase)) return false;
        DateTime last = File.Exists(UpdateStamp) ? File.GetLastWriteTimeUtc(UpdateStamp) : File.GetLastWriteTimeUtc(exe);
        return DateTime.UtcNow - last > TimeSpan.FromDays(3);
    }

    /// <summary>
    /// What needs installing. Deno is optional in the sense that yt-dlp starts without it, but YouTube then loses formats.
    /// yt-dlp needs a copy of its own: one from a distribution can lag behind by months and refuses to update itself.
    /// </summary>
    public static IReadOnlyList<string> Missing()
    {
        var missing = new List<string>();
        foreach (string name in Wanted) if (name == "yt-dlp" ? !HasOwnYtDlp : Find(name) == null) missing.Add(name);
        return missing;
    }

    static bool HasOwnYtDlp => File.Exists(Path.Combine(AppPaths.Tools, Exe("yt-dlp"))) || File.Exists(Path.Combine(AppContext.BaseDirectory, "tools", Exe("yt-dlp")));

    /// <summary>Tools that cannot run at all, wherever they come from.</summary>
    public static IReadOnlyList<string> Unavailable() => Wanted.Where(name => Find(name) == null).ToList();

    /// <summary>Removes tools of this app's own that no longer start, so that they install again. True when any did not.</summary>
    public static async Task<bool> RemoveBrokenAsync()
    {
        async Task<bool> Broken(string name, string flag)
        {
            string own = Path.Combine(AppPaths.Tools, Exe(name));
            if (!File.Exists(own)) return false;
            using var runner = new ProcessRunner();
            bool works;
            try
            {
                Task<RunResult> run = runner.RunAsync(own, [flag], _ => { });
                // A slow start is not a broken tool.
                if (await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(30))).ConfigureAwait(false) != run) { runner.Cancel(); return false; }
                works = (await run.ConfigureAwait(false)).ExitCode == 0;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException) { works = false; }
            if (works) return false;
            try { File.Delete(own); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
            return true;
        }
        bool[] results = await Task.WhenAll(Broken("yt-dlp", "--version"), Broken("ffmpeg", "-version"), Broken("ffprobe", "-version"), Broken("deno", "--version")).ConfigureAwait(false);
        return results.Any(b => b);
    }

    /// <summary>The folder holding ffmpeg, for yt-dlp's --ffmpeg-location; null lets yt-dlp look on PATH.</summary>
    public static string? FfmpegFolder => Ffmpeg is { } path ? Path.GetDirectoryName(path) : null;

    static readonly Dictionary<string, HttpClient> Clients = new();

    // Unlike ordinary requests a tool can take long to arrive, so there is no overall time limit here.
    static HttpClient Http(string proxy)
    {
        lock (Clients)
        {
            if (!Clients.TryGetValue(proxy, out HttpClient? client)) Clients[proxy] = client = CreateClient(proxy);
            return client;
        }
    }

    static HttpClient CreateClient(string proxy)
    {
        var handler = new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All };
        if (proxy.Length > 0) { handler.Proxy = Web.Proxy(proxy); handler.UseProxy = true; }
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(AppPaths.AppName, "1.0"));
        return client;
    }

    sealed record Source(string Tool, string Repository, string Asset);

    static (string YtDlp, string? Ffmpeg, string Deno) Assets()
    {
        bool arm = RuntimeInformation.OSArchitecture == Architecture.Arm64;
        if (OperatingSystem.IsWindows())
            return ("yt-dlp.exe", arm ? "ffmpeg-master-latest-winarm64-gpl.zip" : "ffmpeg-master-latest-win64-gpl.zip", "deno-x86_64-pc-windows-msvc.zip");
        if (OperatingSystem.IsMacOS())
            return ("yt-dlp_macos", null, arm ? "deno-aarch64-apple-darwin.zip" : "deno-x86_64-apple-darwin.zip");
        return (arm ? "yt-dlp_linux_aarch64" : "yt-dlp_linux",
            arm ? "ffmpeg-master-latest-linuxarm64-gpl.tar.xz" : "ffmpeg-master-latest-linux64-gpl.tar.xz",
            arm ? "deno-aarch64-unknown-linux-gnu.zip" : "deno-x86_64-unknown-linux-gnu.zip");
    }

    /// <summary>Downloads whatever is missing. Safe to call again after a failure: finished tools are kept.</summary>
    public static async Task InstallMissingAsync(IProgress<ToolProgress> progress, CancellationToken cancel, string proxy = "")
    {
        var (ytDlp, ffmpeg, deno) = Assets();
        var missing = Missing();
        if (missing.Contains("yt-dlp")) await InstallFromGitHubAsync("yt-dlp", "yt-dlp/yt-dlp", ytDlp, "SHA2-256SUMS", proxy, progress, cancel);
        if (missing.Contains("ffmpeg") || missing.Contains("ffprobe"))
        {
            if (ffmpeg != null) await InstallFromGitHubAsync("ffmpeg", "yt-dlp/FFmpeg-Builds", ffmpeg, "checksums.sha256", proxy, progress, cancel);
            else await InstallMacFfmpegAsync(proxy, progress, cancel);
        }
        if (missing.Contains("deno")) await InstallFromGitHubAsync("deno", "denoland/deno", deno, deno + ".sha256sum", proxy, progress, cancel);
    }

    static async Task InstallFromGitHubAsync(string tool, string repository, string asset, string sums, string proxy, IProgress<ToolProgress> progress, CancellationToken cancel)
    {
        progress.Report(new ToolProgress(tool, 0, Loc.T("tools.finding", tool)));
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/" + repository + "/releases/latest");
        using var response = await Http(proxy).SendAsync(request, cancel);
        string latest = "https://github.com/" + repository + "/releases/latest/download/";
        // GitHub's API allows few anonymous requests from one address. The releases themselves have no such limit, and
        // publish the same checksums in a file.
        if ((int)response.StatusCode is 403 or 429)
        {
            string list = await Http(proxy).GetStringAsync(latest + sums, cancel);
            await DownloadAndInstallAsync(tool, latest + asset, asset, HashFor(list, asset) ?? throw new InvalidDataException(Loc.T("tools.badhash", asset)), proxy, progress, cancel);
            return;
        }
        response.EnsureSuccessStatusCode();
        var release = Json.ParseObject(await response.Content.ReadAsStringAsync(cancel)) ?? throw new InvalidDataException("Unexpected reply from GitHub.");
        var entry = (release.Raw("assets") as List<object?> ?? new()).OfType<Dictionary<string, object?>>().FirstOrDefault(a => a.Str("name") == asset)
            ?? throw new FileNotFoundException(Loc.T("tools.noasset", asset, repository));
        string url = entry.Str("browser_download_url") ?? throw new InvalidDataException("No download link for " + asset);
        string? digest = entry.Str("digest");
        await DownloadAndInstallAsync(tool, url, asset, digest != null && digest.StartsWith("sha256:", StringComparison.Ordinal) ? digest[7..] : null, proxy, progress, cancel);
    }

    /// <summary>The checksum of this file in a release's list of them. A list of one need not name it. Public for tests.</summary>
    public static string? HashFor(string list, string asset)
    {
        string[] lines = list.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var named = lines.Where(l => l.EndsWith(asset, StringComparison.OrdinalIgnoreCase)).ToList();
        // Some are not one line per file: on Windows, Deno's lists the hash and the path on separate lines.
        foreach (string line in named.Concat(lines.Length <= 8 ? lines : Array.Empty<string>()))
        {
            var match = System.Text.RegularExpressions.Regex.Match(line, "\\b[0-9a-fA-F]{64}\\b");
            if (match.Success) return match.Value;
        }
        return null;
    }

    // FFmpeg-Builds has no macOS builds; these are the ones the FFmpeg site links to.
    static async Task InstallMacFfmpegAsync(string proxy, IProgress<ToolProgress> progress, CancellationToken cancel)
    {
        string path = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64/snapshot" : "amd64/release";
        foreach (string name in new[] { "ffmpeg", "ffprobe" })
            await DownloadAndInstallAsync(name, "https://ffmpeg.martin-riedl.de/redirect/latest/macos/" + path + "/" + name + ".zip", name + ".zip", null, proxy, progress, cancel);
    }

    static async Task DownloadAndInstallAsync(string tool, string url, string asset, string? sha256, string proxy, IProgress<ToolProgress> progress, CancellationToken cancel)
    {
        string work = Path.Combine(AppPaths.Cache, "install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            string file = Path.Combine(work, asset);
            await DownloadAsync(tool, url, file, proxy, progress, cancel);
            if (sha256 != null)
            {
                await using var stream = File.OpenRead(file);
                string actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancel));
                if (!actual.Equals(sha256, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(Loc.T("tools.badhash", asset));
            }
            progress.Report(new ToolProgress(tool, 1, Loc.T("tools.unpacking", tool)));
            await Task.Run(() => Unpack(file, work, cancel), cancel);
        }
        finally
        {
            try { Directory.Delete(work, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    static readonly TimeSpan StallLimit = TimeSpan.FromSeconds(60);

    static async Task DownloadAsync(string tool, string url, string target, string proxy, IProgress<ToolProgress> progress, CancellationToken cancel)
    {
        // A connection can go silent without ever failing, and then only a time limit says so.
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        stall.CancelAfter(StallLimit);
        using var response = await Http(proxy).GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stall.Token);
        response.EnsureSuccessStatusCode();
        long? total = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync(cancel);
        await using var output = File.Create(target);
        var buffer = new byte[1 << 16];
        long done = 0;
        var clock = Stopwatch.StartNew();
        int read;
        while (true)
        {
            stall.CancelAfter(StallLimit);
            try { read = await input.ReadAsync(buffer, stall.Token); }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { throw new TimeoutException(Loc.T("tools.stalled", tool)); }
            if (read == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, read), cancel);
            done += read;
            if (clock.ElapsedMilliseconds < 120) continue;
            clock.Restart();
            string size = total is > 0 ? Format.Bytes(done) + " / " + Format.Bytes(total.Value) : Format.Bytes(done);
            progress.Report(new ToolProgress(tool, total is > 0 ? (double)done / total.Value : -1, Loc.T("tools.downloading", tool, size)));
        }
    }

    static readonly string[] Wanted = { "yt-dlp", "ffmpeg", "ffprobe", "deno" };

    /// <summary>Puts the wanted programs from a download into the tools folder. Public for tests.</summary>
    public static void Unpack(string file, string work, CancellationToken cancel = default)
    {
        string name = Path.GetFileName(file);
        if (name.StartsWith("yt-dlp", StringComparison.Ordinal)) { Place(file, "yt-dlp"); return; }
        string unpacked = Path.Combine(work, "unpacked");
        Directory.CreateDirectory(unpacked);
        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) ZipFile.ExtractToDirectory(file, unpacked);
        else if (name.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase)) ExtractXz(file, unpacked);
        else if (name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            using var stream = new GZipStream(File.OpenRead(file), CompressionMode.Decompress);
            TarFile.ExtractToDirectory(stream, unpacked, true);
        }
        foreach (string found in Directory.EnumerateFiles(unpacked, "*", SearchOption.AllDirectories))
        {
            cancel.ThrowIfCancellationRequested();
            string tool = Path.GetFileNameWithoutExtension(found);
            if (Wanted.Contains(tool) && Path.GetFileName(found) == Exe(tool)) Place(found, tool);
        }
    }

    // .NET cannot read xz; every Linux that runs this has tar with xz support.
    static void ExtractXz(string file, string folder)
    {
        var info = new ProcessStartInfo("tar") { UseShellExecute = false, RedirectStandardError = true };
        foreach (string arg in new[] { "-xJf", file, "-C", folder }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start tar.");
        string errors = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidDataException(Loc.T("tools.notar", errors.Trim()));
    }

    static void Place(string source, string tool)
    {
        string target = Path.Combine(AppPaths.Tools, Exe(tool));
        // Into place in one step, so that an interrupted setup never leaves half a program that looks installed.
        string temporary = target + ".new";
        File.Copy(source, temporary, true);
        File.Move(temporary, target, true);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            // A browser-downloaded file is quarantined on macOS, and a quarantined tool would ask for approval on each start.
            if (OperatingSystem.IsMacOS())
            {
                try
                {
                    var info = new ProcessStartInfo("xattr") { UseShellExecute = false, RedirectStandardError = true };
                    foreach (string arg in new[] { "-d", "com.apple.quarantine", target }) info.ArgumentList.Add(arg);
                    using var process = Process.Start(info);
                    process?.WaitForExit();
                }
                catch (System.ComponentModel.Win32Exception) { }
            }
        }
    }
}
