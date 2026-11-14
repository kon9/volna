using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using LibVLCSharp.Avalonia;
using LibVLCSharp.Shared;

namespace Volna.App;

public sealed class ClipPlaybackUnavailableException : InvalidOperationException
{
    public ClipPlaybackUnavailableException() { }
    public ClipPlaybackUnavailableException(string message) : base(message) { }
    public ClipPlaybackUnavailableException(string message, Exception innerException) : base(message, innerException) { }
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

    /// <summary>Call from the UI thread, after adding the control to the visible window.</summary>
    public void PlayFile(string path)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!File.Exists(path)) throw new FileNotFoundException("The preview file is missing.", path);
        Stop();
        EnsureLibrary();
        IsVisible = true;
        try
        {
            // These small previews do not need hardware decoding; software avoids driver-specific seek failures.
            player = new MediaPlayer(library!) { EnableHardwareDecoding = false };
            view!.MediaPlayer = player;
            // Attach the native host before starting, otherwise VLC may open a separate video window.
            UpdateLayout();
            int current = generation;
            endedHandler = (_, _) => PostEvent(false, current);
            failedHandler = (_, _) => PostEvent(true, current);
            player.EndReached += endedHandler;
            player.EncounteredError += failedHandler;
            using var media = new Media(library!, new Uri(System.IO.Path.GetFullPath(path)));
            media.AddOption(":no-video-title-show");
            if (!player.Play(media)) throw new InvalidOperationException("The preview could not be played.");
        }
        catch { Stop(); throw; }
    }

    void EnsureLibrary()
    {
        if (library != null) return;
        // LibVLCSharp finds the official NuGet runtimes beside the application on Windows and macOS,
        // and the distribution's libvlc on Linux. Loading is lazy so a missing player never breaks downloads.
        LibVLC? created = null;
        try
        {
            LibVLCSharp.Shared.Core.Initialize();
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
            throw new ClipPlaybackUnavailableException("The video preview runtime could not be loaded.", ex);
        }
    }

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
