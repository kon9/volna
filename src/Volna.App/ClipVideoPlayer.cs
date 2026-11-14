using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using LibVLCSharp.Avalonia;
using LibVLCSharp.Shared;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Volna.App;

public sealed class ClipPlaybackUnavailableException : InvalidOperationException
{
    public ClipPlaybackUnavailableException() { }
    public ClipPlaybackUnavailableException(string message) : base(message) { }
    public ClipPlaybackUnavailableException(string message, Exception innerException) : base(message, innerException) { }
    public bool MissingBundledRuntime { get; init; }
}

/// <summary>Local preview playback. Network access and cookies remain in yt-dlp, never in the player.</summary>
public sealed class ClipVideoPlayer : UserControl, IDisposable
{
    VideoView? view;
    LibVLC? library;
    MediaPlayer? player;
    EventHandler<EventArgs>? endedHandler, failedHandler;
    bool disposed;
    int generation;

    public event Action? Finished;
    public event Action? Failed;
    public bool IsPlaying => player?.IsPlaying == true;
    public double Seconds => Math.Max(0, (player?.Time ?? 0) / 1000.0);
    public double Duration => Math.Max(0, (player?.Length ?? 0) / 1000.0);
    public bool HasAudio => player?.AudioTrackCount > 0;
    public int Volume { get => player?.Volume ?? 100; set { if (player != null) player.Volume = Math.Clamp(value, 0, 100); } }

    public ClipVideoPlayer()
    {
        Background = Brushes.Black;
        IsVisible = false;
    }

    /// <summary>Validate and load the player before spending time downloading a preview. Call on the UI thread.</summary>
    public void PreparePlayback()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        EnsureLibrary();
    }

    /// <summary>Call from the UI thread, after adding the control to the visible window.</summary>
    public async Task PlayFileAsync(string path)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!File.Exists(path)) throw new FileNotFoundException("The preview file is missing.", path);
        Stop();
        PreparePlayback();
        IsVisible = true;
        int current = generation;
        try
        {
            // These small previews do not need hardware decoding; software avoids driver-specific seek failures.
            var starting = new MediaPlayer(library!) { EnableHardwareDecoding = false };
            player = starting;
            view!.MediaPlayer = starting;
            // A previously hidden ContentControl may not create its native child during UpdateLayout.
            // Let Avalonia finish attaching it before VLC starts, otherwise VLC creates a floating window.
            UpdateLayout();
            var attaching = Stopwatch.StartNew();
            while (true)
            {
                if (disposed || current != generation || player != starting) throw new OperationCanceledException();
                if (!OperatingSystem.IsWindows() || starting.Hwnd != IntPtr.Zero) break;
                if (attaching.Elapsed > TimeSpan.FromSeconds(5))
                    throw new ClipPlaybackUnavailableException("The video preview surface could not be attached to the application window.");
                await Task.Delay(16);
            }
            endedHandler = (_, _) => PostEvent(false, current);
            failedHandler = (_, _) => PostEvent(true, current);
            starting.EndReached += endedHandler;
            starting.EncounteredError += failedHandler;
            using var media = new Media(library!, new Uri(System.IO.Path.GetFullPath(path)));
            media.AddOption(":no-video-title-show");
            if (!starting.Play(media)) throw new InvalidOperationException("The preview could not be played.");
        }
        catch { if (current == generation) Stop(); throw; }
    }

    void EnsureLibrary()
    {
        if (library != null) return;
        // .NET single-file builds may extract the native runtime away from the executable. Resolve it
        // explicitly on Windows; on macOS and Linux use LibVLCSharp's platform-specific discovery.
        // Loading stays lazy so a missing player never breaks ordinary downloads.
        LibVLC? created = null;
        try
        {
            LibVLCSharp.Shared.Core.Initialize(OperatingSystem.IsWindows() ? WindowsRuntimeDirectory() : null);
            created = new LibVLC("--no-video-title-show", "--no-snapshot-preview", "--no-osd");
            var host = new VideoView();
            Content = host;
            view = host;
            library = created;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Content = null;
            created?.Dispose();
            if (ex is ClipPlaybackUnavailableException) throw;
            throw new ClipPlaybackUnavailableException("The video preview runtime could not be loaded.", ex);
        }
    }

    static string WindowsRuntimeDirectory()
    {
        string architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        var roots = new[] { AppContext.BaseDirectory }.Concat(
            (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") as string ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string folder = Path.Combine(root, "libvlc", "win-" + architecture);
            if (IsCompleteRuntime(folder)) return folder;
        }
        // The .NET 10 single-file host does not always expose its extraction directory in AppContext.
        // Its assembly-aware native loader still resolves bundled relative paths. Find the loaded
        // module's actual directory so VLC can discover the adjacent plugins, then release our probe refs.
        nint core = 0, vlc = 0;
        try
        {
            string bundled = Path.Combine("libvlc", "win-" + architecture);
            core = NativeLibrary.Load(Path.Combine(bundled, "libvlccore.dll"), typeof(ClipVideoPlayer).Assembly, null);
            vlc = NativeLibrary.Load(Path.Combine(bundled, "libvlc.dll"), typeof(ClipVideoPlayer).Assembly, null);
            using var process = Process.GetCurrentProcess();
            foreach (ProcessModule module in process.Modules)
            {
                if (module.BaseAddress == vlc && Path.GetDirectoryName(module.FileName) is { } folder
                    && IsCompleteRuntime(folder)) return folder;
            }
        }
        catch (DllNotFoundException) { }
        finally
        {
            if (vlc != 0) NativeLibrary.Free(vlc);
            if (core != 0) NativeLibrary.Free(core);
        }
        throw new ClipPlaybackUnavailableException("The bundled VLC libraries or plugins are missing. Extract the complete application archive before starting Volna.")
        { MissingBundledRuntime = true };
    }

    static bool IsCompleteRuntime(string folder) => File.Exists(Path.Combine(folder, "libvlc.dll"))
        && File.Exists(Path.Combine(folder, "libvlccore.dll"))
        && Directory.Exists(Path.Combine(folder, "plugins"));

    void PostEvent(bool error, int current)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (disposed || current != generation) return;
            // Never call libvlc Stop/Dispose inside its own event callback: it can deadlock.
            Stop();
            if (error) Failed?.Invoke(); else Finished?.Invoke();
        });
    }

    public void SetPaused(bool paused) => player?.SetPause(paused);

    public void Seek(double seconds)
    {
        if (player is { IsSeekable: true } && double.IsFinite(seconds))
            player.Time = (long)(Math.Clamp(seconds, 0, Duration) * 1000);
    }

    public void Stop()
    {
        generation++;
        IsVisible = false;
        if (player == null) return;
        if (endedHandler != null) player.EndReached -= endedHandler;
        if (failedHandler != null) player.EncounteredError -= failedHandler;
        endedHandler = failedHandler = null;
        player.Stop();
        if (view != null) view.MediaPlayer = null;
        player.Dispose();
        player = null;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Stop();
        if (view != null) view.MediaPlayer = null;
        Content = null;
        library?.Dispose();
        player = null;
        library = null;
        GC.SuppressFinalize(this);
    }
}
