# Builds installer/out/HSReplayWatcher-Setup-<version>.exe.
#   powershell -ExecutionPolicy Bypass -File installer\build.ps1 [-HsDir E:\Games\Hearthstone]
# Builds the plugin, stages BepInEx (taken from a Hearthstone folder where it already runs), the plugin, the replay
# server and an embeddable CPython into installer/stage, then compiles HSReplayWatcher.iss with Inno Setup 6.
param(
    [string]$HsDir = "E:\Games\Hearthstone",
    [string]$PythonVersion = "3.12.10"
)
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$repo = Split-Path $here -Parent
$stage = Join-Path $here "stage"
$cache = Join-Path $here "cache"

$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found (winget install JRSoftware.InnoSetup)" }

$version = ([xml](Get-Content (Join-Path $repo "plugin\HsPuzzleBridge.csproj"))).Project.PropertyGroup.Version |
    Where-Object { $_ } | Select-Object -First 1

dotnet build (Join-Path $repo "plugin") -c Release --no-incremental | Out-Host
if ($LASTEXITCODE -ne 0) { throw "plugin build failed" }

if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
$bep = New-Item -ItemType Directory -Force (Join-Path $stage "bepinex")
$plug = New-Item -ItemType Directory -Force (Join-Path $stage "plugin")
$py = New-Item -ItemType Directory -Force (Join-Path $stage "watcher\python")
$srv = New-Item -ItemType Directory -Force (Join-Path $stage "watcher\server")

# BepInEx 5.4.23.5 + doorstop 4.5.0 x64 (older pairs crash Unity 6); HS loads the proxy as version.dll.
foreach ($f in "winhttp.dll", "version.dll", "doorstop_config.ini", ".doorstop_version") {
    Copy-Item (Join-Path $HsDir $f) $bep
}
foreach ($d in "core", "unstripped_corlib") {
    Copy-Item -Recurse (Join-Path $HsDir "BepInEx\$d") (Join-Path $bep "BepInEx\$d")
}
New-Item -ItemType Directory -Force (Join-Path $bep "BepInEx\config") | Out-Null
Copy-Item (Join-Path $HsDir "BepInEx\config\BepInEx.cfg") (Join-Path $bep "BepInEx\config")
Get-ChildItem -Recurse $bep -Filter *.xml | Remove-Item

Copy-Item (Join-Path $HsDir "BepInEx\plugins\com.testhsserver.puzzlebridge.dll") $plug

# Embeddable CPython: its ._pth replaces sys.path, so the server folder is added there (for -m hsserver.catalog).
New-Item -ItemType Directory -Force $cache | Out-Null
$zip = Join-Path $cache "python-$PythonVersion-embed-amd64.zip"
if (-not (Test-Path $zip)) {
    Invoke-WebRequest "https://www.python.org/ftp/python/$PythonVersion/python-$PythonVersion-embed-amd64.zip" -OutFile $zip
}
Expand-Archive $zip $py
$pth = Get-ChildItem $py -Filter "python*._pth" | Select-Object -First 1
Add-Content $pth.FullName "..\server"

New-Item -ItemType Directory -Force (Join-Path $srv "hsserver"), (Join-Path $srv "tools") | Out-Null
Copy-Item (Join-Path $repo "hsserver\*.py"), (Join-Path $repo "hsserver\schema.json"), (Join-Path $repo "hsserver\enums.json") (Join-Path $srv "hsserver")
Copy-Item (Join-Path $repo "tools\replay_server.py") (Join-Path $srv "tools")

& $iscc "/DAppVersion=$version" (Join-Path $here "HSReplayWatcher.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }
Get-Item (Join-Path $here "out\HSReplayWatcher-Setup-$version.exe")
