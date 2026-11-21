#!/bin/bash
# Usage: bash scripts/package-macos.sh osx-arm64 [local|nightly|release] [build] [YYYYMMDD]
set -euo pipefail
rid="${1:?Zielarchitektur osx-arm64 oder osx-x64 angeben}"
channel="${2:-local}"
build="${3:-0}"
date_stamp="${4:-$(date -u +%Y%m%d)}"
case "$rid" in osx-arm64) arch=arm64;; osx-x64) arch=x86_64;; *) exit 2;; esac
case "$channel" in local|nightly|release);; *) exit 2;; esac
[[ "$(uname -s)" == Darwin && "$build" =~ ^[0-9]+$ && "$date_stamp" =~ ^[0-9]{8}$ ]]
root="$(cd "$(dirname "$0")/.." && pwd)"
project="$root/src/ThixShot/ThixShot.csproj"
out="$root/artifacts/packages"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
app="$work/image/ThixShot.app"
bin="$app/Contents/MacOS"
resources="$app/Contents/Resources"
mkdir -p "$bin" "$resources" "$out"
properties=("-p:Channel=$channel" "-p:BuildNumber=$build" "-p:BuildDate=$date_stamp")
dotnet publish "$project" -c Release -r "$rid" --self-contained true -o "$bin" "${properties[@]}" \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false
version="$(dotnet msbuild "$project" -nologo -getProperty:Version "${properties[@]}")"
display_version="$(dotnet msbuild "$project" -nologo -getProperty:InformationalVersion "${properties[@]}")"
for executable in ThixShot ThixShotCapture; do
  test -f "$bin/$executable"
  chmod 755 "$bin/$executable"
  lipo "$bin/$executable" -verify_arch "$arch"
done

iconset="$work/ThixShot.iconset"
mkdir -p "$iconset"
for size in 16 32 128 256 512; do
  sips -z "$size" "$size" "$root/src/ThixShot/Assets/ThixShot.png" --out "$iconset/icon_${size}x${size}.png" >/dev/null
  double=$((size * 2))
  sips -z "$double" "$double" "$root/src/ThixShot/Assets/ThixShot.png" --out "$iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$resources/ThixShot.icns"
cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
  <key>CFBundleIdentifier</key><string>de.thixapps.thixshot</string>
  <key>CFBundleName</key><string>ThixShot</string>
  <key>CFBundleExecutable</key><string>ThixShot</string>
  <key>CFBundleIconFile</key><string>ThixShot.icns</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>CFBundleVersion</key><string>$build</string>
  <key>CFBundleGetInfoString</key><string>ThixShot $display_version</string>
  <key>LSMinimumSystemVersion</key><string>14.0</string>
  <key>LSApplicationCategoryType</key><string>public.app-category.graphics-design</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>CFBundleDevelopmentRegion</key><string>de</string>
  <key>NSScreenCaptureUsageDescription</key><string>ThixShot nimmt den von dir gewählten Bildschirm oder Ausschnitt auf.</string>
  <key>NSAudioCaptureUsageDescription</key><string>ThixShot nimmt den Systemton auf, wenn du ihn für eine Bildschirmaufnahme aktivierst.</string>
</dict></plist>
PLIST
plutil -lint "$app/Contents/Info.plist"
codesign --force --sign - "$bin/ThixShotCapture"
codesign --force --sign - "$app"
codesign --verify --deep --strict "$app"
if [[ "$arch" == "$(uname -m)" ]]; then
  "$bin/ThixShotCapture" self-test "$work/test.mp4"
  test -s "$work/test.mp4"
fi
if [[ "${GITHUB_ACTIONS:-}" == true && "$arch" == "$(uname -m)" ]]; then
  "$bin/ThixShot" >"$work/startup.log" 2>&1 &
  app_pid=$!
  sleep 5
  if ! kill -0 "$app_pid" 2>/dev/null; then
    cat "$work/startup.log"
    echo "ThixShot wurde beim Start beendet." >&2
    exit 1
  fi
  kill "$app_pid"
  wait "$app_pid" || true
fi
ditto -c -k --keepParent "$app" "$out/ThixShot-$rid.zip"
ln -s /Applications "$work/image/Applications"
hdiutil create -volname ThixShot -srcfolder "$work/image" -ov -format UDZO "$out/ThixShot-$rid.dmg"
hdiutil verify "$out/ThixShot-$rid.dmg"
