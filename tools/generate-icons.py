"""Génère le logo ZogmaDeck : icône de l'application (.ico) et visuels du paquet MSIX (Microsoft Store).

Usage (Python 3 + Pillow) :  python tools/generate-icons.py
Écrit src/ZogmaDeck.App/Assets/ZogmaDeck.ico et packaging/msix/Assets/*.png.
Logo : carré arrondi en dégradé bleu, « Z » en escalier formé de tuiles blanches arrondies.
"""
import io
import struct
from pathlib import Path

from PIL import Image, ImageChops, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parent.parent
N = 1254  # repère du dessin de référence


def lerp(a, b, t):
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(3))


def background(size):
    # dégradé diagonal haut-gauche → bas-droite
    c0, c1, c2 = (42, 171, 255), (8, 104, 248), (0, 53, 210)
    g = Image.new('RGB', (size, size))
    px = g.load()
    for y in range(size):
        for x in range(size):
            t = (x + y) / (2 * (size - 1))
            px[x, y] = lerp(c0, c1, t / 0.5) if t < 0.5 else lerp(c1, c2, (t - 0.5) / 0.5)
    return g.convert('RGBA')


_cache = {}


def logo(px):
    """Logo carré de px × px pixels, fond transparent hors du carré arrondi."""
    if px in _cache:
        return _cache[px]
    S = 1024 if px > 64 else 512
    k = S / N
    img = Image.new('RGBA', (S, S), (0, 0, 0, 0))
    mask = Image.new('L', (S, S), 0)
    ImageDraw.Draw(mask).rounded_rectangle([0, 0, S - 1, S - 1], radius=int(253 * k), fill=255)
    img.paste(background(S), (0, 0), mask)
    if px < 32:
        # petites tailles : Z simplifié sur 4 × 4 tuiles plus grandes, lisible à 16 px
        cells = [(0, 0), (0, 1), (0, 2), (0, 3), (1, 2), (2, 1), (3, 0), (3, 1), (3, 2), (3, 3)]
        tile, pitch = 190 * k, 215 * k
        ox = oy = (S - (3 * pitch + tile)) / 2
    else:
        cells = ([(0, c) for c in range(5)] + [(1, 3), (1, 4), (2, 2), (2, 3), (3, 1), (3, 2), (4, 0), (4, 1)]
                 + [(5, c) for c in range(5)])
        tile, pitch, ox, oy = 119 * k, 131 * k, 306 * k, 261 * k
    radius = tile * 0.2
    rects = [(ox + c * pitch, oy + r * pitch) for r, c in cells]
    # ombre douce sous les tuiles
    sh = Image.new('L', (S, S), 0)
    d = ImageDraw.Draw(sh)
    for x, y in rects:
        d.rounded_rectangle([x, y + tile * 0.06, x + tile, y + tile * 1.06], radius=radius, fill=110)
    sh = sh.filter(ImageFilter.GaussianBlur(tile * 0.08))
    shadow = Image.new('RGBA', (S, S), (0, 20, 90, 0))
    shadow.putalpha(ImageChops.multiply(sh, mask))
    img = Image.alpha_composite(img, shadow)
    # tuiles blanches, très léger dégradé vers le bas de chaque tuile
    tl = Image.new('L', (S, S), 0)
    d = ImageDraw.Draw(tl)
    for x, y in rects:
        d.rounded_rectangle([x, y, x + tile, y + tile], radius=radius, fill=255)
    white = Image.new('RGBA', (S, S))
    wd = ImageDraw.Draw(white)
    for y in range(S):
        wd.line([(0, y), (S, y)], fill=lerp((255, 255, 255), (236, 242, 252), ((y - oy) % pitch) / max(1, tile)) + (255,))
    img.paste(white, (0, 0), tl)
    _cache[px] = img.resize((px, px), Image.LANCZOS)
    return _cache[px]


def write_ico(path, sizes=(16, 20, 24, 32, 40, 48, 64, 256)):
    pngs = []
    for s in sizes:
        b = io.BytesIO()
        logo(s).save(b, 'PNG')
        pngs.append(b.getvalue())
    with open(path, 'wb') as f:
        f.write(struct.pack('<HHH', 0, 1, len(sizes)))
        offset = 6 + 16 * len(sizes)
        for s, p in zip(sizes, pngs):
            f.write(struct.pack('<BBBBHHII', s % 256, s % 256, 0, 0, 1, 32, len(p), offset))
            offset += len(p)
        for p in pngs:
            f.write(p)


def canvas(w, h, logo_px):
    """Logo centré sur un fond transparent (tuiles, écran de démarrage)."""
    img = Image.new('RGBA', (w, h), (0, 0, 0, 0))
    img.alpha_composite(logo(logo_px), ((w - logo_px) // 2, (h - logo_px) // 2))
    return img


def msix_assets(out):
    out.mkdir(parents=True, exist_ok=True)
    for f in out.glob('*.png'):
        f.unlink()
    scales = (100, 125, 150, 200, 400)

    def sc(v, scale):
        return round(v * scale / 100)

    # Icône d'application (barre des tâches, menu Démarrer, recherche, Paramètres) : logo plein cadre.
    for scale in scales:
        logo(sc(44, scale)).save(out / f'Square44x44Logo.scale-{scale}.png')
    for size in (16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256):
        for suffix in ('', '_altform-unplated', '_altform-lightunplated'):
            logo(size).save(out / f'Square44x44Logo.targetsize-{size}{suffix}.png')
    # Logo du paquet (Store, programme d'installation d'application).
    for scale in scales:
        logo(sc(50, scale)).save(out / f'StoreLogo.scale-{scale}.png')
    # Vignettes : logo à environ 50 % de la hauteur, fond transparent.
    for scale in scales:
        canvas(sc(150, scale), sc(150, scale), sc(76, scale)).save(out / f'Square150x150Logo.scale-{scale}.png')
        canvas(sc(310, scale), sc(150, scale), sc(76, scale)).save(out / f'Wide310x150Logo.scale-{scale}.png')
        canvas(sc(71, scale), sc(71, scale), sc(40, scale)).save(out / f'SmallTile.scale-{scale}.png')
        canvas(sc(310, scale), sc(310, scale), sc(152, scale)).save(out / f'LargeTile.scale-{scale}.png')
        canvas(sc(620, scale), sc(300, scale), sc(150, scale)).save(out / f'SplashScreen.scale-{scale}.png')


if __name__ == '__main__':
    write_ico(ROOT / 'src' / 'ZogmaDeck.App' / 'Assets' / 'ZogmaDeck.ico')
    msix_assets(ROOT / 'packaging' / 'msix' / 'Assets')
    print('ok')
