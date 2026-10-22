# Third-party notices

Matte itself is under the MIT License (see `LICENSE`). It includes, or downloads on the user's request, the following.

## Included in this repository

| Component | Where | License |
|---|---|---|
| SHARP model code ([apple/ml-sharp](https://github.com/apple/ml-sharp), commit `aed6527`), without its command line | `spatial/sharp/` | Apple sample-code license, see `spatial/sharp/LICENSE`; third-party notices of SHARP in `spatial/sharp/ACKNOWLEDGEMENTS` |
| Spatial Photos ([frozein/SpatialPhotos](https://github.com/frozein/SpatialPhotos)): slicing and `.spatial` export | `spatial/spatial_photos.py`, `spatial/exporter.py` | MIT, see `spatial/LICENSE-SpatialPhotos.md` |
| ddgs_cpu, the CPU splat renderer from Spatial Photos | `native/ddgs/` (built into `SpatialSlice.dll`) | MIT, see `native/ddgs/LICENSE.md` |
| The Spatial renderer in `native/SpatialNative.cpp` follows the drawing of Spatial Photos' web viewer (`viewer.js`) | `native/SpatialNative.cpp` | MIT (Spatial Photos), see `spatial/LICENSE-SpatialPhotos.md` |
| rectpack 0.2.2 ([secnot/rectpack](https://github.com/secnot/rectpack)) | `spatial/rectpack/` | Apache License 2.0, see `spatial/rectpack/LICENSE` |

`spatial/spatial_photos.py` is changed only to use Matte's compiled renderer (`spatial/ddgs.py`) instead of compiling ddgs_cpu on the user's machine. The other files are unchanged.

## Downloaded when the user installs SHARP from Matte

Matte does not ship these. They are downloaded onto the user's computer, into `spatial\runtime`, after the user starts the install on Matte's panel.

| Component | From | License |
|---|---|---|
| SHARP model weights (`sharp_2572gikvuh.pt`) | `ml-site.cdn-apple.com` | Apple Machine Learning Research Model License; scope and acceptance described below. |
| uv 0.12.23 | GitHub releases of astral-sh/uv | MIT or Apache-2.0 |
| Python 3.12.15 (python-build-standalone) | via uv | Python Software Foundation License |
| PyTorch, torchvision, timm, NumPy, SciPy, Pillow, plyfile and their dependencies, as locked in `spatial/requirements-*.txt` | PyPI and download.pytorch.org | each under its own license (mostly BSD, MIT, Apache-2.0) |

SHARP's model weights are under the [Apple Machine Learning Research Model License](https://github.com/apple/ml-sharp/blob/main/LICENSE_MODEL), which permits use only for non-commercial research "with the sole intent to advance scientific knowledge" and excludes product development and commercial use. Matte neither ships nor redistributes the weights; it downloads them from Apple onto your computer only after you accept the license. Whether personal wallpaper use falls within those terms is your judgement.

SHARP's code (`spatial/sharp`) is under Apple's sample-code license, kept with its license and notices. Matte is not affiliated with or endorsed by Apple.
