using Avalonia.Threading;
using Volna.Core;

namespace Volna.App;

public partial class MainWindow
{
    ClipVideoPlayer? clipPlayer;
    ClipPreviewFile? clipPreviewFile;
    ProcessRunner? clipPreviewRunner;
    DispatcherTimer? clipPlaybackTimer;
    int clipPlaybackGeneration;
    bool clipPlaybackPaused, clipPlaybackMuted, clipSeekUpdating;

    async Task PlayClipAsync()
    {
        if (clipTarget == null) return;
        CommitClipTime();
        if (clipPreviewFile != null && clipPlayer != null)
        {
            clipPlaybackPaused = !clipPlaybackPaused;
            clipPlayer.SetPaused(clipPlaybackPaused);
            ClipPlayButton.Content = Loc.T(clipPlaybackPaused ? "clip.resume" : "clip.pause");
            return;
        }
        StopClipPlayback();
        int generation = clipGeneration, playbackGeneration = clipPlaybackGeneration;
        double start = clipRange.Start, end = clipRange.End;
        string url = clipTarget.WebpageUrl ?? clipTarget.SourceUrl;
        ClipPlayButton.IsEnabled = false;
        ClipStopButton.IsVisible = true;
        ClipPlaybackStatus.Text = Loc.T("clip.preview.loading");
        using var runner = new ProcessRunner();
        clipPreviewRunner = runner;
        ClipPreviewFile? prepared = null;
        try
        {
            if (clipPlayer == null)
            {
                clipPlayer = new ClipVideoPlayer();
                ClipPlaybackHost.Content = clipPlayer;
                clipPlayer.Finished += StopClipPlayback;
                clipPlayer.Failed += () => { StopClipPlayback(); ClipPlaybackStatus.Text = Loc.T("clip.preview.failed"); };
            }
            // Check the local player first: a missing runtime must not waste a network download.
            clipPlayer.PreparePlayback();
            prepared = await Volna.Core.ClipPreview.PrepareAsync(url, start, end, prefs.Proxy, runner,
                line => Dispatcher.UIThread.Post(() => Log(line)));
            if (generation != clipGeneration || playbackGeneration != clipPlaybackGeneration) return;
            ClipPlaybackHost.IsVisible = true;
            ClipTitleOverlay.IsVisible = ClipTimeOverlay.IsVisible = false;
            await clipPlayer.PlayFileAsync(prepared.Path);
            if (generation != clipGeneration || playbackGeneration != clipPlaybackGeneration) return;
            clipPlayer.Volume = clipPlaybackMuted ? 0 : 100;
            clipPreviewFile = prepared;
            prepared = null;
            clipSeekUpdating = true;
            ClipPlaybackSeek.Maximum = clipPreviewFile.End - clipPreviewFile.Start;
            ClipPlaybackSeek.Value = 0;
            clipSeekUpdating = false;
            ClipPlaybackSeek.IsVisible = true;
            ClipPlayButton.Content = Loc.T("clip.pause");
            ClipMuteButton.IsVisible = true;
            ClipMuteButton.Content = Loc.T(clipPlaybackMuted ? "clip.unmute" : "clip.mute");
            clipPlaybackTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => UpdateClipPlayback());
            clipPlaybackTimer.Start();
            UpdateClipPlayback();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (generation != clipGeneration || playbackGeneration != clipPlaybackGeneration) return;
            Log("Clip playback: " + ex);
            StopClipPlayback();
            ClipPlaybackStatus.Text = Loc.T(ex switch
            {
                ClipPlaybackUnavailableException { MissingBundledRuntime: true } => "clip.preview.package",
                ClipPlaybackUnavailableException when OperatingSystem.IsLinux() => "clip.preview.runtime",
                ClipPlaybackUnavailableException => "clip.preview.player_failed",
                _ => "clip.preview.failed"
            });
        }
        finally
        {
            prepared?.Dispose();
            if (generation == clipGeneration && playbackGeneration == clipPlaybackGeneration)
            {
                clipPreviewRunner = null;
                ClipPlayButton.IsEnabled = true;
            }
        }
    }

    void UpdateClipPlayback()
    {
        if (clipPlayer == null || clipPreviewFile == null) return;
        string position = Format.Duration(clipPreviewFile.Start + clipPlayer.Seconds) + " / " + Format.Duration(clipPreviewFile.End);
        ClipPlaybackStatus.Text = clipRange.End > clipPreviewFile.End ? Loc.T("clip.preview.limited") + " · " + position : position;
        ClipPreviewTime.Text = Format.Duration(clipPreviewFile.Start + clipPlayer.Seconds);
        clipSeekUpdating = true;
        ClipPlaybackSeek.Value = clipPlayer.Seconds;
        clipSeekUpdating = false;
    }

    void StopClipPlayback()
    {
        clipPlaybackGeneration++;
        clipPreviewRunner?.Cancel();
        clipPreviewRunner = null;
        clipPlaybackTimer?.Stop();
        clipPlayer?.Stop();
        clipPreviewFile?.Dispose();
        clipPreviewFile = null;
        clipPlaybackPaused = false;
        ClipPlaybackHost.IsVisible = false;
        ClipTitleOverlay.IsVisible = ClipTimeOverlay.IsVisible = true;
        ClipPlayButton.IsEnabled = true;
        ClipPlayButton.Content = Loc.T("clip.play");
        ClipStopButton.IsVisible = ClipMuteButton.IsVisible = false;
        ClipPlaybackSeek.IsVisible = false;
        ClipPlaybackStatus.Text = "";
    }

    void ToggleClipMute()
    {
        clipPlaybackMuted = !clipPlaybackMuted;
        if (clipPlayer != null) clipPlayer.Volume = clipPlaybackMuted ? 0 : 100;
        ClipMuteButton.Content = Loc.T(clipPlaybackMuted ? "clip.unmute" : "clip.mute");
    }
}
