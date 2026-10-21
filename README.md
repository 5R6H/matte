# Matte

**A calm, photo-first wallpaper slideshow for Windows that makes any photo look intentional on your desktop, with optional Spatial 3D depth.**

[中文说明](README.zh-CN.md)

Portrait photos on a landscape screen usually end up with black bars or a crop. Matte fills the space instead, and it does it per photo, automatically:

- **Solid colour**: the colour that meets the photo's edge most smoothly, picked by a perceptual (OKLab) seam analysis. Two more suggestions come from the photo's own palette.
- **Extend**: the photo's edges flow outward and dissolve into that colour. It uses a harmonic (membrane) fill, the maths behind seamless cloning, so nothing gets stretched into streaks.
- **Ambient**: the photo itself, enlarged and blurred like frosted acrylic.

Around that sits a quiet tray app: synchronized cross-fades between photos, a panel that takes its colours from the wallpaper, playlists of folders and single photos, shuffle or in-order playback.

### Spatial 3D (optional)

Photos that have a `.spatial` depth file become **parallax 3D wallpapers**. A native Direct3D 11 renderer draws them. It only redraws when something changes, so it costs almost nothing while you work, and it pauses while a fullscreen app is in front. The camera can follow the mouse, move against it, circle slowly, or drift like a handheld shot. Photos without depth play flat, and the two mix freely in one playlist.

Matte installs the depth generator for you when you first need it. See [Spatial generation](#spatial-generation).

## Download

Get `Matte-<version>-win-x64.zip` from the [Releases](../../releases) page, unzip it into a folder of its own (for example `%LOCALAPPDATA%\Matte`) and start `Matte.exe`. Its icon appears in the tray; click it to open the panel and add folders or photos to the playlist.

Matte is not code-signed yet, so Windows SmartScreen may warn the first time you start it.

Requirements: Windows 10 or 11 (x64) with a Direct3D 11 GPU, and the .NET Framework 4.8 that comes with Windows. The interface is in Chinese for now.

## What Matte changes on your computer

- **The desktop wallpaper and its background colour**, while it runs. When you quit, the photo on show stays as your wallpaper.
- **Desktop shortcuts to `Matte.exe`**: their icon follows the wallpaper's colour.
- **Start with Windows** (开机时启动, off by default): a `Matte` entry under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, removed when you turn it off.
- **Its own folder**: settings (`settings.json`), caches, a short log, and, if you use them, Spatial pictures and SHARP.

Nothing else. To uninstall, quit Matte, turn off 开机时启动 if you turned it on, and delete its folder.

## Spatial generation

Matte makes `.spatial` files with Apple's **SHARP** model ([apple/ml-sharp](https://github.com/apple/ml-sharp)). The first time you choose 生成 Spatial 图片, Matte offers to install it in a few clicks. No Python, compiler or particular graphics card is needed.

- **CPU edition (default):** downloads about 2.9 GB and takes about 3.6 GB on disk. One photo took about 50 seconds on the test machine (1.5 to 2.5 minutes on a typical laptop).
- **GPU acceleration (optional):** for NVIDIA cards with 8 GB of graphics memory or more. It downloads another 2.6 GB and is about 2.5 times faster. You can switch between the two later.

SHARP's code is under Apple's sample-code license and ships with Matte. **SHARP's model weights are under Apple's Machine Learning Research Model License, which allows non-commercial research use only.** Matte does not include the weights. It shows the license, and only after you accept it are the weights downloaded from Apple onto your computer. Whether your use fits the license is for you to judge.

Everything is installed into `spatial\runtime` next to `Matte.exe`; 存储与清理 → SHARP → 管理 removes it. Pictures are made in the background at low priority, and making pauses while a fullscreen app is in front or the computer runs on battery. For the details, or to plug in your own generator, see [spatial/README.md](spatial/README.md).

Matte keeps the pictures tidy by itself:
- A renamed or moved photo gets its depth file back, matched by content fingerprint.
- A photo on an unplugged disk keeps its depth file.
- A depth file whose photo is truly gone goes to the Recycle Bin after 30 days.

## Build from source

You need Visual Studio 2022 or its Build Tools with the C++ workload (found automatically through `vswhere`).

```powershell
.\build.ps1                    # build\Matte.exe, the two DLLs and spatial\
.\build.ps1 -VcVars "<path to vcvars64.bat>"   # if Visual Studio is not found
.\package.ps1                  # dist\Matte-<version>-win-x64.zip, as on the Releases page
```

C# is compiled with the `csc.exe` that ships with the .NET Framework. There are no NuGet packages and no project file.

## Project layout

| File | What it does |
|---|---|
| `App.cs` | tray app, Windows wallpaper service (`IDesktopWallpaper`), colour fades |
| `Slideshow.cs`, `Playback.cs` | the slideshow, the playlist and the settings |
| `ColorModel.cs`, `Palette.cs` | automatic colour and palette suggestions |
| `ExtendWallpaper.cs`, `AmbientWallpaper.cs` | the Extend and Ambient backgrounds |
| `SynchronizedFade.cs` | the cross-fade drawn on the desktop |
| `SpatialDesktop.cs`, `SpatialNative.cs`, `SpatialPower.cs`, `native\SpatialNative.cpp` | the Spatial 3D layer, its Direct3D renderer, and when it pauses |
| `SpatialGen.cs`, `SpatialIndex.cs`, `SpatialStore*.cs` | generation, the depth-file index, storage and moving it |
| `SharpSetup.cs`, `SharpPage.cs`, `spatial\`, `native\SpatialSlice.cpp` | installing SHARP, and the generator it runs |
| `GlassPanel.xaml`, `PanelUI.cs`, `PanelPages.cs`, `Backdrop.cs` | the tray panel |

## License

Matte is released under the [MIT License](LICENSE). Third-party code and what is downloaded at your request are listed in [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md); SHARP's model weights keep Apple's research-only license.
