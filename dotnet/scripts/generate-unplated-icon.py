#!/usr/bin/env python3
"""Generate a transparent-background icon for Windows unplated taskbar assets.

Removes the macOS-style white rounded-rectangle background from the source icon
via flood fill, preserving the speech bubble graphics with transparency.

Usage:
    python3 scripts/generate-unplated-icon.py [source_png] [output_png]

Defaults:
    source: dotnet/src/Easydict.WinUI/Assets/macos/white-black-icon.appiconset/icon_512x512@2x.png
    output: dotnet/src/Easydict.WinUI/Assets/icon_unplated_1024.png
"""
import sys
from collections import deque
from PIL import Image


def create_unplated_icon(src_path: str, out_path: str) -> None:
    """Remove white rounded-rect background via flood fill from transparent edges."""
    img = Image.open(src_path).convert("RGBA")
    w, h = img.size
    pixels = img.load()

    visited = [[False] * w for _ in range(h)]
    to_clear: set[tuple[int, int]] = set()

    # Seed BFS with all fully transparent pixels
    queue: deque[tuple[int, int]] = deque()
    for y in range(h):
        for x in range(w):
            _, _, _, a = pixels[x, y]
            if a < 10:
                queue.append((x, y))
                visited[y][x] = True
                to_clear.add((x, y))

    print(f"  Seeds: {len(queue)} transparent pixels")

    # 8-connected flood fill: expand to adjacent near-white or semi-transparent pixels
    dx = [0, 0, 1, -1, 1, 1, -1, -1]
    dy = [1, -1, 0, 0, 1, -1, 1, -1]

    while queue:
        x, y = queue.popleft()
        for i in range(8):
            nx, ny = x + dx[i], y + dy[i]
            if 0 <= nx < w and 0 <= ny < h and not visited[ny][nx]:
                r, g, b, a = pixels[nx, ny]
                brightness = (r + g + b) / 3
                if (brightness > 220 and a > 100) or a < 128:
                    visited[ny][nx] = True
                    to_clear.add((nx, ny))
                    queue.append((nx, ny))

    print(f"  Pixels to clear: {len(to_clear)}")

    for x, y in to_clear:
        pixels[x, y] = (0, 0, 0, 0)

    # Smooth anti-aliased edges bordering cleared pixels
    edge_count = 0
    for y in range(h):
        for x in range(w):
            if (x, y) not in to_clear:
                r, g, b, a = pixels[x, y]
                if a > 0:
                    has_cleared_neighbor = any(
                        0 <= x + dx[i] < w
                        and 0 <= y + dy[i] < h
                        and (x + dx[i], y + dy[i]) in to_clear
                        for i in range(8)
                    )
                    if has_cleared_neighbor and (r + g + b) / 3 > 200:
                        brightness = (r + g + b) / 3
                        new_alpha = max(0, int(a * (1 - (brightness - 200) / 55)))
                        pixels[x, y] = (r, g, b, new_alpha)
                        edge_count += 1

    print(f"  Edge pixels smoothed: {edge_count}")

    img.save(out_path, "PNG")
    print(f"  Saved: {out_path}")


def main() -> None:
    src = sys.argv[1] if len(sys.argv) > 1 else (
        "dotnet/src/Easydict.WinUI/Assets/macos/"
        "white-black-icon.appiconset/icon_512x512@2x.png"
    )
    out = sys.argv[2] if len(sys.argv) > 2 else (
        "dotnet/src/Easydict.WinUI/Assets/icon_unplated_1024.png"
    )

    print(f"Creating unplated icon from: {src}")
    create_unplated_icon(src, out)
    print("Done!")


if __name__ == "__main__":
    main()
