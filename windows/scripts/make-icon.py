#!/usr/bin/env python3
"""Builds windows/src/Envy.App/Assets/Envy.ico from assets/icon.png (macOS: uses sips to crop and resize).

The Mac icon has macOS's margin around the rounded square; Windows icons fill their box, so it's cropped first.
"""
import os, struct, subprocess, sys, tempfile

root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
source = os.path.join(root, "assets", "icon.png")
target = os.path.join(root, "windows", "src", "Envy.App", "Assets", "Envy.ico")
sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]

with tempfile.TemporaryDirectory() as tmp:
    cropped = os.path.join(tmp, "cropped.png")
    subprocess.run(["sips", "-c", "420", "420", source, "--out", cropped], check=True, capture_output=True)
    images = []
    for size in sizes:
        out = os.path.join(tmp, f"{size}.png")
        subprocess.run(["sips", "-z", str(size), str(size), cropped, "--out", out], check=True, capture_output=True)
        images.append((size, open(out, "rb").read()))

header = struct.pack("<HHH", 0, 1, len(images))
offset = 6 + 16 * len(images)
entries, blobs = b"", b""
for size, data in images:
    entries += struct.pack("<BBBBHHII", size % 256, size % 256, 0, 0, 1, 32, len(data), offset)
    blobs += data
    offset += len(data)
open(target, "wb").write(header + entries + blobs)
print(f"Wrote {target} ({len(images)} sizes)")
