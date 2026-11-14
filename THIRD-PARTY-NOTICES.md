# Third-party notices

## Included in Volna

| Component | License |
| --- | --- |
| [.NET runtime](https://github.com/dotnet/runtime) | MIT |
| [Avalonia UI](https://github.com/AvaloniaUI/Avalonia) | MIT |
| [LibVLCSharp](https://code.videolan.org/videolan/LibVLCSharp), including its Avalonia view | LGPL-2.1-or-later |
| [LibVLC](https://code.videolan.org/videolan/vlc) in Windows and macOS packages, from [VideoLAN's official native packages](https://code.videolan.org/videolan/libvlc-nuget) | LGPL-2.1-or-later; bundled modules retain their respective licenses |
| [SkiaSharp](https://github.com/mono/SkiaSharp), [HarfBuzzSharp](https://github.com/mono/SkiaSharp) | MIT |
| [Inter](https://github.com/rsms/inter) typeface | SIL Open Font License 1.1 |
| [Lucide](https://github.com/lucide-icons/lucide) icons | ISC |

LibVLC provides local preview playback. Its native libraries and plugin directories remain separate from the executable so they can be replaced. Linux uses the distribution's LibVLC package. A copy of the LGPL accompanies binary packages in `licenses/LGPL-2.1.txt`. Upstream source and license notices are available through the links above and [VideoLAN's LibVLC licensing page](https://www.videolan.org/vlc/libvlc.html).

## Downloaded on first run

These are separate programs. They are not part of Volna's code or its packages: Volna fetches them from their official releases and starts them as separate processes. Each comes with its own license.

| Program | Source | License |
| --- | --- | --- |
| [yt-dlp](https://github.com/yt-dlp/yt-dlp) | GitHub releases of `yt-dlp/yt-dlp` | Unlicense |
| [FFmpeg](https://ffmpeg.org/) | GitHub releases of `yt-dlp/FFmpeg-Builds` (Windows, Linux); [martin-riedl.de](https://ffmpeg.martin-riedl.de/) (macOS) | GPL (these builds) |
| [Deno](https://github.com/denoland/deno) | GitHub releases of `denoland/deno` | MIT |

## Online services

- [SponsorBlock](https://sponsor.ajay.app/): segment data, used only when cutting is on. It is queried with a hashed prefix of the video's id.
- YouTube Music's web interface: song search.
