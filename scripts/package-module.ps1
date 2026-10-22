<#
.SYNOPSIS
Builds the VRCFaceTracking module and packs it as a zip for VRCFT's
"install module from zip".

.DESCRIPTION
The zip holds module.json, the module and core DLLs and the deps file.
HTC's weights are not included: the module finds them on first start
(see docs/model-sources.md).

.PARAMETER VrcftPath
VRCFaceTracking install to compile against, if not the default Steam path.
#>
param(
    [string]$VrcftPath
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\Unvived.FacialTracker\Unvived.FacialTracker.csproj'
$output = Join-Path $root 'artifacts\module'
$artifacts = Join-Path $root 'artifacts'

$buildArgs = @('build', $project, '-c', 'Release', '-o', $output, '--nologo', '-v', 'quiet')
if ($VrcftPath) { $buildArgs += "-p:VrcftPath=$VrcftPath" }
& dotnet @buildArgs
if ($LASTEXITCODE -ne 0) { throw "build failed" }

$metadata = Get-Content (Join-Path $output 'module.json') -Raw | ConvertFrom-Json
$files = @(
    'module.json',
    'Unvived.FacialTracker.dll',
    'Unvived.FacialTracker.deps.json'
) | ForEach-Object { Join-Path $output $_ }

$zip = Join-Path $artifacts "Unvived.FacialTracker-$($metadata.Version).zip"
Compress-Archive -Path $files -DestinationPath $zip -Force
Write-Host "wrote $zip"
