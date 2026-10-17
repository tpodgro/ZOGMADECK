"""Génère la bibliothèque de sons intégrée de ZogmaDeck (sons originaux de synthèse, domaine public / CC0).

Usage (Python 3 + numpy) :  python tools/generate-sounds.py
Écrit src/ZogmaDeck.App/Assets/Sounds/*.wav (mono, 44,1 kHz, 16 bits) et sounds.tsv (fichier, nom, catégorie).
"""
import wave
from pathlib import Path

import numpy as np

ROOT = Path(__file__).resolve().parent.parent
OUT = ROOT / 'src' / 'ZogmaDeck.App' / 'Assets' / 'Sounds'
SR = 44100
rng = np.random.default_rng(20261017)


# ---------- briques ----------
def t(d):
    return np.arange(int(SR * d)) / SR


def env(n, a=0.005, r=0.05, d=None):
    """Enveloppe attaque / maintien / relâchement (secondes), n échantillons."""
    e = np.ones(n)
    na, nr = max(1, int(a * SR)), max(1, int(r * SR))
    e[:na] = np.linspace(0, 1, na)
    if nr < n:
        e[-nr:] *= np.linspace(1, 0, nr)
    if d:  # décroissance exponentielle
        e *= np.exp(-np.arange(n) / (d * SR))
    return e


def sine(f, d, phase=0.0):
    f = np.broadcast_to(np.asarray(f, dtype=float), (int(SR * d),))
    return np.sin(2 * np.pi * np.cumsum(f) / SR + phase)


def saw(f, d):
    f = np.broadcast_to(np.asarray(f, dtype=float), (int(SR * d),))
    ph = np.cumsum(f) / SR
    return 2 * (ph - np.floor(ph + 0.5))


def square(f, d, duty=0.5):
    f = np.broadcast_to(np.asarray(f, dtype=float), (int(SR * d),))
    ph = np.cumsum(f) / SR
    return np.where((ph % 1) < duty, 1.0, -1.0)


def noise(d):
    return rng.uniform(-1, 1, int(SR * d))


def lowpass(x, cutoff):
    """Passe-bas simple à un pôle (cutoff en Hz, scalaire ou tableau)."""
    cutoff = np.broadcast_to(np.asarray(cutoff, dtype=float), x.shape)
    a = 1 - np.exp(-2 * np.pi * cutoff / SR)
    y = np.empty_like(x)
    acc = 0.0
    for i in range(len(x)):
        acc += a[i] * (x[i] - acc)
        y[i] = acc
    return y


def highpass(x, cutoff):
    return x - lowpass(x, cutoff)


def mix(length, *parts):
    """Superpose des (signal, début en s) dans un tampon de `length` secondes."""
    out = np.zeros(int(SR * length))
    for sig, start in parts:
        s = int(start * SR)
        e = min(len(out), s + len(sig))
        out[s:e] += sig[:e - s]
    return out


def note(freq, d, wave_fn=sine, a=0.005, r=0.08, decay=None, harmonics=((1, 1.0),)):
    sig = sum(g * wave_fn(freq * h, d) for h, g in harmonics)
    return sig * env(len(sig), a, r, decay)


def reverb(x, amount=0.25, delays=(0.029, 0.037, 0.041, 0.053), feedback=0.45):
    """Petite réverbération (peignes en parallèle) pour donner de l'espace."""
    tail = int(0.6 * SR)
    y = np.concatenate([x, np.zeros(tail)])
    wet = np.zeros_like(y)
    for dl in delays:
        k = int(dl * SR)
        buf = y.copy()
        for i in range(k, len(buf)):
            buf[i] += feedback * buf[i - k]
        wet += buf - y
    return y + amount * wet / len(delays)


def write(name, x, title, category):
    x = np.asarray(x, dtype=float)
    x = x - np.mean(x)
    # fondu de sortie pour éviter tout clic
    fade = min(len(x), int(0.01 * SR))
    x[-fade:] *= np.linspace(1, 0, fade)
    # Niveau ressenti homogène : volume efficace (RMS) visé, sans dépasser -1 dBFS en crête.
    peak = np.max(np.abs(x)) or 1
    rms = np.sqrt(np.mean(x ** 2)) or 1
    x = x * min(0.89 / peak, 0.2 / rms)
    data = (x * 32767).astype('<i2').tobytes()
    with wave.open(str(OUT / name), 'wb') as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes(data)
    manifest.append((name, title, category))


