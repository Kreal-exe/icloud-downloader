# Builds iCloud Downloader (C# / Avalonia window + Python engine) and puts it on the Desktop.
#
#   .\build.ps1          build, put iCloudDownloader.exe on the Desktop and open it
#   .\build.ps1 -Zip     also write dist\iCloudDownloader-Windows-x64-v<version>.zip for a release
#                        (the engine bundled as one executable, as GitHub Actions does)
#
# $env:INSTALL_DIR = "D:\Apps\iCloudDownloader"; .\build.ps1   puts it somewhere else.
# Needs the .NET 10 SDK (https://dot.net) and Python 3.10+ (https://python.org). macOS: build-mac.sh.
param([switch]$Zip)
$ErrorActionPreference = "Stop"
$Root = $PSScriptRoot
$Project = Join-Path $Root "app\iCloudDownloader.csproj"
$Version = (([xml](Get-Content $Project)).Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
$Dest = if ($env:INSTALL_DIR) { $env:INSTALL_DIR } else { [Environment]::GetFolderPath("Desktop") }
$Installed = Join-Path $Dest "iCloudDownloader.exe"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"

$dotnet = "dotnet"
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue) -and (Test-Path "C:\Program Files\dotnet\dotnet.exe")) { $dotnet = "C:\Program Files\dotnet\dotnet.exe" }
if (-not ((& $dotnet --list-sdks 2>$null) -match "^10\.")) {
    Write-Error "The .NET 10 SDK is needed: https://dot.net"
}

# The engine runs from the checkout's .venv (the window finds it there); create or update it.
$Python = Join-Path $Root ".venv\Scripts\python.exe"
if (-not (Test-Path $Python)) {
    $py = Get-Command py -ErrorAction SilentlyContinue
    if (-not $py) { $py = Get-Command python -ErrorAction SilentlyContinue }
    if (-not $py) { Write-Error "Python 3 is needed: https://python.org" }
    Write-Host "Creating .venv..."
    & $py.Source -m venv (Join-Path $Root ".venv")
}
Write-Host "Installing the engine..."
& $Python -m pip install --disable-pip-version-check -q -e $Root
if ($LASTEXITCODE) { exit $LASTEXITCODE }

# Build outside the project, so no bin\ or obj\ folders are left in it.
$Out = Join-Path ([IO.Path]::GetTempPath()) ("icloud-downloader-" + [guid]::NewGuid())
$App = Join-Path $Out "iCloudDownloader"
try {
    Write-Host "Building iCloud Downloader $Version..."
    # One self-contained exe: no .NET needed on the user's PC.
    & $dotnet publish $Project -c Release -r win-x64 --self-contained true -o $App --artifacts-path (Join-Path $Out "artifacts") `
        --nologo -v quiet -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true -p:DebugType=none
    if ($LASTEXITCODE) { exit $LASTEXITCODE }

    if ($Zip) {
        Write-Host "Bundling the engine..."
        & $Python -m pip install --disable-pip-version-check -q pyinstaller
        & $Python -m PyInstaller --onefile --name icdl-engine --paths (Join-Path $Root "src") --collect-submodules keyring `
            --collect-data tzdata --noconfirm --log-level WARN --distpath (Join-Path $Out "engine") `
            --workpath (Join-Path $Out "pyi") --specpath $Out (Join-Path $Root "src\icdl_engine.py")
        if ($LASTEXITCODE) { exit $LASTEXITCODE }
        $Package = Join-Path $Out "package\iCloudDownloader"
        New-Item -ItemType Directory -Force (Join-Path $Package "engine") | Out-Null
        Copy-Item (Join-Path $App "iCloudDownloader.exe") $Package
        Copy-Item (Join-Path $Out "engine\icdl-engine.exe") (Join-Path $Package "engine")
        $Dist = Join-Path $Root "dist"
        New-Item -ItemType Directory -Force $Dist | Out-Null
        $Release = Join-Path $Dist "iCloudDownloader-Windows-x64-v$Version.zip"
        Remove-Item $Release -Force -ErrorAction SilentlyContinue
        Compress-Archive -Path $Package -DestinationPath $Release
        Write-Host "Release zip: $Release"
    }

    # Close a running copy first; if it stays open, leave the installed version alone. Closing it stops a download,
    # which continues from the same place when the new version starts.
    $running = @(Get-Process iCloudDownloader -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $Installed })
    if ($running) {
        Write-Host "Closing the running iCloud Downloader..."
        $running | ForEach-Object { $_.CloseMainWindow() | Out-Null }
        $running | ForEach-Object { $_.WaitForExit(10000) | Out-Null }
        if (Get-Process iCloudDownloader -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $Installed }) {
            Write-Error "iCloud Downloader is still running. Close it and run the build again."
        }
    }

    # Only the app's own file is replaced: the folder (the Desktop) keeps everything else.
    New-Item -ItemType Directory -Force $Dest | Out-Null
    Copy-Item (Join-Path $App "iCloudDownloader.exe") $Installed -Force
    Write-Host "Installed: $Installed"
    if (-not $env:CI) { Start-Process $Installed }   # opened right away, except on a CI runner
}
finally {
    Remove-Item -Recurse -Force $Out -ErrorAction SilentlyContinue
}
