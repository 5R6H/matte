# Builds and runs Matte's regression tests (tests\MatteTests.cs) against the sources in this repository: Matte's own
# C# files (the ones build.ps1 compiles) and the tests, as one program in tests\bin. Needs only the .NET Framework 4.8
# compiler that comes with Windows. Exit code: the number of failed tests.
param([switch]$IdentityOnly)
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
if($IdentityOnly){& "$bin\MatteTests.exe" --identity}else{& "$bin\MatteTests.exe"}
$failed=$LASTEXITCODE
# Probe Python 3, in priority order. A present Python 2 (or a broken launcher) is not a Python 3.
$pythonCommand=$null;$pythonArgs=@()
$candidates=@(@{Command=(Join-Path $repo 'spatial\runtime\python.exe');Args=@()},@{Command='py';Args=@('-3')},@{Command='python';Args=@()})
foreach($candidate in $candidates){
 $command=Get-Command $candidate.Command -ErrorAction SilentlyContinue
 if(!$command){continue}
 $probeArgs=$candidate.Args
 try{& $command.Source @probeArgs -c 'import sys; sys.exit(0 if sys.version_info.major == 3 else 1)' 2>$null
  if($LASTEXITCODE -eq 0){$pythonCommand=$command.Source;$pythonArgs=$probeArgs;break}}
 catch{} # An absent launcher runtime is a failed probe, not a test failure.
}
if($pythonCommand){
 & $pythonCommand @pythonArgs (Join-Path $repo 'spatial\test_identity.py')
 if($LASTEXITCODE -ne 0){$failed++}
}else{Write-Output 'SKIP python identity tests: no Python 3'}
exit $failed
