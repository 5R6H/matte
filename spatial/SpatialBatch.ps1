# Matte's built-in Spatial generator (SHARP, installed by Matte into spatial\runtime). Matte runs it with its playlist
# (-RootsFile, one folder or image per line) and the folder the .spatial files go to (-Cache); see README.md here.
param([string]$RootsFile,[string]$Cache)
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=New-Object System.Text.UTF8Encoding($false)
$env:PYTHONIOENCODING='utf-8'
$runtime=Join-Path $PSScriptRoot 'runtime'
$python=Join-Path $runtime 'env\Scripts\python.exe'
$model=Join-Path $runtime 'model\sharp_2572gikvuh.pt'
if(!(Test-Path $python) -or !(Test-Path $model)){Write-Output 'SHARP 还没有安装完成'; exit 3}
# the photos are made at most the size of the largest screen
$video=Get-CimInstance Win32_VideoController | Where-Object {$_.CurrentHorizontalResolution -gt 0} | Sort-Object CurrentHorizontalResolution -Descending | Select-Object -First 1
if($video){$w=[int]$video.CurrentHorizontalResolution;$h=[int]$video.CurrentVerticalResolution}else{$w=2560;$h=1600}
& $python -E -s -u (Join-Path $PSScriptRoot 'matte_sharp.py') --roots-file $RootsFile --cache $Cache --checkpoint $model --width $w --height $h
exit $LASTEXITCODE
