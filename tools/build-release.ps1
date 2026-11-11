# Builds a release into build\release: the app folder, the portable zip and the installer (Inno Setup 6).
# Run the fetch-*.ps1 scripts first so the bundled engines are included. Prints the SHA-256 of each file.
param([string]$Out)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot
$version = ([xml](Get-Content (Join-Path $root 'src\RigControl\RigControl.csproj'))).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$out = if ($Out) { $Out } else { Join-Path $root 'build\release' }
$app = Join-Path $out 'RigControl'

foreach ($engine in 'openrgb\OpenRGB.exe', 'liquidctl\bridge.py', 'headsetcontrol\headsetcontrol.exe') {
    if (-not (Test-Path (Join-Path $root "build\$engine"))) { throw "build\$engine is missing: run the matching tools\fetch-*.ps1 first" }
}
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found (winget install JRSoftware.InnoSetup)' }

if (Test-Path $out) { Remove-Item -Recurse -Force $out }
dotnet run --project (Join-Path $root 'tests\RigControl.Checks') | Select-Object -Last 1
if ($LASTEXITCODE -ne 0) { throw 'self-checks failed' }
dotnet publish (Join-Path $root 'src\RigControl') -c Release -o $app -p:DebugType=none -p:DebugSymbols=false -p:ContinuousIntegrationBuild=true -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'publish failed' }

# Python caches embed the paths of the PC that made them (running the helpers or checks creates them): never ship them.
Get-ChildItem $app -Recurse -Directory -Filter __pycache__ | Remove-Item -Recurse -Force
Get-ChildItem $app -Recurse -File -Include *.pyc | Remove-Item -Force
# Nothing in a release may name this PC's user or where the repo lives.
$latin1 = [Text.Encoding]::Latin1
$needles = @($env:USERNAME, $root) | Where-Object { $_.Length -ge 4 }
$leaks = foreach ($f in Get-ChildItem $app -Recurse -File) {
    $bytes = [IO.File]::ReadAllBytes($f.FullName)
    $text = $latin1.GetString($bytes) + [Text.Encoding]::Unicode.GetString($bytes)
    foreach ($n in $needles) { if ($text.IndexOf($n, [StringComparison]::OrdinalIgnoreCase) -ge 0) { "$($f.FullName.Substring($app.Length)): $n"; break } }
}
if ($leaks) { $leaks | Select-Object -First 20; throw 'the release would contain personal data (listed above)' }

$zip = Join-Path $out "rig-control-$version-win-x64.zip"
Compress-Archive -Path "$app\*" -DestinationPath $zip -CompressionLevel Optimal
& $iscc /Q "/DAppVersion=$version" "/DSourceDir=$app" (Join-Path $root 'installer\RigControl.iss')
if ($LASTEXITCODE -ne 0) { throw 'installer build failed' }

Get-ChildItem $out -File | ForEach-Object { '{0}  {1,7:N1} MB  {2}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash, ($_.Length / 1MB), $_.Name }
