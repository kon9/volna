#!/usr/bin/env bash
# Remakes the pictures and the animation in docs/media from the real interface. Needs ffmpeg.
set -euo pipefail
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
dotnet run -c Release --project tools/Demo/Demo.csproj -- pictures docs/media
dotnet run -c Release --project tools/Demo/Demo.csproj -- frames "$WORK"
ffmpeg -y -v error -framerate 15 -i "$WORK/frame%05d.png" -vf "scale=1600:-2:flags=lanczos,format=yuv420p" -c:v libx264 -crf 20 -preset slow -movflags +faststart docs/media/demo.mp4
ffmpeg -y -v error -framerate 15 -i "$WORK/frame%05d.png" -filter_complex "fps=12,scale=1100:-2:flags=lanczos,split[a][b];[a]palettegen=max_colors=200:stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle" docs/media/demo.gif
ls -la docs/media
