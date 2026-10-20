"""Matte's built-in Spatial generator: Apple's SHARP model (spatial\\sharp, Apple sample-code license) predicts 3D
Gaussians from a photo; Spatial Photos' code (spatial_photos.py / exporter.py, MIT) cuts them into the .spatial format.

  python matte_sharp.py --roots-file <list.txt> --cache <folder> --checkpoint <sharp.pt> --width W --height H
  python matte_sharp.py --trial <image> --out <file.spatial> --checkpoint <sharp.pt> --width W --height H

The roots file has one folder or image per line (Matte's playlist entries). Every photo whose .spatial is missing or
older than the photo is made, at most the screen's size. Progress goes to stdout as the lines Matte reads:
"Processing <n> valid", "Predicting gaussians for <path>", "Nothing to generate". The model's weights are not part of
Matte: Matte downloads them from Apple after the user accepts Apple's license (see README).
"""
import argparse
import ctypes
import math
import os
import sys
import time
from pathlib import Path

import numpy as np
import torch
import torch.nn.functional as F
from PIL import Image, ImageOps

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from sharp.models import PredictorParams, create_predictor   # noqa: E402
from sharp.utils.gaussians import unproject_gaussians         # noqa: E402
import exporter                                                # noqa: E402
import spatial_photos                                          # noqa: E402
from spatial_photos import spatial_photo                       # noqa: E402

EXTS = {'.jpg', '.jpeg', '.png', '.bmp', '.webp'}
# 30 depth slices, 32-pixel blocks, 8% margin; JPEG 90 looked the same as 100 at a third of the size
SLICES, BLOCK, OUTFILL, UVPAD, QUALITY = 30, 32, 0.08, 1, 90
F35MM = 30.0   # the screen-size copies carry no EXIF, so every photo has had SHARP's default focal length
Image.MAX_IMAGE_PIXELS = 200_000_000


def be_quiet():
    """Below-normal priority and two cores left free, so the desktop stays responsive while pictures are made."""
    try:
        ctypes.windll.kernel32.SetPriorityClass(ctypes.windll.kernel32.GetCurrentProcess(), 0x4000)
    except Exception:
        pass
    n = max(1, (os.cpu_count() or 4) - 2)
    torch.set_num_threads(n)
    os.environ.setdefault('MATTE_SLICE_THREADS', str(n))


def excluded_names():
    """Folder names Matte leaves out of the playlist (excluded-folders.txt in Matte's folder)."""
    names = set()
    f = Path(__file__).resolve().parent.parent / 'excluded-folders.txt'
    if f.exists():
        for line in f.read_text('utf-8', errors='ignore').splitlines():
            line = line.strip().rstrip('\\')
            if line and not line.startswith('#'):
                names.add(Path(line).name.lower())
    return {n.lower() for n in names}


def photos_in(roots_file):
    skip = excluded_names()
    found = []
    for line in Path(roots_file).read_text('utf-8-sig').splitlines():
        p = Path(line.strip())
        if not line.strip():
            continue
        if p.is_file():
            if p.suffix.lower() in EXTS:
                found.append(p)
        elif p.is_dir():
            for dirpath, dirnames, filenames in os.walk(p):
                dirnames[:] = [d for d in dirnames if d.lower() not in skip]
                found += [Path(dirpath) / f for f in filenames if Path(f).suffix.lower() in EXTS]
    # the cache is keyed by file name: one file under several names / paths is made once; a name used by two
    # different photos is left out (Matte shows no Spatial for it either)
    by_stem = {}
    for p in found:
        by_stem.setdefault(p.stem.lower(), []).append(p)
    result = []
    for stem, paths in by_stem.items():
        first = paths[0]
        if all(q == first or _same(q, first) for q in paths[1:]):
            result.append(first)
        else:
            print(f'Skipped (name used by different photos): {first.stem}', flush=True)
    return result


def _same(a, b):
    try:
        return os.path.samefile(a, b)
    except OSError:
        return False


def needs(photo, cache):
    out = cache / f'{photo.stem}.spatial'
    try:
        return not out.exists() or photo.stat().st_mtime > out.stat().st_mtime + 1
    except OSError:
        return False


def load_photo(path, width, height):
    """The photo upright, no larger than the screen, and SHARP's focal length."""
    with Image.open(path) as im:
        im = ImageOps.exif_transpose(im).convert('RGB')
        scale = min(1.0, width / im.width, height / im.height)
        if scale < 0.999:
            im = im.resize((max(1, round(im.width * scale)), max(1, round(im.height * scale))), Image.Resampling.LANCZOS)
        img = np.asarray(im).copy()
    h, w = img.shape[:2]
    f_px = F35MM * math.sqrt(w * w + h * h) / math.sqrt(36 ** 2 + 24 ** 2)
    return img, f_px


