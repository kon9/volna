#!/bin/sh
# Builds, tests and runs Volna inside a clean Linux container. Run with Docker from the repository root:
#   docker run --rm -v "$PWD:/src:ro" mcr.microsoft.com/dotnet/sdk:10.0 sh /src/tools/linux-check.sh
set -e
export CI=true # warnings fail the build, as in the workflows
apt-get update -qq >/dev/null
apt-get install -y -qq xz-utils xvfb libx11-6 libice6 libsm6 libfontconfig1 libxrandr2 libxi6 libxcursor1 xauth imagemagick >/dev/null 2>&1
cp -r /src /work && cd /work
find . -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +
echo "=== unit tests"; dotnet test tests/Volna.Tests/Volna.Tests.csproj --nologo -v q 2>&1 | tail -3
echo "=== interface smoke test"; dotnet run -c Release --project tools/Demo/Demo.csproj -- smoke 2>&1 | tail -2
echo "=== agent: first-run tool install and a real download"
dotnet build -c Release src/Volna.Agent/Volna.Agent.csproj --nologo -v q 2>&1 | tail -1
AGENT="dotnet src/Volna.Agent/bin/Release/net10.0/volna-agent.dll"
export VOLNA_FOLDER=/tmp/rw-downloads VOLNA_DATA=/tmp/rw-data
$AGENT account_status
dotnet run -c Release --project tools/Demo/Demo.csproj -- install 2>&1 | tail -4
$AGENT account_status
$AGENT youtube_search --query "Big Buck Bunny" --limit 1
# Big Buck Bunny: an open film by the Blender Foundation (CC BY), free to download.
$AGENT download --target "https://www.youtube.com/watch?v=aqz-KE-bpKQ" --kind audio
# Another site, in a format mp4 cannot hold: the same film's trailer on Wikimedia Commons (CC BY).
$AGENT download --target "https://commons.wikimedia.org/wiki/File:Big_Buck_Bunny_Trailer_400p.ogv" --kind video
$AGENT list_downloads
$AGENT media_info --path "$(find /tmp/rw-downloads -type f -printf '%f\n' | sort | head -1)" | cut -c1-300
echo "=== real window under X"
dotnet build -c Release src/Volna.App/Volna.App.csproj --nologo -v q 2>&1 | tail -1
export VOLNA_DATA=/tmp/rw-gui
# shellcheck disable=SC2016 # expanded by the inner shell
xvfb-run -a -s "-screen 0 1280x860x24" sh -c '
  dotnet src/Volna.App/bin/Release/net10.0/Volna.dll &
  APP=$!; sleep 12; import -window root /src-out/linux-window.png 2>/dev/null || true; kill $APP; wait $APP 2>/dev/null; echo "window ran"'
