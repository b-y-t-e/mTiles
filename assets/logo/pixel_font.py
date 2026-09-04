# -*- coding: utf-8 -*-
"""mTiles pixel font: chunky bitmap letterforms on an exact cell grid.

Body = 24 rows (ascender/cap 0..23), x-height = rows 6..23 (18 rows), stroke = 4.
Everything is generated from those three numbers, so the weight can be retuned
in one place instead of by editing 96 rows of ASCII art.
"""

BODY   = 24          # ascender/cap height in cells
XTOP   = 6           # first row of the x-height band
STROKE = 4
XH     = BODY - XTOP # 18

def _rect(cells, x, y, w, h):
    for yy in range(y, y + h):
        for xx in range(x, x + w):
            cells.add((xx, yy))

def _m():
    """Three stems joined by a shoulder: also the icon mark."""
    s, g = STROKE, STROKE          # stem width, gap width
    w = s*3 + g*2
    c = set()
    _rect(c, 0, XTOP, w, s)                    # shoulder
    for i in range(3):
        _rect(c, i*(s+g), XTOP, s, XH)         # stems
    return w, c

def _T():
    s = STROKE
    w = s*3 + 2                                # a touch wider than the arm x3
    c = set()
    _rect(c, 0, 0, w, s)                       # arm
    _rect(c, (w - s)//2, 0, s, BODY)           # stem
    return w, c

def _i():
    s = STROKE
    c = set()
    _rect(c, 0, 0, s, s)                       # tittle
    _rect(c, 0, XTOP, s, XH)                   # stem
    return s, c

def _l():
    s = STROKE
    c = set()
    _rect(c, 0, 0, s, BODY)
    return s, c

def _e():
    s = STROKE
    w = s*3                                    # stem + counter + stem
    bar = (XH - s*3) // 2                      # counter height
    c = set()
    _rect(c, 0, XTOP, w, s)                    # top
    _rect(c, 0, XTOP, s, XH)                   # spine
    _rect(c, 0, XTOP + s + bar, w, s)          # crossbar
    _rect(c, w - s, XTOP, s, s + bar)          # upper right
    _rect(c, 0, BODY - s, w, s)                # bottom
    return w, c

def _s():
    s = STROKE
    w = s*3
    bar = (XH - s*3) // 2
    c = set()
    _rect(c, 0, XTOP, w, s)                    # top
    _rect(c, 0, XTOP, s, s + bar)              # upper left
    _rect(c, 0, XTOP + s + bar, w, s)          # waist
    _rect(c, w - s, XTOP + s + bar, s, s + bar)# lower right
    _rect(c, 0, BODY - s, w, s)                # bottom
    return w, c

_BUILD = {'m': _m, 'T': _T, 'i': _i, 'l': _l, 'e': _e, 's': _s}
GLYPHS = {ch: fn() for ch, fn in _BUILD.items()}

def word_cells(text, tracking=STROKE):
    """(width, height, {(x, y)}) for a string, in grid cells."""
    cells, x = set(), 0
    for idx, ch in enumerate(text):
        w, g = GLYPHS[ch]
        for cx, cy in g:
            cells.add((x + cx, cy))
        x += w + (tracking if idx != len(text) - 1 else 0)
    return x, BODY, cells
