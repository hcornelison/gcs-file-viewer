#!/bin/sh
# Builds a .tar.gz and a .deb for one Linux architecture.
# Usage: scripts/package-linux.sh <linux-x64|linux-arm64> <version> <output directory>
# The .deb needs dpkg-deb, so that part runs on Debian or Ubuntu; without it only the .tar.gz is built.
set -eu

rid=$1
version=$2
out=$(mkdir -p "$3" && cd "$3" && pwd)

case "$rid" in
  linux-x64) arch=amd64 ;;
  linux-arm64) arch=arm64 ;;
  *) echo "Unsupported runtime: $rid" >&2; exit 1 ;;
esac

cd "$(dirname "$0")/.."
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

name=gcs-file-viewer
app="$work/$name"

dotnet publish src/GcsFileViewer -c Release -r "$rid" --self-contained \
  -p:Version="$version" -p:DebugType=embedded -o "$app"

cp src/GcsFileViewer/Assets/icon.png "$app/icon.png"
cp LICENSE "$app/LICENSE"
python3 scripts/make-notices.py "$app"

tar -czf "$out/GcsFileViewer-$version-$rid.tar.gz" -C "$work" "$name"
echo "Built $out/GcsFileViewer-$version-$rid.tar.gz"

if ! command -v dpkg-deb >/dev/null; then
  echo "dpkg-deb not found; skipping the .deb" >&2
  exit 0
fi

# The .deb installs the same files under /opt, with a launcher on the PATH and a desktop menu entry.
deb="$work/deb"
mkdir -p "$deb/DEBIAN" "$deb/opt" "$deb/usr/bin" "$deb/usr/share/applications" "$deb/usr/share/pixmaps"
cp -R "$app" "$deb/opt/$name"
ln -s "/opt/$name/GcsFileViewer" "$deb/usr/bin/$name"
cp src/GcsFileViewer/Assets/icon.png "$deb/usr/share/pixmaps/$name.png"

cat > "$deb/usr/share/applications/$name.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=GCS File Viewer
Comment=Browse Google Cloud Storage buckets like a file manager
Exec=/opt/$name/GcsFileViewer
Icon=$name
Terminal=false
Categories=Utility;FileTools;Development;
StartupWMClass=GcsFileViewer
EOF

# The .NET runtime is bundled; these are the system libraries it and Avalonia load.
cat > "$deb/DEBIAN/control" <<EOF
Package: $name
Version: $version
Architecture: $arch
Maintainer: hcornelison <hcornelison@users.noreply.github.com>
Section: utils
Priority: optional
Homepage: https://github.com/hcornelison/gcs-file-viewer
Depends: libc6, libgcc-s1, libstdc++6, libssl3 | libssl3t64, libx11-6, libice6, libsm6, libfontconfig1, libicu78 | libicu76 | libicu74 | libicu72 | libicu70 | libicu67
Description: File-manager style browser for Google Cloud Storage
 Browse, search, preview, upload, download, copy, move and delete objects in
 Google Cloud Storage buckets, with a running estimate of what the session costs.
EOF

dpkg-deb --build --root-owner-group "$deb" "$out/${name}_${version}_$arch.deb"
echo "Built $out/${name}_${version}_$arch.deb"
