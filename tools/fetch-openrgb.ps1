# Fetches the OpenRGB 1.1 engine into build\openrgb (not committed). RigControl runs it headless
# (`--server`, no window) as its lighting backend, like the bundled liquidctl.
# The zip is pinned by SHA-256. It was cross-checked: its OpenRGB.exe is byte-identical to the one the
# official OpenRGB 1.1 installer puts on disk.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$out = Join-Path $root 'build\openrgb'

$url = 'https://codeberg.org/OpenRGB/OpenRGB/releases/download/release_1.1/OpenRGB_1.1_Windows_64_8e16de2.zip'
$sha256 = '63291726c6fc84eb479ef2ee752e97e1187f6ce9c49fd27eee40ded588a8172a'
$commit = '8e16de27858cf2be6af54a33ce003df74cb9fd3e'

if (Test-Path $out) { Remove-Item -Recurse -Force $out }
$dl = Join-Path ([IO.Path]::GetTempPath()) "rigcontrol-openrgb-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory $dl | Out-Null
try {
    $zip = Join-Path $dl 'openrgb.zip'
    Invoke-WebRequest $url -OutFile $zip -UseBasicParsing
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
    if ($hash -ne $sha256) { throw "OpenRGB zip checksum mismatch: $hash" }
    Expand-Archive $zip (Join-Path $dl 'x')
    $inner = Get-ChildItem (Join-Path $dl 'x') -Directory | Select-Object -First 1
    Move-Item $inner.FullName $out
}
finally { Remove-Item -Recurse -Force $dl -ErrorAction SilentlyContinue }

# OpenRGB is GPL-2.0: ship its licence and where its exact source lives.
Invoke-WebRequest https://www.gnu.org/licenses/old-licenses/gpl-2.0.txt -OutFile (Join-Path $out 'LICENSE-OpenRGB.txt') -UseBasicParsing
@"
OpenRGB 1.1 (unmodified official Windows 64-bit build), used by RigControl as its lighting engine.
Licence: GPL-2.0, see LICENSE-OpenRGB.txt.
Source for this exact build: https://codeberg.org/OpenRGB/OpenRGB/src/commit/$commit
Release: https://codeberg.org/OpenRGB/OpenRGB/releases/tag/release_1.1
"@ | Set-Content (Join-Path $out 'SOURCE.txt') -Encoding utf8

"OpenRGB engine ready: {0:N1} MB in {1}" -f ((Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), $out
