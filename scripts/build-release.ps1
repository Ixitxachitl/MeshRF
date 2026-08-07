<#
.SYNOPSIS
    Builds a self-contained Windows release of MeshRF and packages it as a
    versioned .zip under dist/.

.DESCRIPTION
    1. Builds the native core (RelWithDebInfo) with CMake.
    2. Publishes the WPF app as a self-contained single-file win-x64 build
       (no .NET install required on the target machine).
    3. Copies the native runtime DLLs next to the published executable.
    4. Zips the staged folder to dist/MeshRF-v<version>-win-x64.zip.

    The version defaults to <VersionPrefix> in Directory.Build.props. Pass
    -Tag to also create an annotated git tag (v<version>) for the release.

.EXAMPLE
    pwsh scripts/build-release.ps1
    pwsh scripts/build-release.ps1 -Version 0.2.0 -Tag
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$NativeConfig = 'RelWithDebInfo',
    [switch]$Tag
)

$ErrorActionPreference = 'Stop'
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
Set-Location $repoRoot

# --- Preflight: ensure linked protobuf schemas are present --------------
$protoSentinel = Join-Path $repoRoot 'third_party/meshtastic_protobufs/meshtastic/mesh.proto'
if (-not (Test-Path $protoSentinel)) {
    throw 'Missing Meshtastic protobuf schema files in third_party/meshtastic_protobufs. Initialize submodules with: git submodule update --init --recursive'
}

# --- Resolve tools -------------------------------------------------------
function Resolve-Cmake {
    $cmd = Get-Command cmake -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $fallback = 'C:\Program Files\CMake\bin\cmake.exe'
    if (Test-Path $fallback) { return $fallback }
    throw 'cmake not found on PATH or at C:\Program Files\CMake\bin.'
}
$cmake = Resolve-Cmake

# --- Resolve version -----------------------------------------------------
if (-not $Version) {
    $props = Join-Path $repoRoot 'Directory.Build.props'
    $xml = [xml](Get-Content $props)
    $Version = ($xml.Project.PropertyGroup.VersionPrefix | Where-Object { $_ }) | Select-Object -First 1
    if (-not $Version) { throw 'Could not read VersionPrefix from Directory.Build.props.' }
}
Write-Host "Building MeshRF v$Version ($NativeConfig)" -ForegroundColor Cyan

# --- Don't fight a running instance for the DLL --------------------------
# Warn before killing: this runs before any build step has proven it will
# succeed, so silently ending the user's running instance (and any unsaved
# session state) on every invocation is a bad trade for a build that might
# fail moments later.
$runningMeshRf = Get-Process -Name MeshRF -ErrorAction SilentlyContinue
if ($runningMeshRf) {
    Write-Host "==> Stopping running MeshRF instance(s) (PID $($runningMeshRf.Id -join ', ')) to release the native DLL" -ForegroundColor Yellow
    $runningMeshRf | Stop-Process -Force
}

# --- 1. Native build -----------------------------------------------------
Write-Host '==> Configuring + building native core' -ForegroundColor Yellow
& $cmake --preset windows-x64
if ($LASTEXITCODE) { throw "cmake configure failed ($LASTEXITCODE)" }
& $cmake --build build/windows-x64 --config $NativeConfig --target mrf_bridge -j
if ($LASTEXITCODE) { throw "native build failed ($LASTEXITCODE)" }

$nativeBin = Join-Path $repoRoot "build/windows-x64/bin/$NativeConfig"
$nativeDlls = @(
    'MeshRF.Native.dll',
    'hackrf.dll',
    'libusb-1.0.dll',
    'pthreadVC2.dll',
    'rtlsdr.dll'
)

# --- 2. Publish managed app ---------------------------------------------
$stage = Join-Path $repoRoot "dist/stage"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Path $stage -Force | Out-Null

Write-Host '==> Publishing self-contained app' -ForegroundColor Yellow
dotnet publish app/MeshRF.App/MeshRF.App.csproj `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:NativeConfig=$NativeConfig `
    -p:Version=$Version `
    --nologo -o $stage
if ($LASTEXITCODE) { throw "dotnet publish failed ($LASTEXITCODE)" }

# --- 3. Drop native runtime DLLs next to the exe -------------------------
Write-Host '==> Bundling native runtime DLLs' -ForegroundColor Yellow
foreach ($dll in $nativeDlls) {
    $src = Join-Path $nativeBin $dll
    if (Test-Path $src) {
        Copy-Item $src -Destination $stage -Force
    } elseif ($dll -eq 'MeshRF.Native.dll') {
        throw "Required native DLL missing: $src"
    } else {
        Write-Warning "Optional native DLL missing (skipped): $dll"
    }
}
# Ship the license alongside the binaries.
Copy-Item (Join-Path $repoRoot 'LICENSE') -Destination $stage -Force
Copy-Item (Join-Path $repoRoot 'README.md') -Destination $stage -Force

# --- 4. Zip --------------------------------------------------------------
$zipName = "MeshRF-v$Version-win-x64.zip"
$zipPath = Join-Path $repoRoot "dist/$zipName"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Write-Host "==> Packaging $zipName" -ForegroundColor Yellow
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipPath -Force
Remove-Item $stage -Recurse -Force

$size = '{0:N1} MB' -f ((Get-Item $zipPath).Length / 1MB)
Write-Host "Release ready: dist/$zipName ($size)" -ForegroundColor Green

# --- 5. Optional git tag -------------------------------------------------
if ($Tag) {
    $tagName = "v$Version"
    if (git tag --list $tagName) {
        Write-Warning "Tag $tagName already exists; skipping."
    } else {
        git tag -a $tagName -m "MeshRF $tagName"
        Write-Host "Created git tag $tagName (push with: git push origin $tagName)" -ForegroundColor Green
    }
}
