"""Write a self-contained contact sheet so the render can actually be looked at.

The Preview pane serves the HTML file alone, so a page that points at sibling
PNGs comes up with broken images. Embedding the pixels as data URIs makes the
page work from anywhere, and it can also carry a stitched JPEG for a quick
side-by-side before/after.

    python Tools/Blender/review_page.py <title> <png> [<png> ...]
    python Tools/Blender/review_page.py --out renders/review.html --stats "12k tris" front.png ...
"""

import argparse
import base64
import io
import os

from PIL import Image

TILE_WIDTH = 520          # px per figure: big enough to judge the silhouette
QUALITY = 84


def data_uri(path):
    img = Image.open(path).convert("RGB")
    if img.width > TILE_WIDTH:
        img = img.resize((TILE_WIDTH, max(1, round(img.height * TILE_WIDTH / img.width))),
                         Image.LANCZOS)
    buf = io.BytesIO()
    img.save(buf, format="JPEG", quality=QUALITY, optimize=True)
    return "data:image/jpeg;base64," + base64.b64encode(buf.getvalue()).decode("ascii")


def build(title, images, stats, note):
    figures = "\n".join(
        '<figure><img src="%s"><figcaption>%s</figcaption></figure>'
        % (data_uri(path), label) for label, path in images)
    return """<!doctype html><meta charset="utf-8"><title>%s</title>
<style>
 body{margin:0;background:#0a0c12;color:#cfd6e4;font:14px/1.55 system-ui,sans-serif;padding:16px}
 h1{font-size:17px;margin:0 0 4px;color:#9fc7ff;letter-spacing:.07em;text-transform:uppercase}
 p{margin:0 0 6px;color:#8a93a6;max-width:110ch}
 .row{display:flex;gap:8px;flex-wrap:wrap;margin-top:10px}
 figure{flex:0 0 23.6%%;margin:0;background:#12151d;border:1px solid #1e2532;
        border-radius:10px;padding:7px}
 img{width:100%%;height:auto;display:block;border-radius:6px;background:#000}
 figcaption{text-align:center;font-size:12px;color:#7f8899;margin-top:6px;
            text-transform:uppercase;letter-spacing:.08em}
 .note{color:#c9a86a}
</style>
<h1>%s</h1>
<p>%s</p>
<p class="note">%s</p>
<div class="row">%s</div>
""" % (title, title, stats, note, figures)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("images", nargs="+", help="label=path, or just path")
    parser.add_argument("--out", default=None)
    parser.add_argument("--title", default="review")
    parser.add_argument("--stats", default="")
    parser.add_argument("--note", default="")
    args = parser.parse_args()

    images = []
    for item in args.images:
        label, _, path = item.rpartition("=")
        if not label or not os.path.exists(path):
            label, path = os.path.splitext(os.path.basename(path))[0], item
        images.append((label, path))
    missing = [path for _, path in images if not os.path.exists(path)]
    if missing:
        raise SystemExit("missing: " + ", ".join(missing))

    out = args.out or os.path.join(os.path.dirname(images[0][1]), "review.html")
    html = build(args.title, images, args.stats, args.note)
    with open(out, "w", encoding="utf-8") as handle:
        handle.write(html)
    print("wrote %s (%.0f KB)" % (out, os.path.getsize(out) / 1024.0))


if __name__ == "__main__":
    main()
