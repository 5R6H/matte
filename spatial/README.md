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
| `SpatialBatch.ps1` | what Matte runs (`-RootsFile`, `-Cache`, or `-Describe`); supplies the same dimensions to description and generation |
| `matte_sharp.py` | describes the desired recipe and runs SHARP on captured photo/checkpoint bytes |
| `identity.py`, `publish.py` | v1 validation, authoritative needs, byte-range store lock, and immutable pair publication (stdlib only) |
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
- `-Cache`: the picture store. Custom output is unverified unless it supplies valid v1 pairs under the identity contract below. Top-level `<stem>.spatial` files are legacy; they are retained and do not satisfy generation needs. Legacy playback becomes flat in T2c, before this change is released.

Matte reads these lines from standard output to show progress:

| Line starts with | Meaning |
|---|---|
| `Processing <n> valid` | `<n>` images are about to be made (may appear more than once; Matte adds them up) |
| `Predicting gaussians for` | one image is being made |
| `Nothing to generate` | everything is up to date |
| `Saved: <name>` | the photo has a verified pair, newly made or reused |
| `Failed: <name>: <reason>` | the photo failed; unreadable input is an error, never "done" |

Exit code `0` means success. Anything else is shown as an error.

Other optional settings: `SpatialPython`, a python.exe your script needs (Matte only checks that it exists), and `SpatialFolder`, where `.spatial` files are kept.

## Desired recipe and v1 publication

Describe the desired recipe before generating, without importing torch or the other generation packages:

```
python matte_sharp.py --describe-recipe --checkpoint <weights.pt> --width 2560 --height 1600
powershell -File SpatialBatch.ps1 -Describe
```

The two output lines are `Recipe: <canonical JSON>` and `RecipeSha: <SHA-256>`. `SpatialBatch.ps1` uses one dimension-selection path for both modes. The recipe includes the generator/exporter revisions, dimensions, EXIF handling, and the SHA-256 of the checkpoint bytes. Generation loads those same captured checkpoint bytes with `torch.load(BytesIO(...))`; it never hashes and loads separate reads of the weights file. `OUTPUT_CONSTANTS` pins output-affecting settings to the generator revision; decimal values are represented as strings in its integer-only canonical JSON.

Generation uses `--roots-file`, `--cache`, `--checkpoint`, `--width`, `--height`, and `--lock`. Artifacts live at:

```
<store>\v1\<full source SHA-256>\<full recipe SHA-256>.spatial
<store>\v1\<full source SHA-256>\<full recipe SHA-256>.spatial.json
<store>\v1\quarantine\<UTC timestamp>-<source SHA-256>-<recipe SHA-256>.spatial
<store>\v1\quarantine\<UTC timestamp>-<source SHA-256>-<recipe SHA-256>.json
```

Needs are determined by full artifact verification for that exact source/desired-recipe pair, never by stems or timestamps. A run scans photos for progress, retaining only digests and status without copying photos to temporary storage. Before generation it re-reads each pending photo, checks needs using its fresh digest, and decodes those same bytes; edits since the scan therefore publish under the new identity. Identical content shares one generated artifact. `Processing N` counts photos needing work, including duplicate locations and unreadable inputs needing attention. Already-current photos report `Saved` without occupying a `Predicting` progress slot.

Sidecar v1 source metadata has exactly `sha256`, `size`, and `name` (a display label). `source.path` is forbidden; source locations belong only in the local retention history.

All cooperating writers and store moves use the **stable** `<Matte dir>\spatial\store.lock`, outside the relocated store. `SpatialBatch.ps1` passes that path. Open/create without truncation, lock byte 0 exclusively (`FileStream.Lock(0,1)` / `msvcrt.locking(..., LK_NBLCK, 1)` at offset 0), retry every 100 ms until timeout, then unlock and close. A second stable file in the same folder, `generator.lock`, uses the same byte-0 protocol. The generator holds it for the whole run, before temp cleanup and scanning, and releases it on completion or failure. A second run fails immediately with `Failed: generation: another generation is running`, before touching the store. Store moves also hold it for their whole duration. The global acquisition order is always **generator.lock, then store.lock**; generation takes store.lock per publication, while a move holds both until complete. Startup temp cleanup requires both locks. Do not delete or relocate either lock file. Custom v1 writers must follow the same lock and publication protocol; a valid sidecar does not exempt a writer from coordination.

Generation writes a unique `.tmp-<16 hex>` artifact while holding generator.lock, outside store.lock and keeps it open so another run cannot clean up live work. Under the lock it checks the destination again: the first valid pair wins; unknown versions are retained untouched and reported as conflicts; inaccessible files defer publication; invalid/orphan files move into quarantine without overwriting earlier recoveries. It flushes/fsyncs the artifact and publishes it with a non-overwriting rename, then writes, flushes/fsyncs and publishes the canonical sidecar. `Saved` follows successful verification and store.lock release; generator.lock remains held until the run ends. Ordinary errors roll back newly installed files and restore quarantined originals; a process crash can leave an orphan that the next run recovers. Startup removes closed leftover `*.tmp-*` files under `v1` on a best-effort basis while holding both locks. Publication does not delete legacy pictures or age out quarantine.

The installer's `--trial <photo> --out <file>` remains a plain `.spatial` output without a v1 sidecar.

## The `.spatial` format

This is the layered depth format read by `native\SpatialNative.cpp`. It has a header, a set of depth slices with their mesh blocks, then a colour image and an alpha image, both JPEG. For the exact layout, see `ParseSpatial` in that file and `exporter.py` here.
