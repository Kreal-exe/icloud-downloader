#!/bin/bash
# Builds iCloud Downloader for macOS (Avalonia window + the engine bundled as one executable), installs it into
# /Applications, clears the quarantine flag and opens it.
#
#   ./build-mac.sh          build, install into /Applications and open the app
#   ./build-mac.sh --dmg    also write dist/iCloudDownloader-macOS-v<version>.dmg for a release
#
# INSTALL_DIR=~/Applications ./build-mac.sh installs somewhere else. Needs python3 and the .NET 10 SDK (https://dot.net).
# The version is the <Version> of app/iCloudDownloader.csproj, shared with the Windows app.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")" && pwd)"
NAME="iCloudDownloader"
BUNDLE_ID="io.github.kreal-exe.icloud-downloader"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/app/iCloudDownloader.csproj" | head -1)"
DEST="${INSTALL_DIR:-/Applications}"
DMG=0
for arg in "$@"; do
  case "$arg" in
    --dmg) DMG=1 ;;
    *) sed -n '2,9p' "$0"; exit 1 ;;
  esac
done
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

command -v python3 >/dev/null || { echo "python3 is needed: xcode-select --install or https://python.org"; exit 1; }
dotnet --list-sdks 2>/dev/null | grep -q '^10\.' || { echo "The .NET 10 SDK is needed: https://dot.net"; exit 1; }
case "$(uname -m)" in arm64) RID=osx-arm64 ;; *) RID=osx-x64 ;; esac

# Build outside the project: synced folders (iCloud Documents) attach extended
# attributes to everything, and codesign refuses bundles that carry them.
OUT="$(mktemp -d -t icloud-downloader)"
trap 'rm -rf "$OUT"' EXIT
APP="$OUT/$NAME.app"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources/engine"

echo "Bundling the engine..."
python3 -m venv "$OUT/venv"
"$OUT/venv/bin/python" -m pip install --disable-pip-version-check -q "$ROOT" pyinstaller
"$OUT/venv/bin/python" -m PyInstaller --onefile --name icdl-engine --paths "$ROOT/src" --collect-submodules keyring \
  --collect-data tzdata --noconfirm --log-level WARN --distpath "$OUT/engine" --workpath "$OUT/pyi" --specpath "$OUT" \
  "$ROOT/src/icdl_engine.py"
cp "$OUT/engine/icdl-engine" "$APP/Contents/Resources/engine/"

echo "Building iCloud Downloader $VERSION ($RID)..."
dotnet publish "$ROOT/app/iCloudDownloader.csproj" -c Release -r "$RID" --self-contained true \
  -o "$APP/Contents/MacOS" --artifacts-path "$OUT/artifacts" --nologo -v quiet -p:DebugType=none -p:UseAppHost=true
chmod +x "$APP/Contents/MacOS/$NAME" "$APP/Contents/Resources/engine/icdl-engine"

ICONSET="$OUT/icon.iconset"; mkdir -p "$ICONSET"
for s in 16 32 128 256 512; do
  sips -z $s $s "$ROOT/app/Assets/icon.png" --out "$ICONSET/icon_${s}x${s}.png" >/dev/null
  sips -z $((s*2)) $((s*2)) "$ROOT/app/Assets/icon.png" --out "$ICONSET/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/icon.icns"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleName</key><string>iCloud Downloader</string>
  <key>CFBundleDisplayName</key><string>iCloud Downloader</string>
  <key>CFBundleIdentifier</key><string>$BUNDLE_ID</string>
  <key>CFBundleExecutable</key><string>$NAME</string>
  <key>CFBundleIconFile</key><string>icon</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>LSApplicationCategoryType</key><string>public.app-category.photography</string>
</dict></plist>
PLIST

# Ad-hoc signature: Apple Silicon refuses to run unsigned code.
codesign --force --deep --sign - "$APP"

if [ "$DMG" = 1 ]; then
  mkdir -p "$ROOT/dist"
  DMGFILE="$ROOT/dist/$NAME-macOS-v$VERSION.dmg"
  rm -f "$DMGFILE"
  hdiutil create -volname "iCloud Downloader" -srcfolder "$APP" -ov -format UDZO "$DMGFILE" >/dev/null
  echo "Release dmg: $DMGFILE"
fi

# Close a running copy first, then replace only the app itself.
osascript -e "quit app \"$NAME\"" >/dev/null 2>&1 || true
mkdir -p "$DEST"
rm -rf "$DEST/$NAME.app"
ditto "$APP" "$DEST/$NAME.app"
xattr -dr com.apple.quarantine "$DEST/$NAME.app" 2>/dev/null || true
echo "Installed: $DEST/$NAME.app"
[ -n "${CI:-}" ] || open "$DEST/$NAME.app"