manifest = []


# ---------- sons ----------
def airhorn():
    d = 1.6
    f = 466 * (1 + 0.004 * sine(5.5, d))
    sig = sum(saw(f * m, d) * g for m, g in ((1, 1), (1.5, 0.6), (2, 0.35)))
    sig = lowpass(sig, 3200)
    e = env(len(sig), 0.02, 0.15)
    # trois coups : court, court, long
    return mix(2.2, (sig[:int(0.22 * SR)] * env(int(0.22 * SR), 0.01, 0.04), 0),
               (sig[:int(0.22 * SR)] * env(int(0.22 * SR), 0.01, 0.04), 0.3),
               (sig * e, 0.6))


def drumroll():
    d = 2.6
    out = np.zeros(int(SR * (d + 0.6)))
    tt = 0.0
    i = 0
    while tt < d:
        rate = 0.075 - 0.035 * min(1, tt / 1.8)  # accélère
        hit = highpass(noise(0.09), 900) * env(int(0.09 * SR), 0.001, 0.02, 0.025)
        hit += 0.5 * sine(190, 0.09) * env(int(0.09 * SR), 0.001, 0.02, 0.03)
        s = int(tt * SR)
        gain = 0.35 + 0.65 * (tt / d) ** 1.5
        out[s:s + len(hit)] += hit * gain * (1 if i % 2 else 0.85)
        tt += rate
        i += 1
    crash = highpass(noise(1.2), 3000) * env(int(1.2 * SR), 0.002, 0.3, 0.35)
    cs = int(d * SR)
    out[cs:cs + len(crash)] += crash[:len(out) - cs] * 1.2
    return out


def rimshot():
    def snare(d=0.25):
        n = highpass(noise(d), 1500) * env(int(d * SR), 0.001, 0.05, 0.06)
        body = sine(185 * np.exp(-t(d) * 8), d) * env(int(d * SR), 0.001, 0.05, 0.05)
        return n * 0.8 + body * 0.6

    def kick(d=0.3):
        f = 55 + 90 * np.exp(-t(d) * 25)
        return sine(f, d) * env(int(d * SR), 0.001, 0.05, 0.09)

    cymbal = highpass(noise(1.4), 5000) * env(int(1.4 * SR), 0.002, 0.4, 0.4)
    cymbal = cymbal + 0.3 * highpass(square(5400, 1.4) * square(7930, 1.4), 4000) * env(int(1.4 * SR), 0.002, 0.4, 0.3)
    return mix(1.9, (snare(), 0), (kick(), 0.18), (snare() * 0.9, 0.36), (cymbal * 0.7, 0.36))


def applause():
    d = 3.2
    out = np.zeros(int(SR * d))
    for _ in range(900):  # beaucoup de claquements aléatoires
        start = rng.uniform(0, d - 0.1)
        clap = lowpass(highpass(noise(0.03), rng.uniform(700, 1400)), rng.uniform(2500, 6000))
        clap *= env(len(clap), 0.0005, 0.01, rng.uniform(0.004, 0.009))
        s = int(start * SR)
        out[s:s + len(clap)] += clap * rng.uniform(0.3, 1)
    shape = np.minimum(1, t(d) / 0.35) * np.minimum(1, (d - t(d)) / 1.0)
    return out * shape


def ding():
    d = 1.6
    return (note(1318.5, d, decay=0.45, r=0.3, harmonics=((1, 1), (2.76, 0.25), (5.4, 0.08)))
            + 0.6 * note(1975.5, d, decay=0.35, r=0.3))


def success():
    seq = [(523.25, 0), (659.25, 0.1), (783.99, 0.2), (1046.5, 0.3)]
    parts = [(note(f, 0.5 if i == 3 else 0.18, decay=0.25 if i == 3 else 0.1, r=0.1,
                   harmonics=((1, 1), (2, 0.3), (3, 0.12))), s) for i, (f, s) in enumerate(seq)]
    return reverb(mix(1.0, *parts), 0.35)


def fail_buzzer():
    d = 0.9
    sig = square(110, d) * 0.6 + saw(116, d) * 0.6
    return lowpass(sig, 1800) * env(len(sig), 0.01, 0.08)


