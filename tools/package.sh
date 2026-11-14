#!/usr/bin/env bash
# Builds one release package into dist/. Usage: tools/package.sh <runtime id> [version]
#   win-x64, linux-x64, linux-arm64, osx-arm64, osx-x64
# macOS packages must be built on macOS, because the bundle has to be signed there.
set -euo pipefail
RID="$1"
VERSION="${2:-1.1.0}"
OUT="dist/work/$RID"
rm -rf "$OUT"
mkdir -p "$OUT" dist

# Both programs carry their own .NET, so users install nothing.
COMMON=(-c Release -r "$RID" --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:Version="$VERSION" --nologo)
# Many Linux systems have no ICU, and without this a program that needs it refuses to start.
case "$RID" in linux-*) COMMON+=(-p:InvariantGlobalization=true) ;; esac
dotnet publish src/Volna.App/Volna.App.csproj "${COMMON[@]}" -o "$OUT/app"
dotnet publish src/Volna.Agent/Volna.Agent.csproj "${COMMON[@]}" -p:PublishTrimmed=true -p:TrimMode=partial -o "$OUT/agent"

NAME="Volna-$VERSION-$RID"
STAGE="$OUT/$NAME"
mkdir -p "$STAGE"
cp LICENSE THIRD-PARTY-NOTICES.md "$STAGE/"
cp -R packaging/licenses "$STAGE/licenses"

case "$RID" in
  win-*)
    cp "$OUT/app/Volna.exe" "$OUT/agent/volna-agent.exe" "$STAGE/"
    cp -R "$OUT/app/libvlc" "$STAGE/"
    # Windows tar decides the format by the extension, but the tar of Git Bash would write a tar under this name.
    (cd "$OUT" && powershell.exe -NoProfile -Command "Compress-Archive -Force -Path '$NAME' -DestinationPath '../../$NAME.zip'")
    ;;
  linux-*)
    cp "$OUT/app/Volna" "$OUT/agent/volna-agent" "$STAGE/"
    chmod +x "$STAGE/Volna" "$STAGE/volna-agent"
    cp assets/generated/icon-256.png "$STAGE/volna.png"
    cp packaging/linux/volna.desktop packaging/linux/install.sh "$STAGE/"
    chmod +x "$STAGE/install.sh"
    tar -C "$OUT" -czf "dist/$NAME.tar.gz" "$NAME"
    ;;
  osx-*)
    APP="$STAGE/Volna.app"
    mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
    cp "$OUT/app/Volna" "$OUT/agent/volna-agent" "$APP/Contents/MacOS/"
    cp -R "$OUT/app/libvlc" "$APP/Contents/MacOS/"
    chmod +x "$APP/Contents/MacOS/Volna" "$APP/Contents/MacOS/volna-agent"
    cp assets/generated/icon.icns "$APP/Contents/Resources/icon.icns"
    sed "s/@VERSION@/$VERSION/g" packaging/macos/Info.plist > "$APP/Contents/Info.plist"
    # No paid Apple certificate: an ad-hoc signature lets it run, and the README explains the first-start warning.
    codesign --force --deep --sign - "$APP"
    (cd "$OUT" && ditto -c -k --keepParent "$NAME" "../../$NAME.zip")
    ;;
  *) echo "Unknown runtime: $RID" >&2; exit 2 ;;
esac
ls -la dist
