"""Matte's built-in Spatial generator: Apple's SHARP model (spatial\\sharp, Apple sample-code license) predicts 3D
Gaussians from a photo; Spatial Photos' code (spatial_photos.py / exporter.py, MIT) cuts them into the .spatial format.

  python matte_sharp.py --roots-file <list.txt> --cache <folder> --lock <store.lock> --checkpoint <sharp.pt> --width W --height H
  python matte_sharp.py --describe-recipe --checkpoint <sharp.pt> --width W --height H
  python matte_sharp.py --trial <image> --out <file.spatial> --checkpoint <sharp.pt> --width W --height H

The roots file has one folder or image per line (Matte's playlist entries). Each source/recipe without a verified v1
pair is generated from a byte snapshot, at most the screen's size. Progress goes to stdout as the lines Matte reads:
"Processing <n> valid", "Predicting gaussians for <path>", "Nothing to generate". The model's weights are not part of
Matte: Matte downloads them from Apple after the user accepts Apple's license (see README).
"""
import argparse
import ctypes
import io
import math
import os
import sys
import time
from pathlib import Path

import identity
import publish

EXTS = {'.jpg', '.jpeg', '.png', '.bmp', '.webp'}
GENERATOR = 'matte-sharp/1'
EXPORTER = 'spatial-photos/1'
# Decimal constants are strings so the identity canonicalizer remains integer-only. Consumers convert them explicitly.
OUTPUT_CONSTANTS = {'OUTFILL': '0.08', 'BLOCK': 32, 'SLICES': 30, 'QUALITY': 90, 'UVPAD': 1,
                    'F35MM': '30.0', 'INTERNAL_SIZE': [1536, 1536], 'RESIZE_FILTER': 'LANCZOS',
                    'atlasBlockLimit': 256}
SLICES, BLOCK, UVPAD, QUALITY = (OUTPUT_CONSTANTS[k] for k in ('SLICES', 'BLOCK', 'UVPAD', 'QUALITY'))
OUTFILL, F35MM = (float(OUTPUT_CONSTANTS[k]) for k in ('OUTFILL', 'F35MM'))


def read_checkpoint(path):
    data = Path(path).read_bytes()
    return data, identity.sha256(data)


def recipe_for(model_sha, width, height):
    return identity.validate_recipe({'exporter': EXPORTER, 'generator': GENERATOR, 'height': height,
                                     'model': model_sha, 'upright': 'exif-v1', 'width': width})


def be_quiet():
    """Below-normal priority and two cores left free, so the desktop stays responsive while pictures are made."""
    try:
        ctypes.windll.kernel32.SetPriorityClass(ctypes.windll.kernel32.GetCurrentProcess(), 0x4000)
    except Exception:
        pass
    import torch
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
        if p.suffix.lower() in EXTS and not p.is_dir():
            found.append(p)  # Include missing/unreadable single images: publish.run reports them as errors.
        elif p.is_dir():
            for dirpath, dirnames, filenames in os.walk(p):
                dirnames[:] = [d for d in dirnames if d.lower() not in skip]
                found += [Path(dirpath) / f for f in filenames if Path(f).suffix.lower() in EXTS]
    # Paths are locations only. Keep colliding stems; content deduplication belongs to publish.run.
    result, seen = [], set()
    for p in found:
        key = os.path.normcase(os.path.abspath(p))
        if key not in seen:
            seen.add(key)
            result.append(p)
    return result


def load_photo(data, width, height):
    """The photo upright, no larger than the screen, and SHARP's focal length."""
    import numpy as np
    from PIL import Image, ImageOps
    Image.MAX_IMAGE_PIXELS = 200_000_000
    with Image.open(io.BytesIO(data)) as im:
        im = ImageOps.exif_transpose(im).convert('RGB')
        scale = min(1.0, width / im.width, height / im.height)
        if scale < 0.999:
            im = im.resize((max(1, round(im.width * scale)), max(1, round(im.height * scale))), getattr(Image.Resampling, OUTPUT_CONSTANTS['RESIZE_FILTER']))
        img = np.asarray(im).copy()
    h, w = img.shape[:2]
    f_px = F35MM * math.sqrt(w * w + h * h) / math.sqrt(36 ** 2 + 24 ** 2)
    return img, f_px


def predict_image(predictor, image, f_px, device):
    """sharp.cli.predict.predict_image (Apple), here so its command line's extra packages are not needed."""
    import torch
    import torch.nn.functional as F
    from sharp.utils.gaussians import unproject_gaussians
    with torch.no_grad():
        internal = OUTPUT_CONSTANTS['INTERNAL_SIZE']
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