def sad_trombone():
    parts = []
    notes = [(293.66, 0, 0.42), (277.18, 0.45, 0.42), (261.63, 0.9, 0.42), (246.94, 1.35, 1.3)]
    for i, (f, s, d) in enumerate(notes):
        vib = 1 + (0.012 * sine(6, d) * np.minimum(1, t(d) / 0.4) if i == 3 else 0)
        bend = 1 - (0.03 * np.minimum(1, t(d) / d) if i == 3 else 0)
        sig = saw(f * vib * bend, d)
        cutoff = 400 + 1600 * np.abs(np.sin(np.pi * np.minimum(t(d) / 0.35, 1)))  # effet « wah »
        sig = lowpass(sig, cutoff) * env(len(sig), 0.03, 0.12)
        parts.append((sig, s))
    return reverb(mix(2.9, *parts), 0.2)


def fanfare():
    chord = lambda fs, d, dec: sum(note(f, d, saw, a=0.01, r=0.15, decay=dec) for f in fs)
    part1 = lowpass(chord([523.25, 659.25, 783.99], 0.16, 0.2), 3500)
    part2 = lowpass(chord([523.25, 659.25, 783.99], 0.16, 0.2), 3500)
    part3 = lowpass(chord([698.46, 880.0, 1046.5, 1396.9], 1.1, 0.6), 4000)
    return reverb(mix(1.8, (part1, 0), (part2, 0.18), (part3, 0.36)), 0.3)


def countdown():
    beep = lambda f, d: note(f, d, a=0.003, r=0.02, harmonics=((1, 1), (3, 0.15)))
    return mix(3.6, (beep(880, 0.15), 0), (beep(880, 0.15), 1), (beep(880, 0.15), 2), (beep(1760, 0.7), 3))


def laser():
    d = 0.45
    f = 2400 * np.exp(-t(d) * 7) + 180
    sig = square(f, d, 0.3) * 0.5 + sine(f * 1.01, d) * 0.5
    return lowpass(sig, 6000) * env(len(sig), 0.002, 0.1, 0.25)


def boing():
    d = 0.9
    f = 180 + 260 * np.exp(-t(d) * 4) * (1 + 0.35 * np.sin(2 * np.pi * 14 * t(d)) * np.exp(-t(d) * 3))
    return sine(f, d) * env(int(d * SR), 0.003, 0.15, 0.4) + 0.3 * sine(2 * f, d) * env(int(d * SR), 0.003, 0.1, 0.2)


def whoosh():
    d = 0.9
    n = noise(d)
    sweep = 300 + 3500 * np.sin(np.pi * t(d) / d) ** 2
    sig = lowpass(highpass(n, sweep * 0.3), sweep)
    return sig * np.sin(np.pi * t(d) / d) ** 2


def coin():
    a = note(987.77, 0.08, square, a=0.001, r=0.01)
    b = note(1318.5, 0.45, square, a=0.001, r=0.2, decay=0.18)
    return lowpass(mix(0.6, (a, 0), (b, 0.075)), 7000)


def level_up():
    freqs = [523.25, 659.25, 783.99, 1046.5, 1318.5, 1567.98, 2093.0]
    parts = [(note(f, 0.09, square, a=0.002, r=0.02) * 0.6, i * 0.07) for i, f in enumerate(freqs)]
    parts.append((note(2093.0, 0.5, square, a=0.002, r=0.3, decay=0.25) * 0.6, len(freqs) * 0.07))
    return lowpass(mix(1.2, *parts), 6500)


def glitch():
    d = 0.8
    out = np.zeros(int(SR * d))
    pos = 0
    while pos < len(out):
        seg = int(rng.uniform(0.015, 0.06) * SR)
        kind = rng.integers(0, 3)
        if kind == 0:
            s = square(rng.uniform(80, 1600), seg / SR) * 0.6
        elif kind == 1:
            s = noise(seg / SR) * 0.5
        else:
            s = np.zeros(seg)
        out[pos:pos + seg] = s[:len(out) - pos]
        pos += seg
    return np.round(out * 6) / 6  # bitcrush


def censor():
    d = 0.75
    return sine(1000, d) * env(int(d * SR), 0.005, 0.01)


def suspense():
    hit = lambda f, d: lowpass(sum(saw(f * m, d) * g for m, g in ((1, 1), (2, 0.5), (0.5, 0.8))), 1400) * env(int(d * SR), 0.01, 0.25, d * 0.6)
    return reverb(mix(3.0, (hit(146.83, 0.32), 0), (hit(138.59, 0.32), 0.4), (hit(130.81, 1.9), 0.8)), 0.45)


