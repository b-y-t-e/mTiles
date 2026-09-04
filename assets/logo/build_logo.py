# -*- coding: utf-8 -*-
"""Renders every mTiles brand asset from the pixel font beside this file.

Run it from anywhere: `python assets/logo/build_logo.py`. Nothing here is
hand-drawn, so a colour change or a retuned stroke weight is one constant and a
re-run rather than a round of re-exporting from a drawing tool.

What it writes:
  assets/logo/mtiles-banner.png      the README banner (dark ground + tagline)
  assets/logo/mtiles-wordmark.png    the wordmark alone, transparent
  assets/logo/mtiles-mark.png        the app mark alone, transparent
  src/mTiles/Assets/mtiles-icon.png  window icon, and the AppImage's own icon
  src/mTiles/Assets/mtiles-icon-256.png  the hicolor 256x256 entry
  src/mTiles/Assets/mtiles-icon-large.png
  src/mTiles/Assets/mTiles-logo.png  the in-app wordmark resource
  src/mTiles/mtiles.ico              the executable icon, every size rendered
                                     natively rather than downsampled
  docs/favicon.ico                   the landing page's icon. Its own copy: the
                                     page is served from docs/, so it cannot
                                     reach a file above that directory
"""
import os
import struct
import sys

from PIL import Image, ImageDraw, ImageFont

# Importing the font beside this file would leave a __pycache__ in the
# repository, which is the generator's litter rather than anything the
# workspace's .gitignore should have to carry.
sys.dont_write_bytecode = True
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from pixel_font import GLYPHS, BODY  # noqa: E402

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, '..', '..'))
ASSETS = os.path.join(REPO, 'src', 'mTiles', 'Assets')

GREEN = (143, 191, 107, 255)   # #8FBF6B
BG    = (26, 27, 38, 255)      # #1A1B26
BLUE  = (122, 162, 247, 255)   # #7AA2F7

# The tagline is set in a coding font on purpose: it is the one part of the
# lockup that is prose, and a bitmap face at that size stops being readable.
TAGLINE_FONTS = [
    r'C:\Windows\Fonts\CascadiaMono.ttf',
    r'C:\Windows\Fonts\consola.ttf',
    '/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf',
]

TRACKING = 3
# The tittle of the i sits at cap height, so 'Ti' cannot be tucked under the
# arm of the T the way it would be in an outline face - it would touch.
KERN = {'mT': -1}


def wordmark_cells(text='mTiles', tracking=TRACKING, kern=KERN):
    cells, x = set(), 0
    for i, ch in enumerate(text):
        w, glyph = GLYPHS[ch]
        for cx, cy in glyph:
            cells.add((x + cx, cy))
        if i != len(text) - 1:
            x += w + tracking + kern.get(text[i:i + 2], 0)
    return cells


def paint(img, cells, ox, oy, scale, colour):
    d = ImageDraw.Draw(img)
    for cx, cy in cells:
        x0, y0 = ox + cx * scale, oy + cy * scale
        d.rectangle([x0, y0, x0 + scale - 1, y0 + scale - 1], fill=colour)


def wordmark(scale, bg=None, colour=GREEN):
    cells = wordmark_cells()
    w = (max(c[0] for c in cells) + 1) * scale
    img = Image.new('RGBA', (w, BODY * scale), bg or (0, 0, 0, 0))
    paint(img, cells, 0, 0, scale, colour)
    return img


def tagline_font(size):
    for path in TAGLINE_FONTS:
        if os.path.exists(path):
            return ImageFont.truetype(path, size)
    raise SystemExit('no monospace font found for the tagline')


