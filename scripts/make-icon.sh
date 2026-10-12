#!/bin/sh
# Rebuilds the app icon files from src/GcsFileViewer/Assets/icon.svg. Run it after changing the SVG.
# macOS only: it uses sips to render the SVG and iconutil to build the .icns.
set -eu

cd "$(dirname "$0")/.."
assets=src/GcsFileViewer/Assets
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

sips -s format png "$assets/icon.svg" --out "$work/1024.png" >/dev/null
for size in 16 24 32 48 64 128 256 512; do
  sips -z "$size" "$size" "$work/1024.png" --out "$work/$size.png" >/dev/null
done

# icon.png: shown in the macOS Dock while the app runs, and usable as a Linux desktop icon.
cp "$work/512.png" "$assets/icon.png"

# icon.icns: for a macOS .app bundle.
iconset="$work/icon.iconset"
mkdir "$iconset"
cp "$work/16.png" "$iconset/icon_16x16.png"
cp "$work/32.png" "$iconset/icon_16x16@2x.png"
cp "$work/32.png" "$iconset/icon_32x32.png"
cp "$work/64.png" "$iconset/icon_32x32@2x.png"
cp "$work/128.png" "$iconset/icon_128x128.png"
cp "$work/256.png" "$iconset/icon_128x128@2x.png"
cp "$work/256.png" "$iconset/icon_256x256.png"
cp "$work/512.png" "$iconset/icon_256x256@2x.png"
cp "$work/512.png" "$iconset/icon_512x512.png"
cp "$work/1024.png" "$iconset/icon_512x512@2x.png"
iconutil -c icns "$iconset" -o "$assets/icon.icns"

# icon.ico: the Windows executable's icon and the window icon. An .ico is a small directory followed by the
# images, which may be stored as PNGs.
python3 - "$work" "$assets/icon.ico" <<'PY'
import struct, sys
work, out = sys.argv[1], sys.argv[2]
sizes = [16, 24, 32, 48, 64, 128, 256]
images = [open(f"{work}/{size}.png", "rb").read() for size in sizes]
offset = 6 + 16 * len(sizes)
header = struct.pack("<HHH", 0, 1, len(sizes))
entries = b""
for size, image in zip(sizes, images):
    entries += struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(image), offset)
    offset += len(image)
open(out, "wb").write(header + entries + b"".join(images))
PY

ls -l "$assets"
