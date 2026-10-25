# Builds Matte into build\: Matte.exe, SpatialNative.dll (the Spatial renderer, native\SpatialNative.cpp),
# SpatialSlice.dll (the SHARP generator's slicer, native\SpatialSlice.cpp) and spatial\ (the generator's Python side).
# They have to stay together; Matte keeps its settings and caches in the folder its exe is in.
# Needs Windows 10/11, the .NET Framework 4.8 compiler that comes with Windows, and Visual Studio (or its Build Tools)
# with the C++ workload, found with vswhere; -VcVars points at a vcvars64.bat directly instead.
param([string]$VcVars='',[switch]$NativeOnly)
$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
if(!$VcVars){
 $vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
 if(Test-Path $vswhere){$vs=& $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath;if($vs){$VcVars=Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'}}
}
if(!$VcVars -or !(Test-Path $VcVars)){throw "Visual Studio with the C++ workload was not found. Install it (Build Tools are enough), or pass -VcVars <path to vcvars64.bat>."}
if(!(Test-Path build\obj)){New-Item -ItemType Directory build\obj | Out-Null}
# vcvars' own warnings go nowhere: on stderr they would stop this script
cmd /d /c "call `"$VcVars`" >nul 2>nul && cl /nologo /LD /O2 /EHsc /MT /std:c++17 /W3 /utf-8 /Fobuild\obj\ /Febuild\SpatialNative.dll native\SpatialNative.cpp /link /NOLOGO >build\obj\cl.log"
if($LASTEXITCODE -ne 0){Get-Content build\obj\cl.log; throw "cl (SpatialNative) failed"}
if($NativeOnly){return}
cmd /d /c "call `"$VcVars`" >nul 2>nul && cl /nologo /LD /O2 /EHsc /MT /std:c++20 /W3 /utf-8 /Fobuild\obj\ /Febuild\SpatialSlice.dll native\SpatialSlice.cpp /link /NOLOGO >build\obj\cl-slice.log"
if($LASTEXITCODE -ne 0){Get-Content build\obj\cl-slice.log; throw "cl (SpatialSlice) failed"}
if(Test-Path build\spatial){Remove-Item build\spatial -Recurse -Force}
Copy-Item spatial build\spatial -Recurse -Exclude '__pycache__'
$fx='C:\Windows\Microsoft.NET\Framework64\v4.0.30319';$wpf="$fx\WPF"
& "$fx\csc.exe" /nologo /target:winexe /optimize+ /out:build\Matte.exe /win32icon:Matte.ico /win32manifest:app.manifest `
 /r:System.Web.Extensions.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Core.dll /r:Microsoft.CSharp.dll /r:System.Xaml.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll `
 "/r:$wpf\PresentationCore.dll" "/r:$wpf\PresentationFramework.dll" "/r:$wpf\WindowsBase.dll" `
 AssemblyInfo.cs App.cs PlaylistInventory.cs Slideshow.cs SlidePlayback.cs SpatialVerification.cs SpatialFormat.cs SpatialFailures.cs DisplayCache.cs SynchronizedFade.cs SpatialMode.cs SpatialCatalog.cs SpatialDesktop.cs SpatialNative.cs SpatialPower.cs FullscreenCheck.cs Swatch.cs ColorModel.cs PanelUI.cs PanelPages.cs Playback.cs SpatialGen.cs Backdrop.cs AmbientWallpaper.cs Palette.cs SpatialStore.cs SpatialStoreMove.cs ExtendWallpaper.cs SpatialIndex.cs SpatialRetention.cs SpatialIdentity.cs SpatialStoreLock.cs Theme.cs SharpSetup.cs SharpPage.cs /resource:GlassPanel.xaml,GlassPanel.xaml
if($LASTEXITCODE -ne 0){throw "csc failed"}
