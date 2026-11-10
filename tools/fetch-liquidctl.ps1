# Builds the bundled liquidctl runtime into build\liquidctl (not committed):
#   build\liquidctl\python\            embeddable Python 3.13 (python.org, SHA-256 pinned)
#   build\liquidctl\python\site-packages\  liquidctl + deps (PyPI, --require-hashes)
#   build\liquidctl\bridge.py          RigControl's helper (copied from src)
# Needs Python 3.13 x64 on the build machine (to run pip; crcmod/docopt are source-only).
# Run once before publishing; the app project picks the folder up when it exists.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$out = Join-Path $root 'build\liquidctl'
$py = Join-Path $out 'python'

$pyVersion = '3.13.17'
$pySha256 = '9e35319bffedab8b36ad44d8f377cb4f1733bcec257a0b6d04d94f01486ddb38'

if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force $py | Out-Null

$dl = Join-Path ([IO.Path]::GetTempPath()) "rigcontrol-pyembed-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory $dl | Out-Null
try {
    $zip = Join-Path $dl "python-$pyVersion-embed-amd64.zip"
    Invoke-WebRequest "https://www.python.org/ftp/python/$pyVersion/python-$pyVersion-embed-amd64.zip" -OutFile $zip -UseBasicParsing
    $hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
    if ($hash -ne $pySha256) { throw "Python embed checksum mismatch: $hash" }
    Expand-Archive $zip $py
}
finally { Remove-Item -Recurse -Force $dl -ErrorAction SilentlyContinue }

# Packages: hash-checked, installed for this exact Python version/ABI.
# --no-compile: compiled .pyc files embed the absolute build path (which contains the builder's user name).
& py -3.13 -m pip install --disable-pip-version-check --no-warn-script-location --no-compile --require-hashes `
    -r (Join-Path $PSScriptRoot 'liquidctl-requirements.txt') --target (Join-Path $py 'site-packages')
if ($LASTEXITCODE -ne 0) { throw 'pip install failed' }
Remove-Item -Recurse -Force (Join-Path $py 'site-packages\bin') -ErrorAction SilentlyContinue
Get-ChildItem $out -Recurse -Directory -Filter __pycache__ | Remove-Item -Recurse -Force
# pip's install records list absolute paths for some files; they aren't needed at run time.
Get-ChildItem $py -Recurse -File -Include RECORD, direct_url.json, INSTALLER, REQUESTED | Remove-Item -Force

# The embeddable Python only looks where python313._pth says; add site-packages.
$pth = Get-ChildItem $py -Filter 'python*._pth' | Select-Object -First 1
Add-Content $pth.FullName 'site-packages'

Copy-Item (Join-Path $root 'src\RigControl.Core\liquidctl\bridge.py') $out
"liquidctl runtime ready: {0:N1} MB in {1}" -f ((Get-ChildItem $out -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), $out
