"""The CPU splat renderer (ddgs_cpu from Spatial Photos, MIT) through Matte's SpatialSlice.dll.

Same interface as Spatial Photos' ddgs_cpu module, which compiles the renderer on the user's machine; Matte ships it
compiled, so no C++ compiler is needed. The DLL sits next to Matte.exe (one folder up from here), or MATTE_SLICE_DLL.
"""
import ctypes
import os
from typing import NamedTuple

import numpy as np
import torch

_DLL = os.environ.get('MATTE_SLICE_DLL') or os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', 'SpatialSlice.dll')
_lib = None
_F = ctypes.POINTER(ctypes.c_float)


def _load():
    global _lib
    if _lib is None:
        _lib = ctypes.WinDLL(os.path.abspath(_DLL))
        _lib.DdgsRender.restype = ctypes.c_ulonglong
        _lib.DdgsRender.argtypes = [ctypes.c_uint, ctypes.c_uint, _F, _F, ctypes.c_float, ctypes.c_float,
                                    ctypes.c_uint, _F, _F, _F, _F, _F, ctypes.c_uint, _F, _F, _F]
    return _lib


def _arr(t):
    return np.ascontiguousarray(t.detach().to('cpu', torch.float32).numpy())


def _ptr(a):
    return a.ctypes.data_as(_F)


class RenderOutputs:
    COLOR = 1 << 0
    ALPHA = 1 << 1
    DEPTH = 1 << 2


class Settings:
    def __init__(self, width, height, view, proj, focalX, focalY, outputs=RenderOutputs.COLOR, debug=False, threads=None):
        self.width, self.height = int(width), int(height)
        self.view, self.proj = view, proj
        self.focalX, self.focalY = float(focalX), float(focalY)
        self.outputs = outputs
        self.threads = int(os.environ.get('MATTE_SLICE_THREADS', '0')) if threads is None else int(threads)


class RenderResult(NamedTuple):
    color: torch.Tensor | None
    alpha: torch.Tensor | None
    depth: torch.Tensor | None


def render(settings, means, scales, rotations, opacities, harmonics, normalizeRotations=True):
    if normalizeRotations:
        rotations = rotations / torch.norm(rotations, dim=-1, keepdim=True).clamp(min=1e-8)
    device = means.device
    m, s, r, o = _arr(means), _arr(scales), _arr(rotations), _arr(opacities)
    h = _arr(harmonics).reshape(len(m), -1)[:, :3].copy() if len(m) else np.zeros((0, 3), np.float32)   # plain rgb, a stride of 3 (a slice can be empty)
    view, proj = _arr(settings.view), _arr(settings.proj)
    H, W = settings.height, settings.width
    color = np.zeros((H, W, 3), np.float32); alpha = np.zeros((H, W, 1), np.float32); depth = np.zeros((H, W, 1), np.float32)
    _load().DdgsRender(W, H, _ptr(view), _ptr(proj), settings.focalX, settings.focalY, len(m), _ptr(m), _ptr(s), _ptr(r),
                       _ptr(o), _ptr(h), settings.threads, _ptr(color), _ptr(alpha), _ptr(depth))
    to = lambda a: torch.from_numpy(a).to(device)
    return RenderResult(
        color=to(color) if settings.outputs & RenderOutputs.COLOR else None,
        alpha=to(alpha) if settings.outputs & RenderOutputs.ALPHA else None,
        depth=to(depth) if settings.outputs & RenderOutputs.DEPTH else None,
    )
