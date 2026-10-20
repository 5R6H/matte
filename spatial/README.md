# Spatial generation

Matte makes `.spatial` files with Apple's **SHARP** model. The first time you choose 生成 Spatial 图片, Matte offers to install it.

## What the installer does

Everything goes into `spatial\runtime` next to `Matte.exe`. Nothing else on the computer is changed, and deleting that folder uninstalls it. You can also remove it from 存储与清理.

1. Downloads [uv](https://github.com/astral-sh/uv) 0.12.23 and checks its SHA-256.
2. Uses uv to make a private Python 3.12.15 and install the packages locked in `requirements-cpu.txt`, each checked by hash. With 显卡加速 on (NVIDIA, 8 GB of graphics memory or more), it installs `requirements-cu128.txt` instead.
3. Shows Apple's license. Only after you accept does it download the model weights from Apple (`ml-site.cdn-apple.com`, 2.68 GB) and check their SHA-256. If SHARP's own tools already put the weights in `%USERPROFILE%\.cache\torch\hub\checkpoints`, that copy is used instead of a second download.
4. Makes one stock Windows picture as a trial, which also measures how long one photo takes.

Downloads resume after a break, and also after Matte is restarted.

| Edition | Download | On disk | One photo (2026-10 test machine) |
|---|---|---|---|
| CPU (default) | about 2.9 GB | about 3.6 GB | about 50 s |
| GPU (NVIDIA, 8 GB+) | about 5.5 GB | about 7.5 GB | about 20 s |

Pictures are made in the background at below-normal priority, leaving two CPU cores free. Making pauses while a fullscreen app is in front or the computer runs on battery.

## The model's license

SHARP's **code** (in `sharp\`, from [apple/ml-sharp](https://github.com/apple/ml-sharp) at `aed6527`) is under Apple's sample-code license; see `sharp\LICENSE`.

SHARP's **weights** are under the Apple Machine Learning Research Model License, which allows non-commercial research use only. Matte does not include them. They are downloaded from Apple onto your computer after you accept that license, and whether your use fits it is for you to judge.

## Files here

| File | What it is |
|---|---|
| `SpatialBatch.ps1` | what Matte runs (`-RootsFile`, `-Cache`); starts Matte's own Python on `matte_sharp.py` |
| `matte_sharp.py` | finds the photos that need a `.spatial`, runs SHARP and writes the files |
| `sharp\` | SHARP's model code (Apple, unchanged, without its command line) |
| `spatial_photos.py`, `exporter.py` | turn SHARP's Gaussians into the `.spatial` format ([Spatial Photos](https://github.com/frozein/SpatialPhotos), MIT, see `LICENSE-SpatialPhotos.md`) |
| `rectpack\` | packs the slices into one picture ([rectpack](https://github.com/secnot/rectpack) 0.2.2, Apache-2.0, see `rectpack\LICENSE`) |
| `ddgs.py` | calls the slicer in `SpatialSlice.dll` (Spatial Photos' CPU renderer, built by `build.ps1` from `native\ddgs`) |
| `requirements.in`, `requirements-*.txt` | the packages, and the locked lists the installer uses |

## Using your own generator

To use your own generator, set its path as `SpatialGenerator` in `settings.json` in Matte's folder. Matte runs:

```
powershell.exe -NoProfile -ExecutionPolicy Bypass -File <script> -RootsFile <list.txt> -Cache <folder>
```

- `-RootsFile`: a UTF-8 text file with one path per line. Each line is a folder (every image in it, subfolders included) or a single image.
- `-Cache`: the folder to write into, one `<file name without extension>.spatial` per image. Skip images whose file is newer than the image.

Matte reads these lines from standard output to show progress:

| Line starts with | Meaning |
|---|---|
| `Processing <n> valid` | `<n>` images are about to be made (may appear more than once; Matte adds them up) |
| `Predicting gaussians for` | one image is being made |
| `Nothing to generate` | everything is up to date |

Exit code `0` means success. Anything else is shown as an error.

Other optional settings: `SpatialPython`, a python.exe your script needs (Matte only checks that it exists), and `SpatialFolder`, where `.spatial` files are kept.

## The `.spatial` format

This is the layered depth format read by `native\SpatialNative.cpp`. It has a header, a set of depth slices with their mesh blocks, then a colour image and an alpha image, both JPEG. For the exact layout, see `ParseSpatial` in that file and `exporter.py` here.