@torch.no_grad()
def predict_image(predictor, image, f_px, device):
    """sharp.cli.predict.predict_image (Apple), here so its command line's extra packages are not needed."""
    internal = (1536, 1536)
    image_pt = torch.from_numpy(image.copy()).float().to(device).permute(2, 0, 1) / 255.0
    _, height, width = image_pt.shape
    disparity_factor = torch.tensor([f_px / width]).float().to(device)
    resized = F.interpolate(image_pt[None], size=(internal[1], internal[0]), mode='bilinear', align_corners=True)
    gaussians_ndc = predictor(resized, disparity_factor)
    intrinsics = torch.tensor([[f_px, 0, width / 2, 0], [0, f_px, height / 2, 0], [0, 0, 1, 0], [0, 0, 0, 1]]).float().to(device)
    resized_intrinsics = intrinsics.clone()
    resized_intrinsics[0] *= internal[0] / width
    resized_intrinsics[1] *= internal[1] / height
    return unproject_gaussians(gaussians_ndc, torch.eye(4).to(device), resized_intrinsics, internal)


def make(predictor, device, photo, out, width, height):
    image, f_px = load_photo(photo, width, height)
    h, w = image.shape[:2]
    out_w = ((math.floor((1 + OUTFILL) * w) + BLOCK - 1) // BLOCK) * BLOCK
    out_h = ((math.floor((1 + OUTFILL) * h) + BLOCK - 1) // BLOCK) * BLOCK
    g = predict_image(predictor, image, f_px, device)
    if g.mean_vectors.numel() == 0:
        raise ValueError('the model found nothing in this photo')
    atlas, vertices = spatial_photo(image=image, gaussians=g, focalY=f_px, outputWidth=out_w, outputHeight=out_h,
                                    numSlices=SLICES, blockSize=BLOCK, opaqueOnly=False, atlasBlockLimit=256)
    tmp = out.with_name(out.name + '.part')
    exporter.export_spatial(atlas=atlas, vertices=vertices, imageWidth=out_w, imageHeight=out_h, originalWidth=w,
                            originalHeight=h, focal=f_px, blockSize=BLOCK, outPath=tmp, uvPadding=UVPAD,
                            numSlices=SLICES, opaqueOnly=False, quality=QUALITY)
    os.replace(tmp, out)


def load_model(checkpoint):
    device = torch.device('cuda' if torch.cuda.is_available() and os.environ.get('MATTE_SHARP_DEVICE') != 'cpu' else 'cpu')
    predictor = create_predictor(PredictorParams())
    predictor.load_state_dict(torch.load(checkpoint, weights_only=True, map_location='cpu'))
    predictor.eval().to(device)
    use_device(device)
    return predictor, device


def use_device(device):
    spatial_photos.DEVICE = device   # its tensors follow the model's device
    torch.set_default_device(device)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--roots-file'); ap.add_argument('--cache')
    ap.add_argument('--trial'); ap.add_argument('--out')
    ap.add_argument('--checkpoint', required=True)
    ap.add_argument('--width', type=int, default=2560); ap.add_argument('--height', type=int, default=1600)
    a = ap.parse_args()
    be_quiet()

    if a.trial:
        t = time.time()
        predictor, device = load_model(a.checkpoint)
        loaded = time.time()
        make(predictor, device, Path(a.trial), Path(a.out), a.width, a.height)
        print(f'Trial ok: {time.time() - loaded:.1f} s per photo on {device.type} (model loaded in {loaded - t:.1f} s)', flush=True)
        return 0

    cache = Path(a.cache); cache.mkdir(parents=True, exist_ok=True)
    todo = [p for p in photos_in(a.roots_file) if needs(p, cache)]
    print(f'- Processing {len(todo)} valid image files -', flush=True)
    if not todo:
        print('Nothing to generate', flush=True)
        return 0
    predictor, device = load_model(a.checkpoint)
    failed = 0
    for photo in todo:
        print(f'Predicting gaussians for {photo}...', flush=True)
        try:
            try:
                make(predictor, device, photo, cache / f'{photo.stem}.spatial', a.width, a.height)
            except torch.cuda.OutOfMemoryError:
                # too little graphics memory: the rest on the CPU (slower, the same result)
                print('GPU memory too small, using the CPU', flush=True)
                torch.cuda.empty_cache(); device = torch.device('cpu'); predictor.to(device); use_device(device)
                make(predictor, device, photo, cache / f'{photo.stem}.spatial', a.width, a.height)
        except Exception as e:   # one bad photo does not stop the rest
            failed += 1
            print(f'Failed: {photo.name}: {e}', flush=True)
        if device.type == 'cuda':
            torch.cuda.empty_cache()
    return 1 if failed else 0


if __name__ == '__main__':
    sys.exit(main())
