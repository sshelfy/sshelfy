<#
.SYNOPSIS
    Builds sshelfy as a single self-contained sshelfy.exe and installs it for the current user.

.DESCRIPTION
    Output goes to artifacts\sshelfy; unless -NoInstall is given, the exe is copied to
    %LocalAppData%\Programs\sshelfy and a Start menu shortcut is created. Run it again after
    pulling or changing the code to update. Your data (hosts, vault, history, settings, logs) lives in
    %AppData%\sshelfy and is not touched by building or installing.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\publish.ps1
#>
param(
    [switch]$NoInstall,
    [switch]$NoShortcut
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$output = Join-Path $root 'artifacts\sshelfy'
$installDir = Join-Path $env:LOCALAPPDATA 'Programs\sshelfy'

# Version 1.0.<number of commits>, with the commit hash shown in the informational version.
# Without git or without any commit yet (a fresh clone of an empty repo, a source ZIP) it is 1.0.0+local.
$version = '1.0.0'
$hash = 'local'
$dirty = ''
if ((Get-Command git -ErrorAction SilentlyContinue) -and (git -C $root rev-parse --verify --quiet HEAD)) {
    $version = "1.0.$((git -C $root rev-list --count HEAD).Trim())"
    $hash = (git -C $root rev-parse --short HEAD).Trim()
    $dirty = if (git -C $root status --porcelain) { '-dirty' } else { '' }
}

Write-Host "Building sshelfy $version ($hash$dirty)..." -ForegroundColor Cyan
if (Test-Path $output) { Remove-Item -Recurse -Force $output }

dotnet publish (Join-Path $root 'src\Sshelfy.App\Sshelfy.App.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=embedded `
    -p:Version=$version `
    -p:InformationalVersion="$version+$hash$dirty" `
    -o $output
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }

# Native debug symbols from SkiaSharp/HarfBuzz (~100 MB) are of no use to the installed app.
Get-ChildItem $output -Filter *.pdb | Remove-Item -Force

$exe = Join-Path $output 'sshelfy.exe'
Write-Host "Built $exe ($([math]::Round((Get-Item $exe).Length / 1MB)) MB)" -ForegroundColor Green
if ($NoInstall) { return }

# The exe cannot be replaced while it runs.
$running = Get-Process -Name sshelfy -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($installDir, [StringComparison]::OrdinalIgnoreCase) }
if ($running) { throw "sshelfy is running from $installDir. Close it and run this script again." }

New-Item -ItemType Directory -Force $installDir | Out-Null
Get-ChildItem $installDir -Filter *.pdb | Remove-Item -Force
Copy-Item (Join-Path $output '*') $installDir -Recurse -Force
Write-Host "Installed to $installDir" -ForegroundColor Green

if (-not $NoShortcut) {
    $shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'sshelfy.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($shortcut)
    $link.TargetPath = Join-Path $installDir 'sshelfy.exe'
    $link.WorkingDirectory = $installDir
    $link.Description = 'sshelfy SSH and SFTP client'
    $link.Save()
    Write-Host "Start menu shortcut: $shortcut" -ForegroundColor Green
}