def banner(scale=14, text='Cross-platform terminal manager'):
    wm = wordmark(scale)
    font = tagline_font(int(scale * 3.8))
    probe = ImageDraw.Draw(Image.new('RGB', (1, 1)))
    box = probe.textbbox((0, 0), text, font=font)
    tw, th = box[2] - box[0], box[3] - box[1]

    top, gap, bottom = scale * 6, scale * 5, scale * 6
    W = max(wm.width, tw) + scale * 12
    H = top + wm.height + gap + th + bottom
    img = Image.new('RGBA', (W, H), BG)
    img.alpha_composite(wm, ((W - wm.width) // 2, top))
    ImageDraw.Draw(img).text(
        ((W - tw) // 2 - box[0], top + wm.height + gap - box[1]),
        text, font=font, fill=BLUE)
    return img


# The icon's m is a coarser build of the wordmark's: five units wide, one unit
# of shoulder over four of stem. The wordmark letter is twenty cells across and
# a 16px icon has no room for it, so the mark is redrawn rather than shrunk.
MARK_UNITS = 5
MARK = {(c, 0) for c in range(MARK_UNITS)}
MARK |= {(c, r) for c in (0, 2, 4) for r in range(1, 5)}


def icon(size, ground=BG, colour=GREEN):
    unit = max(1, round(size * 0.62 / MARK_UNITS))
    while unit * MARK_UNITS > size - 2:
        unit -= 1
    img = Image.new('RGBA', (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    if ground is not None:
        d.rounded_rectangle([0, 0, size - 1, size - 1],
                            radius=max(1, round(size * 0.22)), fill=ground)
    off = (size - MARK_UNITS * unit) // 2
    paint(img, MARK, off, off, unit, colour)
    return img


ICO_SIZES = (16, 20, 24, 32, 40, 48, 64, 96, 128, 256)


def write_ico(path, sizes=ICO_SIZES):
    """Write a multi-size .ico with each size rendered at its own scale.

    Pillow's own ICO writer resamples one source image down to every size,
    which turns hard pixel edges into grey fringes at exactly the sizes that
    matter (16 and 32 in Explorer and the taskbar). Each entry here is the
    icon() drawn natively for that size, stored as PNG - accepted for every
    size by Windows Vista and later, which is every version this app targets.
    """
    blobs = []
    for size in sizes:
        import io
        buf = io.BytesIO()
        icon(size).save(buf, format='PNG')
        blobs.append((size, buf.getvalue()))

    header = struct.pack('<HHH', 0, 1, len(blobs))
    offset = len(header) + 16 * len(blobs)
    entries, payload = b'', b''
    for size, blob in blobs:
        entries += struct.pack('<BBBBHHII',
                               0 if size >= 256 else size,
                               0 if size >= 256 else size,
                               0, 0, 1, 32, len(blob), offset)
        payload += blob
        offset += len(blob)
    with open(path, 'wb') as f:
        f.write(header + entries + payload)


def main():
    os.makedirs(HERE, exist_ok=True)

    banner().save(os.path.join(HERE, 'mtiles-banner.png'))
    wordmark(14).save(os.path.join(HERE, 'mtiles-wordmark.png'))
    icon(512, ground=None).save(os.path.join(HERE, 'mtiles-mark.png'))

    icon(512).save(os.path.join(ASSETS, 'mtiles-icon.png'))
    # hicolor names its directories after the size of what is in them, so the
    # 512 above cannot go in 256x256/apps. Rendered rather than downsampled,
    # for the reason write_ico() gives.
    icon(256).save(os.path.join(ASSETS, 'mtiles-icon-256.png'))
    icon(1024).save(os.path.join(ASSETS, 'mtiles-icon-large.png'))
    wordmark(14).save(os.path.join(ASSETS, 'mTiles-logo.png'))

    write_ico(os.path.join(REPO, 'src', 'mTiles', 'mtiles.ico'))
    write_ico(os.path.join(REPO, 'docs', 'favicon.ico'), sizes=(16, 32, 48, 180))

    print('wrote banner, wordmark, mark, icons, mtiles.ico and docs/favicon.ico')


if __name__ == '__main__':
    main()
