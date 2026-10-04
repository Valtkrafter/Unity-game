"""Turn AnimShowcaseCapture frames into labelled GIFs (one per clip, plus comparison grids).

usage: python make_gifs.py <showcase dir> [--grid name=ClipA,ClipB,...]...
"""
import argparse, glob, os, re
from PIL import Image, ImageDraw, ImageFont

def font(size):
    for f in ('C:/Windows/Fonts/segoeuib.ttf', 'C:/Windows/Fonts/arialbd.ttf'):
        if os.path.exists(f): return ImageFont.truetype(f, size)
    return ImageFont.load_default()

def fps_of(report, name):
    m = re.search(rf'^{re.escape(name)}: \d+ frames @ (\d+) fps', report, re.M)
    return int(m.group(1)) if m else 30

def load(dir_, view):
    return [Image.open(p).convert('RGB') for p in sorted(glob.glob(os.path.join(dir_, f'{view}_*.jpg')))]

def label(im, text, size=22):
    d = ImageDraw.Draw(im)
    f = font(size)
    d.rectangle((0, 0, im.width, size + 12), fill=(20, 20, 26))
    d.text((8, 4), text, fill=(255, 255, 255), font=f)
    return im

def save_gif(frames, path, fps):
    pal = [f.convert('P', palette=Image.ADAPTIVE, colors=200) for f in frames]
    pal[0].save(path, save_all=True, append_images=pal[1:], duration=int(round(1000 / fps)), loop=0, optimize=True)

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('dir'); ap.add_argument('--grid', action='append', default=[])
    ap.add_argument('--scale', type=float, default=0.75)
    ap.add_argument('--titles', action='append', default=[], help='Clip=Title')
    ap.add_argument('--no-single', action='store_true')
    a = ap.parse_args()
    report = open(os.path.join(a.dir, 'report.txt')).read()
    titles = dict(t.split('=', 1) for t in a.titles)
    gif_dir = os.path.join(a.dir, 'gifs'); os.makedirs(gif_dir, exist_ok=True)
    clips = sorted(d for d in os.listdir(a.dir) if os.path.isdir(os.path.join(a.dir, d)) and d != 'gifs')
    for c in (clips if not a.no_single else []):
        fr, sd = load(os.path.join(a.dir, c), 'front'), load(os.path.join(a.dir, c), 'side')
        if not fr: continue
        out = []
        for x, y in zip(fr, sd):
            im = Image.new('RGB', (x.width + y.width, x.height))
            im.paste(x, (0, 0)); im.paste(y, (x.width, 0))
            im = im.resize((int(im.width * a.scale), int(im.height * a.scale)), Image.LANCZOS)
            out.append(label(im, titles.get(c, c)))
        save_gif(out, os.path.join(gif_dir, f'{c}.gif'), fps_of(report, c))
        print('gif', c, len(out))
    for g in a.grid:
        name, items = g.split('=', 1); items = items.split(',')
        name, view = (name.split('@') + ['front'])[:2]
        seqs = [load(os.path.join(a.dir, c), view) for c in items]
        n = min(len(s) for s in seqs)
        cw, ch = int(seqs[0][0].width * 0.6), int(seqs[0][0].height * 0.6)
        out = []
        for k in range(n):
            im = Image.new('RGB', (cw * len(items), ch))
            for j, (c, s) in enumerate(zip(items, seqs)):
                cell = label(s[k].resize((cw, ch), Image.LANCZOS), titles.get(c, c), 15)
                im.paste(cell, (j * cw, 0))
            out.append(im)
        save_gif(out, os.path.join(gif_dir, f'{name}.gif'), fps_of(report, items[0]))
        print('grid', name, n)

if __name__ == '__main__':
    main()
