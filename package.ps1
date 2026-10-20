# Builds Matte and packs what a user needs into dist\Matte-<version>-win-x64.zip (for a GitHub release): Matte.exe,
# the two DLLs, spatial\ (the SHARP generator's scripts; the model itself is downloaded from Apple by the user), the
# license and notices. Nothing from this computer's own use of Matte (settings, caches, pictures) goes in.
$ErrorActionPreference='Stop'
Set-Location $PSScriptRoot
& .\build.ps1
$version=(Get-Content VERSION -TotalCount 1).Trim()
$name="Matte-$version-win-x64"
$stage=Join-Path 'dist' $name
if(Test-Path $stage){Remove-Item $stage -Recurse -Force}
New-Item -ItemType Directory $stage | Out-Null
Copy-Item build\Matte.exe,build\SpatialNative.dll,build\SpatialSlice.dll,LICENSE,THIRD_PARTY_NOTICES.md,README.md,README.zh-CN.md $stage
Copy-Item spatial (Join-Path $stage 'spatial') -Recurse -Exclude '__pycache__','runtime','pictures'
Get-ChildItem $stage -Recurse -Directory -Filter __pycache__ | Remove-Item -Recurse -Force
$zip=Join-Path 'dist' "$name.zip"
if(Test-Path $zip){Remove-Item $zip}
# Entry names with / (the zip standard): Compress-Archive and ZipFile.CreateFromDirectory on .NET Framework write \,
# which tools other than Windows' own misread.
Add-Type -AssemblyName System.IO.Compression,System.IO.Compression.FileSystem
$root=(Resolve-Path 'dist').Path;$full=(Resolve-Path $stage).Path
$archive=[IO.Compression.ZipFile]::Open((Join-Path $root "$name.zip"),[IO.Compression.ZipArchiveMode]::Create)
try{Get-ChildItem $full -Recurse -File | ForEach-Object {
 $entry=$name+'/'+$_.FullName.Substring($full.Length+1).Replace('\','/')
 [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$_.FullName,$entry,[IO.Compression.CompressionLevel]::Optimal)}}
finally{$archive.Dispose()}
$hash=(Get-FileHash $zip -Algorithm SHA256).Hash
"$zip  $([math]::Round((Get-Item $zip).Length/1MB,2)) MB  SHA-256 $hash"
