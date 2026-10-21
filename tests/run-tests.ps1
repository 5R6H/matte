# Builds and runs Matte's regression tests (tests\MatteTests.cs) against the sources in this repository: Matte's own
# C# files (the ones build.ps1 compiles) and the tests, as one program in tests\bin. Needs only the .NET Framework 4.8
# compiler that comes with Windows. Exit code: the number of failed tests.
$ErrorActionPreference='Stop'
$repo=Resolve-Path (Join-Path $PSScriptRoot '..')
$bin=Join-Path $PSScriptRoot 'bin'
if(!(Test-Path $bin)){New-Item -ItemType Directory $bin | Out-Null}
# Matte's sources: exactly what build.ps1 compiles
$build=Get-Content (Join-Path $repo 'build.ps1') -Raw
$sources=[regex]::Matches($build,'\b[A-Za-z]+\.cs\b') | ForEach-Object { Join-Path $repo $_.Value } | Select-Object -Unique
$fx='C:\Windows\Microsoft.NET\Framework64\v4.0.30319';$wpf="$fx\WPF"
& "$fx\csc.exe" /nologo /target:exe /platform:x64 /nowarn:0162,0169,0414,0649 /out:"$bin\MatteTests.exe" /main:MatteTests "/win32manifest:$repo\app.manifest" `
 /r:System.Web.Extensions.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Core.dll /r:Microsoft.CSharp.dll /r:System.Xaml.dll /r:System.IO.Compression.dll /r:System.IO.Compression.FileSystem.dll `
 "/r:$wpf\PresentationCore.dll" "/r:$wpf\PresentationFramework.dll" "/r:$wpf\WindowsBase.dll" `
 $sources (Join-Path $PSScriptRoot 'MatteTests.cs') "/resource:$repo\GlassPanel.xaml,GlassPanel.xaml"
if($LASTEXITCODE -ne 0){throw "csc failed"}
& "$bin\MatteTests.exe"
exit $LASTEXITCODE
