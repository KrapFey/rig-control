# Fetches HeadsetControl (official Windows build) into build\headsetcontrol (not committed).
# RigControl runs it for headset settings (battery, sidetone, auto-off, EQ, ...). SHA-256 pinned (matches the
# digest GitHub publishes for the release asset).
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$out = Join-Path $root 'build\headsetcontrol'

$version = '4.2.0'
$url = "https://github.com/Sapd/HeadsetControl/releases/download/$version/headsetcontrol-windows-x86_64.exe"
$sha256 = '81acae4c083aaa40a2e9c1bce7b3dd9625e74b9d9ee96384bb43b2dff7ff0fa5'

if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force $out | Out-Null
$exe = Join-Path $out 'headsetcontrol.exe'
Invoke-WebRequest $url -OutFile $exe -UseBasicParsing
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash.ToLower()
if ($hash -ne $sha256) { Remove-Item -Recurse -Force $out; throw "HeadsetControl checksum mismatch: $hash" }

# HeadsetControl is GPL-3.0: ship its licence and where its source lives.
Invoke-WebRequest "https://raw.githubusercontent.com/Sapd/HeadsetControl/$version/license" -OutFile (Join-Path $out 'LICENSE-HeadsetControl.txt') -UseBasicParsing
@"
HeadsetControl $version (unmodified official Windows x86_64 build), used by RigControl for headset settings.
Licence: GPL-3.0, see LICENSE-HeadsetControl.txt.
Source: https://github.com/Sapd/HeadsetControl/tree/$version
"@ | Set-Content (Join-Path $out 'SOURCE.txt') -Encoding utf8

"HeadsetControl ready: {0:N1} MB in {1}" -f ((Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), $out