def boxing_bell():
    d = 1.8
    partials = ((1, 1.0), (2.32, 0.6), (4.25, 0.35), (6.63, 0.2))
    one = sum(g * sine(820 * h, d) * env(int(d * SR), 0.001, 0.2, 0.7 / h ** 0.5) for h, g in partials)
    return mix(2.6, (one, 0), (one, 0.35), (one, 0.7))


def crickets():
    d = 3.5
    out = np.zeros(int(SR * d))
    for start in np.arange(0.1, d - 0.3, 0.55):
        for k in range(3):  # trois stridulations par série
            pulse = d_ = 0.035
            chirp = sine(4700, d_) * env(int(d_ * SR), 0.003, 0.01)
            s = int((start + rng.uniform(-0.02, 0.02) + k * 0.06) * SR)
            out[s:s + len(chirp)] += chirp * rng.uniform(0.6, 1)
    # second grillon plus loin
    out2 = np.roll(out, int(0.27 * SR)) * 0.4
    return lowpass(out + out2, 9000)


def notification():
    return mix(0.7, (note(880, 0.12, decay=0.08, r=0.05, harmonics=((1, 1), (2, 0.2))), 0),
               (note(1174.66, 0.45, decay=0.18, r=0.2, harmonics=((1, 1), (2, 0.2))), 0.11))


def heartbeat():
    beat = lambda: sine(55 + 25 * np.exp(-t(0.18) * 20), 0.18) * env(int(0.18 * SR), 0.003, 0.05, 0.06)
    parts = []
    for i in range(3):
        parts += [(beat(), i * 0.85), (beat() * 0.7, i * 0.85 + 0.22)]
    return lowpass(mix(2.8, *parts), 400)


SOUNDS = [
    ('klaxon.wav', 'Klaxon', 'Réactions', airhorn),
    ('roulement-tambour.wav', 'Roulement de tambour', 'Musique', drumroll),
    ('ba-dum-tss.wav', 'Ba dum tss', 'Réactions', rimshot),
    ('applaudissements.wav', 'Applaudissements', 'Réactions', applause),
    ('grillons.wav', 'Grillons (silence gênant)', 'Réactions', crickets),
    ('trombone-triste.wav', 'Trombone triste', 'Réactions', sad_trombone),
    ('suspense.wav', 'Suspense', 'Musique', suspense),
    ('fanfare.wav', 'Fanfare de victoire', 'Musique', fanfare),
    ('succes.wav', 'Succès', 'Jeu', success),
    ('echec.wav', 'Échec (buzzer)', 'Jeu', fail_buzzer),
    ('niveau-superieur.wav', 'Niveau supérieur', 'Jeu', level_up),
    ('piece.wav', 'Pièce', 'Jeu', coin),
    ('laser.wav', 'Laser', 'Jeu', laser),
    ('boing.wav', 'Boing', 'Effets', boing),
    ('whoosh.wav', 'Whoosh (transition)', 'Effets', whoosh),
    ('glitch.wav', 'Glitch', 'Effets', glitch),
    ('battement-coeur.wav', 'Battements de cœur', 'Effets', heartbeat),
    ('cloche-ring.wav', 'Cloche de ring', 'Signaux', boxing_bell),
    ('compte-a-rebours.wav', 'Compte à rebours', 'Signaux', countdown),
    ('ding.wav', 'Ding', 'Signaux', ding),
    ('notification.wav', 'Notification', 'Signaux', notification),
    ('bip-censure.wav', 'Bip de censure', 'Signaux', censor),
]

if __name__ == '__main__':
    OUT.mkdir(parents=True, exist_ok=True)
    for f in OUT.glob('*.wav'):
        f.unlink()
    for file, title, category, fn in SOUNDS:
        write(file, fn(), title, category)
    with open(OUT / 'sounds.tsv', 'w', encoding='utf-8', newline='\n') as f:
        f.write('# Sons originaux générés par tools/generate-sounds.py — domaine public (CC0)\n')
        for file, title, category in manifest:
            f.write(f'{file}\t{title}\t{category}\n')
    total = sum(p.stat().st_size for p in OUT.glob('*.wav'))
    print(f'{len(manifest)} sons, {total / 1024:.0f} Ko')
