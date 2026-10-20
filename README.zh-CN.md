# Matte

**一个以照片为中心、安静的 Windows 壁纸轮播：让任何一张照片在桌面上都好看，还可以选择开启 Spatial 3D 景深效果。**

[English](README.md)

竖拍的照片放到横屏电脑上，通常会留下黑边或被裁掉。Matte 会为每张照片自动填满两边：

- **纯色**：用感知色彩空间（OKLab）分析照片边缘，选出和边缘最融合的颜色；另外从照片自己的色板里给出两个备选色。
- **延展**：照片边缘向外流动，渐渐融进背景色。用的是调和（膜）填充，也就是"无缝克隆"背后的数学方法，不会拉出条纹。
- **氛围**：照片本身放大并模糊，有亚克力磨砂质感。

围绕这些的是一个安静的托盘小程序：换图时同步渐变，面板颜色跟随壁纸；播放列表可以放文件夹，也可以放单张照片，支持随机或按顺序播放，还能直接旋转、翻转、删除。

### Spatial 3D（可选）

有 `.spatial` 景深文件的照片会变成**有视差的 3D 壁纸**。渲染器是原生 Direct3D 11，只在画面变化时才绘制，平时几乎不占资源；有全屏程序在前面时会自动暂停。镜头可以跟随鼠标、反向跟随、缓慢环绕，或像手持相机一样轻轻晃动。没有景深文件的照片按普通照片播放，两者可以混在同一个播放列表里。

第一次需要时，Matte 会帮你装好生成景深的工具，见[生成 Spatial 图片](#生成-spatial-图片)。

## 下载

在 [Releases](../../releases) 页面下载 `Matte-<版本>-win-x64.zip`，解压到一个单独的文件夹（比如 `%LOCALAPPDATA%\Matte`），运行 `Matte.exe`。托盘里会出现它的图标，点开面板就能把文件夹或照片加进播放列表。

Matte 暂时没有代码签名，第一次运行时 Windows SmartScreen 可能会提示。

运行环境：Windows 10 或 11（64 位），显卡支持 Direct3D 11；Windows 自带的 .NET Framework 4.8。界面目前是中文。

## Matte 会改动电脑上的什么

- **桌面壁纸和背景色**：运行期间由 Matte 设置。退出时，当前那张照片会留作壁纸。
- **指向 `Matte.exe` 的桌面快捷方式**：图标颜色会跟随壁纸。
- **开机时启动**（默认关闭）：打开后会在 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` 下添加一项 `Matte`，关闭时删除。
- **它自己的文件夹**：设置（`settings.json`）、缓存、一个很短的日志；如果用到的话，还有 Spatial 图片和 SHARP。

除此之外不改动别的。卸载时：退出 Matte；如果开过「开机时启动」，先把它关掉；然后删除它的文件夹即可。

## 生成 Spatial 图片

Matte 用 Apple 的 **SHARP** 模型（[apple/ml-sharp](https://github.com/apple/ml-sharp)）生成 `.spatial` 文件。第一次点「生成 Spatial 图片」时，Matte 会引导你安装，点几下就能装好，不需要装 Python 或编译器，也不挑显卡。

- **CPU 版（默认）**：下载约 2.9 GB，占用约 3.6 GB。测试电脑上每张约 50 秒，普通笔记本约 1.5–2.5 分钟。
- **显卡加速（可选）**：适用于显存 8 GB 以上的 NVIDIA 显卡，再下载 2.6 GB，速度快约 2.5 倍。之后可以随时在两者之间切换。

SHARP 的代码采用 Apple 的示例代码许可证，随 Matte 一起发布。**SHARP 的模型权重适用 Apple 的 Machine Learning Research Model License，仅限非商业研究用途。** Matte 不附带权重：它会先展示许可证，你同意之后，才由你的电脑直接从 Apple 下载。你的使用是否符合许可证，由你自己判断。

所有内容都装在 `Matte.exe` 旁边的 `spatial\runtime` 文件夹里，在「存储与清理 → SHARP → 管理」里可以移除。生成在后台以低优先级进行，全屏或用电池时自动暂停。细节和接入自己的生成工具，见 [spatial/README.md](spatial/README.md)。

Matte 会自动整理这些景深文件：
- 照片改名或换了文件夹：按内容指纹自动重新对应。
- 照片所在的磁盘没接上：保留它的景深文件。
- 照片确实被删除了：30 天后把对应的景深文件移到回收站。

## 从源码编译

需要 Visual Studio 2022 或它的 Build Tools，并装上 C++ 组件（会通过 `vswhere` 自动找到）。

```powershell
.\build.ps1                    # 生成 build\Matte.exe、两个 DLL 和 spatial\
.\build.ps1 -VcVars "<vcvars64.bat 的路径>"   # 找不到 Visual Studio 时
.\package.ps1                  # 生成 dist\Matte-<版本>-win-x64.zip，和 Releases 页面上的一样
```

C# 部分用 .NET Framework 自带的 `csc.exe` 编译，不需要 NuGet 包，也没有项目文件。

## 许可证

Matte 采用 [MIT 许可证](LICENSE)发布。第三方代码，以及按你的要求下载的内容，列在 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) 里；SHARP 的模型权重仍然适用 Apple 的研究用途许可证。