def make(predictor, device, photo_bytes, out, width, height):
    import exporter
    from spatial_photos import spatial_photo
    image, f_px = load_photo(photo_bytes, width, height)
    h, w = image.shape[:2]
    out_w = ((math.floor((1 + OUTFILL) * w) + BLOCK - 1) // BLOCK) * BLOCK
    out_h = ((math.floor((1 + OUTFILL) * h) + BLOCK - 1) // BLOCK) * BLOCK
    g = predict_image(predictor, image, f_px, device)
    if g.mean_vectors.numel() == 0:
        raise ValueError('the model found nothing in this photo')
    atlas, vertices = spatial_photo(image=image, gaussians=g, focalY=f_px, outputWidth=out_w, outputHeight=out_h,
                                    numSlices=SLICES, blockSize=BLOCK, opaqueOnly=False, atlasBlockLimit=OUTPUT_CONSTANTS['atlasBlockLimit'])
    exporter.export_spatial(atlas=atlas, vertices=vertices, imageWidth=out_w, imageHeight=out_h, originalWidth=w,
                            originalHeight=h, focal=f_px, blockSize=BLOCK, outPath=out, uvPadding=UVPAD,
                            numSlices=SLICES, opaqueOnly=False, quality=QUALITY)


def load_model(checkpoint_bytes):
    import torch
    from sharp.models import PredictorParams, create_predictor
    device = torch.device('cuda' if torch.cuda.is_available() and os.environ.get('MATTE_SHARP_DEVICE') != 'cpu' else 'cpu')
    predictor = create_predictor(PredictorParams())
    predictor.load_state_dict(torch.load(io.BytesIO(checkpoint_bytes), weights_only=True, map_location='cpu'))
    predictor.eval().to(device)
    use_device(device)
    return predictor, device


def use_device(device):
    import torch
    import spatial_photos
    spatial_photos.DEVICE = device   # its tensors follow the model's device
    torch.set_default_device(device)


def main(argv=None):
    ap = argparse.ArgumentParser()
    ap.add_argument('--roots-file'); ap.add_argument('--cache'); ap.add_argument('--lock')
    ap.add_argument('--trial'); ap.add_argument('--out'); ap.add_argument('--describe-recipe', action='store_true')
    ap.add_argument('--checkpoint', required=True)
    ap.add_argument('--width', type=int, default=2560); ap.add_argument('--height', type=int, default=1600)
    a = ap.parse_args(argv)
    started = time.time()
    checkpoint_bytes, model_sha = read_checkpoint(a.checkpoint)
    fields = recipe_for(model_sha, a.width, a.height)
    if a.describe_recipe:
        print('Recipe: ' + identity.canonical(fields), flush=True)
        print('RecipeSha: ' + identity.recipe_sha256(fields), flush=True)
        return 0
    if a.trial:
        if not a.out:
            ap.error('--trial requires --out')
        be_quiet()
        predictor, device = load_model(checkpoint_bytes)
        loaded = time.time()
        out = Path(a.out); temp = out.with_name(out.name + '.part')
        try:
            make(predictor, device, Path(a.trial).read_bytes(), temp, a.width, a.height)
            os.replace(temp, out)
        finally:
            if temp.exists():
                temp.unlink()
        print(f'Trial ok: {time.time() - loaded:.1f} s per photo on {device.type} (model loaded in {loaded - started:.1f} s)', flush=True)
        return 0
    if not a.roots_file or not a.cache or not a.lock:
        ap.error('generation requires --roots-file, --cache and --lock')
    predictor = device = None

    def generate(photo_bytes, temp):
        nonlocal predictor, device, checkpoint_bytes
        import torch
        if predictor is None:
            be_quiet()
            predictor, device = load_model(checkpoint_bytes)
            checkpoint_bytes = None
        try:
            try:
                make(predictor, device, photo_bytes, temp, a.width, a.height)
            except torch.cuda.OutOfMemoryError:
                print('GPU memory too small, using the CPU', flush=True)
                torch.cuda.empty_cache(); device = torch.device('cpu'); predictor.to(device); use_device(device)
                make(predictor, device, photo_bytes, temp, a.width, a.height)
        finally:
            if device.type == 'cuda':
                torch.cuda.empty_cache()

    try:
        return publish.run(photos_in(a.roots_file), a.cache, fields, a.lock, generate)
    except (OSError, ValueError) as e:
        print(f'Failed: store: {e}', flush=True)
        return 1


if __name__ == '__main__':
    sys.exit(main())
