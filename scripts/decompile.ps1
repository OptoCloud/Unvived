<#
.SYNOPSIS
  Import binaries into the Ghidra project ghidra_project/sranipal and decompile
  every function to dumps/<binary>/all_functions.c (+ decompiled/*.c).

.EXAMPLE
  pwsh scripts/decompile.ps1                        # the default target set
  pwsh scripts/decompile.ps1 vendor\SRanipal\ViveSR_API.dll
  pwsh scripts/decompile.ps1 -Project sranipal2 <files>   # a second run in parallel
#>
param([string[]]$Files, [string]$Project = 'sranipal')
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$ghidra = if ($env:GHIDRA_HOME) { $env:GHIDRA_HOME } else { 'C:\Tools\ghidra_12.0.4_PUBLIC' }

if (-not $Files) {
    $sr = 'vendor\SRanipal'
    $eye = "$sr\eye_camera_device\vive_pro_eye"
    $Files = @(
        # Client SDK, as shipped with the VRCFT SRanipal module
        'vendor\client\SRanipal.dll', 'vendor\client\ViveSR_Client.dll', 'vendor\client\libHTC_License.dll',
        # Runtime
        "$sr\sr_runtime.exe", "$sr\ViveSR_API.dll", "$sr\ViveSR_Runtime.dll",
        # Eye
        "$sr\EyeCameraModule.dll", "$sr\EyePredictionModule.dll", "$sr\EyePredictionTVM.dll",
        "$eye\EyeCameraDevice.dll", "$eye\EyeDeviceConfiguration.dll", "$eye\libEChipConnection.dll",
        "$eye\USBDevConnectDLL.dll",
        # Lip
        "$sr\LCameraModule.dll", "$sr\LPredictionModule.dll", "$sr\LipPredictionTVM.dll"
    )
}

$proj = Join-Path $repo 'ghidra_project'
New-Item -ItemType Directory -Force $proj, (Join-Path $repo 'logs') | Out-Null
$env:OUTDIR = Join-Path $repo 'dumps'

foreach ($f in $Files) {
    $path = Join-Path $repo $f
    if (-not (Test-Path $path)) { Write-Warning "missing: $f"; continue }
    Write-Host "== $f"
    $log = Join-Path $repo "logs\ghidra_$([IO.Path]::GetFileName($f)).log"
    & "$ghidra\support\analyzeHeadless.bat" $proj $Project -import $path -overwrite `
        -scriptPath $PSScriptRoot -postScript BulkDecompile.java *> $log
    Select-String -Path $log -Pattern '\[BulkDecompile\] done|ERROR' | ForEach-Object { "   $($_.Line.Trim())" }
}
