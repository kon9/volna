#!/bin/sh
# Installs Volna for the current user: no root needed. Run it from the unpacked folder.
set -e
HERE="$(cd "$(dirname "$0")" && pwd)"
BIN="${XDG_BIN_HOME:-$HOME/.local/bin}"
DATA="${XDG_DATA_HOME:-$HOME/.local/share}"
mkdir -p "$BIN" "$DATA/applications" "$DATA/icons/hicolor/256x256/apps"
# Through a temporary name: a program that is running cannot be overwritten, but it can be replaced.
place() {
  cp "$HERE/$1" "$BIN/.$2.new"
  chmod +x "$BIN/.$2.new"
  mv -f "$BIN/.$2.new" "$BIN/$2"
}
place Volna volna
place volna-agent volna-agent
cp "$HERE/volna.png" "$DATA/icons/hicolor/256x256/apps/volna.png"
sed "s|^Exec=.*|Exec=\"$BIN/volna\" %U|" "$HERE/volna.desktop" > "$DATA/applications/volna.desktop"
update-desktop-database "$DATA/applications" 2>/dev/null || true
echo "Installed. Find Volna in your applications, or run: $BIN/volna"
