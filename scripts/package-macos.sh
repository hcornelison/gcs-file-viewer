#!/bin/sh
# Builds "GCS File Viewer.app" for one macOS architecture and zips it.
# Usage: scripts/package-macos.sh <osx-arm64|osx-x64> <version> <output directory>
# macOS only: it uses codesign and ditto.
#
# The app is ad-hoc signed, not signed with an Apple Developer ID, so Gatekeeper blocks the first launch
# until the user allows it under System Settings > Privacy & Security.
set -eu

rid=$1
version=$2
out=$(mkdir -p "$3" && cd "$3" && pwd)

cd "$(dirname "$0")/.."
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

app="$work/GCS File Viewer.app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"

dotnet publish src/GcsFileViewer -c Release -r "$rid" --self-contained \
  -p:Version="$version" -p:DebugType=embedded -o "$app/Contents/MacOS"

cp src/GcsFileViewer/Assets/icon.icns "$app/Contents/Resources/icon.icns"

python3 scripts/make-notices.py "$app/Contents/MacOS"
mv "$app/Contents/MacOS/THIRD-PARTY-NOTICES.txt" "$app/Contents/Resources/"
cp LICENSE "$app/Contents/Resources/LICENSE"

cat > "$app/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key>
  <string>GCS File Viewer</string>
  <key>CFBundleDisplayName</key>
  <string>GCS File Viewer</string>
  <key>CFBundleIdentifier</key>
  <string>io.github.hcornelison.gcs-file-viewer</string>
  <key>CFBundleExecutable</key>
  <string>GcsFileViewer</string>
  <key>CFBundleIconFile</key>
  <string>icon.icns</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleShortVersionString</key>
  <string>$version</string>
  <key>CFBundleVersion</key>
  <string>$version</string>
  <key>LSMinimumSystemVersion</key>
  <string>14.0</string>
  <key>LSApplicationCategoryType</key>
  <string>public.app-category.developer-tools</string>
  <key>NSHighResolutionCapable</key>
  <true/>
  <key>NSPrincipalClass</key>
  <string>NSApplication</string>
</dict>
</plist>
EOF

codesign --force --deep --sign - "$app"
codesign --verify --deep --strict "$app"

zip="$out/GcsFileViewer-$version-$rid.zip"
rm -f "$zip"
ditto -c -k --sequesterRsrc --keepParent "$app" "$zip"
echo "Built $zip"
